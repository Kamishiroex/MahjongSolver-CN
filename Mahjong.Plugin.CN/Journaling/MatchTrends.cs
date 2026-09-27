using System.Collections.Immutable;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record MatchTrend(string Key, uint? DutyId, string MatchType, string Engine, string Version,
    string? ConfigurationFingerprint, string Opponents, string Intervention, int Records, int Completed,
    int PlacementSamples, double? MeanPlacement, double? FourthRate, int RatingSamples, double? MeanRatingDelta,
    int HandSamples, double? WinRate, double? DealInRate, int AutomaticSamples, int AutomaticCompleted,
    int Takeovers, int Stops, int Submissions, int ObservedTransitions, int Timeouts, int CancelledWindows);

internal static class MatchTrends
{
    internal static ImmutableArray<MatchTrend> Build(IEnumerable<MatchSummary> source) => source
        .GroupBy(r=>new {r.DutyId,r.MatchType,r.Engine,r.PluginVersion,r.ConfigurationFingerprint,r.MixedConfiguration,
            r.OpponentEnvironment,Intervention=r.RecordedTakeovers is null?"接管未知":r.RecordedTakeovers>0?"有接管":"无记录接管"})
        .Select(g=>
        {
            var valid=g.Where(r=>r.IntegrityVerified && r.CompletedUtc is not null).ToArray();
            // Unidentified/mixed configurations remain visible but never become comparable strength samples.
            var comparable=valid.Where(r=>r.ConfigurationFingerprint is not null && !r.MixedConfiguration).ToArray();
            var ranks=comparable.Where(r=>r.Placement is >=1 and <=4).Select(r=>r.Placement!.Value).ToArray();
            var ratings=comparable.Where(r=>r.RatingDelta.HasValue).Select(r=>r.RatingDelta!.Value).ToArray();
            var hands=comparable.Where(r=>r.HandsWithVerifiedOutcome>0 && r.Wins>=0 && r.DealIns>=0 &&
                r.Wins<=r.HandsWithVerifiedOutcome && r.DealIns<=r.HandsWithVerifiedOutcome).ToArray();
            int total=hands.Sum(r=>r.HandsWithVerifiedOutcome!.Value);
            var key=g.Key;
            string identity=$"{key.DutyId}:{key.MatchType}:{key.Engine}:{key.PluginVersion}:{key.ConfigurationFingerprint}:{key.MixedConfiguration}:{key.Intervention}";
            return new MatchTrend(identity,key.DutyId,key.MatchType,key.Engine,key.PluginVersion,key.ConfigurationFingerprint,
                key.OpponentEnvironment,key.Intervention,g.Count(),valid.Length,ranks.Length,
                ranks.Length>0?ranks.Average():null,ranks.Length>0?(double)ranks.Count(n=>n==4)/ranks.Length:null,
                ratings.Length,ratings.Length>0?ratings.Average():null,total,
                total>0?(double)hands.Sum(r=>r.Wins!.Value)/total:null,total>0?(double)hands.Sum(r=>r.DealIns!.Value)/total:null,
                valid.Count(r=>r.AutomaticWholeMatch.HasValue),valid.Count(r=>r.AutomaticWholeMatch==true),
                g.Sum(r=>r.RecordedTakeovers??0),g.Sum(r=>r.Stops.Length),g.Sum(r=>r.Submissions),
                g.Sum(r=>r.ObservedTransitions),g.Sum(r=>r.ObservationTimeouts),g.Sum(r=>r.CancelledWindows));
        }).OrderByDescending(r=>r.Records).ToImmutableArray();
}
