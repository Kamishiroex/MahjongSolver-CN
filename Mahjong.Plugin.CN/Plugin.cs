using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Collections.Immutable;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Mahjong.Cn;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Access;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface Interface { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IClientState Client { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle Lifecycle { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider Textures { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;
    [PluginService] internal static ICondition Conditions { get; private set; } = null!;
    [PluginService] internal static IDutyState DutyState { get; private set; } = null!;
    [PluginService] internal static IPartyList Party { get; private set; } = null!;
    [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    internal static readonly string[] Markers = ["入桌/开局", "摸牌", "弃牌/排序", "赤五/宝牌", "吃/碰后", "明杠", "暗杠", "加杠", "立直", "荣和/自摸", "流局/换局", "退桌/重载"];
    private readonly WindowSystem windows = new("Mahjong.Plugin.CN");
    private readonly object gate = new();
    private readonly MainWindow window;
    private readonly VisibleUiReader reader;
    internal CnSession Session { get; }
    internal ICnActionAdapter Actions { get; } = new DisabledActionAdapter();
    internal Mahjong.Plugin.Dalamud.Plugin? PlayRuntime { get; private set; }
    private readonly DiagnosticBuffer frames = new();
    private readonly LowerHandTracker lowerHandTracker = new();
    private PublicMonitorSession? publicMonitor;
    private AiProbeController? aiProbe;
    internal AiProbeController AiProbe
    {
        get { lock (gate) return aiProbe ??= new(code => Log.Warning("Local AI cleanup: {Code}", code)); }
    }
    internal string DefaultEngineDirectory { get; }
    private RuntimeIdentity? monitorIdentity;
    private string? monitorUldHash;
    internal PublicMonitorSession PublicMonitor => publicMonitor ??= new();
    internal bool Monitoring => publicMonitor?.IsActive == true;
    private readonly Queue<LifecycleEntry> events = new();
    private readonly HashSet<string> candidates = new(VisibleUiReader.CandidateNames);
    private readonly List<HandPreviewReview> lowerHandReviews = [];
    private RuntimeIdentity? capturedIdentity;
    private bool capturedLowerHand;
    private bool capturedPublicLayout;
    private string? capturedUldHash;
    private readonly string diagnosticsDirectory;
    private readonly GameplayStopRecorder stopRecorder;
    private readonly TestCodeAccess testAccess;
    private Task<string>? export;
    private LocalCaptureRecorder? recorder;
    private LocalCaptureRecorder? finalizingRecorder;
    private Task<string?>? recordingArchive;
    private string? lastArchivePath;
    private string? recordingDirectory;
    private long savedFrames;
    private double lastPoll;
    private double captureStart;
    private long sequence;
    private bool disposed;
    private volatile bool gameplayAllowed;
    private volatile bool readObservationAllowed;
    private int modeRequestVersion;
    internal RuntimeIdentity Identity { get; private set; }
    internal bool Capturing { get; private set; }
    internal int MarkerIndex;
    internal bool CaptureLowerHand { get; private set; } = true;
    internal bool CapturePublicLayout { get; private set; } = true;
    internal string? LowerHandUldHash { get; private set; }
    internal string LowerHandReviewStatus { get; private set; } = "尚未人工核对；此预览不作为完整牌局。";
    internal string Status { get; private set; } = "已停止。进入多玛方城后选择“手动提醒”或“自动打牌”。";
    internal string ExportStatus { get; private set; } = "尚未导出";
    internal DiagnosticFrame? Latest { get; private set; }
    internal int FrameCount => frames.Count;
    internal int TimelineCount => frames.TimelineCount;
    internal long DroppedFrameCount => frames.DroppedDetailedFrames;
    internal long SavedFrameCount => savedFrames;
    internal bool SavingArchive => recordingArchive is { IsCompleted: false };
    internal string RecordingLocation => recordingDirectory ?? "尚未开始本地整场采集";
    internal string? RecordingPath => lastArchivePath ?? recordingDirectory;
    internal string? LastStopPath => stopRecorder.LastPath;
    internal string? StopRecordingError => stopRecorder.LastError;
    internal bool TestAccessUnlocked => testAccess.IsUnlocked;
    internal string TestAccessStatus => testAccess.StatusMessage;
    internal DateTimeOffset? TestAccessExpiresAt => testAccess.ExpiresAtUtc;

    public Plugin()
    {
        Identity = RuntimeIdentity.Read(Interface, Client);
        Session = new(new CnCompatibilityProfile(RuntimeIdentity.TargetGame, 15), Identity.GameVersion, Identity.Api);
        diagnosticsDirectory = Path.Combine(Interface.GetPluginConfigDirectory(), "diagnostics");
        logsDirectory = Path.Combine(Interface.GetPluginConfigDirectory(), "logs");
        DefaultEngineDirectory = Path.Combine(Interface.GetPluginConfigDirectory(), "engines", "akochan");
        LoadEngineSettings();
        stopRecorder = new GameplayStopRecorder(diagnosticsDirectory);
        testAccess = new(Path.Combine(Interface.GetPluginConfigDirectory(), "test-access.json"));
        solverPreference = new(Path.Combine(Interface.GetPluginConfigDirectory(), "solver-preference.json"));
        RestoreSolverPreference();
        reader = new(name => GameGui.GetAddonByName(name).Address,
            (address, count) => global::Dalamud.SafeMemory.ReadBytes(address, count, out var data) ? data : null);
        Ui.GlassTheme.Initialize(Interface.GetPluginConfigDirectory());
        window = new(this);
        windows.AddWindow(window);
        LoadTableAutomationOptions();
        LoadTaskRules();
        LoadNetworkPreferences();
        LoadJournalSettings();
        // Saved preferences alone never authorize queueing or game input after reload.
        if (!Commands.AddHandler("/mjcn", new CommandInfo(OnCommand) { HelpMessage = Brand.ProductName + "：queue 自动排队设置；manual 提醒；auto 自动打牌；pause 暂停；recover 核对恢复；logs 导出日志；stop 全停；monitor 只读牌局；ai 测试版设置。" }))
            throw new InvalidOperationException("/mjcn 已被占用，未注册插件命令。");
        try
        {
            Interface.UiBuilder.Draw += Draw;
            Interface.UiBuilder.OpenMainUi += Open;
            Interface.UiBuilder.OpenConfigUi += Open;
            Framework.Update += Update;
            DutyState.DutyCompleted += OnAutomationDutyCompleted;
            Lifecycle.RegisterListener(AddonEvent.PostSetup, OnLifecycle);
            Lifecycle.RegisterListener(AddonEvent.PreFinalize, OnLifecycle);
        }
        catch { Dispose(); throw; }
        if (Identity.Error is not null) Status = Identity.Error;
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "monitor": StartPublicMonitor(); window.ShowPublicMonitor(); break;
            case "ai": window.ShowAi(); break;
            case "queue": window.ShowQueue(); break;
            case "rating": DispatchUi(ReadOwnRating); Open(); break;
            case "pause": PausePlay(); Open(); break;
            case "logs": ExportGameLogs(); Open(); break;
            case "recover": StartLogRecovery(); Open(); break;
            case "manual": ActivatePlay(false); Open(); break;
            case "auto": ActivatePlay(true); Open(); break;
            case "off": Stop("用户停止。"); Open(); break;
            case "start": StartFullSession(); Open(); break;
            case "stop": Stop("用户停止：建议、操作和采集均已停止。"); Open(); break;
            case "export": Export(); Open(); break;
            default: window.IsOpen = !window.IsOpen; break;
        }
    }

    internal void Start() { lock (gate) StartCore(); }

    internal void StartAiProbe(string directory)
    {
        if (!RequireTestAccessCore()) return;
        SuspendTableAutomation("正在切换到测试版自检，自动排队和进桌开打已暂停。");
        gameplayAllowed = false;
        int request = Interlocked.Increment(ref modeRequestVersion);
        _ = Framework.RunOnFrameworkThread(() =>
        {
            if (request != Volatile.Read(ref modeRequestVersion)) return;
            lock (gate)
            {
                if (disposed || request != Volatile.Read(ref modeRequestVersion) || !RequireTestAccessCore()) return;
                StopCore("切换到本地 AI 离线自检；游戏建议与操作已停止。");
                Identity = RuntimeIdentity.Read(Interface, Client);
                if (Identity.Error is not null) { Status = Identity.Error; return; }
                if (!Client.IsLoggedIn) { Status = "请先登录游戏，再运行插件内 AI 自检。"; return; }
                AiProbe.Start(directory);
            }
        });
    }

    internal void ExportAiProbe()
    {
        lock (gate)
        {
            if (disposed || export is not null || aiProbe?.Result is not { } result) return;
            var report = new
            {
                SchemaVersion = 1, Kind = "AkochanOfflineSelfCheck", PluginVersion = LocalCaptureRecorder.PluginVersion,
                ExportedUtc = DateTimeOffset.UtcNow, Identity, Decision = result,
                LiveGameInput = false, AutomaticActions = false,
            };
            ExportStatus = "正在导出本地 AI 自检结果…";
            export = Task.Run(() =>
            {
                Directory.CreateDirectory(diagnosticsDirectory);
                string file = Path.Combine(diagnosticsDirectory, $"mjcn-ai-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
                using var entry = zip.CreateEntry("ai-self-check.json", CompressionLevel.Optimal).Open();
                DiagnosticPrivacy.Serialize(entry, report);
                return file;
            });
        }
    }

    internal void StartPublicMonitor()
    {
        SuspendTableAutomation("正在切换到只读监视，自动排队和进桌开打已暂停。");
        // Selecting read-only immediately revokes input, before the queued start runs.
        gameplayAllowed = false;
        int request = Interlocked.Increment(ref modeRequestVersion);
        _ = Framework.RunOnFrameworkThread(() =>
        {
            if (request != Volatile.Read(ref modeRequestVersion)) return;
            lock (gate)
            {
                if (disposed || request != Volatile.Read(ref modeRequestVersion)) return;
                StopCore("切换到公开牌局只读监视；旧操作及采集已停止。");
                Identity = RuntimeIdentity.Read(Interface, Client);
                if (Identity.Error is not null) { Status = Identity.Error; return; }
                if (!Client.IsLoggedIn) { Status = "请先登录游戏，再开启只读监视。"; return; }
                if (!CheckLowerHandResource()) return;
                EnsureJournalCore("public_monitor");
                candidates.Clear(); candidates.UnionWith(VisibleUiReader.CandidateNames);
                lowerHandTracker.Clear();
                captureStart = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                lastPoll = 0;
                PublicMonitor.Start(PublicObservationAssembler.CreateAuditedContext(Identity, LowerHandUldHash));
                monitorIdentity = Identity;
                monitorUldHash = LowerHandUldHash;
                Status = "只读监视已开启；等候公开牌桌。事件与错误自动记录在本机，可随时导出。";
            }
        });
    }

    private bool CheckLowerHandResource()
    {
        LowerHandUldHash = null;
        try
        {
            var resource = DataManager.GetFile<Lumina.Data.Files.UldFile>("ui/uld/emj.uld");
            LowerHandUldHash = resource is null ? null : Convert.ToHexString(SHA256.HashData(resource.Data));
            if (string.Equals(LowerHandUldHash, LowerHandProfile.EmjUldSha256, StringComparison.OrdinalIgnoreCase)) return true;
            Stop("LOWER_HAND_RESOURCE_MISMATCH：麻将界面资源不匹配，未读取任何牌面。请导出诊断。");
        }
        catch (Exception ex) { Stop("LOWER_HAND_RESOURCE_ERROR：" + ex.GetType().Name); }
        return false;
    }

    internal void ActivatePlay(bool automatic)
    {
        lock (gate)
        {
            if(disposed || !RequireSelectedSourceAccess() || automatic && !RequireOperationCapability())return;
            // Re-selecting a running mode is not a request to stop the table coordinator.
            // A queued pause has already revoked gameplayAllowed, so it is never hidden here.
            var requested = automatic ? Mahjong.Plugin.Dalamud.PlayMode.Automatic : Mahjong.Plugin.Dalamud.PlayMode.Manual;
            if (!disposed && gameplayAllowed && SelectedSourceAccessValid && (!automatic || GameOperationsAuthorized) && PlayRuntime?.Mode == requested &&
                Identity is { Error: null }) return;
        }
        if (taskRun?.HasUnfinishedRun == true) { Status = "现有任务尚未结束，请使用继续任务或结束任务。"; return; }
        SuspendTableAutomation("已主动选择打牌模式；排队及进桌自动开打已暂停。");
        int request = Interlocked.Increment(ref modeRequestVersion);
        if(automatic) { pendingOperationRequest=request; pendingOperationGeneration=Volatile.Read(ref operationGeneration); }
        _ = Framework.RunOnFrameworkThread(() =>
        {
            if (request == Volatile.Read(ref modeRequestVersion)) ActivatePlayCore(automatic, request);
        });
    }

    private void ActivatePlayCore(bool automatic, int request)
    {
        lock (gate)
        {
            if (disposed || request != Volatile.Read(ref modeRequestVersion)) return;
            if (!RequireSelectedSourceAccess()) return;
            if(automatic && (!RequireOperationCapability() || !GameOperationsAuthorized &&
                (pendingOperationRequest!=request || pendingOperationGeneration!=Volatile.Read(ref operationGeneration))))
            { Status="自动操作缺少有效的本次任务授权，请重新主动启动。";return; }
            if (ExperimentalHandAiEnabled && EngineMaintenanceBusy) { Status = "测试版文件处理尚未完成，请稍候再启动。"; return; }
            if (ExperimentalHandAiEnabled && EngineActivationError is { } engineError)
            {
                StopCore(engineError);
                window.ShowAi();
                return;
            }
            // Check again after the queued request reaches the framework; a lease can
            // expire between clicking a mode and executing this delegate.
            if (journalActive && journal?.Fault is { } existingJournalFault)
            {
                gameplayAllowed = false;
                Status = "JOURNAL_ERROR：" + existingJournalFault + "；请先停止全部并处理日志存储错误，再重新选择模式。";
                return;
            }
            aiProbe?.Stop("已切换到实验打牌模式。");
            Identity = RuntimeIdentity.Read(Interface, Client);
            if (Identity.Error is not null) { StopCore(Identity.Error); return; }
            if (!Client.IsLoggedIn) { StopCore("请先登录游戏，再选择手动提醒或自动打牌。"); return; }
            if (!CheckLowerHandResource()) return;
            EnsureJournalCore(automatic ? "automatic" : "manual");
            if (journal?.Fault is { } journalFault)
            {
                gameplayAllowed = false;
                Status = "JOURNAL_ERROR：" + journalFault + "；请先停止全部并处理日志存储错误，再重新选择模式。";
                return;
            }
            if (!startingFromTableAutomation && !BeginManagedTask(automatic, false)) return;
            if(automatic && !GameOperationsAuthorized && !GrantTaskOperations(pendingOperationGeneration))return;
            pendingOperationRequest=-1;
            ratingReadingEnabled = true;
            if (Monitoring)
            {
                publicMonitor!.Stop("已切换到实验打牌模式。");
                Latest = null;
                lowerHandTracker.Clear();
            }
            try
            {
                // No old upstream entry, telemetry, memory dump recorder or input hook is initialized.
                gameplayAllowed = true;
                readObservationAllowed = true;
                recoveryPending = false;
                EnsurePlayRuntimeCore();
                PlayRuntime!.AutoAdvancePreference = AutomationOptions.AutoAdvanceAfterHand;
                PlayRuntime.KeepAutomaticBetweenHandsPreference = AutomationOptions.KeepAutomaticBetweenHands;
                PlayRuntime!.SetMode(automatic ? Mahjong.Plugin.Dalamud.PlayMode.Automatic : Mahjong.Plugin.Dalamud.PlayMode.Manual);
                if (PlayRuntime.Mode != Mahjong.Plugin.Dalamud.PlayMode.Off) AcknowledgeStopAlert();
                RecordJournalEvent("mode_selected", new
                {
                    Mode = automatic ? "automatic" : "manual",
                    DecisionSource = TechnicalDecisionSource,
                    AutomaticResumedFromDisk = false,
                });
                Status = PlayRuntime.Status;
            }
            catch (Exception ex)
            {
                gameplayAllowed = false;
                PlayRuntime?.StopAutomation("启动失败：" + ex.GetType().Name);
                Status = "启动失败：" + ex.GetType().Name + "；可在插件错误日志中查看详情。";
                Log.Error(ex, "CN gameplay runtime initialization failed");
            }
        }
    }

    internal void StartFullSession()
    {
        lock (gate)
        {
            if (Capturing || SavingArchive || disposed) return;
            CaptureLowerHand = true;
            CapturePublicLayout = true;
            StartCore();
        }
    }

    private void StartCore()
    {
        if (disposed) return;
        if (Capturing || SavingArchive) return;
        aiProbe?.Stop("已切换到整场诊断采集。");
        Identity = RuntimeIdentity.Read(Interface, Client);
        if (Identity.Error is not null) { Stop(Identity.Error); return; }
        if ((CaptureLowerHand || CapturePublicLayout) && !CheckLowerHandResource()) return;
        if (!CaptureLowerHand && !CapturePublicLayout) LowerHandUldHash = null;
        EnsureJournalCore("diagnostics");
        publicMonitor?.Stop("已切换到整场诊断采集。");
        var sessionHeader = new
        {
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            UpstreamCommit = RuntimeIdentity.Upstream,
            Identity,
            LowerHandCaptureEnabled = CaptureLowerHand,
            PublicLayoutCaptureEnabled = CapturePublicLayout,
            EmjUldHash = LowerHandUldHash,
            FullGameStateVerified = false,
            GameplayModeAtCaptureStart = PlayRuntime?.Mode.ToString() ?? "Off",
            AutomationEnabledAtCaptureStart = PlayRuntime?.Mode == Mahjong.Plugin.Dalamud.PlayMode.Automatic,
        };
        try { recorder = LocalCaptureRecorder.Start(diagnosticsDirectory, sessionHeader); }
        catch (Exception ex)
        { Stop("LOCAL_CAPTURE_START_FAILED：" + ex.GetType().Name); return; }
        recordingArchive = null;
        finalizingRecorder = null;
        lastArchivePath = null;
        recordingDirectory = recorder.SessionDirectory;
        savedFrames = 0;
        ExportStatus = "本地自动保存中：" + recordingDirectory;
        frames.Clear(); events.Clear(); Latest = null; lowerHandTracker.Clear();
        capturedIdentity = Identity;
        capturedLowerHand = CaptureLowerHand;
        capturedPublicLayout = CapturePublicLayout;
        capturedUldHash = LowerHandUldHash;
        lowerHandReviews.Clear();
        LowerHandReviewStatus = "尚未人工核对；此预览不作为完整牌局。";
        candidates.Clear(); candidates.UnionWith(VisibleUiReader.CandidateNames);
        Capturing = true;
        Session.ResumeReadOnly();
        captureStart = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        lastPoll = 0;
        Status = "本地诊断采集中；打牌模式见助手页，诊断采集不会主动启用操作。";
    }

    internal void Stop(string reason)
    {
        RevokeUiIntents();
        SuspendTableAutomation(reason);
        gameplayAllowed = false;
        Interlocked.Increment(ref modeRequestVersion);
        if (Framework.IsInFrameworkUpdateThread) { lock (gate) StopCore(reason); }
        else _ = Framework.RunOnFrameworkThread(() => { lock (gate) StopCore(reason); });
    }

    internal void VerifyTestCode(string input)
    {
        lock (gate)
        {
            if (disposed) return;
            // A lease is a capability, never a run intent or a standard-mode gate.
            EnforceBetaAccess();
            testAccess.TryUnlock(input);
            BetaAccessStatus = testAccess.StatusMessage;
            if (TestAccessUnlocked && PendingStopAlert?.Reason.StartsWith("BETA_ACCESS_EXPIRED", StringComparison.Ordinal) == true)
            {
                AcknowledgeStopAlert();
                Status = "测试版已重新验证，原选择与任务进度已保留；请主动开始提示或授权继续任务，尚未自动启动。";
            }
        }
    }

    private void StopCore(string reason)
    {
        ratingReadingEnabled = false;
        CancelRatingRefresh();
        if (CurrentRating is { } rating) CurrentRating = rating with
        { Freshness = Mahjong.Cn.Rating.RatingFreshness.Cached, FailureReason = "全部读取已停止。" };
        journalContinueAfterTable = false;
        SuspendTableAutomation(reason);
        gameplayAllowed = false;
        readObservationAllowed = false;
        recoveryPending = false;
        recoveryCandidates = [];
        aiProbe?.Stop(reason);
        PlayRuntime?.StopAutomation(reason);
        CompleteJournalCore(reason);
        if (recorder is { } activeRecorder)
        {
            activeRecorder.TryAppend("status", new { Code = "CAPTURE_STOPPED", Reason = reason });
            finalizingRecorder = activeRecorder;
            recordingArchive = activeRecorder.CompleteAsync(reason);
            recorder = null;
            ExportStatus = "正在自动封存整场记录：" + recordingDirectory;
        }
        Capturing = false;
        publicMonitor?.Stop(reason);
        Session.Stop(reason);
        Actions.Stop();
        Latest = null;
        lowerHandTracker.Clear();
        Status = reason;
    }

    private void Update(IFramework framework)
    {
        lock (gate)
        {
            DrainUiIntents();
            try { UpdateCore(); } finally { PublishUiSnapshot(); }
        }
    }

    private void UpdateCore()
    {
        if (disposed) return;
        EnforceBetaAccess();
        PollEngineMaintenance();
        UpdateCommunity();
        UpdateRatingCore();
        UpdateTaskCore();
        PollJournalMaintenance();
        UpdateJournalCore();
        UpdateResultsCore();
        UpdateTableAutomationCore();
        if (aiProbe?.Busy == true && (Identity.Error is not null || !Client.IsLoggedIn))
            StopCore(Identity.Error ?? "SCENE_EXIT：已登出，本地 AI 自检已取消。");
        if (recordingArchive is { IsCompleted: true })
        {
            lastArchivePath = recordingArchive.IsCompletedSuccessfully ? recordingArchive.Result : null;
            string? archiveFault = finalizingRecorder?.Fault;
            ExportStatus = lastArchivePath is not null
                ? lastArchivePath + (archiveFault is null ? "" : "\n注意：此包为部分记录，具体原因：" + archiveFault)
                : "自动封存失败（" + (archiveFault ?? recordingArchive.Exception?.GetBaseException().GetType().Name ?? "UNKNOWN") +
                    "），可能没有已完成记录；请检查本地目录：" + recordingDirectory;
            recordingArchive = null;
            finalizingRecorder = null;
        }
        if (export is { IsCompleted: true })
        {
            ExportStatus = export.IsCompletedSuccessfully ? export.Result : "导出失败：" + export.Exception?.GetBaseException().GetType().Name;
            export = null;
        }
        if (!Capturing && !Monitoring) return;
        if (Identity.Error is not null) { StopCore(Identity.Error); return; }
        if (recorder?.Fault is { } fault) { Stop("LOCAL_CAPTURE_FAILED：" + fault); return; }
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (now - captureStart >= 3600) { Stop(Monitoring ? "只读监视已达到 60 分钟上限；请主动重新开启。" : "已达到 60 分钟上限，整场记录正在自动封存。"); return; }
        if (now - lastPoll < (Monitoring ? 0.1 : 0.5)) return;
        lastPoll = now;
        try
        {
            if (!Client.IsLoggedIn)
            {
                if (!Append(new(++sequence, DateTimeOffset.UtcNow, Markers[MarkerIndex], "未登录/退出；牌局已清空", []))) return;
                Stop("SCENE_EXIT：未登录或已退出，停止采集并清空当前牌局。");
                return;
            }
            reader.BeginSample();
            // Metadata-only diagnostics can start without loading emj.uld. A journal
            // being active is not authorization to read additional public resources.
            bool readPublicFields = (Monitoring || journalActive) && LowerHandUldHash == LowerHandProfile.EmjUldSha256;
            var probes = candidates.Order().Select(n => reader.Probe(n, true, Monitoring || capturedLowerHand,
                Monitoring || capturedPublicLayout, capturePublicFaces: readPublicFields, capturePublicStatus: readPublicFields)).ToArray();
            if (readPublicFields)
            {
                probes = probes.Select(p => AttachCurrentMatchRules(p, ReadCurrentMatchRules)).ToArray();
            }
            string? error = probes.FirstOrDefault(p => p.Error is not null)?.Error;
            bool visible = probes.Any(p => p.Visible && p.Ready);
            string recognition = error is not null ? "READ_ERROR：" + error
                : visible ? "发现可见候选 Addon；手牌/摸牌/牌河/副露/宝牌/座位/局数/立直/点数均待映射"
                : "NO_TABLE：未发现就绪的可见候选 Addon；不沿用上一帧牌局";
            // Until CN fields are mapped, a visible UI is an incomplete read, never an empty valid game.
            if (!Monitoring)
                Session.Observe(new ReadObservation(sequence + 1, visible || error is not null ? ObservationPhase.ReadError : ObservationPhase.OutsideTable,
                    Error: error ?? "CN_READ_PROFILE_UNVERIFIED：可见 UI 尚未映射为完整牌局"));
            bool monitorSample = Monitoring;
            if (!Append(new(++sequence, DateTimeOffset.UtcNow, Markers[MarkerIndex], recognition, probes))) return;
            if (monitorSample)
            {
                Status = PublicMonitor.Status;
                if (!PublicMonitor.IsActive) { StopCore(Status); return; }
                // A rejected sample invalidates current data; no engine consumes this path.
                return;
            }
            Status = recognition;
            if (error is not null)
            {
                // Keep the local diagnostic session alive across transient UI updates.
                // Advice/actions remain stopped; no rejected observation is reused.
                Session.Stop(recognition);
                Actions.Stop();
                lowerHandTracker.Clear();
                Latest = null;
                Status = recognition + "；当前识别已清空，后续独立采样仅用于诊断。";
            }
        }
        catch (Exception ex)
        {
            var error = "UPDATE_ERROR：" + ex.GetType().Name;
            if (Append(new(++sequence, DateTimeOffset.UtcNow, Markers[MarkerIndex], error, []))) Stop(error);
        }
    }

    private bool Append(DiagnosticFrame frame)
    {
        RecordPublicTable(frame);
        if (capturedLowerHand || Monitoring)
        {
            var observed = frame.Addons.All(x => x.Error is null)
                ? frame.Addons.FirstOrDefault(x => x.Name == "Emj") : null;
            var reading = lowerHandTracker.Observe(frame.Sequence, observed);
            frame = frame with { Addons = frame.Addons.Select(x => x.Name == "Emj"
                ? x with { LowerHandReading = reading } : x).ToArray() };
        }
        Latest = frame;
        if (Monitoring)
        {
            PublicMonitor.Observe(frame);
            if (!PublicMonitor.IsActive) StopCore(PublicMonitor.Status);
            return true;
        }
        frames.Append(frame);
        // Background serialization gets immutable managed copies, never game pointers.
        var frozen = frame with { Addons = frame.Addons.Select(x => x with
        {
            VisibleNodes = x.VisibleNodes.ToImmutableArray(),
            LowerHandFaces = x.LowerHandFaces?.ToImmutableArray(),
            PublicLayouts = x.PublicLayouts?.ToImmutableArray(),
        }).ToImmutableArray() };
        if (!Record("frame", frozen)) return false;
        savedFrames++;
        return true;
    }

    private bool Record(string kind, object payload)
    {
        if (recorder is null || recorder.TryAppend(kind, payload)) return true;
        Stop("LOCAL_CAPTURE_FAILED：" + (recorder.Fault ?? "QUEUE_CLOSED"));
        return false;
    }

    private void OnLifecycle(AddonEvent type, AddonArgs args) { lock (gate) OnLifecycleCore(type, args); }

    private void OnLifecycleCore(AddonEvent type, AddonArgs args)
    {
        if (type == AddonEvent.PreFinalize && args.AddonName == Readers.CnRatingProfileAccess.RootAddonName)
            ratingProfile?.ForgetOwnership();
        OnJournalTableLifecycle(type, args.AddonName);
        if (!disposed && aiProbe?.Busy == true && type == AddonEvent.PreFinalize && args.AddonName is "Emj" or "EmjL")
            StopCore("SCENE_EXIT：牌桌退出，本地 AI 自检已取消。");
        if (disposed || (!Capturing && !Monitoring) || Identity.Error is not null) return;
        string name = args.AddonName;
        // Discover only Mahjong-like UI resource names; these names are still unverified candidates.
        if (!Regex.IsMatch(name, @"\AEmj[A-Za-z0-9_]{0,24}\z", RegexOptions.CultureInvariant)) return;
        if (Monitoring)
        {
            if (type == AddonEvent.PreFinalize && name is "Emj" or "EmjL")
                StopCore("SCENE_EXIT：牌桌卸载，只读监视已停止，旧字段已清空。");
            return;
        }
        if (candidates.Count < 16) candidates.Add(name);
        var entry = new LifecycleEntry(DateTimeOffset.UtcNow, name, type.ToString());
        events.Enqueue(entry);
        if (!Record("lifecycle", entry)) return;
        while (events.Count > 128) events.Dequeue();
        if (type == AddonEvent.PreFinalize)
        {
            Session.Observe(new ReadObservation(++sequence, ObservationPhase.OutsideTable));
            Actions.Stop();
            Latest = null;
            lowerHandTracker.Clear();
            Status = "SCENE_EXIT：麻将候选界面卸载，当前牌局已清空。";
        }
    }

    internal void Export() { lock (gate) ExportCore(); }

    internal void ExportPublicMonitor()
    {
        _ = Framework.RunOnFrameworkThread(() =>
        {
            lock (gate)
            {
                if (disposed || export is not null) return;
                // Capture only immutable, managed observations. This export never starts a reader.
                var snapshot = publicMonitor?.Current ?? publicMonitor?.Last;
                var summary = publicMonitor?.Evidence;
                var manifest = new
                {
                    SchemaVersion = 1, Kind = "ReadOnlyPublicMonitor", ExportedUtc = DateTimeOffset.UtcNow,
                    PluginVersion = LocalCaptureRecorder.PluginVersion, UpstreamCommit = RuntimeIdentity.Upstream,
                    Identity = monitorIdentity ?? Identity, Profile = "Emj-guarded-visible-v1", UldHash = monitorUldHash,
                    LatestAttemptIdentity = Identity,
                    IsLiveSnapshot = Monitoring && publicMonitor?.Current is not null,
                    Status = publicMonitor?.Status ?? "尚未开启只读监视。",
                    Engine = "none", FullGameStateVerified = false, ContainsNormalizedEvents = false,
                    Scope = "已白名单限制的下方可见牌面、公开布局元数据摘要及未知字段；没有完整手牌或事件流。无自动上传。",
                };
                var readiness = snapshot is null ? null : Mahjong.Cn.PublicState.ReadinessEvaluator.Evaluate(
                    snapshot, Mahjong.Cn.PublicState.ReadinessProfiles.CompleteDecision);
                ExportStatus = "正在导出公开牌局诊断…";
                export = Task.Run(() =>
                {
                    Directory.CreateDirectory(diagnosticsDirectory);
                    string file = Path.Combine(diagnosticsDirectory, $"mjcn-public-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
                    using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
                    void Write(string name, object? value)
                    {
                        using var entry = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                        var options = new JsonSerializerOptions { WriteIndented = true };
                        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                        JsonSerializer.Serialize(entry, value, options);
                    }
                    Write("manifest.json", manifest);
                    Write("public-state.json", snapshot);
                    Write("validation.json", readiness);
                    Write("evidence-summary.json", summary);
                    return file;
                });
            }
        });
    }

    private void RecordGameplayStop(Mahjong.Plugin.Dalamud.GameplayStopInfo stop)
    {
        bool completed = (tableAutomation.MatchCompleted && stop.Reason == MatchCompletedStop) ||
            (normalTaskStopReason is not null && stop.Reason == normalTaskStopReason);
        if (!completed) AlertUnexpectedStop(stop);
        if (!completed && !startingFromTableAutomation && !stop.Reason.StartsWith("SCENE_EXIT：", StringComparison.Ordinal))
            SuspendTableAutomation("打牌已停止，自动排队与进桌开打同步暂停：" + stop.Reason);
        if (RecordStopInJournal(stop)) return;
        var payload = new
        {
            SchemaVersion = 1, Kind = "GameplayStop", PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            UpstreamCommit = RuntimeIdentity.Upstream, Identity, Stop = stop, CnFullGameVerified = false,
        };
        // Serialization copies only managed public state. The bounded writer does all filesystem work off-thread.
        stopRecorder.TryRecord(JsonSerializer.Serialize(payload));
    }

    internal void ExportGameplay()
    {
        _ = Framework.RunOnFrameworkThread(() =>
        {
            lock (gate)
            {
                if (disposed || export is not null) return;
                var runtime = PlayRuntime;
                bool active = runtime is not null && runtime.Mode != Mahjong.Plugin.Dalamud.PlayMode.Off;
                // Public structured state only; no names, chat, addon memory, addresses or opponent concealed tiles.
                var report = new
                {
                    SchemaVersion = 1, Kind = "ExperimentalUpstreamGameplay",
                    PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
                    UpstreamCommit = RuntimeIdentity.Upstream, Identity,
                    ExportedUtc = DateTimeOffset.UtcNow, Mode = runtime?.Mode.ToString() ?? "Off",
                    Status = runtime?.Status ?? Status,
                    IsLiveSnapshot = active,
                    Snapshot = active ? runtime!.Aggregator.Latest : runtime?.LastStoppedSnapshot,
                    Choice = active ? runtime!.Aggregator.LastChoice : runtime?.LastStoppedChoice,
                    Scored = active ? runtime!.Aggregator.LastScored : null,
                    Error = active ? runtime!.Aggregator.LastScorerError : runtime?.LastStoppedError,
                    LastAction = active ? runtime!.AutoPlay?.LastActionDescription : runtime?.LastStoppedAction,
                    Menu = active ? runtime!.ActiveCallMenu : runtime?.LastStoppedMenu,
                    Dispatch = active ? runtime!.ActiveCallDispatch : runtime?.LastStoppedDispatch,
                    DecisionSource = TechnicalDecisionSource,
                    GlobalAi = LastGlobalAiTrace,
                    CnFullGameVerified = false,
                };
                ExportStatus = "正在导出助手快照…";
                export = Task.Run(() =>
                {
                    Directory.CreateDirectory(diagnosticsDirectory);
                    string file = Path.Combine(diagnosticsDirectory, $"mjcn-gameplay-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
                    using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
                    using var entry = zip.CreateEntry("gameplay.json", CompressionLevel.Optimal).Open();
                    DiagnosticPrivacy.Serialize(entry, report);
                    return file;
                });
            }
        });
    }

    internal void SetLowerHandCapture(bool value)
    {
        lock (gate)
        {
            if (Capturing || disposed) return;
            CaptureLowerHand = value;
        }
    }

    internal void SetPublicLayoutCapture(bool value)
    {
        lock (gate)
        {
            if (Capturing || disposed) return;
            CapturePublicLayout = value;
        }
    }

    internal void RecordLowerHandReview(long frameSequence, bool matches)
    {
        lock (gate)
        {
            if (!Capturing || !capturedLowerHand || Latest?.Sequence != frameSequence) return;
            var addon = Latest.Addons.FirstOrDefault(x => x.Name == "Emj");
            if (addon?.LowerHandReading is not { Stable: true }) return;
            var faces = addon.LowerHandFaces;
            if (faces is null || !LowerHandProfile.CheckPreview(faces, _ => true).Eligible) return;
            if (lowerHandReviews.Count == 128) lowerHandReviews.RemoveAt(0);
            var review = new HandPreviewReview(frameSequence, DateTimeOffset.UtcNow, matches, faces.ToImmutableArray());
            lowerHandReviews.Add(review);
            if (!Record("review", review)) return;
            LowerHandReviewStatus = matches ? $"已记录第 {frameSequence} 帧与本家手牌一致；这不代表后续画面也已确认。"
                : $"已记录第 {frameSequence} 帧不一致；请停止并导出，由开发者检查。";
        }
    }

    private void ExportCore()
    {
        if (disposed || export is not null) return;
        if (recorder is not null) { Stop("用户结束采集并封存整场记录。"); return; }
        if (recordingArchive is not null) return;
        if (lastArchivePath is not null) return;
        // Capture managed immutable copies on the framework/UI thread; background IO never reads game memory.
        var report = new
        {
            SchemaVersion = 2,
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            UpstreamCommit = RuntimeIdentity.Upstream,
            ExportedUtc = DateTimeOffset.UtcNow,
            Identity = capturedIdentity ?? Identity,
            IdentityScope = "Identity / LowerHandCaptureEnabled / LowerHandUldHash belong to the retained capture. LatestAttempt records the most recent start attempt/options and may differ after a rejected start.",
            LatestAttempt = new { Identity, RequestedLowerHandCapture = CaptureLowerHand,
                RequestedPublicLayoutCapture = CapturePublicLayout, LowerHandUldHash, Status },
            Status,
            DiagnosticScope = "手动开始才采集。下方牌面范围须通过固定 ULD、父链、模板和正面壳检查，仅读所选资源 IconId/TexPathHash 标量；公开布局范围只读限定节点的变换、父链和当前壳矩形，不读新增区域牌面资源。不读其他三家暗牌、文本、AtkValue、资源路径字符串或原始内存，无账号、聊天、地址或自动上传。实际范围以本会话开关为准。",
            LowerHandCaptureEnabled = capturedLowerHand,
            PublicLayoutCaptureEnabled = capturedPublicLayout,
            PublicLayoutContract = "Opt-in public layout metadata only: audited lower-meld/river/dora-display paths, transforms, numeric parent IDs/types, selected shell part rectangles. No face image headers, face resource IDs/hashes, asset dereferences or text reads in these new regions. Does not identify melds, rivers or dora semantically.",
            LowerHandUldHash = capturedUldHash,
            LowerHandReviews = lowerHandReviews.ToArray(),
            LowerHandReadingContract = "LowerHandReading decodes only audited visible lower-face icons with matching resource hashes after two independent identical samples. It is a partial image observation, not a complete hand, drawn tile, active turn or engine snapshot. Any rejection clears its tiles. Stable images may also appear during settlement.",
            DroppedOldFrames = frames.DroppedDetailedFrames,
            TotalDetailedNodes = frames.TotalDetailedNodes,
            TotalPublicLayoutNodes = frames.TotalPublicLayoutNodes,
            FirstTableFrameSequence = frames.FirstTableFrame?.Sequence,
            TextLengthContract = "NodeText.BufUsed - 1; invalid values stay null. Schema 1 StringLength readings cannot prove displayed text was empty.",
            Parsed = new UnparsedFields(),
            Suggestions = "This report describes the separate strict diagnostic reader; experimental upstream suggestions are exported using the gameplay snapshot button.",
            Automation = "Diagnostic collection does not enable input. Experimental gameplay mode is selected separately by the user.",
            SessionStatus = Session.Status,
            ActionCapabilities = Actions.Capabilities,
            Frames = frames.DetailedFrames,
            Timeline = frames.Timeline,
            Lifecycle = events.ToArray(),
        };
        ExportStatus = "正在导出…";
        export = Task.Run(() =>
        {
            Directory.CreateDirectory(diagnosticsDirectory);
            string file = Path.Combine(diagnosticsDirectory, $"mjcn-diagnostic-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
            using (var entry = zip.CreateEntry("report.json", CompressionLevel.Optimal).Open())
                DiagnosticPrivacy.Serialize(entry, report);
            using (var writer = new StreamWriter(zip.CreateEntry("说明.txt").Open(), Encoding.UTF8))
                writer.Write("此包是国服 UI 诊断，不能证明完整读牌或操作正确。请回传整个 ZIP。Schema 2 保留独立 Timeline 及首个牌桌详细帧；Frames 为保留的历史结构。显式开启下方牌面核对后，LowerHandFaces 包含下方候选及拒绝原因；仅通过版本/资源/可见正面壳检查者可含 IconId 和 FacePathHash，未知图标仍拒绝预览。FacePathHash 为4字节资源哈希，null表示未读取或不可用，与数值0不同。LowerHandReviews 为用户核对标记；不含其他三家暗牌、文本、AtkValue、原始内存或地址。不需要用户研究内存。源码和依据见 docs/cn。\n");
            return file;
        });
    }

    private void Draw() => windows.Draw();
    private void Open() => window.IsOpen = true;

    public void Dispose() { lock (gate) DisposeCore(); }

    private void DisposeCore()
    {
        if (disposed) return;
        disposed = true;
        historyCancellation?.Cancel();
        communityLifetime?.Cancel();
        communityRequest?.Cancel();
        communityHttp?.Dispose();
        engineMaintenanceCancellation.Cancel();
        mortalSession?.Dispose(); mortalSession = null;
        gameplayAllowed = false;
        readObservationAllowed = false;
        Interlocked.Increment(ref modeRequestVersion);
        StopCore("插件已卸载。");
        aiProbe?.Dispose();
        if (PlayRuntime is not null)
        {
            PlayRuntime.SnapshotObserved -= RecordRuntimeSnapshot;
            PlayRuntime.ObservationInvalidated -= RecordObservationGap;
            PlayRuntime.ActionSubmissionRecorded -= RecordActionSubmission;
            PlayRuntime.ReviewRecorded -= RecordReviewEvent;
            PlayRuntime.DecisionReviewIdProvider = null;
        }
        PlayRuntime?.Dispose();
        var stoppedRuntime = PlayRuntime;
        PlayRuntime = null;
        if (stoppedRuntime is not null)
            _ = FinishGameplayStopRecordingAsync(stoppedRuntime, stopRecorder, RecordGameplayStop);
        else
            _ = stopRecorder.CompleteAsync();
        Framework.Update -= Update;
        DutyState.DutyCompleted -= OnAutomationDutyCompleted;
        Interface.UiBuilder.Draw -= Draw;
        Interface.UiBuilder.OpenMainUi -= Open;
        Interface.UiBuilder.OpenConfigUi -= Open;
        Lifecycle.UnregisterListener(AddonEvent.PostSetup, OnLifecycle);
        Lifecycle.UnregisterListener(AddonEvent.PreFinalize, OnLifecycle);
        Commands.RemoveHandler("/mjcn");
        windows.RemoveAllWindows();
        frames.Clear(); events.Clear();
        lowerHandReviews.Clear();
        // An in-flight local export may finish, but it holds only managed copies and can perform no game action.
    }

    internal static async Task FinishGameplayStopRecordingAsync(Mahjong.Plugin.Dalamud.Plugin runtime,
        GameplayStopRecorder recorder, Action<Mahjong.Plugin.Dalamud.GameplayStopInfo> sink)
    {
        // Off-thread Dispose may still have native teardown queued. Keep the sink alive
        // until that teardown emits the copied stop report, then drain only managed I/O.
        // Never synchronously wait here: the framework may be waiting for Dispose to return.
        try { await runtime.StopCompletion.ConfigureAwait(false); }
        finally
        {
            runtime.Stopped -= sink;
            await recorder.CompleteAsync().ConfigureAwait(false);
        }
    }
}

internal sealed record HandPreviewReview(long Sequence, DateTimeOffset Utc, bool UserConfirmedMatch,
    IReadOnlyList<HandFaceCandidate> Candidates);
