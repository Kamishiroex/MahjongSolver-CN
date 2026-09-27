using System.IO.Compression;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mahjong.Core;
using Mahjong.Cn.Events;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Journaling;
using Mahjong.Plugin.CN.PublicState;
using Mahjong.Plugin.Dalamud;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private readonly string? logsDirectory;
    private GameJournal? journal;
    private bool journalActive;
    private string? journalReportedFault;
    private string? runtimeJournalHash;
    private string? tableJournalHash;
    private string? previousRuntimeJournalHash;
    private long runtimeJournalSequence;
    private double lastJournalTablePoll;
    private DateTimeOffset lastJournalTableHeartbeat;
    private int unchangedJournalTableSamples;
    private readonly LowerHandTracker journalLowerTracker = new();
    private readonly PublicTableTracker journalTableTracker = new();
    private LowerHandReading? journalLower;
    private DateTimeOffset journalLowerUtc;
    private PublicTableReading? journalTable;
    private string? journalCheckpointHash;
    private string? journalCheckpointBoundary;
    private DateTimeOffset lastJournalCheckpoint;
    private PublicEventLedger? publicLedger;
    private PublicMonitorSession? journalPublicMonitor;
    private string? journalTrackingEpochToken;
    internal Mahjong.Cn.PublicState.PublicSnapshot? CurrentJournalPublicSnapshot => journalActive ? journalPublicMonitor?.Current : null;
    private readonly RuntimeEventDiffer runtimeEventDiffer = new();
    private readonly Dictionary<string, PublicTableTile[]> journalRiverBaseline = new(StringComparer.Ordinal);
    private Guid publicRecoverySession;
    private long publicRecoveryRuntimeSequence = -1;
    private int publicRecoverySamples;
    internal PublicTableReading? CurrentPublicTable => journalActive ? journalTable : null;
    internal string JournalStatus => journal?.Fault is { } fault ? "日志写入失败：" + fault :
        journalActive ? $"正在记录本场事件与错误；保留最近 {JournalKeepMatches} 场，旧记录后台清理。" : "日志未记录；选择模式后自动开始。";
    internal string? JournalLocation => journal?.DirectoryPath;
    internal string RecoveryStatus { get; private set; } = "暂停可保留记牌；重新加载后的恢复需与当前桌面核对。";

    private void EnsureJournalCore(string reason)
    {
        if (journalActive || logsDirectory is null || disposed || Identity.Error is not null) return;
        journal = new GameJournal(logsDirectory);
        ScheduleJournalMaintenance();
        journalActive = true;
        reviewTableStarted=false;reviewDuty=null;reviewDecisionId=null;reviewDecisionHash=null;
        reviewDutyCompleted=false;ResetResults();
        journalReportedFault = runtimeJournalHash = tableJournalHash = previousRuntimeJournalHash = null;
        recordedAutomationStatus = null;
        runtimeJournalSequence = 0;
        lastJournalTableHeartbeat = default;
        unchangedJournalTableSamples = 0;
        journalLowerTracker.Clear();
        globalAiProjector?.Clear();
        journalTableTracker.Clear();
        journalLower = null;
        journalTable = null;
        journalCheckpointHash = null;
        journalCheckpointBoundary = null;
        lastJournalCheckpoint = default;
        publicLedger = null;
        journalTrackingEpochToken = null;
        journalPublicMonitor = new PublicMonitorSession();
        journalPublicMonitor.Start(PublicObservationAssembler.CreateAuditedContext(Identity, LowerHandUldHash));
        runtimeEventDiffer.Reset();
        journalRiverBaseline.Clear();
        publicRecoverySession = Guid.Empty;
        publicRecoveryRuntimeSequence = -1;
        publicRecoverySamples = 0;
        RecordJournalEvent("session_started", new
        {
            PluginVersion = LocalCaptureRecorder.PluginVersion, Identity, Reason = reason,
            Scope = "Public observations only; source quality is retained. Submitted actions are not game events.",
            FullHistoryVerified = false,
        });
        if(reviewConfigurationMetadata is not null) RecordJournalEvent("review_configuration",reviewConfigurationMetadata);
    }

    private void RecordJournalEvent<T>(string type, T payload)
    {
        if (journalActive) journal?.Event(type, payload);
    }

    private void CompleteJournalCore(string reason)
    {
        if (!journalActive) return;
        var ending = journal;
        Task stopped = PlayRuntime?.StopCompletion ?? Task.CompletedTask;
        long endSequence = runtimeJournalSequence;
        journalPublicMonitor?.Stop(reason);
        RecordPublicTrackingEpoch();
        journalActive = false;
        globalAiProjector?.Clear();
        journalLower = null; journalTable = null;
        journalLowerTracker.Clear(); journalTableTracker.Clear();
        publicRecoverySamples = 0;
        if (ending is not null) _ = FinishJournalAndMaintainAsync(ending, stopped, reason, endSequence);
    }

    private async Task FinishJournalAndMaintainAsync(GameJournal ending, Task stopped, string reason, long endSequence)
    {
        try { await FinishJournalAsync(ending, stopped, reason, endSequence).ConfigureAwait(false); }
        catch (Exception ex) { journalMaintenanceStatus = "日志封存失败：" + ex.GetType().Name; }
        finally { lock (gate) ScheduleJournalMaintenance(); }
    }

    private static async Task FinishJournalAsync(GameJournal ending, Task stopped, string reason, long endSequence)
    {
        try { await stopped.ConfigureAwait(false); }
        finally
        {
            ending.Event("session_stopped", new { Reason = reason, RuntimeSequence = endSequence });
            await ending.CompleteAsync().ConfigureAwait(false);
            await MatchSummaryBuilder.SaveAsync(ending.DirectoryPath).ConfigureAwait(false);
        }
    }

    private void RecordPublicMonitorLedger()
    {
        RecordPublicTrackingEpoch();
        if (!journalActive || journalPublicMonitor?.Current is not { } snapshot) return;
        if (publicLedger?.SessionId != snapshot.SessionId) publicLedger = new PublicEventLedger(snapshot.SessionId);
        var appended = publicLedger.Observe(snapshot);
        foreach (var entry in appended.Entries) RecordJournalEvent("public_event", entry);
        foreach (var issue in appended.Issues) journal?.Error("PUBLIC_EVENT_INVALID", issue);
        if (journalPublicMonitor.RoundProgress is { } round)
            foreach (var signal in round.Signals) RecordJournalEvent("public_round_signal", signal);
        if (journalPublicMonitor.OwnHandProgress?.Transition is { } hand)
            RecordJournalEvent("public_hand_delta", new { snapshot.Observation, snapshot.RoundId, Delta = hand });
        if (journalPublicMonitor.CallProgress is { } calls)
            foreach (var call in calls.Events) RecordJournalEvent("public_call_event", call);
        if (journalPublicMonitor.RiverProgress is { } rivers)
            foreach (var change in rivers.NewEvents) RecordJournalEvent("public_river_event", new { snapshot.RoundId, Change = change });
    }

    private void RecordPublicTrackingEpoch()
    {
        if (!journalActive || journalPublicMonitor is not { } monitor) return;
        string? token = monitor.TrackingEpochToken;
        if (token == journalTrackingEpochToken) return;
        RecordJournalEvent("public_tracking_epoch", new
        {
            PreviousToken = journalTrackingEpochToken, Token = token, Code = monitor.TrackingEpochStatus,
            ObservationSequence = monitor.Current?.Observation?.Sequence,
            RoundIdConfirmed = monitor.Current?.RoundId.IsConfirmed == true,
            HistoryComplete = false,
        });
        journalTrackingEpochToken = token;
    }

    internal void PausePlay()
    {
        RevokeUiIntents();
        SuspendTableAutomation("用户暂停：自动排队及进桌开打已暂停。游戏中已经提交的报名请在任务搜索器取消。");
        gameplayAllowed = false;
        int request = Interlocked.Increment(ref modeRequestVersion);
        _ = Framework.RunOnFrameworkThread(() =>
        {
            lock (gate)
            {
                if (disposed || request != Volatile.Read(ref modeRequestVersion)) return;
                recoveryPending = false;
                aiProbe?.Stop("用户暂停计算。");
                PlayRuntime?.PauseAutomation("用户暂停打牌；继续只读记牌，重新选择手动或自动即可继续。");
                RecordJournalEvent("play_paused", new { ObservationContinues = PlayRuntime?.IsObservingPaused == true });
                Status = PlayRuntime?.Status ?? "已暂停；尚无活动牌桌读取器。";
            }
        });
    }

    private bool RecordStopInJournal(GameplayStopInfo stop)
    {
        if (journal is null) return false; // Legacy tests/older explicit diagnostics keep their existing writer.
        var context = new
        {
            stop.Utc, stop.Reason, Mode = stop.PreviousMode.ToString(), stop.Layout,
            RuntimeSequence = runtimeJournalSequence, stop.Error, stop.Menu, stop.Dispatch,
        };
        journal.Event("play_stopped", context);
        _ = journal.SaveStopAsync(context);
        if (stop.Error is not null || stop.Reason.Contains("ERROR", StringComparison.Ordinal) ||
            stop.Reason.Contains("TIMEOUT", StringComparison.Ordinal) || stop.Reason.Contains("MISSING", StringComparison.Ordinal) ||
            stop.Reason.Contains("失败", StringComparison.Ordinal) || stop.Reason.Contains("异常", StringComparison.Ordinal))
            journal.Error("GAMEPLAY_STOP", context);
        return true;
    }

    private void RecordActionSubmission(GameplayActionSubmission submission)
    {
        lock (gate)
        {
            TrackRecoverySubmission(submission);
            NoteUiEvent("操作请求："+submission.Result+"（提交结果不等于游戏已接受）");
            RecordJournalEvent("action_submission", submission);
            if (submission.Result != "Submitted") journal?.Error("ACTION_SUBMISSION_REJECTED", submission);
        }
    }

    private void RecordRuntimeSnapshot(StateSnapshot snapshot)
    {
        lock (gate)
        {
            if (!journalActive || disposed || PlayRuntime is null) return;
            // This is the original reader's observation, explicitly NOT an upgraded verified CN state.
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(snapshot);
            string hash = Convert.ToHexString(SHA256.HashData(json));
            if (hash == runtimeJournalHash) return;
            previousRuntimeJournalHash = runtimeJournalHash;
            runtimeJournalHash = hash;
            runtimeJournalSequence++;
            foreach (var change in runtimeEventDiffer.Observe(PlayRuntime.ObservationSessionId, PlayRuntime.ObservationSequence, snapshot))
                RecordJournalEvent(change.Type, change);
            RecordJournalEvent("runtime_observation", new
            {
                Sequence = runtimeJournalSequence, PlayRuntime.ObservationSessionId, PlayRuntime.ObservationSequence,
                Snapshot = snapshot, Source = "upstream-experimental-reader", Mapping = "LegacyAssumption",
                SnapshotSha256 = hash, PreviousSnapshotSha256 = previousRuntimeJournalHash,
                FullHistoryVerified = false,
            });
        }
    }

    private void RecordRuntimeCheckpoint(StateSnapshot snapshot)
    {
        // The typed tracker checkpoint is a recovery candidate, never saved armed input or a suggestion.
        if (PlayRuntime?.ExportMeldCheckpoint() is not { } checkpoint) return;
        if (publicRecoverySamples < 2 || publicRecoverySession != PlayRuntime.ObservationSessionId ||
            publicRecoveryRuntimeSequence != PlayRuntime.ObservationSequence ||
            !RecoveryEvidenceBuilder.TryBuildAnchor(snapshot, checkpoint.MeldAkadora, journalLower, journalTable, out _, out _)) return;
        string combined = runtimeJournalHash + ":" + tableJournalHash;
        if (combined == journalCheckpointHash) return;
        journalCheckpointHash = combined;
        var saved = new SavedRecoveryState(1, Identity, LocalCaptureRecorder.PluginVersion,
            PlayRuntime.ObservationSessionId, PlayRuntime.ObservationSequence, snapshot, checkpoint,
            runtimeJournalSequence, journalLower, journalTable);
        journal?.UpdateRecovery(saved);
        string boundary = PlayRuntime.ObservationSessionId + ":" + journalPublicMonitor?.TrackingEpochToken + ":" +
            JsonSerializer.Serialize(snapshot.OurMelds);
        var now = DateTimeOffset.UtcNow;
        if (boundary == journalCheckpointBoundary && now - lastJournalCheckpoint < TimeSpan.FromSeconds(30)) return;
        journalCheckpointBoundary = boundary;
        lastJournalCheckpoint = now;
        RecordJournalEvent("recovery_checkpoint", saved);
    }

    private void UpdateJournalCore()
    {
        if (journal?.Fault is { } fault && journalReportedFault != fault)
        {
            journalReportedFault = fault;
            _ = journal.SaveStopAsync(new { Utc = DateTimeOffset.UtcNow, Reason = "JOURNAL_ERROR:" + fault,
                PluginVersion = LocalCaptureRecorder.PluginVersion, Identity });
            gameplayAllowed = false;
            PlayRuntime?.PauseAutomation("JOURNAL_ERROR：" + fault + "；已暂停打牌，完整事件历史不能保证。");
            RecoveryStatus = "日志出现缺口：" + fault;
        }
        if (!journalActive || Identity.Error is not null || !Client.IsLoggedIn ||
            PlayRuntime is not { } runtime || (runtime.Mode == PlayMode.Off && !runtime.IsObservingPaused)) return;
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now - lastJournalTablePoll < 0.1) return;
        lastJournalTablePoll = now;
        if (LowerHandUldHash != LowerHandProfile.EmjUldSha256) return;
        try
        {
            reader.BeginSample();
            var addon = reader.Probe("Emj", true, captureLowerHand: true, capturePublicLayout: false,
                capturePublicFaces: true, capturePublicStatus: true);
            addon = AttachCurrentMatchRules(addon, ReadCurrentMatchRules);
            if(addon is {Present:true,Visible:true,Ready:true,Error:null})
            {
                BeginReviewTable(false);
                if(addon.PublicMatchRules is { } rules && reviewDuty!=rules.DutyId)
                {
                    reviewDuty=rules.DutyId;
                    RecordJournalEvent("review_table_context",new {rules.DutyId,rules.MatchType,rules.OpenTanyao,
                        Source=rules.Code,OpponentEnvironment="unknown"});
                }
            }
            var frame = new DiagnosticFrame(++sequence, DateTimeOffset.UtcNow, "自动事件记录", "只读公开桌面", [addon]);
            RecordPublicTable(frame);
        }
        catch (Exception ex)
        {
            journal?.Error("PUBLIC_TABLE_READ_ERROR", new { Error = ex.GetType().Name, Sequence = sequence });
            globalAiProjector?.Clear();
            if (ExperimentalHandAiEnabled)
            {
                PlayRuntime?.ActiveAggregator?.ApplySnapshot(null);
                PlayRuntime?.PauseAutomation("PUBLIC_TABLE_READ_ERROR: " + ex.GetType().Name);
            }
            journalLowerTracker.Clear(); journalTableTracker.Clear();
            journalLower = null; journalTable = null;
            publicRecoverySamples = 0;
            journalPublicMonitor?.Observe(new DiagnosticFrame(++sequence, DateTimeOffset.UtcNow, "读取失败", "READ_ERROR",
                [new AddonProbe("Emj", true, false, false, 0, [], "PUBLIC_TABLE_READ_ERROR:" + ex.GetType().Name)]));
            RecordPublicTrackingEpoch();
        }
    }

    private void RecordPublicTable(DiagnosticFrame frame)
    {
        if (!journalActive) return;
        if (PlayRuntime is { } currentRuntime)
        {
            if (publicRecoverySession != currentRuntime.ObservationSessionId || publicRecoveryRuntimeSequence != currentRuntime.ObservationSequence)
            {
                // Recovery needs two fresh samples after each runtime change, but its
                // freshness counter must not repeatedly erase the independent UI history.
                if (publicRecoverySession != currentRuntime.ObservationSessionId)
                { journalLowerTracker.Clear(); journalTableTracker.Clear(); }
                publicRecoverySession = currentRuntime.ObservationSessionId;
                publicRecoveryRuntimeSequence = currentRuntime.ObservationSequence;
                publicRecoverySamples = 0;
            }
            publicRecoverySamples = Math.Min(publicRecoverySamples + 1, 2);
        }
        var addon = frame.Addons.FirstOrDefault(x => x.Name == "Emj");
        journalLower = journalLowerTracker.Observe(frame.Sequence, addon);
        journalLowerUtc = frame.Utc;
        journalTable = journalTableTracker.Observe(frame.Sequence, addon);
        if (journalPublicMonitor is { IsActive: false } && addon is { Present: true, Visible: true, Ready: true, Error: null })
            journalPublicMonitor.Start(PublicObservationAssembler.CreateAuditedContext(Identity, LowerHandUldHash));
        journalPublicMonitor?.Observe(frame with { Addons = frame.Addons.Select(x => x.Name == "Emj"
            ? x with { LowerHandReading = journalLower, PublicTableReading = journalTable } : x).ToArray() });
        (globalAiProjector ??= new()).Observe(journalPublicMonitor?.Current,
            addon is null ? null : addon with { LowerHandReading = journalLower, PublicTableReading = journalTable },
            journalPublicMonitor?.RiverProgress, journalPublicMonitor?.CallProgress,
            journalPublicMonitor?.RoundProgress, journalPublicMonitor?.OwnHandProgress,
            journalPublicMonitor?.TrackingEpochToken);
        RecordPublicMonitorLedger();
        TryRecoverCurrentTableCore();
        var observation = new { Lower = journalLower, Table = journalTable, Status = addon?.PublicStatusCandidates,
            Dora = addon?.PublicDoraCandidates, RoundTitle = addon?.RoundTitleResource,
            ActionMenu = addon?.PublicActionMenu, OpponentHands = addon?.PublicOpponentHands,
            HandInteraction = addon?.PublicHandInteraction, Riichi = addon?.PublicRiichiCandidates, Rules = addon?.PublicMatchRules,
            ProbeError = addon?.Error };
        string hash = PublicObservationFingerprint.Compute(JsonSerializer.SerializeToElement(observation));
        bool tableChanged = hash != tableJournalHash;
        tableJournalHash = hash;
        if (PlayRuntime?.RecoverySnapshot is { } currentSnapshot) RecordRuntimeCheckpoint(currentSnapshot);
        if (!tableChanged)
        {
            unchangedJournalTableSamples++;
            // Observe/project/recovery still run at 10 Hz above. A small liveness marker
            // replaces repeated full snapshots caused by polling and pulse animation.
            if (frame.Utc - lastJournalTableHeartbeat >= TimeSpan.FromSeconds(30))
            {
                RecordJournalEvent("public_table_heartbeat", new
                {
                    frame.Sequence, frame.Utc, SemanticSnapshotSha256 = hash,
                    UnchangedSamples = unchangedJournalTableSamples,
                    LowerCode = journalLower?.Code, TableCode = journalTable?.Code,
                    ProbeError = addon?.Error,
                });
                lastJournalTableHeartbeat = frame.Utc;
                unchangedJournalTableSamples = 0;
            }
            return;
        }
        lastJournalTableHeartbeat = frame.Utc;
        unchangedJournalTableSamples = 0;
        RecordVisibleRiverChanges(frame.Sequence);
        journal?.Diagnostic("public_table_observation", new
        {
            frame.Sequence, frame.Utc, Lower = journalLower, Table = journalTable,
            Status = addon?.PublicStatusCandidates, Dora = addon?.PublicDoraCandidates, RoundTitle = addon?.RoundTitleResource,
            ActionMenu = addon?.PublicActionMenu, OpponentHands = addon?.PublicOpponentHands,
            HandInteraction = addon?.PublicHandInteraction, Riichi = addon?.PublicRiichiCandidates, Rules = addon?.PublicMatchRules,
            ProbeError = addon?.Error,
            SnapshotSha256 = hash, SnapshotHashKind = "public-table-semantic-v2", FullHistoryVerified = false,
        });
        RecordJournalEvent("public_table_changed", new { frame.Sequence, frame.Utc, SnapshotSha256 = hash,
            LowerCode = journalLower?.Code, TableCode = journalTable?.Code,
            DiagnosticDetail = "bounded-export-ring", FullHistoryVerified = false });
        if (addon?.Error is { } probeError)
            journal?.Error("PUBLIC_TABLE_READ_ERROR", new { frame.Sequence, Location = probeError });
        if (journalTable is { Rejections.Length: > 0 })
            journal?.Error("PUBLIC_FACE_REJECTED", new { frame.Sequence, journalTable.Code, journalTable.Rejections });
        else if (addon?.Error is null) journal?.ErrorsRecovered();
    }

    private void RecordVisibleRiverChanges(long observationSequence)
    {
        if (journalTable is null || journalTable.Areas.IsDefault) return;
        foreach (var area in journalTable.Areas)
        {
            bool usable = area.ContainerVerified && area.ContainerVisible && area.EnumerationCompleted && area.UnknownComponents == 0 &&
                !journalTable.Rejections.Any(x => x.Area == area.Area);
            if (!usable) { journalRiverBaseline.Remove(area.Area); continue; }
            if (!area.Stable) continue; // Keep the last stable comparison baseline, never display it as current.
            var now = journalTable.Tiles.Where(x => x.Area == area.Area).ToArray();
            if (journalRiverBaseline.TryGetValue(area.Area, out var before))
            {
                var oldBag = before.Select(x => (x.Kind34, x.RedFive)).ToList();
                var added = new List<(int Kind34, bool RedFive)>();
                foreach (var tile in now.Select(x => (x.Kind34, x.RedFive)))
                    if (!oldBag.Remove(tile)) added.Add(tile);
                if (added.Count > 0 || oldBag.Count > 0)
                    RecordJournalEvent("river_visible_change", new
                    {
                        Sequence = observationSequence, area.Area, Source = "pinned-visible-images", Mapping = "Candidate",
                        Added = added.Select(x => new { x.Kind34, x.RedFive }).ToArray(),
                        Removed = oldBag.Select(x => new { x.Kind34, x.RedFive }).ToArray(),
                        TemporalOrderKnown = false, FullHistoryVerified = false,
                    });
            }
            else RecordJournalEvent("river_visible_baseline", new
            {
                Sequence = observationSequence, area.Area, VisibleTiles = now.Length, Mapping = "Candidate",
                TemporalOrderKnown = false, FullHistoryVerified = false,
            });
            journalRiverBaseline[area.Area] = now;
        }
    }

    internal void ExportGameLogs()
    {
        lock (gate)
        {
            if (disposed || export is not null) return;
            var current = journal;
            if (current is null) { ExportStatus = "尚无事件日志；选择手动、自动或只读监视后会自动开始。"; return; }
            ExportStatus = "正在导出事件日志与错误日志…";
            string file = Path.Combine(diagnosticsDirectory, $"mjcn-logs-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            export = current.ExportAsync(file);
        }
    }
}
