using Mahjong.Cn.Rating;

namespace Mahjong.Cn.Tasks;

public sealed record StopRuleSet(int MatchLimit = 0, double? ActiveSecondsLimit = null,
    DateTimeOffset? DeadlineUtc = null, int? TargetRating = null, int? RatingFloor = null,
    int? MaximumRatingLoss = null, int? ConsecutiveFourthLimit = null)
{
    public bool NeedsRating => TargetRating is not null || RatingFloor is not null || MaximumRatingLoss is not null;
    public string? Validate() => MatchLimit is < 0 or > 9999 ? "MATCH_LIMIT_INVALID" :
        ActiveSecondsLimit is { } s && (!double.IsFinite(s) || s <= 0 || s > 604800) ? "DURATION_INVALID" :
        TargetRating is < 0 or > 99999 || RatingFloor is < 0 or > 99999 ? "RATING_RANGE_INVALID" :
        TargetRating is { } t && RatingFloor is { } f && f >= t ? "RATING_RULES_CONFLICT" :
        MaximumRatingLoss is <= 0 or > 99999 || ConsecutiveFourthLimit is <= 0 or > 9999 ? "LOSS_LIMIT_INVALID" : null;
}
public enum StopDecision { Continue, StopAfterMatch, HoldForRequiredData, StopImmediately }
public sealed record StopEvaluation(StopDecision Decision, string Code);
public sealed record StopRuleInput(int CompletedMatches, double ActiveSeconds, bool InMatch,
    bool StopRequested, string CharacterContext, string ProfileVersion, DateTimeOffset NowUtc,
    RatingObservation? Rating, RatingObservation? StartingRating = null,
    Guid? RequiredMatchAssociation = null, int? ConsecutiveFourth = 0);

/// <summary>Pure rules: no game/clock/disk/network calls. Known OR stops precede missing data.</summary>
public static class StopRuleEvaluator
{
    public static StopEvaluation Evaluate(StopRuleSet rules, StopRuleInput x)
    {
        if (rules.Validate() is { } invalid) return new(StopDecision.StopImmediately, invalid);
        StopEvaluation Stop(string code) => new(StopDecision.StopAfterMatch, code);
        if (x.StopRequested) return Stop("USER_AFTER_MATCH");
        if (rules.MatchLimit > 0 && x.CompletedMatches >= rules.MatchLimit) return Stop("MATCH_LIMIT");
        if (rules.ActiveSecondsLimit is { } seconds && x.ActiveSeconds >= seconds) return Stop("DURATION_LIMIT");
        if (rules.DeadlineUtc is { } deadline && x.NowUtc >= deadline) return Stop("DEADLINE");
        if (rules.ConsecutiveFourthLimit is { } limit && x.ConsecutiveFourth >= limit) return Stop("FOURTH_LIMIT");
        bool trusted = x.Rating?.Trusted(x.CharacterContext, x.ProfileVersion, x.NowUtc, x.RequiredMatchAssociation) == true;
        if (trusted)
        {
            int rating=x.Rating!.CurrentRating!.Value;
            if (rules.TargetRating is { } target && rating >= target) return Stop("TARGET_RATING");
            if (rules.RatingFloor is { } floor && rating <= floor) return Stop("RATING_FLOOR");
            // The baseline is intentionally old, but had to be trusted when the task started.
            if (rules.MaximumRatingLoss is { } loss && x.StartingRating is { Mapping: RatingMapping.Verified,
                Freshness: RatingFreshness.Fresh, CurrentRating: { } start } initial &&
                initial.LocalCharacterContext == x.CharacterContext && initial.ProfileVersion == x.ProfileVersion &&
                rating - start <= -loss) return Stop("RATING_LOSS");
        }
        // Do not interrupt an ongoing match for routine score refresh; hold its next-match boundary.
        if (rules.NeedsRating && !trusted && !x.InMatch) return new(StopDecision.HoldForRequiredData,"RATING_REFRESH_REQUIRED");
        if (rules.MaximumRatingLoss is not null && x.StartingRating?.CurrentRating is null && !x.InMatch)
            return new(StopDecision.HoldForRequiredData,"RATING_BASELINE_REQUIRED");
        if (rules.ConsecutiveFourthLimit is not null && x.ConsecutiveFourth is null && !x.InMatch)
            return new(StopDecision.HoldForRequiredData,"PLACEMENT_REQUIRED");
        return new(StopDecision.Continue,"CONTINUE");
    }
}
