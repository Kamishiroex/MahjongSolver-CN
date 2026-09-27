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
            ? row with {Placement=result.Placement,PlacementEvidence=result.Code}:row).ToImmutableArray();
    }
}
