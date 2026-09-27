using System.Collections.Immutable;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record HistoryCoverage(int Records,int Completed,int Ranked,int Rated,int Hands,int VerifiedHands,
    int Untrusted,int Mixed,int Interrupted);

internal static class HistoryQuery
{
    internal static bool HasGap(MatchSummary r)=>!r.IntegrityVerified || r.CompletedUtc is null ||
        r.Placement is null || r.RatingDelta is null || r.ObservedHands==0 ||
        (r.HandsWithVerifiedOutcome??0)<r.ObservedHands;
    internal static ImmutableArray<MatchSummary> Filter(IEnumerable<MatchSummary> source,DateTimeOffset now,
        int days=0,uint? duty=null,string? backend=null,bool gapsOnly=false)=>source.Where(r=>
            (days<=0 || r.StartedUtc>=now.AddDays(-days)) && (!duty.HasValue || r.DutyId==duty) &&
            (backend is null || r.Engine==backend) && (!gapsOnly || HasGap(r)))
        .OrderByDescending(r=>r.StartedUtc).ToImmutableArray();
    internal static HistoryCoverage Coverage(IEnumerable<MatchSummary> source)
    {
        var rows=source.ToArray();var valid=rows.Where(r=>r.IntegrityVerified).ToArray();
        return new(rows.Length,valid.Count(r=>r.CompletedUtc.HasValue),valid.Count(r=>r.Placement.HasValue),
            valid.Count(r=>r.RatingDelta.HasValue),rows.Sum(r=>r.ObservedHands),valid.Sum(r=>r.HandsWithVerifiedOutcome??0),
            rows.Count(r=>!r.IntegrityVerified),rows.Count(r=>r.MixedConfiguration),rows.Count(r=>r.Stops.Length>0));
    }
}
