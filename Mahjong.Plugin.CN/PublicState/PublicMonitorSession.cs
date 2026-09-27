using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.PublicState;

internal sealed record RegionEvidence(string Area, int MetadataRecords, int RejectedRecords);
internal sealed record MonitorEvidence(long Sequence, DateTimeOffset Utc, string Code, string Reason,
    ImmutableArray<RegionEvidence> Regions, int LowerCandidates, int AcceptedLowerFaces);

/// <summary>
/// Projection of the existing guarded public reader, not a new memory reader or policy.
/// No game services, pointers, action dispatchers, process/network APIs or engine exist here.
/// State revisions identify observations; they are not invented Mahjong events or rounds.
/// </summary>
internal sealed class PublicMonitorSession
{
    private Guid sessionId;
    private long lastSequence = -1;
    private long revision;
    private bool sawTable;
    private readonly PublicTableTracker publicTableTracker = new();
    private readonly PublicRoundEventTracker roundTracker = new();
    private readonly PublicOwnHandTransitionTracker ownHandTracker = new();
    private readonly PublicCallEventTracker callTracker = new();
    private readonly PublicRiverEventTracker riverTracker = new();
    private readonly PublicTrackingEpoch trackingEpoch = new();
    private readonly PublicIppatsuTracker ippatsuTracker = new();
    private readonly PublicRiichiWindowTracker riichiWindowTracker = new();
    private readonly PublicDrawKindTracker drawKindTracker = new();
    private readonly PublicFuritenTracker furitenTracker = new();
    private PublicObservationContext? observationContext;
    internal bool IsActive { get; private set; }
    internal PublicSnapshot? Current { get; private set; }
    internal PublicSnapshot? Last { get; private set; }
    internal MonitorEvidence? Evidence { get; private set; }
    internal PublicRoundProgress? RoundProgress { get; private set; }
    internal PublicOwnHandTransitionObservation? OwnHandProgress { get; private set; }
    internal PublicCallTrackingResult? CallProgress { get; private set; }
    internal PublicRiverHistoryObservation? RiverProgress { get; private set; }
    internal string? TrackingEpochToken => trackingEpoch.Token;
    internal string TrackingEpochStatus => trackingEpoch.Code;
    internal string Status { get; private set; } = "只读监视未开启。";

    internal void Start(PublicObservationContext? context = null)
    {
        observationContext = context;
        sessionId = Guid.NewGuid();
        lastSequence = -1;
        revision = 0;
        sawTable = false;
        publicTableTracker.Clear();
        roundTracker.Reset();
        ownHandTracker.Clear();
        callTracker.Clear();
        riverTracker.Clear();
        trackingEpoch.Clear();
        ippatsuTracker.Clear();
        riichiWindowTracker.Clear();
        drawKindTracker.Clear();
        furitenTracker.Clear();
        RoundProgress = null;
        OwnHandProgress = null;
        CallProgress = null;
        RiverProgress = null;
        Current = Last = null;
        Evidence = null;
        IsActive = true;
        Status = "只读监视待命；等待进入牌桌。";
    }

    internal void Stop(string reason)
    {
        IsActive = false;
        Current = null;
        publicTableTracker.Clear();
        roundTracker.Reset();
        ownHandTracker.Clear();
        callTracker.Clear();
        riverTracker.Clear();
        trackingEpoch.Clear();
        ippatsuTracker.Clear();
        riichiWindowTracker.Clear();
        drawKindTracker.Clear();
        furitenTracker.Clear();
        RoundProgress = null;
        OwnHandProgress = null;
        CallProgress = null;
        RiverProgress = null;
        observationContext = null;
        Status = reason;
        // Last is explicitly historical and is available only to manual diagnostic export.
    }

    internal void Observe(DiagnosticFrame frame)
    {
        if (!IsActive) return;
        if (frame.Sequence <= lastSequence)
        {
            Invalidate(frame, "STALE_OBSERVATION", "采样序号未递增；已清除当前牌面。", StabilityState.Unknown);
            return;
        }
        lastSequence = frame.Sequence;
        var visible = frame.Addons.Where(x => x.Present && x.Visible).ToArray();
        if (sawTable && !frame.Addons.Any(x => x.Present && x.Name is "Emj" or "EmjL"))
        {
            Stop("SCENE_EXIT：牌桌关闭，只读监视已停止；重新进入后需主动开启。");
            return;
        }
        if (visible.Length > 1)
        {
            Invalidate(frame, "MULTIPLE_TABLES", "同时存在多个可见候选，不能拼接为同一牌局。", StabilityState.Unknown);
            return;
        }
        if (frame.Addons.FirstOrDefault(x => x.Error is not null) is { } failed)
        {
            Invalidate(frame, "READ_ERROR", failed.Error!, StabilityState.Unknown);
            return;
        }
        if (visible.Length == 0 || !visible[0].Ready)
        {
            Invalidate(frame, "NO_READY_TABLE", "牌桌不可见或处于过渡，已清除旧牌面。", StabilityState.Transition);
            return;
        }
        sawTable = true;
        var addon = visible[0];
        if (addon.Name != "Emj")
        {
            Invalidate(frame, "UNSUPPORTED_LAYOUT", "EmjL 尚无本机公开牌面映射实测；保留未知。", StabilityState.Unknown);
            return;
        }
        addon = addon with { PublicTableReading = publicTableTracker.Observe(frame.Sequence, addon) };
        var reading = addon.LowerHandReading;
        if (reading is not { Stable: true } || reading.Tiles.IsDefaultOrEmpty)
        {
            Invalidate(frame, reading?.Code ?? "LOWER_NOT_READ", reading?.Reason ?? "等待受控下方牌面读取。",
                reading?.Code == "LOWER_STABILIZING" ? StabilityState.Transition : StabilityState.Unknown, addon);
            return;
        }

        // Recheck the managed boundary as well. A replay or caller must not upgrade an
        // unguarded node, a different resource or a stale slot into confirmed imagery.
        var faces = addon.LowerHandFaces;
        if (!LowerHandProfile.CheckLayout(faces).Eligible || faces!.Count != reading.Tiles.Length ||
            reading.Tiles.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != reading.Tiles.Length ||
            reading.Tiles.GroupBy(x => x.Kind34).Any(x => x.Count() > 4))
        {
            Invalidate(frame, "LOWER_PROJECTION_CONFLICT", "牌面与当前候选的数量或路径不一致。", StabilityState.Unknown, addon);
            return;
        }
        var orderedFaces = faces.OrderBy(x => x.X).ThenBy(x => x.Y).ToArray();
        for (int index = 0; index < reading.Tiles.Length; index++)
        {
            var tile = reading.Tiles[index];
            var face = orderedFaces[index];
            if (face.Path != tile.Path || tile.DisplayPosition != index + 1 || face.DiagnosticStatus != LowerHandProfile.VerifiedIconStatus ||
                face.IconId != tile.IconId || face.FacePathHash != tile.FacePathHash ||
                !LowerTileCatalog.TryDecode(tile.IconId, tile.FacePathHash, out var identity) ||
                identity.Kind34 != tile.Kind34 || identity.RedFive != tile.RedFive)
            {
                Invalidate(frame, "LOWER_PROJECTION_CONFLICT", "资源、路径或赤牌身份未同时匹配，旧牌面已清除。", StabilityState.Unknown, addon);
                return;
            }
        }
        var observation = Reference(frame);
        long stateRevision = ++revision;
        var tiles = reading.Tiles.Select(x => new PublicHandTile(new VisibleTile(x.Kind34, x.RedFive),
            new UiTileSlot(x.Path, x.DisplayPosition, stateRevision))).ToImmutableArray();
        Current = PublicObservationAssembler.Assemble(new PublicSnapshot
        {
            SessionId = sessionId, StateRevision = stateRevision, Observation = observation,
            Stability = StabilityState.Stable,
            Synchronization = SynchronizationState.HistoryGap,
            SynchronizationReason = "只有离散可见图像，尚无可信开局、完整实体手牌或连续事件；不能供完整牌局 AI 决策。",
            LowerVisibleFaces = Field<ImmutableArray<PublicHandTile>>.Known(tiles, observation),
        }, addon, observationContext);
        ObserveTransitions(frame, addon);
        Last = Current;
        Evidence = Summarize(frame, "LOWER_VISIBLE_ONLY", "已确认的是受限图像身份，非完整手牌或摸牌身份。", tiles.Length);
        Status = $"只读监视：下方 {tiles.Length} 张可见牌面已稳定；公开桌面与局况按各自证据记录，完整牌局不同步。";
    }

    private void Invalidate(DiagnosticFrame frame, string code, string reason, StabilityState stability, AddonProbe? publicAddon = null)
    {
        if (publicAddon is null) publicTableTracker.Clear();
        Current = PublicObservationAssembler.Assemble(new PublicSnapshot
        {
            SessionId = sessionId, StateRevision = ++revision, Observation = Reference(frame),
            Stability = stability, Synchronization = SynchronizationState.HistoryGap,
            SynchronizationReason = code + "：" + reason,
            LowerVisibleFaces = code.Contains("CONFLICT", StringComparison.Ordinal) || code == "MULTIPLE_TABLES"
                ? new Field<ImmutableArray<PublicHandTile>> { Availability = Availability.Conflict,
                    SourceKind = SourceKind.Observed, Observation = Reference(frame), Reason = reason }
                : Field<ImmutableArray<PublicHandTile>>.Unknown(reason),
        }, publicAddon, observationContext);
        ObserveTransitions(frame, publicAddon);
        Last = Current;
        Evidence = Summarize(frame, code, reason, 0);
        Status = code + "：" + reason;
    }

    private void ObserveTransitions(DiagnosticFrame frame, AddonProbe? addon)
    {
        if (Current is null) return;
        RoundProgress = roundTracker.Observe(Current, addon);
        Current = Current with { RoundId = RoundProgress.RoundId };
        string? previousEpoch = trackingEpoch.Token;
        string? epoch = trackingEpoch.Observe(Current, addon, observationContext);
        if (previousEpoch != epoch)
        {
            // A local gap starts a new baseline. Downstream trackers see every raw slot
            // in that baseline and must not report late-decoded old tiles as new events.
            ownHandTracker.Clear();
            callTracker.Clear();
            riverTracker.Clear();
        }
        OwnHandProgress = ownHandTracker.Observe(frame.Sequence, frame.Utc, addon, observationContext,
            epoch);
        CallProgress = callTracker.Observe(frame.Sequence, frame.Utc,
            epoch, addon,
            observationContext?.ClientVersion, observationContext?.UldSha256);
        Current = ippatsuTracker.Observe(Current, epoch, CallProgress, observationContext);
        Current = riichiWindowTracker.Observe(Current, epoch, observationContext);
        Current = drawKindTracker.Observe(Current, epoch, CallProgress, OwnHandProgress, observationContext);
        Current = furitenTracker.Observe(Current, epoch, addon, observationContext);
        RiverProgress = riverTracker.Observe(frame.Sequence, frame.Utc, addon, observationContext,
            epoch);
        if (!RoundProgress.RoundId.IsConfirmed)
        {
            // An empty baseline of a local observation segment is not proof that the
            // actual round began there. Keep real new events, but never expose their
            // local sequence as a recovered round-long discard ordinal.
            RiverProgress = RiverProgress with
            {
                HasHistoryGap = true,
                Slots = RiverProgress.Slots.Select(slot => slot with { DiscardOrdinal = null }).ToImmutableArray(),
                NewEvents = RiverProgress.NewEvents.Select(change => change with { DiscardOrdinal = null }).ToImmutableArray(),
                Issues = RiverProgress.Issues.Any(issue => issue.Code == "RIVER_PARTIAL_OBSERVATION_EPOCH")
                    ? RiverProgress.Issues
                    : RiverProgress.Issues.Add(new("RIVER_PARTIAL_OBSERVATION_EPOCH", frame.Sequence)),
            };
        }
        // These independently evidenced transitions do not yet form a complete event prefix.
        // Do not clear HistoryGap or fill LegalActions merely because the trackers made progress.
    }

    private static ObservationReference Reference(DiagnosticFrame frame) => new(frame.Sequence, frame.Utc,
        "VisibleUiReader/LowerHandTracker:Emj",
        "docs/cn/TILE-RESOURCE-EVIDENCE.md; docs/cn/FOURTH-CAPTURE.md; docs/cn/PUBLIC-MONITOR-EVIDENCE.md", []);

    private static MonitorEvidence Summarize(DiagnosticFrame frame, string code, string reason, int accepted)
    {
        // These are metadata record counts, NEVER tile counts or event order.
        var addon = frame.Addons.FirstOrDefault(x => x.Name == "Emj");
        var regions = (addon?.PublicLayouts ?? []).GroupBy(x => x.Area, StringComparer.Ordinal)
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new RegionEvidence(x.Key, x.Count(), x.Count(y => y.Status != "PUBLIC_LAYOUT_METADATA_ONLY")))
            .ToImmutableArray();
        return new(frame.Sequence, frame.Utc, code, reason, regions, addon?.LowerHandFaces?.Count ?? 0, accepted);
    }
}
