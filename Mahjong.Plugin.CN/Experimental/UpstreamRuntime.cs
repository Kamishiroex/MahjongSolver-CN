using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.Composition;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Logging;
using Mahjong.Plugin.Game;
using Mahjong.Plugin.Game.Variants;
using Mahjong.Policy.Abstractions;
using Mahjong.Policy.Efficiency;
using Mahjong.Rules.Rulesets;
using Mahjong.Core;
using Mahjong.Cn.Events;

namespace Mahjong.Plugin.Dalamud;

internal enum PlayMode { Off, Manual, Automatic }

internal sealed record GameplayStopInfo(DateTimeOffset Utc, string Reason, PlayMode PreviousMode,
    string? Layout, StateSnapshot? Snapshot, ActionChoice? Choice, string? Error, string? LastAction,
    CallMenuObservation? Menu, InputDispatcher.CallDispatchObservation? Dispatch);

/// <summary>An input attempt, explicitly not evidence that the game performed it.</summary>
internal sealed record GameplayActionSubmission(DateTimeOffset Utc, Guid ObservationSessionId, long ObservationSequence,
    string Label, string Result, int? Option, int? TileId, int? Slot, int? AddonStateCode, string Route)
{
    public string Source => "InputDispatcher/submission";
    public bool ActionConfirmed => false;
    public Guid SubmissionId { get; init; }
    public Guid? DecisionId { get; init; }
}

/// <summary>
/// Minimal runtime facade for the explicitly selected upstream experimental play modes.
/// This is NOT a Dalamud entry point and never constructs the upstream plugin/DI container,
/// telemetry, hooks, input loggers, raw-memory recorders or game-log services.
/// Construct and enable modes on the framework thread, as required by AddonEmjReader.
/// Stop and Dispose may be requested from any thread; native teardown is queued to the framework.
/// </summary>
internal sealed class Plugin : IDisposable
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly IGameGui gameGui;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly Func<bool> identityGate;
    private readonly Func<string?>? identityError;
    private readonly Func<string?>? accessError;
    private readonly Func<bool>? inputGate;
    private readonly Func<IPolicy>? policyFactory;
    private AddonEmjReader? reader;
    private StateAggregator? aggregator;
    private InputDispatcher? dispatcher;
    private AutoPlayLoop? autoPlay;
    private IPolicy? policy;
    private bool identityAllowed;
    private bool stopping;
    private bool pausedObservation;
    private bool observationReadFailed;
    private int lastObservationHash;
    private bool hasObservationHash;
    private volatile bool operationBlocked = true;
    private volatile bool disposed;
    private long modeGeneration;
    private readonly object stopGate = new();
    private StopIntent? pendingStopIntent;
    private Task stopCompletion = Task.CompletedTask;
    private sealed record StopIntent(DateTimeOffset Utc, string Reason, PlayMode PreviousMode);

    public IConfigService<Configuration> ConfigService { get; }
    public Configuration Configuration => ConfigService.Current;
    internal bool AutoAdvancePreference { get; set; } = true;
    internal bool KeepAutomaticBetweenHandsPreference { get; set; }
    public MeldTracker MeldTracker { get; } = new();
    public IPolicy Policy => policy ?? throw new InvalidOperationException("实验模式尚未启用。");
    public AddonEmjReader AddonReader => reader ?? throw new InvalidOperationException("实验读取尚未启用。");
    public StateAggregator Aggregator => aggregator ?? throw new InvalidOperationException("实验提示尚未启用。");
    internal StateAggregator? ActiveAggregator => aggregator;
    internal StateSnapshot? LastStoppedSnapshot { get; private set; }
    internal ActionChoice? LastStoppedChoice { get; private set; }
    internal string? LastStoppedError { get; private set; }
    internal string? LastStoppedAction { get; private set; }
    internal CallMenuObservation? LastStoppedMenu { get; private set; }
    internal InputDispatcher.CallDispatchObservation? LastStoppedDispatch { get; private set; }
    internal CallMenuObservation? ActiveCallMenu => reader?.LastCallMenu;
    internal InputDispatcher.CallDispatchObservation? ActiveCallDispatch => dispatcher?.LastCallDispatch;
    internal string? ActiveDiscardPath => dispatcher?.LastDiscardPath;
    public InputDispatcher Dispatcher => dispatcher ?? throw new InvalidOperationException("实验操作尚未启用。");
    public AutoPlayLoop? AutoPlay => autoPlay;
    public IFindingsLog? FindingsLog => null;
    public PlayMode Mode { get; private set; } = PlayMode.Off;
    public bool HasObservedTable { get; private set; }

    internal unsafe bool TryConfirmKyushu(nint expectedOwner)
    {
        if (!CanOperate || expectedOwner == 0 || gameGui.GetAddonByName("Emj").Address != expectedOwner) return false;
        var dialog = (FFXIVClientStructs.FFXIV.Client.UI.AddonSelectYesno*)gameGui.GetAddonByName("SelectYesno").Address;
        var click = Mahjong.Plugin.CN.Automation.CnKyushuConfirmation.Find(
            (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)expectedOwner, dialog);
        if (click == null || !CanOperate) return false;
        var copy = *click; copy.NextEvent = null;
        FFXIVClientStructs.FFXIV.Component.GUI.AtkEventData data = default;
        copy.Listener->ReceiveEvent(FFXIVClientStructs.FFXIV.Component.GUI.AtkEventType.ButtonClick,
            (int)copy.Param, &copy, &data);
        RecordActionSubmission("kyushu-confirm", InputDispatcher.DispatchResult.Submitted,
            route: "registered-owned-confirm");
        return true;
    }
    public string Status { get; private set; } = "已停止；请选择手动提醒或自动打牌。";
    internal event Action<GameplayStopInfo>? Stopped;
    /// <summary>Framework-thread public managed values only; no suggestions or queued actions.</summary>
    internal event Action<StateSnapshot>? SnapshotObserved;
    internal event Action<string>? ObservationInvalidated;
    internal event Action<GameplayActionSubmission>? ActionSubmissionRecorded;
    internal event Action<string,object>? ReviewRecorded;
    internal Func<StateSnapshot,Guid?>? DecisionReviewIdProvider;
    internal Guid? ReviewDecisionId(StateSnapshot state)
    { try { return DecisionReviewIdProvider?.Invoke(state); } catch { return null; } }
    internal void RecordReview(string kind,object value)
    { try { ReviewRecorded?.Invoke(kind,value); } catch { /* Observation must not alter input. */ } }
    internal Guid ObservationSessionId { get; private set; } = Guid.NewGuid();
    internal long ObservationSequence { get; private set; }
    internal StateSnapshot? RecoverySnapshot { get; private set; }
    internal bool IsObservingPaused => pausedObservation && reader is not null && !disposed;
    internal MeldTrackerCheckpoint? ExportMeldCheckpoint() => MeldTracker.ExportCheckpoint();

    internal Guid RecordActionSubmission(string label, InputDispatcher.DispatchResult result, int? option = null,
        Tile? tile = null, int? slot = null, int? state = null, string? route = null, Guid? decisionId = null)
    {
        // A native call may synchronously tear down this runtime before returning.
        // Preserve its managed submission result even then; this method never reads
        // the addon or arms input, and listeners may safely ignore a closed journal.
        static string Bounded(string? value, int limit) => string.IsNullOrEmpty(value) ? "(unknown)" : value.Length <= limit ? value : value[..limit];
        var entry = new GameplayActionSubmission(DateTimeOffset.UtcNow, ObservationSessionId, ObservationSequence,
            Bounded(label, 120), result.ToString(), option, tile is { Id: < 34 } knownTile ? knownTile.Id : null,
            slot, state, Bounded(route, 160)) { SubmissionId=Guid.NewGuid(),DecisionId=decisionId };
        try { ActionSubmissionRecorded?.Invoke(entry); }
        catch (Exception ex)
        {
            try { log.Error($"[MahjongCN] Submission journal listener failed: {ex.GetType().Name}"); }
            catch { /* Host logging may already be disposed too. */ }
        }
        return entry.SubmissionId;
    }

    /// <summary>Applies reconciled CURRENT public state only; never resumes a mode.</summary>
    internal bool TryRestoreOwnMelds(ReconciliationResult result, RecoveryTableObservation current, out string code)
    {
        code = "RECOVERY_AWAITING_TABLE_EVIDENCE";
        if (result is null || current is null || disposed || !framework.IsInFrameworkUpdateThread || Mode != PlayMode.Off || !pausedObservation ||
            !RefreshIdentityGate() || reader is null || RecoverySnapshot is not { } snapshot) return false;
        if (result.ObservationSessionId != ObservationSessionId || current.ObservationSessionId != ObservationSessionId ||
            result.ObservationSequence != ObservationSequence || current.Sequence != ObservationSequence)
        { code = "RECOVERY_STALE_OBSERVATION"; return false; }
        if (!result.CanRestoreOwnMelds || result.RestoredOwnMelds.IsDefault || !current.Stable || current.OwnHand?.IsConfirmed != true)
            return false;
        if (result.RestoredOwnMelds.Any(meld => meld is null || meld.Kind != MeldKind.AnKan) &&
            result.MeldSeatBasis != RecoverySeatBasis.RelativeToLocalPlayer)
        { code = "RECOVERY_MELD_SEAT_BASIS_UNKNOWN"; return false; }
        int closedReds = snapshot.AkaDora - MeldTracker.MeldAkadora;
        if (current.OwnHand.Value.IsDefault ||
            !current.OwnHand.Value.Select(tile => tile.Id).Order().SequenceEqual(snapshot.Hand.Select(tile => (int)tile.Id).Order()) ||
            current.OwnHand.Value.Count(tile => tile.Red) != closedReds)
        { code = "RECOVERY_CURRENT_HAND_MISMATCH"; return false; }
        var restored = new List<Meld>();
        int meldReds = 0;
        foreach (var meld in result.RestoredOwnMelds)
        {
            if (meld is null || meld.Tiles.IsDefault || meld.Tiles.Length is < 3 or > 4 ||
                meld.Tiles.Any(tile => tile.Id is < 0 or > 33 || (tile.Red && tile.Id is not (4 or 13 or 22))) ||
                (meld.ClaimedTile is { } claimedValue && claimedValue.Id is < 0 or > 33) ||
                (meld.Kind == MeldKind.AnKan && (meld.FromSeat is not null || meld.ClaimedTile is not null)))
            { code = "RECOVERY_INVALID_MELD"; return false; }
            if (meld.Kind != MeldKind.AnKan && (meld.FromSeat is not (>= 1 and <= 3) || meld.ClaimedTile is null))
            { code = "RECOVERY_MELD_SOURCE_UNKNOWN"; return false; }
            restored.Add(new(meld.Kind, meld.Tiles.Select(tile => Tile.FromId(tile.Id)).Order().ToArray(),
                meld.ClaimedTile is { } claimed ? Tile.FromId(claimed.Id) : null,
                meld.Kind == MeldKind.AnKan ? -1 : meld.FromSeat!.Value));
            meldReds += meld.Tiles.Count(tile => tile.Red);
        }
        if (!MeldTracker.TryRebaseFromReconciledState(restored, meldReds, snapshot.Hand,
                snapshot.Seats.Select(seat => seat.DiscardCount).ToArray(), closedReds, snapshot.WallRemaining, out code))
            return false;
        PublishSnapshot(snapshot with { OurMelds = restored, AkaDora = closedReds + meldReds });
        Status = "RECOVERY_CURRENT_STATE_REBASED：当前副露已核对，仍处于暂停；请选择手动或自动模式。遗漏事件历史仍不完整。";
        return true;
    }

    /// <summary>
    /// Restores the old experimental policy's inventory after an explicit complete
    /// visible-group match. This never changes public-field quality or AI readiness.
    /// </summary>
    internal bool TryRestoreExperimentalMelds(ReconciliationResult result, ExperimentalRecoveryAnchor current, out string code)
    {
        code = "RECOVERY_AWAITING_TABLE_EVIDENCE";
        if (result is null || current is null || disposed || !framework.IsInFrameworkUpdateThread || Mode != PlayMode.Off || !pausedObservation ||
            !RefreshIdentityGate() || reader is null || RecoverySnapshot is not { } snapshot || snapshot.AddonStateCode == 29)
            return false;
        if (result.ObservationSessionId != ObservationSessionId || result.ObservationSequence != ObservationSequence)
        { code = "RECOVERY_STALE_OBSERVATION"; return false; }
        if (!result.CanRestoreExperimentalMelds || result.ExperimentalMelds.IsDefault || result.ExperimentalMelds.Length > 4 ||
            result.MeldSeatBasis != RecoverySeatBasis.RelativeToLocalPlayer) return false;
        int closedReds = snapshot.AkaDora - MeldTracker.MeldAkadora;
        if (current.OwnHand.IsDefault || current.Scores.IsDefault || current.RiverCounts.IsDefault || current.DoraDisplay.IsDefault ||
            !current.OwnHand.Select(tile => tile.Id).Order().SequenceEqual(snapshot.Hand.Select(tile => (int)tile.Id).Order()) ||
            current.OwnHand.Count(tile => tile.Red) != closedReds || !current.Scores.SequenceEqual(snapshot.Scores) ||
            !current.RiverCounts.SequenceEqual(snapshot.Seats.Select(seat => seat.DiscardCount)) ||
            !current.DoraDisplay.Select(tile => tile.Id).SequenceEqual(snapshot.DoraIndicators.Select(tile => (int)tile.Id)))
        { code = "RECOVERY_CURRENT_ANCHOR_MISMATCH"; return false; }
        var restored = new List<Meld>();
        int meldReds = 0;
        foreach (var meld in result.ExperimentalMelds)
        {
            if (meld is null || meld.TileIds.IsDefault || meld.TileIds.Length is < 3 or > 4 ||
                meld.TileIds.Any(id => id is < 0 or > 33) || meld.RedTileCount is < 0 or > 1 ||
                meld.ClaimedTileId is < 0 or > 33 ||
                (meld.Kind == MeldKind.AnKan && (meld.FromRelativePlayer is not null || meld.ClaimedTileId is not null)) ||
                (meld.Kind != MeldKind.AnKan && (meld.FromRelativePlayer is not (>= 1 and <= 3) || meld.ClaimedTileId is null)))
            { code = "RECOVERY_INVALID_MELD"; return false; }
            restored.Add(new(meld.Kind, meld.TileIds.Select(Tile.FromId).Order().ToArray(),
                meld.ClaimedTileId is { } claimed ? Tile.FromId(claimed) : null,
                meld.Kind == MeldKind.AnKan ? -1 : meld.FromRelativePlayer!.Value));
            meldReds += meld.RedTileCount;
        }
        if (!MeldTracker.TryRebaseFromReconciledState(restored, meldReds, snapshot.Hand,
                snapshot.Seats.Select(seat => seat.DiscardCount).ToArray(), closedReds, snapshot.WallRemaining, out code))
            return false;
        PublishSnapshot(snapshot with { OurMelds = restored, AkaDora = closedReds + meldReds });
        code = "RECOVERY_EXPERIMENTAL_MATCH";
        Status = "RECOVERY_EXPERIMENTAL_MATCH：可见副露与日志完成实验性核对，仍暂停；可重新选择原实验策略。完整 AI 历史仍不可用。";
        return true;
    }
    // Completes after the most recently requested cleanup has run (or scheduling failed).
    // Await only asynchronously: queued teardown needs the framework thread to remain free.
    internal Task StopCompletion { get { lock (stopGate) return stopCompletion; } }

    /// <summary>Dispatch must check this immediately before every game input.</summary>
    public bool CanOperate => !disposed && !operationBlocked && !stopping && Mode == PlayMode.Automatic &&
        identityAllowed && RefreshIdentityGate() && InputGateAllows() && CheckReadErrors();

    public Plugin(IDalamudPluginInterface pluginInterface, IFramework framework, IPluginLog log,
        IGameGui gameGui, IAddonLifecycle addonLifecycle, Func<bool> identityGate,
        Func<string?>? accessError = null, Func<bool>? inputGate = null, Func<IPolicy>? policyFactory = null,
        Func<string?>? identityError = null)
    {
        ArgumentNullException.ThrowIfNull(pluginInterface);
        ArgumentNullException.ThrowIfNull(framework);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(gameGui);
        ArgumentNullException.ThrowIfNull(addonLifecycle);
        ArgumentNullException.ThrowIfNull(identityGate);
        this.pluginInterface = pluginInterface;
        this.framework = framework;
        this.log = log;
        this.gameGui = gameGui;
        this.addonLifecycle = addonLifecycle;
        this.identityGate = identityGate;
        this.identityError = identityError;
        this.accessError = accessError;
        this.inputGate = inputGate;
        this.policyFactory = policyFactory;

        Configuration loaded;
        try { loaded = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration(); }
        catch (Exception ex)
        {
            loaded = new Configuration();
            log.Warning($"[MahjongCN] Config load failed: {ex.GetType().Name}; using disabled defaults.");
        }
        // Disk copies never retain an armed mode, even if the host terminates without Dispose.
        // Preserve user tuning preferences; these session flags always start disabled.
        ConfigService = new DalamudConfigService(config => pluginInterface.SavePluginConfig(DisabledCopy(config)),
            DisabledCopy(loaded));
        try { ConfigService.Update(config => config); }
        catch (Exception ex)
        {
            Status = "已停止；配置暂时无法保存：" + ex.GetType().Name;
            log.Warning($"[MahjongCN] Disabled config save failed: {ex.GetType().Name}.");
        }
        // Register before any aggregator/loop so identity loss stops them before this frame's work.
        framework.Update += OnFrameworkUpdate;
        RefreshIdentityGate();
    }

    public void SetMode(PlayMode mode)
    {
        if (disposed) return;
        if (mode == PlayMode.Off)
        {
            PauseAutomation("已停止提醒与自动打牌；继续只读记牌，可重新选择模式。");
            return;
        }
        if (mode is not (PlayMode.Manual or PlayMode.Automatic))
        {
            StopAutomation("未知模式，已停止。");
            return;
        }
        if (!RefreshIdentityGate()) return;
        if (Mode == mode) return;
        if (Mode is PlayMode.Manual or PlayMode.Automatic)
        {
            SwitchActiveMode(mode);
            return;
        }
        if (pausedObservation && reader is not null)
        {
            ResumeObservedMode(mode);
            return;
        }

        StopAutomation("正在切换模式，旧排队操作已取消。");
        if (!RefreshIdentityGate()) return;
        try
        {
            policy = policyFactory?.Invoke() ?? new EfficiencyPolicy(new DomanRuleSet());
            var addon = new MahjongAddon(gameGui, log);
            EnsureReadChain();
            aggregator = new StateAggregator(reader!, framework, Policy);
            aggregator.Changed += PublishSnapshot;
            ConfigureMode(mode);
            if (mode == PlayMode.Automatic)
                autoPlay = new AutoPlayLoop(this, framework, log, addon);
            Interlocked.Increment(ref modeGeneration);
            LastStoppedSnapshot = null;
            LastStoppedChoice = null;
            LastStoppedError = null;
            LastStoppedAction = null;
            LastStoppedMenu = null;
            LastStoppedDispatch = null;
            Mode = mode;
            operationBlocked = false;
            Status = ActiveStatus(mode);
            ObserveTable(AddonReader.Poll());
        }
        catch (Exception ex)
        {
            log.Error($"[MahjongCN] Experimental mode initialization failed: {ex.GetType().Name}: {ex.Message}");
            StopAutomation("实验模式初始化失败：" + ex.GetType().Name + "；已停止。");
        }
    }

    private void EnsureReadChain()
    {
        if (reader is not null) return;
        var assemblyDirectory = pluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
            throw new InvalidOperationException("无法确定实际插件安装目录。");
        string layoutsDirectory = Path.Combine(assemblyDirectory, "layouts");
        if (JsonLayoutProfileLoader.LoadAll(layoutsDirectory).Count == 0)
            throw new InvalidDataException("安装目录缺少可用 layouts 配置。");
        var addon = new MahjongAddon(gameGui, log);
        reader = new AddonEmjReader(addonLifecycle, log, addon, MeldTracker,
            pluginInterface.GetPluginConfigDirectory(), layoutsDirectory, findings: null);
        reader.ObservationChanged += OnObservationChanged;
        dispatcher = new InputDispatcher(addon, () => AddonReader.ActiveLayout, () => CanOperate);
    }

    /// <summary>Starts only a reader for reload reconciliation. Never arms policy or input.</summary>
    internal void BeginRecoveryObservation()
    {
        if (disposed || !RefreshIdentityGate()) return;
        if (!framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("RECOVERY_REQUIRES_FRAMEWORK_THREAD");
        if (Mode != PlayMode.Off) PauseAutomation("已暂停；继续只读核对当前牌桌。");
        try
        {
            EnsureReadChain();
            pausedObservation = true;
            operationBlocked = true;
            Status = "RECOVERY_AWAITING_TABLE_EVIDENCE：只读观察当前牌桌，未恢复建议或操作。";
            ObserveTable(reader!.Poll());
        }
        catch (Exception ex)
        {
            StopAutomation("RECOVERY_OBSERVATION_FAILED：" + ex.GetType().Name);
        }
    }

    private void ResumeObservedMode(PlayMode mode)
    {
        long generation;
        lock (stopGate)
        {
            if (disposed || !pausedObservation || reader is null) return;
            operationBlocked = true;
            generation = Interlocked.Increment(ref modeGeneration);
        }
        try
        {
            // A fresh aggregator starts with no choice or scores. The retained reader
            // supplies a new observation before any new loop can produce game input.
            policy = policyFactory?.Invoke() ?? new EfficiencyPolicy(new DomanRuleSet());
            aggregator = new StateAggregator(reader!, framework, policy);
            aggregator.Changed += PublishSnapshot;
            ConfigureMode(mode);
            if (disposed || Interlocked.Read(ref modeGeneration) != generation || !RefreshIdentityGate())
            {
                RestoreDisabledConfigurationAfterInterruptedSwitch();
                return;
            }
            lock (stopGate)
            {
                if (disposed || modeGeneration != generation || !pausedObservation) return;
                pausedObservation = false;
                if (mode == PlayMode.Automatic)
                    autoPlay = new AutoPlayLoop(this, framework, log, new MahjongAddon(gameGui, log));
                Mode = mode;
                operationBlocked = false;
                Status = ActiveStatus(mode);
            }
            LastStoppedChoice = null;
            LastStoppedError = null;
            LastStoppedAction = null;
            LastStoppedDispatch = null;
        }
        catch (Exception ex)
        {
            PauseAutomation("RECOVERY_RESUME_FAILED：" + ex.GetType().Name + "；继续只读记牌。");
            RestoreDisabledConfigurationAfterInterruptedSwitch();
        }
    }

    /// <summary>Stops advice/input immediately, retaining only continuous read history.</summary>
    internal void PauseAutomation(string reason)
    {
        if (disposed) return;
        if (reader is null) { StopAutomation(reason); return; }
        long generation;
        TaskCompletionSource completion;
        lock (stopGate)
        {
            var previousMode = Mode;
            operationBlocked = true;
            Mode = PlayMode.Off;
            if (previousMode != PlayMode.Off)
                pendingStopIntent ??= new(DateTimeOffset.UtcNow, reason, previousMode);
            Status = pendingStopIntent?.Reason ?? reason;
            generation = Interlocked.Increment(ref modeGeneration);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopCompletion = completion.Task;
        }
        autoPlay?.CancelPendingActions();
        void Cleanup()
        {
            try
            {
                if (!disposed && Interlocked.Read(ref modeGeneration) == generation)
                    CleanupStoppedModules(reason, preserveObservation: true);
            }
            catch (Exception ex) { ReportStopCleanupFailure(ex); }
            finally { completion.TrySetResult(); }
        }
        try
        {
            if (framework.IsInFrameworkUpdateThread) Cleanup();
            else _ = ObserveScheduledStopAsync(framework.RunOnFrameworkThread(Cleanup), completion);
        }
        catch (Exception ex) { ReportStopCleanupFailure(ex); completion.TrySetResult(); }
    }

    private void SwitchActiveMode(PlayMode mode)
    {
        // Keep the continuously observed table and its inferred history. A normal Stop,
        // error, scene exit or access failure still tears down this entire read session.
        long switchGeneration;
        lock (stopGate)
        {
            if (disposed || Mode == PlayMode.Off) return;
            operationBlocked = true;
            switchGeneration = Interlocked.Increment(ref modeGeneration);
        }
        autoPlay?.CancelPendingActions();
        try
        {
            if (reader is null || aggregator is null || dispatcher is null || policy is null)
                throw new InvalidOperationException("活动模式的读取链路不完整。");
            autoPlay?.Dispose();
            autoPlay = null;
            ConfigureMode(mode);
            if (!IsCurrentActiveSwitch(switchGeneration))
            {
                RestoreDisabledConfigurationAfterInterruptedSwitch();
                return;
            }
            // Configuration persistence can take time; expiry/stop must still win before
            // publishing the new mode. The native dispatcher checks the gate again later.
            if (!RefreshIdentityGate())
            {
                RestoreDisabledConfigurationAfterInterruptedSwitch();
                return;
            }
            lock (stopGate)
            {
                // StopAutomation uses the same lock to publish Off and advance the
                // generation. No persistence or external gate callback runs under it.
                if (!disposed && Mode != PlayMode.Off && modeGeneration == switchGeneration)
                {
                    if (mode == PlayMode.Automatic)
                        autoPlay = new AutoPlayLoop(this, framework, log, new MahjongAddon(gameGui, log));
                    Mode = mode;
                    operationBlocked = false;
                    Status = HasObservedTable ? ActiveStatus(mode) : mode == PlayMode.Automatic
                        ? "自动打牌待命：等待进入牌桌；立即停止可取消待命。"
                        : "手动提醒待命：等待进入牌桌。";
                    return;
                }
            }
            RestoreDisabledConfigurationAfterInterruptedSwitch();
        }
        catch (Exception ex)
        {
            // The old loop was already canceled. Failure must never leave a half-switched
            // mode armed. A stop which already won keeps its original reason.
            StopAutomation("模式切换失败：" + ex.GetType().Name + "；已停止。", switchGeneration);
            RestoreDisabledConfigurationAfterInterruptedSwitch();
            try { log.Error($"[MahjongCN] Mode switch failed: {ex.GetType().Name}."); }
            catch { /* Host logging failure cannot reenable a stopped mode. */ }
        }
    }

    private bool IsCurrentActiveSwitch(long generation)
    {
        lock (stopGate) return !disposed && Mode != PlayMode.Off && modeGeneration == generation;
    }

    private void RestoreDisabledConfigurationAfterInterruptedSwitch()
    {
        lock (stopGate)
        {
            if (!disposed && Mode != PlayMode.Off) return;
        }
        // A synchronous save callback may stop and disable the runtime, after which
        // the outer ConfigService.Update still publishes its stale armed record.
        // Repair that record after the outer update returns, without replacing the
        // winning stop reason or holding stopGate during filesystem I/O.
        try
        {
            if (Configuration != DisabledCopy(Configuration)) ConfigService.Update(DisabledCopy);
        }
        catch (Exception ex)
        {
            try { log.Warning($"[MahjongCN] Interrupted switch config save failed: {ex.GetType().Name}."); }
            catch { }
        }
    }

    private void ConfigureMode(PlayMode mode) => ConfigService.Update(config => config with
    {
        AutomationArmed = mode == PlayMode.Automatic,
        SuggestionOnly = mode != PlayMode.Automatic,
        TosAccepted = mode == PlayMode.Automatic,
        AutoPlayConfirmed = mode == PlayMode.Automatic,
        AutoAdvanceAfterHand = mode == PlayMode.Automatic && AutoAdvancePreference,
        KeepAutomaticBetweenHands = KeepAutomaticBetweenHandsPreference,
        EnableGameLogging = false,
        DevMode = false,
    });

    private bool InputGateAllows()
    {
        try { return inputGate?.Invoke() ?? true; }
        catch { return false; }
    }

    public bool RefreshIdentityGate()
    {
        if (disposed) return false;
        // Re-evaluate the lease on every mode transition, framework tick and native-input
        // gate. A successful unlock is never cached here and never resumes a stopped mode.
        string? accessFailure;
        try { accessFailure = accessError?.Invoke(); }
        catch (Exception ex) { accessFailure = "TEST_ACCESS_CHECK_FAILED：" + ex.GetType().Name + "；已停止。"; }
        if (accessFailure is not null)
        {
            identityAllowed = false;
            StopAutomation(accessFailure);
            return false;
        }
        string? identityFailure = null;
        try { identityAllowed = identityGate(); }
        catch (Exception ex)
        {
            identityAllowed = false;
            identityFailure = "RUNTIME_GATE_CHECK_FAILED：" + ex.GetType().Name;
            log.Warning($"[MahjongCN] Identity check failed: {ex.GetType().Name}.");
        }
        if (!identityAllowed)
        {
            try { identityFailure ??= identityError?.Invoke(); }
            catch (Exception ex) { identityFailure = "RUNTIME_GATE_DIAGNOSTIC_FAILED：" + ex.GetType().Name; }
            StopAutomation((identityFailure ?? "RUNTIME_GATE_REJECTED：版本、登录或运行条件不符合") +
                "；已停止，条件恢复后须重新选择模式。");
        }
        return identityAllowed;
    }

    private bool CheckReadErrors()
    {
        if (aggregator?.LastScorerError is not { } error) return true;
        if (error.StartsWith("READ_FAILED", StringComparison.Ordinal) || error.StartsWith("SCHEMA_MISMATCH", StringComparison.Ordinal))
            InvalidateObservation("RECOVERY_READ_INTERRUPTED");
        PauseAutomation("READ_OR_POLICY_ERROR：" + error + "；已暂停输入并保留只读记牌，可核对桌面后重试。");
        return false;
    }

    public void StopAutomation(string reason) => StopAutomation(reason, null);

    private void StopAutomation(string reason, long? expectedGeneration)
    {
        long generation;
        TaskCompletionSource completion;
        lock (stopGate)
        {
            if (expectedGeneration is { } expected &&
                (disposed || Mode == PlayMode.Off || modeGeneration != expected)) return;
            var previousMode = Mode;
            // Publish Off before any cancellation, configuration I/O or disposal can fail.
            operationBlocked = true;
            Mode = PlayMode.Off;
            if (previousMode != PlayMode.Off)
                pendingStopIntent ??= new(DateTimeOffset.UtcNow, reason, previousMode);
            // Repeated Off/Dispose requests may supersede a queued cleanup, but must not
            // erase the first active mode's reason or suppress its one stop report.
            Status = pendingStopIntent?.Reason ?? reason;
            generation = Interlocked.Increment(ref modeGeneration);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stopCompletion = completion.Task;
        }
        // The queue generation is thread safe. Stateful loop teardown stays on the framework.
        autoPlay?.CancelPendingActions();
        void Cleanup()
        {
            try
            {
                if (Interlocked.Read(ref modeGeneration) == generation) CleanupStoppedModules(reason);
            }
            catch (Exception ex) { ReportStopCleanupFailure(ex); }
            finally { completion.TrySetResult(); }
        }
        try
        {
            if (framework.IsInFrameworkUpdateThread) Cleanup();
            else _ = ObserveScheduledStopAsync(framework.RunOnFrameworkThread(Cleanup), completion);
        }
        catch (Exception ex)
        {
            ReportStopCleanupFailure(ex);
            completion.TrySetResult();
        }
    }

    private async Task ObserveScheduledStopAsync(Task scheduled, TaskCompletionSource completion)
    {
        try { await scheduled.ConfigureAwait(false); }
        catch (Exception ex) { ReportStopCleanupFailure(ex); }
        finally { completion.TrySetResult(); }
    }

    private void ReportStopCleanupFailure(Exception exception)
    {
        // Input is already blocked. Host teardown may also have closed the logger.
        Status += " STOP_CLEANUP_FAILED: " + exception.GetType().Name;
        try { log.Warning($"[MahjongCN] Stop cleanup failed: {exception.GetType().Name}."); }
        catch { /* Never turn a stop request into a new escaping exception. */ }
    }

    private void CleanupStoppedModules(string reason, bool preserveObservation = false)
    {
        if (stopping) return;
        stopping = true;
        StopIntent? intent;
        lock (stopGate)
        {
            intent = pendingStopIntent;
            pendingStopIntent = null;
        }
        reason = intent?.Reason ?? reason;
        try
        {
            if (aggregator is not null)
            {
                // Retain only public managed values for explicit error export, never for play.
                LastStoppedSnapshot = aggregator.Latest;
                LastStoppedChoice = aggregator.LastChoice;
                LastStoppedError = aggregator.LastScorerError;
                LastStoppedAction = autoPlay?.LastActionDescription;
            }
            if (reader is not null) LastStoppedMenu = reader.LastCallMenu;
            if (dispatcher is not null) LastStoppedDispatch = dispatcher.LastCallDispatch;
            if (intent is not null)
            {
                // Immutable managed copies are handed off before teardown. Observers never receive pointers.
                var stop = new GameplayStopInfo(intent.Utc, reason, intent.PreviousMode,
                    reader?.ActiveLayout?.Name, LastStoppedSnapshot, LastStoppedChoice, LastStoppedError, LastStoppedAction,
                    LastStoppedMenu, LastStoppedDispatch);
                TryCleanup(() => Stopped?.Invoke(stop), "record stop reason");
            }
            TryCleanup(() => autoPlay?.Stop(), "cancel pending actions");
            TryCleanup(() => autoPlay?.Dispose(), "dispose autoplay");
            autoPlay = null;
            if (aggregator is not null) aggregator.Changed -= PublishSnapshot;
            TryCleanup(() => aggregator?.Dispose(), "dispose aggregator");
            aggregator = null;
            TryCleanup(() => (policy as IDisposable)?.Dispose(), "dispose decision policy");
            policy = null;
            pausedObservation = preserveObservation && reader is not null && !disposed;
            if (!pausedObservation)
            {
                if (reader is not null) reader.ObservationChanged -= OnObservationChanged;
                TryCleanup(() => reader?.Dispose(), "dispose reader");
                reader = null;
                dispatcher = null;
                HasObservedTable = false;
                MeldTracker.Clear();
                InvalidateObservation("RECOVERY_OBSERVATION_ENDED");
            }
            try
            {
                // Avoid writing an already disabled configuration on every failed gate check.
                if (Configuration != DisabledCopy(Configuration)) ConfigService.Update(DisabledCopy);
            }
            catch (Exception ex)
            {
                // CanOperate is already false even if persistence leaves Current unchanged.
                log.Warning($"[MahjongCN] Stop config save failed: {ex.GetType().Name}.");
                Status = reason + " 配置保存失败：" + ex.GetType().Name + "；当前运行仍已停止。";
            }
        }
        finally { stopping = false; }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed || (Mode == PlayMode.Off && !pausedObservation) || !RefreshIdentityGate()) return;
        try
        {
            if (reader is not null) ObserveTable(reader.Poll());
            if (Mode != PlayMode.Off) CheckReadErrors();
            else if (pausedObservation && reader is not null)
            {
                var snapshot = reader.TryBuildSnapshot();
                if (snapshot is not null) PublishSnapshot(snapshot);
                else if (RecoverySnapshot is not null) InvalidateObservation("RECOVERY_READ_INTERRUPTED");
            }
        }
        catch (Exception ex)
        {
            log.Error($"[MahjongCN] Runtime table observation failed: {ex.GetType().Name}.");
            InvalidateObservation("RECOVERY_READ_INTERRUPTED");
            PauseAutomation("TABLE_OBSERVATION_ERROR：" + ex.GetType().Name + "；已暂停，等待重新核对桌面。");
        }
    }

    private void OnObservationChanged(AddonEmjObservation observation)
    {
        if (disposed || (Mode == PlayMode.Off && !pausedObservation)) return;
        ObserveTable(observation);
    }

    private void ObserveTable(AddonEmjObservation observation)
    {
        if (observation.Present && observation.IsVisible)
        {
            if (!HasObservedTable && Mode != PlayMode.Off) Status = ActiveStatus(Mode);
            HasObservedTable = true;
        }
        else if (HasObservedTable && !observation.Present)
            StopAutomation("SCENE_EXIT：牌桌已关闭，提醒与自动操作已停止；重新入桌后须重新选择模式。");
        else if (!HasObservedTable && Mode != PlayMode.Off)
            Status = Mode == PlayMode.Automatic
                ? "自动打牌待命：等待进入牌桌；立即停止可取消待命。"
                : "手动提醒待命：等待进入牌桌。";
    }

    internal void PublishSnapshot(StateSnapshot snapshot)
    {
        if (disposed || (Mode == PlayMode.Off && !pausedObservation)) return;
        if (snapshot.Hand.Count > 14 || snapshot.OurMelds.Count > 4 || snapshot.Seats.Count != 4 ||
            snapshot.Scores.Count != 4 || snapshot.DoraIndicators.Count > 5 ||
            snapshot.Seats.Any(seat => seat.Discards.Count > 40 || seat.Melds.Count > 4))
        {
            InvalidateObservation("RECOVERY_INVALID_PUBLIC_SNAPSHOT");
            return;
        }
        if (RecoverySnapshot is { } previous &&
            ((snapshot.AddonStateCode == 29 && previous.AddonStateCode != 29) ||
             snapshot.WallRemaining > previous.WallRemaining + 5))
            InvalidateObservation("RECOVERY_NEW_HAND");
        observationReadFailed = false;
        // Copy nested mutable Meld.Tiles too. The journal never receives live tracker
        // buffers or actionable legal/menu state, and never persists ura indicators.
        var copy = snapshot with
        {
            Hand = snapshot.Hand.ToArray(),
            OurMelds = snapshot.OurMelds.Select(CloneMeld).ToArray(),
            Scores = snapshot.Scores.ToArray(),
            DoraIndicators = snapshot.DoraIndicators.ToArray(),
            UraDoraIndicators = [],
            Legal = LegalActions.None,
            Seats = snapshot.Seats.Select(seat => seat with
            {
                Discards = seat.Discards.ToArray(),
                DiscardIsTedashi = seat.DiscardIsTedashi.ToArray(),
                Melds = seat.Melds.Select(CloneMeld).ToArray(),
            }).ToArray(),
        };
        int hash = PublicSnapshotHash(copy);
        if (hasObservationHash && hash == lastObservationHash && RecoverySnapshot is { } existing && SamePublicSnapshot(existing, copy)) return;
        RecoverySnapshot = copy;
        ObservationSequence++;
        hasObservationHash = true;
        lastObservationHash = hash;
        TryCleanup(() => SnapshotObserved?.Invoke(copy), "record public observation");
    }

    private void InvalidateObservation(string reason)
    {
        if (reason == "RECOVERY_READ_INTERRUPTED" && observationReadFailed) return;
        observationReadFailed = reason == "RECOVERY_READ_INTERRUPTED";
        ObservationSessionId = Guid.NewGuid();
        ObservationSequence = 0;
        RecoverySnapshot = null;
        hasObservationHash = false;
        TryCleanup(() => ObservationInvalidated?.Invoke(reason), "record observation boundary");
    }

    private static Meld CloneMeld(Meld meld) => meld with { Tiles = meld.Tiles.ToArray() };

    private static int PublicSnapshotHash(StateSnapshot snapshot)
    {
        var hash = new HashCode();
        hash.Add(snapshot.AddonStateCode); hash.Add(snapshot.WallRemaining); hash.Add(snapshot.AkaDora);
        foreach (var tile in snapshot.Hand) hash.Add(tile.Id);
        foreach (int score in snapshot.Scores) hash.Add(score);
        foreach (var tile in snapshot.DoraIndicators) hash.Add(tile.Id);
        foreach (var meld in snapshot.OurMelds)
        { hash.Add(meld.Kind); hash.Add(meld.ClaimedFromSeat); hash.Add(meld.ClaimedTile); foreach (var tile in meld.Tiles) hash.Add(tile.Id); }
        foreach (var seat in snapshot.Seats)
        { hash.Add(seat.DiscardCount); hash.Add(seat.Riichi); foreach (var tile in seat.Discards) hash.Add(tile.Id); }
        return hash.ToHashCode();
    }

    private static bool SamePublicSnapshot(StateSnapshot a, StateSnapshot b)
    {
        if (a.SchemaVersion != b.SchemaVersion || a.AddonStateCode != b.AddonStateCode || a.WallRemaining != b.WallRemaining ||
            a.AkaDora != b.AkaDora || a.OurSeat != b.OurSeat || a.OurRiichi != b.OurRiichi ||
            a.OurIppatsu != b.OurIppatsu || a.OurDoubleRiichi != b.OurDoubleRiichi || a.RoundWind != b.RoundWind ||
            a.Honba != b.Honba || a.RiichiSticks != b.RiichiSticks || a.TurnIndex != b.TurnIndex ||
            a.DealerSeat != b.DealerSeat || a.SeatInfoKnown != b.SeatInfoKnown ||
            !a.Hand.SequenceEqual(b.Hand) || !a.Scores.SequenceEqual(b.Scores) ||
            !a.DoraIndicators.SequenceEqual(b.DoraIndicators) || !SameMelds(a.OurMelds, b.OurMelds)) return false;
        for (int i = 0; i < 4; i++)
        {
            var x = a.Seats[i]; var y = b.Seats[i];
            if (x.DiscardCount != y.DiscardCount || x.Riichi != y.Riichi || x.RiichiDiscardIndex != y.RiichiDiscardIndex ||
                x.Ippatsu != y.Ippatsu || x.IsTenpaiCalled != y.IsTenpaiCalled ||
                !x.Discards.SequenceEqual(y.Discards) || !x.DiscardIsTedashi.SequenceEqual(y.DiscardIsTedashi) ||
                !SameMelds(x.Melds, y.Melds)) return false;
        }
        return true;
    }

    private static bool SameMelds(IReadOnlyList<Meld> a, IReadOnlyList<Meld> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Kind != b[i].Kind || a[i].ClaimedTile != b[i].ClaimedTile ||
                a[i].ClaimedFromSeat != b[i].ClaimedFromSeat || !a[i].Tiles.SequenceEqual(b[i].Tiles)) return false;
        return true;
    }

    private static string ActiveStatus(PlayMode mode) => mode == PlayMode.Automatic
        ? "自动打牌已开启；决策来源见当前选择，立即停止可取消后续操作。"
        : "手动提醒已开启；显示引擎建议，不发送游戏操作。";

    private void TryCleanup(Action action, string step)
    {
        try { action(); }
        catch (Exception ex)
        {
            try { log.Warning($"[MahjongCN] {step}: {ex.GetType().Name}."); }
            catch { /* A closed host logger must not skip the remaining teardown. */ }
        }
    }

    private static Configuration DisabledCopy(Configuration config) => config with
    {
        AutomationArmed = false,
        SuggestionOnly = true,
        TosAccepted = false,
        AutoPlayConfirmed = false,
        AutoAdvanceAfterHand = false,
        EnableGameLogging = false,
        DevMode = false,
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        StopAutomation("插件已卸载；提醒和自动打牌已停止。");
    }
}
