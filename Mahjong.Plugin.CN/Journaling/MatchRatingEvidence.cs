using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Cn.Rating;
using Mahjong.Plugin.CN.Readers;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record RatingPageEvidence(int CurrentRating, int MatchesPlayed, DateTimeOffset ReadAtUtc,
    string ProfileVersion, long EvidenceRevision)
{
    internal static RatingPageEvidence? From(RatingObservation? value,string context,DateTimeOffset now,bool beforeQueue=false) =>
        value is not null && value.ReadAtUtc<=now && now-value.ReadAtUtc<=TimeSpan.FromMinutes(beforeQueue?30:2) &&
        value.Trusted(context,MahjongRatingReader.Profile,beforeQueue?value.ReadAtUtc:now) && value.MatchesPlayed is >=0 and <=99999
        ? new(value.CurrentRating!.Value,value.MatchesPlayed.Value,value.ReadAtUtc,value.ProfileVersion,value.EvidenceRevision):null;
}
internal sealed record MatchRatingAnchor(Guid MatchId, Guid ContextToken, DateTimeOffset StartedUtc, RatingPageEvidence Before);
internal sealed record MatchRatingSupplement(int Schema, Guid MatchId, Guid ContextToken, string JournalTailSha256, RatingPageEvidence After);

/// <summary>One completed match between two profile counters. Hash binding prevents
/// a stale/copied sidecar from being associated with a different journal.</summary>
internal static class MatchRatingEvidence
{
    internal static int? Delta(MatchRatingAnchor anchor,RatingPageEvidence after,DateTimeOffset completed)
    {
        var before=anchor.Before;
        bool Valid(RatingPageEvidence r)=>r.ProfileVersion==MahjongRatingReader.Profile && r.EvidenceRevision>0 &&
            r.CurrentRating is >=0 and <=99999 && r.MatchesPlayed is >=0 and <=99999;
        if(anchor.MatchId==Guid.Empty || anchor.ContextToken==Guid.Empty || before is null || after is null || !Valid(before) || !Valid(after) ||
            before.ReadAtUtc>anchor.StartedUtc || anchor.StartedUtc-before.ReadAtUtc>TimeSpan.FromMinutes(30) ||
            completed<anchor.StartedUtc || after.ReadAtUtc<=completed || after.ReadAtUtc-completed>TimeSpan.FromMinutes(5) ||
            after.EvidenceRevision<=before.EvidenceRevision || after.MatchesPlayed!=before.MatchesPlayed+1)return null;
        return after.CurrentRating-before.CurrentRating;
    }

    internal static MatchRatingAnchor? Anchor(JournalReadResult read,Guid match)
    {
        if(!read.IntegrityPassed || read.IncompleteTail)return null;
        try
        {
            var anchors=read.Lines.Where(x=>x.Entry.Kind=="review_rating_anchor")
                .Select(x=>x.Entry.Data.Deserialize<MatchRatingAnchor>()).Where(a=>a is {Before:not null} && a.MatchId==match).ToArray();
            return anchors.Length==1?anchors[0]:null;
        }
        catch(JsonException){return null;}
    }

    internal static ImmutableArray<MatchSummary> Apply(JournalReadResult read,ImmutableArray<MatchSummary> rows,MatchRatingSupplement supplement)
    {
        if(supplement.Schema!=2 || !read.IntegrityPassed || read.IncompleteTail || read.Lines.IsEmpty ||
            read.Lines[^1].Sha256!=supplement.JournalTailSha256 || !read.Lines.Any(l=>l.Entry.Kind=="session_stopped"))return rows;
        var anchor=Anchor(read,supplement.MatchId);
        if(anchor is null || anchor.ContextToken!=supplement.ContextToken || supplement.After is null ||
            supplement.After.CurrentRating is <0 or >99999 || supplement.After.ProfileVersion!=Readers.MahjongRatingReader.Profile)return rows;
        var completions=read.Lines.Select(l=>l.Entry).Where(e=>e.Kind=="match_result" &&
            MatchSummaryBuilder.Text(e.Data,"Source")=="IDutyState.DutyCompleted" &&
            MatchSummaryBuilder.Text(e.Data,"MatchId")==anchor.MatchId.ToString()).ToArray();
        if(completions.Length!=1)return rows;
        var completion=completions[0];
        int? delta=Delta(anchor,supplement.After,completion.Utc);
        return rows.Select(row=>row.Id==anchor.MatchId && row.IntegrityVerified && row.CompletedUtc is not null
            ? row with {RatingBefore=anchor.Before.CurrentRating,RatingAfter=supplement.After.CurrentRating,RatingDelta=delta,
                RatingAssociation=delta.HasValue?"资料页总场数增加 1，与本场完成事件对应":"总场数或时间关联不符"}:row).ToImmutableArray();
    }
}
