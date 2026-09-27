using System.Collections.Immutable;
using Mahjong.Cn.PublicState;
using Mahjong.Core;

namespace Mahjong.Cn.Events;

public enum RecoveryContinuity { Interrupted, ContinuousObservation }
public enum RecoverySeatBasis { Unknown, RelativeToLocalPlayer }
public enum ReconciliationStatus { AwaitingEvidence, MatchedCandidate, Conflict, CurrentStateRebuilt, ExperimentalMatch }
public sealed record ReconciliationFinding(string Path, string Code, string Reason);
public sealed record RecoveryRiver(ScreenPosition Position, Field<ImmutableArray<VisibleDiscard>> Tiles);

public sealed record MeldRecoveryCheckpoint(Guid ObservationSessionId, long Sequence, DateTimeOffset CapturedUtc,
    string RuntimeFingerprint, Field<ImmutableArray<VisibleTile>> OwnHand,
    Field<ImmutableArray<VisibleMeld>> OwnMelds, ImmutableArray<RecoveryRiver> Rivers)
{
    public Field<string> RoundId { get; init; } = Field<string>.Unknown("没有独立确认的牌局 ID。");
    public RecoverySeatBasis MeldSeatBasis { get; init; }
}

public sealed record RecoveryTableObservation(Guid ObservationSessionId, long Sequence, DateTimeOffset ObservedAtUtc,
    string RuntimeFingerprint, Field<ImmutableArray<VisibleTile>> OwnHand,
    Field<ImmutableArray<VisibleMeld>> VisibleOwnMelds, ImmutableArray<RecoveryRiver> Rivers)
{
    public Field<string> RoundId { get; init; } = Field<string>.Unknown("没有独立确认的牌局 ID。");
    public bool Stable { get; init; }
    public RecoverySeatBasis MeldSeatBasis { get; init; }
}

public sealed record ReconciliationResult(Guid ObservationSessionId, long ObservationSequence,
    ReconciliationStatus Status, ImmutableArray<VisibleMeld> RestoredOwnMelds,
    ImmutableArray<ReconciliationFinding> Evidence, ImmutableArray<ReconciliationFinding> Issues)
{
    public bool CanRestoreOwnMelds => Status == ReconciliationStatus.CurrentStateRebuilt;
    public bool CanRestoreExperimentalMelds => Status == ReconciliationStatus.ExperimentalMatch;
    public RecoverySeatBasis MeldSeatBasis { get; init; }
    // Reconciliation proves a current state, never the order/completeness of missed game events.
    public bool HistoryComplete => false;
    public ImmutableArray<ExperimentalMeldCheckpoint> ExperimentalMelds { get; init; } = [];
}

/// <summary>
/// Compares public evidence without inventing a round ID, hand identity, missing events or tile slots.
/// A caller must invalidate ContinuousObservation on a lifecycle boundary or an observation gap.
/// A matching hand/river alone is insufficient after interruption: a missed added kan can leave
/// both unchanged after a replacement draw. Cross-reload restoration therefore needs current melds.
/// </summary>
public static class TableReconciler
{
    public static ReconciliationResult Compare(MeldRecoveryCheckpoint checkpoint, RecoveryTableObservation current,
        RecoveryContinuity continuity)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(current);
        var evidence = ImmutableArray.CreateBuilder<ReconciliationFinding>();
        var issues = ImmutableArray.CreateBuilder<ReconciliationFinding>();
        void Issue(string path, string code, string reason) => issues.Add(new(path, code, reason));
        void Match(string path, string code, string reason) => evidence.Add(new(path, code, reason));
        ReconciliationResult Result(ReconciliationStatus status, ImmutableArray<VisibleMeld> melds = default,
            RecoverySeatBasis basis = RecoverySeatBasis.Unknown) =>
            new(current.ObservationSessionId, current.Sequence, status, melds.IsDefault ? [] : melds,
                evidence.ToImmutable(), issues.ToImmutable()) { MeldSeatBasis = basis };

        if (string.IsNullOrWhiteSpace(current.RuntimeFingerprint) || current.RuntimeFingerprint != checkpoint.RuntimeFingerprint)
        { Issue("RuntimeFingerprint", "profile-mismatch", "旧日志的客户端或布局身份与当前观察不一致。"); return Result(ReconciliationStatus.Conflict); }
        if (current.ObservationSessionId == Guid.Empty || current.Sequence < 0 || checkpoint.Sequence < 0)
        { Issue("Observation", "observation-unknown", "缺少当前观察身份或序号。"); return Result(ReconciliationStatus.AwaitingEvidence); }
        if (!current.Stable)
        { Issue("Stability", "transition", "当前桌面处于动画或过渡，不恢复副露。"); return Result(ReconciliationStatus.AwaitingEvidence); }
        if (current.RoundId?.IsConfirmed == true && checkpoint.RoundId?.IsConfirmed == true
            && current.RoundId.Value != checkpoint.RoundId.Value)
        { Issue("RoundId", "round-mismatch", "日志与桌面已确认属于不同牌局。"); return Result(ReconciliationStatus.Conflict); }
        if (current.OwnHand is null || checkpoint.OwnHand is null ||
            current.OwnHand.Availability == Availability.Conflict || checkpoint.OwnHand.Availability == Availability.Conflict)
        { Issue("OwnHand", "hand-conflict", "手牌观察缺失或矛盾。"); return Result(ReconciliationStatus.Conflict); }
        if (!current.OwnHand.HasValue || !checkpoint.OwnHand.HasValue)
        { Issue("OwnHand", "hand-unknown", "无法核对当前手牌与日志中的手牌，赤牌身份也必须已知。"); return Result(ReconciliationStatus.AwaitingEvidence); }
        if (!ValidTiles(current.OwnHand.Value) || !ValidTiles(checkpoint.OwnHand.Value))
        { Issue("OwnHand", "tile-invalid", "手牌牌种、赤牌身份或数量无效。"); return Result(ReconciliationStatus.Conflict); }
        bool sameHand = SameTiles(current.OwnHand.Value, checkpoint.OwnHand.Value);
        if (!sameHand) Issue("OwnHand", "hand-mismatch", "手牌多重集或赤牌身份发生变化；不能把旧副露直接补入。");
        else Match("OwnHand", "hand-matched", "手牌种类及赤牌多重集一致；排序和当前 UI 槽位不作为永久身份。");

        bool riversMatch = CompareRivers(checkpoint.Rivers, current.Rivers, evidence, issues, out bool trustedRivers);
        if (issues.Any(i => i.Code is "river-conflict" or "river-shortened" or "river-prefix-mismatch"))
            return Result(ReconciliationStatus.Conflict);
        if (current.VisibleOwnMelds?.Availability == Availability.Conflict || checkpoint.OwnMelds?.Availability == Availability.Conflict)
        { Issue("OwnMelds", "meld-conflict", "公开副露或日志副露存在冲突。"); return Result(ReconciliationStatus.Conflict); }

        bool currentMelds = Confirmed(current.VisibleOwnMelds);
        bool checkpointMelds = Confirmed(checkpoint.OwnMelds);
        if (currentMelds && !ValidInventory(current.OwnHand.Value, current.VisibleOwnMelds!.Value))
        { Issue("OwnMelds", "inventory-conflict", "当前副露与手牌张数、牌种或赤牌数量矛盾。"); return Result(ReconciliationStatus.Conflict); }
        if (checkpointMelds && !ValidInventory(checkpoint.OwnHand.Value, checkpoint.OwnMelds!.Value))
        { Issue("Checkpoint.OwnMelds", "checkpoint-inventory-conflict", "旧日志副露与旧手牌库存矛盾。"); return Result(ReconciliationStatus.Conflict); }
        if (currentMelds)
        {
            if (!Confirmed(current.OwnHand))
            { Issue("OwnHand", "mapping-candidate", "当前手牌仍是候选映射，不能作为恢复依据。"); return Result(ReconciliationStatus.MatchedCandidate); }
            if (!RestoreSeatBasis(current.VisibleOwnMelds!.Value, current.MeldSeatBasis))
            { Issue("OwnMelds.FromSeat", "seat-basis-unknown", "公开副露来源须是本机为 0 的相对玩家方位；门风不能转换为此坐标。"); return Result(ReconciliationStatus.MatchedCandidate); }
            // Only current confirmed values are returned. Differing/unverified log data is not merged.
            if (checkpoint.OwnMelds?.HasValue == true && !SameMelds(checkpoint.OwnMelds.Value, current.VisibleOwnMelds!.Value))
                Issue("Checkpoint.OwnMelds", "checkpoint-melds-stale", "日志副露与当前可见副露不同；采用当前已确认副露，不补造遗漏事件。");
            Match("OwnMelds", "current-melds-observed", "当前稳定桌面已独立确认完整自家副露，可重建当前状态。");
            Issue("History", "history-gap", "桌面重建不能恢复中断期间事件的完整顺序。");
            return Result(ReconciliationStatus.CurrentStateRebuilt, current.VisibleOwnMelds!.Value, current.MeldSeatBasis);
        }

        bool continuous = continuity == RecoveryContinuity.ContinuousObservation
            && checkpoint.ObservationSessionId != Guid.Empty && current.ObservationSessionId == checkpoint.ObservationSessionId
            && current.Sequence >= checkpoint.Sequence && current.ObservedAtUtc >= checkpoint.CapturedUtc;
        if (!sameHand) return Result(ReconciliationStatus.AwaitingEvidence);
        if (continuous && riversMatch && trustedRivers && Confirmed(current.OwnHand) && Confirmed(checkpoint.OwnHand) && checkpointMelds
            && RestoreSeatBasis(checkpoint.OwnMelds!.Value, checkpoint.MeldSeatBasis))
        {
            Match("ObservationSessionId", "continuous-observation", "同一未中断的读侧会话保持副露历史；该会话 ID 不是麻将局 ID。");
            Issue("History", "history-not-proven-complete", "恢复当前副露不代表已有完整 AI 事件历史。");
            return Result(ReconciliationStatus.CurrentStateRebuilt, checkpoint.OwnMelds!.Value, checkpoint.MeldSeatBasis);
        }
        Issue("OwnMelds", "meld-evidence-required", "中断后需要当前已确认的公开副露；手牌和牌河相似不能排除遗漏加杠。");
        if (!continuous) Issue("History", "history-gap", "读侧会话已中断或不同，旧日志不能自动认作当前完整牌局。");
        if (!checkpointMelds) Issue("Checkpoint.OwnMelds", "mapping-candidate", "旧副露缺少确认依据，候选值只供核对。");
        return Result(riversMatch ? ReconciliationStatus.MatchedCandidate : ReconciliationStatus.AwaitingEvidence);
    }

    public static ReconciliationResult RebuildCurrentState(RecoveryTableObservation current)
    {
        ArgumentNullException.ThrowIfNull(current);
        // The new checkpoint mirrors only current observations. Interrupted mode forces the
        // independent visible-meld branch and cannot promote an unknown current meld field.
        var mirror = new MeldRecoveryCheckpoint(current.ObservationSessionId, current.Sequence, current.ObservedAtUtc,
            current.RuntimeFingerprint, current.OwnHand, current.VisibleOwnMelds, current.Rivers)
            { RoundId = current.RoundId, MeldSeatBasis = current.MeldSeatBasis };
        return Compare(mirror, current, RecoveryContinuity.Interrupted);
    }

    private static bool CompareRivers(ImmutableArray<RecoveryRiver> oldRivers, ImmutableArray<RecoveryRiver> rivers,
        ImmutableArray<ReconciliationFinding>.Builder evidence, ImmutableArray<ReconciliationFinding>.Builder issues,
        out bool trusted)
    {
        trusted = false;
        if (oldRivers.IsDefault || rivers.IsDefault || oldRivers.Length != 4 || rivers.Length != 4 ||
            oldRivers.Any(x => x is null) || rivers.Any(x => x is null) ||
            oldRivers.Select(x => x.Position).Distinct().Count() != 4 || rivers.Select(x => x.Position).Distinct().Count() != 4)
        { issues.Add(new("Rivers", "rivers-unknown", "四个屏幕方位的牌河证据不完整。")); return false; }
        bool matched = true;
        trusted = true;
        foreach (var river in rivers)
        {
            string path = $"Rivers.{river.Position}";
            var before = oldRivers.FirstOrDefault(x => x.Position == river.Position)?.Tiles;
            var after = river.Tiles;
            if (before is null || after is null || before.Availability == Availability.Conflict || after.Availability == Availability.Conflict)
            { issues.Add(new(path, "river-conflict", "牌河来源或解析结果矛盾。")); matched = trusted = false; continue; }
            if (!before.HasValue || !after.HasValue)
            { issues.Add(new(path, "river-unknown", "该方向牌河未确认，未知不等于空牌河。")); matched = trusted = false; continue; }
            if (before.Value.Length > 32 || after.Value.Length > 32 || before.Value.Any(x => x is null) || after.Value.Any(x => x is null))
            { issues.Add(new(path, "river-conflict", "牌河超出容量或存在缺损项。")); matched = trusted = false; continue; }
            if (after.Value.Length < before.Value.Length)
            { issues.Add(new(path, "river-shortened", "牌河缩短，不能排除换局或遗漏被鸣牌历史。")); matched = trusted = false; continue; }
            for (int i = 0; i < before.Value.Length; i++)
                if (before.Value[i].Tile != after.Value[i].Tile ||
                    (before.Value[i].Tedashi.HasValue && after.Value[i].Tedashi.HasValue && before.Value[i].Tedashi != after.Value[i].Tedashi) ||
                    (before.Value[i].Claimed == true && after.Value[i].Claimed == false))
                { issues.Add(new(path, "river-prefix-mismatch", "牌河已有顺序、赤牌或被鸣状态与日志冲突。")); matched = false; break; }
            trusted &= Confirmed(before) && Confirmed(after);
            if (matched) evidence.Add(new(path, "river-prefix-matched", "已有牌河顺序与当前公开前缀一致；新增牌仍需原始观察确定时序。"));
        }
        return matched;
    }

    internal static bool SameTiles(ImmutableArray<VisibleTile> a, ImmutableArray<VisibleTile> b) =>
        !a.IsDefault && !b.IsDefault && a.OrderBy(x => x.Id).ThenBy(x => x.Red).SequenceEqual(b.OrderBy(x => x.Id).ThenBy(x => x.Red));

    private static bool Confirmed<T>(Field<T>? field) => field?.IsConfirmed == true &&
        (field.SourceKind != SourceKind.Derived || field.Observation!.DerivationInputs.Length > 0);

    internal static bool RestoreSeatBasis(ImmutableArray<VisibleMeld> melds, RecoverySeatBasis basis) =>
        !melds.IsDefault && melds.All(m => m is not null && (m.Kind == MeldKind.AnKan ||
            (basis == RecoverySeatBasis.RelativeToLocalPlayer && m.FromSeat is >= 1 and <= 3 && m.ClaimedTile is not null)));

    internal static bool SameMelds(ImmutableArray<VisibleMeld> a, ImmutableArray<VisibleMeld> b)
    {
        if (a.IsDefault || b.IsDefault || a.Length != b.Length) return false;
        var unused = b.ToList();
        foreach (var meld in a)
        {
            if (meld is null) return false;
            int index = unused.FindIndex(other => other is not null && other.Kind == meld.Kind && SameTiles(other.Tiles, meld.Tiles)
                && (other.FromSeat is null || meld.FromSeat is null || other.FromSeat == meld.FromSeat)
                && (other.RiverIndex is null || meld.RiverIndex is null || other.RiverIndex == meld.RiverIndex)
                && (other.ClaimedTile is null || meld.ClaimedTile is null || other.ClaimedTile == meld.ClaimedTile));
            if (index < 0) return false;
            unused.RemoveAt(index);
        }
        return true;
    }

    internal static bool ValidTiles(ImmutableArray<VisibleTile> tiles) => !tiles.IsDefault && tiles.Length <= 14 &&
        tiles.All(t => t.Id is >= 0 and <= 33 && (!t.Red || t.Id is 4 or 13 or 22)) &&
        tiles.GroupBy(t => t.Id).All(g => g.Count() <= 4 && g.Count(t => t.Red) <= 1);

    internal static bool ValidInventory(ImmutableArray<VisibleTile> hand, ImmutableArray<VisibleMeld> melds)
    {
        if (!ValidTiles(hand) || melds.IsDefault || melds.Length > 4 || hand.Length + melds.Length * 3 is not (13 or 14)) return false;
        var all = hand.ToList();
        foreach (var meld in melds)
        {
            if (!ValidMeld(meld)) return false;
            all.AddRange(meld.Tiles);
        }
        return all.GroupBy(x => x.Id).All(g => g.Count() <= 4 && g.Count(t => t.Red) <= 1);
    }

    internal static bool ValidMeld(VisibleMeld? meld)
    {
        if (meld is null || !Enum.IsDefined(meld.Kind) || !ValidTiles(meld.Tiles)) return false;
        bool kan = meld.Kind is MeldKind.AnKan or MeldKind.MinKan or MeldKind.ShouMinKan;
        if (meld.Tiles.Length != (kan ? 4 : 3) || meld.FromSeat is < 0 or > 3 || meld.RiverIndex is < 0 or > 31) return false;
        if (meld.Kind == MeldKind.Chi)
        {
            var ids = meld.Tiles.Select(x => x.Id).Order().ToArray();
            if (ids[0] >= 27 || ids[0] / 9 != ids[2] / 9 || ids[1] != ids[0] + 1 || ids[2] != ids[0] + 2) return false;
        }
        else if (meld.Tiles.Any(x => x.Id != meld.Tiles[0].Id)) return false;
        if (meld.Kind == MeldKind.AnKan && (meld.FromSeat is not null || meld.ClaimedTile is not null)) return false;
        return meld.ClaimedTile is not { } called || meld.Tiles.Contains(called);
    }
}
