namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private Mahjong.Cn.Engines.MortalEngineSession? mortalSession;
    internal string? MortalWarmupStatus => MortalSelected ? mortalSession?.Status : null;
    private bool backgroundWarmupRequested;
    internal bool MortalPreparing => ExperimentalHandAiEnabled && MortalSelected && mortalSession is { Preparation.IsCompleted: false };
    internal bool MortalPrepared => mortalSession?.IsReady == true;
    internal string? EnginePreparationSummary => !ExperimentalHandAiEnabled || !MortalSelected ? null : mortalSession switch
    {
        null => "模型尚未准备；选择测试来源或开始任务后会提前加载。",
        { Preparation.IsFaulted: true } or { Preparation.IsCanceled: true } => "模型准备失败，请检查测试版技术详情；重新开始可重试。",
        { IsReady: true } ready => $"模型已就绪 · 加载与预热 {ready.WarmupMilliseconds / 1000:F1} 秒 · 暂停与换局保持常驻",
        { Preparation.IsCompletedSuccessfully: true } => "模型进程已退出；继续或重新开始时会重新准备。",
        _ => "模型准备中 · 正在校验、加载和首次推理；自动报名与入桌确认等待就绪。",
    };

    private void RequestSelectedMortalWarmup() => backgroundWarmupRequested = true;

    private void UpdateSelectedMortalWarmup()
    {
        if (!backgroundWarmupRequested || EngineMaintenanceBusy) return;
        backgroundWarmupRequested = false;
        if (disposed || !ExperimentalHandAiEnabled || !MortalSelected || !BetaRuntimeAccessValid || !SelectedEngineInstalled) return;
        PrepareSelectedMortal();
    }

    // The table coordinator cannot enqueue/accept/start a new game before preparation completes.
    private bool AwaitSelectedMortalForTable()
    {
        if (!ExperimentalHandAiEnabled || !MortalSelected) return true;
        if (mortalSession is null) { RequestSelectedMortalWarmup(); UpdateSelectedMortalWarmup(); }
        if (MortalPrepared) return true;
        if (mortalSession?.Preparation.IsCompleted == true && !tableAutomation.MatchCompleted)
        {
            PausePlay();
            Status = EnginePreparationSummary!;
            AlertUnexpectedStop(new(DateTimeOffset.UtcNow, Status, Mahjong.Plugin.Dalamud.PlayMode.Off,
                null, null, null, null, null, null, null));
        }
        return false;
    }

    private void PrepareSelectedMortal()
    {
        if (!ExperimentalHandAiEnabled || !BetaRuntimeAccessValid) return;
        if (!MortalSelected)
        {
            mortalSession?.Dispose(); mortalSession = null;
            return;
        }
        string directory = DefaultGlobalEngineDirectory;
        if (mortalSession is { } current && current.Directory == directory &&
            (!current.Preparation.IsCompleted || current.IsReady)) return;
        mortalSession?.Dispose();
        mortalSession = new(directory);
    }
    private Experimental.AkochanGlobalObservationProjector? globalAiProjector;
    private Experimental.GlobalAiTrace? lastGlobalAiTrace;
    internal Experimental.GlobalAiTrace? LastGlobalAiTrace => Volatile.Read(ref lastGlobalAiTrace);

    private Experimental.AkochanGlobalProjection ProjectGlobalAiInput(Mahjong.Core.StateSnapshot state) =>
        (globalAiProjector ??= new()).Project(state,
            PlayRuntime?.ObservationSessionId.ToString("N") + ":" + PlayRuntime?.ObservationSequence);

    private void RecordGlobalAiTrace(Experimental.GlobalAiTrace value)
    {
        Volatile.Write(ref lastGlobalAiTrace, value);
        // Preserve the exact request/result, while respecting the journal's per-line
        // bound even for long public histories and large ranked candidate lists.
        byte[] json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
        if (json.Length <= 48_000) RecordJournalEvent("global_ai_" + value.Phase, value);
        else
        {
            string traceId = Guid.NewGuid().ToString("N");
            // Even a worst-case six-character JSON escape for every Base64 character
            // stays below 128 KiB (12,000 bytes -> 16,000 chars -> <=96,000 bytes).
            const int chunkBytes = 12_000;
            int chunks = (json.Length + chunkBytes - 1) / chunkBytes;
            for (int index = 0; index < chunks; index++)
                RecordJournalEvent("global_ai_trace_chunk", new
                {
                    TraceId = traceId, value.Phase, value.InputSha256, Index = index, Count = chunks,
                    Encoding = "base64-utf8-json", TotalBytes = json.Length,
                    Data = Convert.ToBase64String(json, index * chunkBytes, Math.Min(chunkBytes, json.Length - index * chunkBytes)),
                });
        }
        if (value.Error is { } code) journal?.Error(code, new { Stage = "global-ai", value.InputSha256 });
    }

    private Experimental.HandAiObservation? ObserveHandForAi()
    {
        if (!journalActive || journalLower is null || PlayRuntime is not { } runtime) return null;
        return new(runtime.ObservationSessionId.ToString("N") + ":" + runtime.ObservationSequence,
            journalLowerUtc, journalLower.Stable,
            System.Collections.Immutable.ImmutableArray.CreateRange(journalLower.Tiles.Select(t =>
                new Mahjong.Cn.VisibleTile(t.Kind34, t.RedFive))));
    }

    // Only a previously explicit source choice is restored; models/leases alone never select it.
    private Access.SolverPreferenceStore? solverPreference;
    private void RestoreSolverPreference()
    {
        if (solverPreference?.Error is { } error) { BetaAccessStatus = error; return; }
        if (solverPreference?.PreferBeta != true) return;
        Interlocked.Exchange(ref experimentalHandAiEnabled, 1);
        // A previously selected source with a still-valid lease may prepare at login.
        // Merely validating/renewing a lease never requests preparation or starts play.
        if (TestAccessUnlocked) RequestSelectedMortalWarmup();
        BetaAccessStatus = TestAccessUnlocked ? "已沿用上次的测试版来源与模型；尚未启动。" :
            "上次选择的测试版需要重新验证，模型选择已保留；不会切换来源或开始打牌。";
    }
    private int experimentalHandAiEnabled;
    private string experimentalHandAiStatus = "测试版未启用。";

    internal bool ExperimentalHandAiEnabled => Volatile.Read(ref experimentalHandAiEnabled) != 0;
    internal string DecisionSourceLabel => ExperimentalHandAiEnabled ? "测试版" : "标准求解器";
    internal string TechnicalDecisionSource => ExperimentalHandAiEnabled ? GlobalBackendLabel : "upstream-efficiency";

    internal Mahjong.Policy.Abstractions.IPolicy CreateDecisionPolicy() => CreateDecisionPolicy(CreateGlobalPolicy);
    internal Mahjong.Policy.Abstractions.IPolicy CreateDecisionPolicy(Func<Mahjong.Policy.Abstractions.IPolicy> betaFactory)
    {
        if (!ExperimentalHandAiEnabled) return new Mahjong.Policy.Efficiency.EfficiencyPolicy(new Mahjong.Rules.Rulesets.DomanRuleSet());
        if (!BetaRuntimeAccessValid) throw new InvalidOperationException("BETA_ACCESS_REQUIRED");
        return betaFactory();
    }
    private Experimental.AkochanGlobalPolicy CreateGlobalPolicy()
    {
        if (!ExperimentalHandAiEnabled || !BetaRuntimeAccessValid) throw new InvalidOperationException("BETA_ACCESS_REQUIRED");
        int generation = Volatile.Read(ref betaGeneration);
        bool AccessValid() => !disposed && ExperimentalHandAiEnabled && BetaRuntimeAccessValid && generation == Volatile.Read(ref betaGeneration);
        if (!MortalSelected) return new(DefaultGlobalEngineDirectory, ProjectGlobalAiInput,
            ReportExperimentalHandAiStatus, RecordGlobalAiTrace, AccessValid);
        PrepareSelectedMortal();
        var session = mortalSession!;
        return new(ProjectGlobalAiInput, ReportExperimentalHandAiStatus, RecordGlobalAiTrace,
            session.AnalyzeAsync,
            expectedCommit: Mahjong.Cn.Engines.MortalInstallation.Commit, engineLabel: GlobalBackendLabel,
            preparation: session.Preparation, preparationStatus: () => session.Status, accessValid: AccessValid);
    }

    internal string ExperimentalHandAiStatus => ExperimentalHandAiEnabled
        ? Volatile.Read(ref experimentalHandAiStatus)
        : "实验 AI 已关闭；当前使用原牌效策略。";

    internal void SetExperimentalHandAiEnabled(bool enabled)
    {
        lock (gate)
        {
            if (disposed) return;
            if (enabled && !RequireTestAccessCore()) return;
            if (ExperimentalHandAiEnabled == enabled)
            {
                if (solverPreference?.Save(enabled) == false) BetaAccessStatus = solverPreference.Error!;
                if (enabled) RequestSelectedMortalWarmup();
                return;
            }
            Interlocked.Increment(ref betaGeneration);
            betaExpiryHandled = false;
            SuspendTableAutomation("决策来源已切换，请重新启动自动排队 / 进桌开打。");
            // Choosing a source does not arm play. Block native input and invalidate
            // already-queued activations before publishing the new selection.
            gameplayAllowed = false;
            Interlocked.Increment(ref modeRequestVersion);
            string selected = enabled ? "测试版" : "标准求解器";
            string reason = $"决策来源已切换为{selected}；打牌已暂停，请继续原任务或明确开始新任务。";
            PlayRuntime?.PauseAutomation(reason);
            Interlocked.Exchange(ref experimentalHandAiEnabled, enabled ? 1 : 0);
            if (solverPreference?.Save(enabled) == false) BetaAccessStatus = solverPreference.Error!;
            if (!enabled) { backgroundWarmupRequested = false; mortalSession?.Dispose(); mortalSession = null; }
            else RequestSelectedMortalWarmup();
            Volatile.Write(ref experimentalHandAiStatus, enabled
                ? $"已选择 {GlobalBackendLabel}；将在后台提前预热，尚未开始打牌或排队。"
                : "已选择上游牌效；本模式不调用 akochan。");
            RecordJournalEvent("decision_source_selected", new
            {
                Source = enabled ? MortalSelected ? "experimental-mortal-global" : "experimental-akochan-global" : "upstream-efficiency",
                AutomaticEnabled = false,
                ObservationContinues = PlayRuntime?.IsObservingPaused == true,
            });
            Status = reason;
        }
    }

    /// <summary>The actual policy reports engine use, waiting, or fallback; the UI never infers engine success.</summary>
    internal void ReportExperimentalHandAiStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return;
        if (Volatile.Read(ref experimentalHandAiStatus) == status) return;
        Volatile.Write(ref experimentalHandAiStatus, status);
        RecordJournalEvent("global_ai_status", new { Status = status, Scope = "current-public-table", FullHistoryVerified = false });
    }
}
