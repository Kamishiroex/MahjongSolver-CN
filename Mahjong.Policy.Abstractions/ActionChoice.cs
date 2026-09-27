namespace Mahjong.Policy.Abstractions;

public enum ActionKind : byte
{
    Pass,
    Discard,
    Riichi,
    Tsumo,
    Ron,
    Pon,
    Chi,
    AnKan,
    MinKan,
    ShouMinKan,
    Kyushukyuhai,
}

/// <summary>
/// <see cref="Reasoning"/> is a human summary; <see cref="Steps"/> is the structured
/// per-evaluator rationale chain.
/// </summary>
public sealed record ActionChoice(
    ActionKind Kind,
    Tile? DiscardTile = null,
    MeldCandidate? Call = null,
    string Reasoning = "",
    IReadOnlyList<Reason>? Steps = null)
{
    /// <summary>Optional exact physical discard identity, supplied only by policies that retain red tiles.</summary>
    public bool? DiscardRed { get; init; }
    /// <summary>True requires the separate drawn slot; false requires a closed-hand slot.</summary>
    public bool? DiscardTsumogiri { get; init; }

    public IReadOnlyList<Reason> ReasonSteps => Steps ?? [];

    public static ActionChoice Pass(string why = "", IReadOnlyList<Reason>? steps = null) =>
        new(ActionKind.Pass, Reasoning: why, Steps: steps);

    public static ActionChoice Discard(Tile t, string why = "", IReadOnlyList<Reason>? steps = null) =>
        new(ActionKind.Discard, DiscardTile: t, Reasoning: why, Steps: steps);

    public static ActionChoice DeclareRiichi(Tile discard, string why = "", IReadOnlyList<Reason>? steps = null) =>
        new(ActionKind.Riichi, DiscardTile: discard, Reasoning: why, Steps: steps);

    public static ActionChoice DeclareTsumo(string why = "") =>
        new(ActionKind.Tsumo, Reasoning: why);

    public static ActionChoice DeclareRon(string why = "") =>
        new(ActionKind.Ron, Reasoning: why);
}
