using System.Collections.Immutable;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.PublicState;

/// <summary>Current public facts only; does not fabricate an acceptance timestamp or mjai history.</summary>
internal static class PublicCurrentFacts
{
    internal const string Evidence = "docs/cn/PUBLIC-INPUT-GAPS-20260925.md";
    internal static bool Audited(PublicObservationContext? context) =>
        context?.ClientVersion == RuntimeIdentity.TargetGame && context.UldSha256 == LowerHandProfile.EmjUldSha256 &&
        context.Profile?.ClientVersion == context.ClientVersion && context.Profile.UldSha256 == context.UldSha256;

    internal static PublicSnapshot Apply(PublicSnapshot snapshot, AddonProbe addon, PublicObservationContext? context)
    {
        if (!Audited(context) || snapshot.Observation is not { } observation) return snapshot;
        var reference = observation with { Source = "PublicCurrentFacts/current-public-display", Evidence = Evidence };
        var rules = addon.PublicMatchRules;
        if (rules is { Code: "PUBLIC_CURRENT_MAHJONG_DUTY_VERIFIED" } &&
            rules.DutyId is 643 or 644 or 645 or 650 or 766 or 767 or 768 or 769 &&
            rules.MatchType == (rules.DutyId >= 766 ? "east" : "hanchan") &&
            rules.OpenTanyao == (rules.DutyId is not (650 or 769)))
            snapshot = snapshot with { Rules = snapshot.Rules with
            {
                MatchType = Field<string>.Known(rules.MatchType, reference with
                { Source = "GameMain.CurrentContentFinderConditionId+CN.ContentFinderCondition",
                    DerivationInputs = [$"ContentFinderCondition:{rules.DutyId}"] }),
                OpenTanyao = Field<bool>.Known(rules.OpenTanyao, reference),
            } };
        if (addon.PublicRiichiCandidates is not { Count: 4 } sticks ||
            sticks.Select(x => x.ScreenDirection).Distinct().Count() != 4) return snapshot;
        string[] directions = ["bottom", "right", "top", "left"];
        return snapshot with { Players = snapshot.Players.Select(p =>
        {
            int id = (int)p.Position;
            if (id is < 0 or > 3) return p;
            var stick = sticks.SingleOrDefault(s => s.ScreenDirection == directions[id]);
            if (stick?.Path != $"Emj/{100 + id}/2") return p;
            bool? declared = stick switch
            {
                { StickVisible: true, Code: "PUBLIC_RIICHI_STICK_CANDIDATE" } => true,
                { StickVisible: false, Code: "PUBLIC_RIICHI_STICK_HIDDEN_OWNER" } => false,
                _ => null,
            };
            if (declared is null) return p;
            bool currentRiver = p.RiverImages.Observation?.Sequence == observation.Sequence &&
                p.RiverImages.Observation.ObservedAtUtc == observation.ObservedAtUtc &&
                p.RiverImages.Value is { RegionReadable: true };
            var rotated = (currentRiver ? p.RiverImages.Value : null)?.Tiles.Where(t => t.Stable &&
                t.SlotPath == $"Emj/{117 + 3 * id}/4" && t.DisplayPosition?.IsSideways == true).ToArray() ?? [];
            if (declared == false && rotated.Length > 0)
                return p with { RiichiDeclared = Field<bool>.Conflict(false, reference,
                    "RIICHI_DISPLAY_CONFLICT：立直棒明确隐藏，但仍有稳定宣言横牌，等待显示一致。") };
            p = p with { RiichiDeclared = Field<bool>.Known(declared.Value, reference) };
            if (declared == false)
                return p with { RiichiEstablished = Field<bool>.Known(false, reference, SourceKind.Derived),
                    Ippatsu = Field<bool>.Known(false, reference, SourceKind.Derived) };
            // A later discard by this SAME riichi player proves play continued after the declaration
            // and their first post-reach turn has ended. Works on a mid-round public-table baseline.
            // Do not infer the precise acceptance time or an earlier positive ippatsu interval.
            if (rotated.Length == 1 && rotated[0].DisplayPosition is { } position &&
                p.RiverImages.Value!.Tiles.Any(t => t.Stable && t.DisplayPosition is { IsSideways: false } next &&
                    next.ReadOrder > position.ReadOrder))
                p = p with { RiichiEstablished = Field<bool>.Known(true, reference, SourceKind.Derived),
                    Ippatsu = Field<bool>.Known(false, reference, SourceKind.Derived) };
            return p;
        }).ToImmutableArray() };
    }
}
