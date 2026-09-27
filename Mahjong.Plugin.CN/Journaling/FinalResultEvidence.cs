using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Plugin.CN.Readers;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record FinalResultReading(int? Placement,string Code,ResultUiSample Sample);
internal sealed record FinalResultSupplement(int Schema,Guid MatchId,string JournalTailSha256,
    DateTimeOffset ReadAtUtc,ResultUiSample Sample);

internal static class FinalResultEvidence
{
    internal static FinalResultReading Read(ResultUiSample sample)
    {
        FinalResultReading Unknown(string reason)=>new(null,reason,sample);
        if(sample.Addon!="EmjTotalResult" || sample.Profile!=ResultUiReader.Profile || !sample.Visible || sample.Error is not null ||
            sample.Values is null || sample.Values.Count>64 || sample.Values.Any(v=>v is null))
            return Unknown("FINAL_RESULT_UNAVAILABLE");
        var ranks=new List<int>();var ours=new List<int>();
        foreach(int row in new[]{20,21,22,23})
        {
            var rank=sample.Values.Where(v=>v.Path==$"EmjTotalResult/{row}/10").ToArray();
            if(rank.Length!=1 || rank[0].Number is not (>=1 and <=4))return Unknown("FINAL_RANKS_INCOMPLETE");
            ranks.Add(rank[0].Number!.Value);
            if(sample.Values.Count(v=>v.Path==$"EmjTotalResult/{row}/15" && v.IsSelf)==1)ours.Add(ranks[^1]);
        }
        if(ranks.Distinct().Count()!=4)return Unknown("FINAL_RANKS_CONTRADICTORY");
        return ours.Count==1?new(ours[0],"FINAL_RESULT_CONFIRMED",sample):Unknown("FINAL_SELF_UNIDENTIFIED");
    }

    internal static ImmutableArray<MatchSummary> Apply(JournalReadResult read,ImmutableArray<MatchSummary> rows,FinalResultSupplement value)
    {
        if(value.Schema!=1 || !read.IntegrityPassed || read.IncompleteTail || read.Lines.IsEmpty ||
            read.Lines[^1].Sha256!=value.JournalTailSha256 || value.Sample is null)return rows;
        var completions=read.Lines.Select(l=>l.Entry).Where(e=>e.Kind=="match_result" &&
            MatchSummaryBuilder.Text(e.Data,"Source")=="IDutyState.DutyCompleted" &&
            MatchSummaryBuilder.Text(e.Data,"MatchId")==value.MatchId.ToString()).ToArray();
        if(completions.Length!=1 || !read.Lines.Any(l=>l.Entry.Kind=="session_stopped") ||
            value.ReadAtUtc<completions[0].Utc.AddSeconds(-5) || value.ReadAtUtc>completions[0].Utc.AddMinutes(5))return rows;
        var result=Read(value.Sample);
        return rows.Select(row=>row.Id==value.MatchId && row.IntegrityVerified && row.CompletedUtc is not null
            ? CompleteLastHand(read,row with {Placement=result.Placement,PlacementEvidence=result.Code},value.Sample):row).ToImmutableArray();
    }
    private static MatchSummary CompleteLastHand(JournalReadResult read,MatchSummary row,ResultUiSample sample)
    {
        // Match four final names to the four actually visible table names in memory.
        // Never infer seat order from rank or points (ties and seat rotations exist).
        var scores=sample.Values.Where(v=>v.RelativeSeat.HasValue && v.Path.EndsWith("/4",StringComparison.Ordinal)).ToArray();
        if(scores.Length!=4 || scores.Any(v=>v.Number is null || v.RelativeSeat is <0 or >3) ||
            scores.Select(v=>v.RelativeSeat).Distinct().Count()!=4)return row;
        var start=read.Lines.LastOrDefault(l=>l.Entry.Kind=="review_hand_started" &&
            MatchSummaryBuilder.Text(l.Entry.Data,"MatchId")==row.Id.ToString());
        string? round=start is null?null:MatchSummaryBuilder.Text(start.Entry.Data,"RoundId");
        if(round is null)return row;
        var proofs=new List<PendingHandResult>();
        foreach(var line in read.Lines.Where(l=>l.Entry.Kind=="review_hand_pending" && l.Entry.Sequence>=start!.Entry.Sequence &&
            MatchSummaryBuilder.Text(l.Entry.Data,"MatchId")==row.Id.ToString()))
        {
            try {if(line.Entry.Data.TryGetProperty("Evidence",out var e) && e.Deserialize<PendingHandResult>() is {} p && p.RoundId==round)proofs.Add(p);}
            catch(JsonException){return row;}
        }
        if(proofs.Count==0 || proofs.Select(p=>JsonSerializer.Serialize(p)).Distinct().Count()!=1)return row;
        var proof=proofs[0];
        if(proof.Banner is null)return row;
        string? kind=ResultResourceCatalog.Banner(proof.Banner);
        var hand=HandResultTracker.Read(round,kind,new("Emj",ResultUiReader.Profile,true,[],null),proof.ScoresBefore,
            scores.OrderBy(v=>v.RelativeSeat).Select(v=>v.Number!.Value).ToArray());
        if(!hand.Complete)return row;
        var existing=row.Hands.FirstOrDefault(h=>h.RoundId==round);
        if(existing?.Complete==true && (existing.Kind!=hand.Kind || existing.SelfWon!=hand.SelfWon || existing.SelfDealtIn!=hand.SelfDealtIn))
            hand=hand with {SelfWon=null,SelfDealtIn=null,Code="RESULT_CONTRADICTORY"};
        var hands=row.Hands.Where(h=>h.RoundId!=round).Append(hand).ToImmutableArray();
        var known=hands.Where(h=>h.Complete && HandResultTracker.Valid(h)).ToArray();
        return row with {Hands=hands,ObservedHands=Math.Max(row.ObservedHands,hands.Length),
            HandsWithVerifiedOutcome=known.Length>0?known.Length:null,Wins=known.Length>0?known.Count(h=>h.SelfWon==true):null,
            DealIns=known.Length>0?known.Count(h=>h.SelfDealtIn==true):null};
    }
}
