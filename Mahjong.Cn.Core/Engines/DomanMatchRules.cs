namespace Mahjong.Cn.Engines;

/// <summary>Public match context, separate from engine-specific round numbering.
/// Match length comes from the observed duty. Other rules describe the official Doman
/// guide, not a claim that every CN ending/timeout has been observed in this client.</summary>
public sealed record DomanMatchRules
{
    public int SchemaVersion { get; init; } = 1;
    public string ProfileId { get; init; } = "doman-public-v1";
    public string? MatchType { get; init; }
    public int? ScheduledHands { get; init; }
    public bool? OpenTanyao { get; init; }
    public string MatchEvidence { get; init; } = "unknown";
    public string RuleEvidence { get; init; } = "official-doman-guide; CN-ending-verification-pending";
    public bool ScoreExtension { get; init; } = false;
    public bool DealerRepeatsOnWin { get; init; } = true;
    public bool DealerRepeatsOnTenpai { get; init; } = true;
    public bool LeadingDealerEndsOnWinOrTenpai { get; init; } = true;
    public string TieBreak { get; init; } = "initial-seat-order";
    public string FinalObjective { get; init; } = "placement";
    // The time-limit state is not read; do not turn elapsed plugin time into all-last.
    public bool? TimeLimitFinalHand { get; init; }

    public static DomanMatchRules FromObservedMatch(string? matchType, bool? openTanyao) => new()
    {
        MatchType = matchType is "east" or "hanchan" ? matchType : null,
        ScheduledHands = matchType switch { "east" => 4, "hanchan" => 8, _ => null },
        OpenTanyao = openTanyao,
        MatchEvidence = matchType is "east" or "hanchan" ? "observed-current-duty" : "unknown",
    };
}
