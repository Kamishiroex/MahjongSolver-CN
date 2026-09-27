namespace Mahjong.Cn.Rating;

public enum RatingMapping { Unsupported, Candidate, Verified }
public enum RatingFreshness { Unknown, Fresh, NeedsRefresh, Cached }
public sealed record RatingObservation(int? CurrentRating, int? HighestRating, string? Rank,
    string LocalCharacterContext, DateTimeOffset ReadAtUtc, string Source,
    RatingMapping Mapping, RatingFreshness Freshness, string ProfileVersion,
    Guid? MatchAssociation, long EvidenceRevision, string? FailureReason, int? MatchesPlayed = null)
{
    public bool Trusted(string context, string profile, DateTimeOffset now, Guid? match = null) =>
        CurrentRating is >= 0 and <= 99999 && Mapping == RatingMapping.Verified &&
        Freshness == RatingFreshness.Fresh && EvidenceRevision > 0 &&
        !string.IsNullOrEmpty(context) && LocalCharacterContext == context && ProfileVersion == profile &&
        ReadAtUtc <= now + TimeSpan.FromSeconds(5) && now - ReadAtUtc <= TimeSpan.FromMinutes(2) &&
        (match is null || MatchAssociation == match);
}
