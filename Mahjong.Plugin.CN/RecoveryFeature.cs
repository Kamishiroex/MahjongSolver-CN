using System.Collections.Immutable;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private ImmutableArray<RecoveryCandidate> recoveryCandidates = [];
    private bool recoveryPending;
    private int recoveryRequest;
    private string? lastRecoveryCode;

    private void EnsurePlayRuntimeCore()
    {
        if (PlayRuntime is not null) return;
        PlayRuntime = new Mahjong.Plugin.Dalamud.Plugin(Interface, Framework, Log, GameGui, Lifecycle,
            () => readObservationAllowed && !disposed && Identity.Error is null && Client.IsLoggedIn,
            accessError: () => null,
            inputGate: () => SelectedSourceAccessValid && gameplayAllowed && (taskRun?.Plan is null || taskRun.AllowsGameplay),
            policyFactory: () => CreateReviewedPolicy(CreateDecisionPolicy()),
            identityError: () => disposed ? "PLUGIN_DISPOSED：插件正在卸载" :
                Identity.Error is { } error ? "IDENTITY_MISMATCH：" + error :
                !Client.IsLoggedIn ? "CLIENT_NOT_LOGGED_IN：客户端已退出登录或连接状态改变" :
                !readObservationAllowed ? "OBSERVATION_DISABLED：公开牌局读取已关闭" :
                "RUNTIME_GATE_REJECTED：运行条件在检查期间改变");
        PlayRuntime.Stopped += RecordGameplayStop;
        PlayRuntime.SnapshotObserved += RecordRuntimeSnapshot;
        PlayRuntime.ObservationInvalidated += RecordObservationGap;
        PlayRuntime.ActionSubmissionRecorded += RecordActionSubmission;
        PlayRuntime.ReviewRecorded += RecordReviewEvent;
        PlayRuntime.DecisionReviewIdProvider = ReviewDecisionFor;
    }

    internal void StartLogRecovery()
    {
        SuspendTableAutomation("正在恢复读取，自动排队及进桌开打已暂停。");
        gameplayAllowed = false;
        int request = Interlocked.Increment(ref modeRequestVersion);
        _ = Framework.RunOnFrameworkThread(() =>
        {
            lock (gate)
            {
                if (disposed || request != Volatile.Read(ref modeRequestVersion)) return;
                Identity = RuntimeIdentity.Read(Interface, Client);
                if (Identity.Error is not null) { StopCore(Identity.Error); return; }
                if (!Client.IsLoggedIn || logsDirectory is null) { RecoveryStatus = "请先登录并进入同一牌桌。"; return; }
                if (!CheckLowerHandResource()) return;
                aiProbe?.Stop("正在恢复桌面状态。");
                publicMonitor?.Stop("正在只读核对恢复状态。");
                readObservationAllowed = true;
                EnsureJournalCore("recovery");
                EnsurePlayRuntimeCore();
                PlayRuntime!.PauseAutomation("正在读取日志并核对当前公开桌面；操作关闭。");
                PlayRuntime.BeginRecoveryObservation();
                recoveryCandidates = [];
                recoveryPending = true;
                recoveryRequest = request;
                lastRecoveryCode = null;
                RecoveryStatus = "正在读取本机事件日志；随后自动核对当前公开桌面。";
                var identity = Identity;
                Task committed = journal?.Completion ?? Task.CompletedTask;
                _ = Task.Run(() => LoadRecoveryAsync(logsDirectory, identity, committed, request));
            }
        });
    }

    private async Task LoadRecoveryAsync(string root, RuntimeIdentity identity, Task committed, int request)
    {
        await committed.ConfigureAwait(false);
        RecoveryLoadResult loaded;
        try { loaded = await RecoveryJournal.LoadAsync(root, identity).ConfigureAwait(false); }
        catch (Exception ex) { loaded = new([], ["RECOVERY_LOAD_" + ex.GetType().Name]); }
        await Framework.RunOnFrameworkThread(() =>
        {
            lock (gate)
            {
                if (disposed || request != Volatile.Read(ref modeRequestVersion) || !recoveryPending) return;
                recoveryCandidates = loaded.Candidates;
                foreach (string error in loaded.Errors) journal?.Error(error, new { Stage = "load-recovery-log" });
                RecoveryStatus = recoveryCandidates.Length == 0
                    ? "没有可用的同版本恢复点。旧版诊断包不含新恢复日志；当前继续只读记牌。"
                    : $"已读回 {recoveryCandidates.Length} 个恢复点，等待手牌、四方牌河和本家副露稳定。";
                RecordJournalEvent("recovery_log_loaded", new { Candidates = recoveryCandidates.Length, loaded.Errors, AutomaticEnabled = false });
            }
        }).ConfigureAwait(false);
    }

    private void TryRecoverCurrentTableCore()
    {
        if (!recoveryPending || recoveryCandidates.IsDefaultOrEmpty || recoveryRequest != Volatile.Read(ref modeRequestVersion) ||
            PlayRuntime is not { IsObservingPaused: true, RecoverySnapshot: { } snapshot } runtime) return;
        if (publicRecoverySamples < 2 || publicRecoverySession != runtime.ObservationSessionId ||
            publicRecoveryRuntimeSequence != runtime.ObservationSequence) return;
        string code = "RECOVERY_AWAITING_EVIDENCE";
        foreach (var saved in recoveryCandidates)
        {
            if (!RecoveryEvidenceBuilder.TryBuild(saved, snapshot, runtime.MeldTracker.MeldAkadora, journalLower, journalTable,
                    runtime.ObservationSessionId, runtime.ObservationSequence, out var result, out var anchor, out code)) continue;
            // The restore itself rechecks the current session, sequence and runtime snapshot atomically.
            recoveryPending = false;
            if (!runtime.TryRestoreExperimentalMelds(result, anchor, out code)) { recoveryPending = true; continue; }
            RecoveryStatus = "已用日志和当前公开桌面恢复原实验策略的副露库存。请选择手动或自动继续；遗漏历史仍标记为缺失。";
            RecordJournalEvent("recovery_applied", new
            {
                SourceLog = saved.LogName, saved.Utc, saved.IncompleteTail, Code = code,
                runtime.ObservationSessionId, runtime.ObservationSequence, result.Evidence, result.Issues,
                Mapping = "LegacyAssumption", FullHistoryVerified = false, AutomaticEnabled = false,
            });
            return;
        }
        RecoveryStatus = "暂不能恢复：" + code + "。当前继续只读核对；不会套用不一致的旧副露。";
        if (lastRecoveryCode == code) return;
        lastRecoveryCode = code;
        RecordJournalEvent("recovery_waiting", new { Code = code, AutomaticEnabled = false });
    }

    private void RecordObservationGap(string reason)
    {
        lock (gate)
        {
            runtimeJournalHash = tableJournalHash = journalCheckpointHash = null;
            journalLowerTracker?.Clear(); journalTableTracker?.Clear();
            globalAiProjector?.Clear();
            runtimeEventDiffer?.Reset();
            journalRiverBaseline?.Clear();
            journalLower = null; journalTable = null;
            publicRecoverySamples = 0;
            RecordJournalEvent("history_gap", new { Reason = reason, FullHistoryVerified = false });
            // A new round or leaving the table invalidates an in-progress old-round recovery.
            if (reason.Contains("NEW_HAND", StringComparison.Ordinal) || reason.Contains("SCENE", StringComparison.Ordinal))
            { recoveryPending = false; recoveryCandidates = []; RecoveryStatus = "牌桌或牌局边界已变化，旧恢复请求已取消。"; }
        }
    }
}
