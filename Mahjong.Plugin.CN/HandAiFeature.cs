namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private Mahjong.Cn.Engines.MortalEngineSession? mortalSession;
    internal string? MortalWarmupStatus => MortalSelected ? mortalSession?.Status : null;

    private void PrepareSelectedMortal()
    {
        if (!ExperimentalHandAiEnabled || !TestAccessUnlocked) return;
        if (!MortalSelected)
        {
            mortalSession?.Dispose(); mortalSession = null;
            return;
        }
        string directory = DefaultGlobalEngineDirectory;
        if (mortalSession is { } current && current.Directory == directory &&
            !current.Preparation.IsFaulted && !current.Preparation.IsCanceled) return;
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

    // Stored models and leases never select a backend or authorize a new session.
    private int experimentalHandAiEnabled;
    private string experimentalHandAiStatus = "测试版未启用。";

    internal bool ExperimentalHandAiEnabled => Volatile.Read(ref experimentalHandAiEnabled) != 0;
    internal string DecisionSourceLabel => ExperimentalHandAiEnabled ? "测试版" : "标准求解器";
    internal string TechnicalDecisionSource => ExperimentalHandAiEnabled ? GlobalBackendLabel : "upstream-efficiency";

    internal Mahjong.Policy.Abstractions.IPolicy CreateDecisionPolicy() => CreateDecisionPolicy(CreateGlobalPolicy);
    internal Mahjong.Policy.Abstractions.IPolicy CreateDecisionPolicy(Func<Mahjong.Policy.Abstractions.IPolicy> betaFactory)
    {
        if (!ExperimentalHandAiEnabled) return new Mahjong.Policy.Efficiency.EfficiencyPolicy(new Mahjong.Rules.Rulesets.DomanRuleSet());
        if (!TestAccessUnlocked) throw new InvalidOperationException("BETA_ACCESS_REQUIRED");
        return betaFactory();
    }
    private Experimental.AkochanGlobalPolicy CreateGlobalPolicy()
    {
        if (!ExperimentalHandAiEnabled || !TestAccessUnlocked) throw new InvalidOperationException("BETA_ACCESS_REQUIRED");
        int generation = Volatile.Read(ref betaGeneration);
        bool AccessValid() => !disposed && ExperimentalHandAiEnabled && TestAccessUnlocked && generation == Volatile.Read(ref betaGeneration);
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
            if (ExperimentalHandAiEnabled == enabled) return;
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
            if (!enabled) { mortalSession?.Dispose(); mortalSession = null; }
            Volatile.Write(ref experimentalHandAiStatus, enabled
                ? $"已选择 {GlobalBackendLabel}；选择手动或自动后，等待本次引擎结果。"
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
