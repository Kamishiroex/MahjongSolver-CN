using System.Collections.Immutable;
using Mahjong.Cn.PublicState;
using Mahjong.Core;

namespace Mahjong.Cn.Events;

// These are deliberately not Field<T>: they remain experimental legacy observations, including
// relative coordinates and aggregate red counts. A match never promotes PublicSnapshot quality.
public sealed record ExperimentalMeldCheckpoint(MeldKind Kind, ImmutableArray<int> TileIds,
    int RedTileCount, int? FromRelativePlayer, int? RiverIndex, int? ClaimedTileId);

public sealed record VisibleAreaFootprint(ScreenPosition Position, ImmutableArray<VisibleTile> Tiles,
    bool Readable, string SourceEvidence);

public sealed record VisibleMeldGroupEvidence(string GroupPath, ImmutableArray<VisibleTile> Faces,
    int VisibleSlots, bool AllVisibleSlotsDecoded, bool Stable, string SourceEvidence)
{
    // This represents public backs, never decoded hidden faces. Five-tile kans remain
    // unsupported because a red five may be behind a public back.
    public int VerifiedBackSlots { get; init; }
    public int? ClosedKanKind34 { get; init; }
    public bool HasUnambiguousClosedKanInventory => !AllVisibleSlotsDecoded && VisibleSlots == 4 &&
        VerifiedBackSlots == 2 && ClosedKanKind34 is >= 0 and <= 33 and not (4 or 13 or 22) &&
        !Faces.IsDefault && Faces.Length == 2 && Faces.All(t => t.Id == ClosedKanKind34 && !t.Red);
    public ImmutableArray<VisibleTile> Inventory => HasUnambiguousClosedKanInventory
        ? Enumerable.Repeat(new VisibleTile(ClosedKanKind34!.Value), 4).ToImmutableArray() : Faces;
}

public sealed record ExperimentalRecoveryAnchor(ImmutableArray<VisibleTile> OwnHand,
    ImmutableArray<int> Scores, ImmutableArray<VisibleTile> DoraDisplay, ImmutableArray<int> RiverCounts,
    ImmutableArray<VisibleAreaFootprint> TableFootprint);

public enum ExperimentalRecoveryMode { Exact, InterruptedProgress }

public sealed record ExperimentalRecoveryEvidence(ExperimentalRecoveryAnchor CheckpointAnchor,
    ExperimentalRecoveryAnchor CurrentAnchor, ImmutableArray<ExperimentalMeldCheckpoint> CheckpointMelds,
    ImmutableArray<VisibleMeldGroupEvidence> CurrentMeldGroups)
{
    public ExperimentalRecoveryMode Mode { get; init; }
}

public static class ExperimentalTableReconciler
{
    /// <summary>Matches only independently visible complete groups to saved legacy melds.
    /// Does not recover temporal event history, infer a seat wind, or supply trusted AI input.</summary>
    public static ReconciliationResult CompareExperimental(MeldRecoveryCheckpoint checkpoint,
        RecoveryTableObservation current, ExperimentalRecoveryEvidence proof)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proof);
        var matches = ImmutableArray.CreateBuilder<ReconciliationFinding>();
        var issues = ImmutableArray.CreateBuilder<ReconciliationFinding>();
        ReconciliationResult Result(ReconciliationStatus status, string code, string reason)
        {
            issues.Add(new("ExperimentalRecovery", code, reason));
            return new(current.ObservationSessionId, current.Sequence, status, [], matches.ToImmutable(), issues.ToImmutable())
            {
                MeldSeatBasis = RecoverySeatBasis.RelativeToLocalPlayer,
                ExperimentalMelds = status == ReconciliationStatus.ExperimentalMatch ? proof.CheckpointMelds : [],
            };
        }
        if (current.ObservationSessionId == Guid.Empty || current.Sequence < 0 || !current.Stable)
            return Result(ReconciliationStatus.AwaitingEvidence, "current-observation-incomplete", "当前观察身份或稳定状态不足。");
        if (checkpoint.CapturedUtc > current.ObservedAtUtc ||
            (checkpoint.ObservationSessionId == current.ObservationSessionId && checkpoint.Sequence > current.Sequence))
            return Result(ReconciliationStatus.Conflict, "checkpoint-from-future", "当前观察早于待恢复检查点。");
        if (current.OwnHand?.Availability == Availability.Conflict || current.VisibleOwnMelds?.Availability == Availability.Conflict)
            return Result(ReconciliationStatus.Conflict, "current-observation-conflict", "当前公开观察存在冲突，不能用实验锚点绕过。");
        if (string.IsNullOrWhiteSpace(checkpoint.RuntimeFingerprint) || checkpoint.RuntimeFingerprint != current.RuntimeFingerprint)
            return Result(ReconciliationStatus.Conflict, "profile-mismatch", "旧记录与当前客户端或布局版本不一致。");
        if (checkpoint.RoundId?.IsConfirmed == true && current.RoundId?.IsConfirmed == true && checkpoint.RoundId.Value != current.RoundId.Value)
            return Result(ReconciliationStatus.Conflict, "round-mismatch", "已知不同牌局，不能恢复旧副露。");
        if (!ValidAnchor(proof.CheckpointAnchor) || !ValidAnchor(proof.CurrentAnchor))
            return Result(ReconciliationStatus.AwaitingEvidence, "anchor-incomplete", "手牌、点数、宝牌、四家牌河计数及四方向公开牌面证据必须完整。");
        var old = proof.CheckpointAnchor;
        var now = proof.CurrentAnchor;
        if (!Enum.IsDefined(proof.Mode)) return Result(ReconciliationStatus.AwaitingEvidence, "recovery-mode-unknown", "未知实验恢复模式。");
        bool progressed = proof.Mode == ExperimentalRecoveryMode.InterruptedProgress;
        if (!old.Scores.SequenceEqual(now.Scores) || !old.DoraDisplay.SequenceEqual(now.DoraDisplay) ||
            (!progressed && (!TableReconciler.SameTiles(old.OwnHand, now.OwnHand) || !old.RiverCounts.SequenceEqual(now.RiverCounts))) ||
            (progressed && old.RiverCounts.Where((count, index) => now.RiverCounts[index] < count).Any()))
            return Result(ReconciliationStatus.Conflict, "legacy-anchor-mismatch", "旧记录与当前手牌及赤牌、点数、宝牌或牌河计数不一致。");
        if (progressed && (old.RiverCounts.Sum() == 0 || old.TableFootprint.All(x => x.Tiles.Length == 0)))
            return Result(ReconciliationStatus.AwaitingEvidence, "prior-river-anchor-required", "中断进度恢复至少需要旧记录已有的公开弃牌锚点。");
        foreach (var area in now.TableFootprint)
            if (!(progressed ? ContainsTiles(area.Tiles, old.TableFootprint.Single(x => x.Position == area.Position).Tiles) :
                TableReconciler.SameTiles(old.TableFootprint.Single(x => x.Position == area.Position).Tiles, area.Tiles)))
                return Result(ReconciliationStatus.Conflict, "visible-footprint-mismatch", "公开桌面牌面多重集改变；不把显示顺序当作出牌时序。");
        matches.Add(new("Anchors", "legacy-anchors-matched", "旧及当前公开锚点一致；保持实验候选级别。"));
        if (proof.CheckpointMelds.IsDefault || proof.CurrentMeldGroups.IsDefault || proof.CheckpointMelds.Length > 4 ||
            proof.CurrentMeldGroups.Length != proof.CheckpointMelds.Length)
            return Result(ReconciliationStatus.AwaitingEvidence, "meld-groups-incomplete", "当前完整副露组数与旧记录不符。");
        if (proof.CurrentMeldGroups.Any(g => g is null || !g.Stable ||
            string.IsNullOrWhiteSpace(g.GroupPath) || string.IsNullOrWhiteSpace(g.SourceEvidence) || g.Faces.IsDefault ||
            (!(g.AllVisibleSlotsDecoded && g.VerifiedBackSlots == 0 && g.ClosedKanKind34 is null &&
               g.VisibleSlots == g.Faces.Length && g.VisibleSlots is 3 or 4) && !g.HasUnambiguousClosedKanInventory) ||
            !TableReconciler.ValidTiles(g.Faces)) ||
            proof.CurrentMeldGroups.Select(g => g.GroupPath).Distinct(StringComparer.Ordinal).Count() != proof.CurrentMeldGroups.Length)
            return Result(ReconciliationStatus.AwaitingEvidence, "meld-face-incomplete", "副露存在未知背牌、遮挡、赤五歧义或不稳定组，不能实验恢复。");
        var unmatched = proof.CurrentMeldGroups.ToList();
        var visibleMelds = ImmutableArray.CreateBuilder<VisibleMeld>();
        foreach (var meld in proof.CheckpointMelds)
        {
            if (meld is null || meld.TileIds.IsDefault || !Enum.IsDefined(meld.Kind) || meld.RedTileCount is < 0 or > 1 ||
                meld.RiverIndex is < 0 or > 31 || (meld.Kind == MeldKind.AnKan ?
                    meld.FromRelativePlayer is not null || meld.ClaimedTileId is not null :
                    meld.FromRelativePlayer is not (>= 1 and <= 3) || meld.ClaimedTileId is null))
                return Result(ReconciliationStatus.Conflict, "legacy-meld-invalid", "旧副露来源或赤牌总数无效。");
            int index = unmatched.FindIndex(g => (!g.HasUnambiguousClosedKanInventory || meld.Kind == MeldKind.AnKan) &&
                g.Inventory.Select(x => x.Id).Order().SequenceEqual(meld.TileIds.Order()) &&
                g.Inventory.Count(x => x.Red) == meld.RedTileCount);
            if (index < 0)
                return Result(ReconciliationStatus.Conflict, "meld-multiset-mismatch", "当前副露组牌种、赤牌或三/四张数量与旧记录不同；可能遗漏加杠。");
            var faces = unmatched[index].Inventory;
            unmatched.RemoveAt(index);
            // For inventory validation only; do not assert which physical occurrence was claimed.
            if (meld.ClaimedTileId is { } claimed && !faces.Any(x => x.Id == claimed))
                return Result(ReconciliationStatus.Conflict, "claimed-tile-mismatch", "旧鸣牌种类不在当前副露中。");
            visibleMelds.Add(new(meld.Kind, faces, meld.FromRelativePlayer, meld.RiverIndex, null));
        }
        if (!TableReconciler.ValidInventory(now.OwnHand, visibleMelds.ToImmutable()))
            return Result(ReconciliationStatus.Conflict, "inventory-conflict", "恢复后的牌数、牌种或赤牌库存矛盾。");
        matches.Add(new("OwnMelds", "visible-groups-matched", "每个旧副露均有当前完整公开牌面组独立匹配；仅恢复原实验策略的副露库存。"));
        return Result(ReconciliationStatus.ExperimentalMatch, "experimental-history-gap", "这是实验性当前状态匹配；缺失事件历史仍保留，不能用于完整 AI 重放。");
    }

    private static bool ContainsTiles(ImmutableArray<VisibleTile> current, ImmutableArray<VisibleTile> previous) =>
        previous.GroupBy(x => x).All(group => current.Count(x => x == group.Key) >= group.Count());

    private static bool ValidAnchor(ExperimentalRecoveryAnchor? a) => a is not null && TableReconciler.ValidTiles(a.OwnHand) &&
        !a.Scores.IsDefault && a.Scores.Length == 4 && a.Scores.All(x => x is >= -1000000 and <= 1000000) &&
        !a.DoraDisplay.IsDefault && a.DoraDisplay.Length <= 5 && TableReconciler.ValidTiles(a.DoraDisplay) &&
        !a.RiverCounts.IsDefault && a.RiverCounts.Length == 4 && a.RiverCounts.All(x => x is >= 0 and <= 32) &&
        !a.TableFootprint.IsDefault && a.TableFootprint.Length == 4 &&
        a.TableFootprint.All(x => x is not null && Enum.IsDefined(x.Position) && x.Readable && !string.IsNullOrWhiteSpace(x.SourceEvidence)
            && !x.Tiles.IsDefault && x.Tiles.Length <= 96 && x.Tiles.All(t => t.Id is >= 0 and <= 33 && (!t.Red || t.Id is 4 or 13 or 22))) &&
        a.TableFootprint.Select(x => x.Position).Distinct().Count() == 4;
}
