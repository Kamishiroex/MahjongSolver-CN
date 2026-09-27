using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;

namespace Mahjong.Cn.Engines;

public sealed class MortalGlobalEngine : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private EngineProcessHost? host;
    private readonly object disposeGate = new();
    private Task? disposal;
    private string? hostDirectory;
    public int? ProcessId => host?.ProcessId;
    private static readonly string SessionScript = ReadSessionScript();

    private static string ReadSessionScript()
    {
        using var stream = typeof(MortalGlobalEngine).Assembly.GetManifestResourceStream("mortal-session.py")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task PrepareAsync(MortalInstallation install, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try { await EnsureReadyAsync(install).ConfigureAwait(false); linked.Token.ThrowIfCancellationRequested(); }
        finally { serial.Release(); }
    }

    private async Task EnsureReadyAsync(MortalInstallation install)
    {
        if (host is { State: EngineProcessState.Running } && hostDirectory == install.Directory) return;
        if (host is not null) { await host.DisposeAsync().ConfigureAwait(false); host = null; }
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(60));
        var spec = new EngineProcessSpec(Path.Combine(install.Directory, "python", "python.exe"), install.Directory,
            ["-B", "-c", SessionScript, install.Directory]);
        host = await EngineProcessHost.StartAsync(spec, startup.Token).ConfigureAwait(false);
        try
        {
            string id = Guid.NewGuid().ToString("N");
            string response = await host.ExchangeAsync(JsonSerializer.Serialize(new { type = "warmup", id }),
                TimeSpan.FromSeconds(60), startup.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "ready" || root.GetProperty("id").GetString() != id ||
                root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("engine_commit").GetString() != MortalInstallation.Commit)
                throw new AkochanException("MORTAL_WARMUP_IDENTITY");
            hostDirectory = install.Directory;
        }
        catch { await host.DisposeAsync().ConfigureAwait(false); host = null; throw; }
    }
    public async Task<AkochanGlobalDecision> AnalyzeAsync(MortalInstallation install, AkochanGlobalSnapshot input,
        CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = linked.Token;
        await serial.WaitAsync(token).ConfigureAwait(false);
        try { return await AnalyzeCore(install, input, token).ConfigureAwait(false); }
        finally { serial.Release(); }
    }

    private async Task<AkochanGlobalDecision> AnalyzeCore(MortalInstallation install, AkochanGlobalSnapshot input, CancellationToken token)
    {
        string hash = AkochanGlobalEngine.ComputeInputSha256(input); // Reuses public inventory validation.
        var watch = Stopwatch.StartNew();
        await EnsureReadyAsync(install).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        string payload = JsonSerializer.Serialize(new { input_sha256 = hash, snapshot = input });
        // A stale turn cancels its consumer, not the warm model. Drain the bounded
        // exchange before the next request; never misattribute or play an old result.
        string response = await host!.ExchangeAsync(payload, TimeSpan.FromSeconds(9), lifetime.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        if (root.GetProperty("input_sha256").GetString() != hash || root.GetProperty("schema").GetInt32() != 1)
            throw new AkochanException("MORTAL_RESPONSE_MISMATCH");
        if (root.TryGetProperty("error", out var error)) throw new AkochanException("MORTAL_INPUT:" + error.GetString());
        if (root.GetProperty("engine_commit").GetString() != MortalInstallation.Commit)
            throw new AkochanException("MORTAL_ENGINE_IDENTITY");
        var applied = root.GetProperty("applied");
        if (install.HasRuleAwareFeatures) ValidateAppliedContext(applied, input);
        var counts = Enumerable.Range(0, 34).Select(i => input.Hand.Count(t => t.Id == i));
        if (!applied.GetProperty("hand").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(counts) ||
            !applied.GetProperty("scores").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(input.Players.Select(p => p.Score)) ||
            !applied.GetProperty("river_counts").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(input.Players.Select(p => p.River.Length)) ||
            !applied.GetProperty("meld_counts").EnumerateArray().Select(x => x.GetInt32()).SequenceEqual(input.Players.Select(p => p.Melds.Length)) ||
            applied.GetProperty("wall").GetInt32() != input.WallRemaining)
            throw new AkochanException("MORTAL_APPLIED_STATE_MISMATCH");
        var result = ImmutableArray.CreateBuilder<AkochanGlobalCandidate>();
        var candidates = root.GetProperty("candidates");
        if (candidates.GetArrayLength() is 0 or > 256) throw new AkochanException("MORTAL_CANDIDATE_COUNT");
        foreach (var c in candidates.EnumerateArray())
        {
            double score = c.GetProperty("score").GetDouble();
            if (!double.IsFinite(score)) throw new AkochanException("MORTAL_NONFINITE_SCORE");
            result.Add(new(AkochanReplay.ParseGlobalMoves(c.GetProperty("moves").GetRawText(), input,
                mortalSingleAction: true), score));
        }
        var assumptions = root.GetProperty("assumptions").EnumerateArray().Select(x => x.GetString()!).ToImmutableArray();
        if (!install.HasRuleAwareFeatures)
            assumptions = assumptions.Add("当前个人运行包仍为旧版公开输入桥；尚未应用赛程进度、同分顺位和历史特征修正。");
        return new(result.ToImmutable(), hash, assumptions,
            MortalInstallation.Commit, watch.Elapsed.TotalMilliseconds, applied.GetRawText())
            { EngineName = "凡夫 Mortal V4 582500", HistoryComplete = false };
    }

    public static void ValidateAppliedContext(JsonElement applied, AkochanGlobalSnapshot input)
    {
        int first = input.MatchFirstRound;
        double progress = Math.Clamp(first + input.RoundWind * 4 + input.HandNumber - 1, 0, 7) / 7.0;
        bool allLast = first + input.RoundWind * 4 + input.HandNumber >= 8;
        int ourInitialSeat = (input.HandNumber - 1 + 4 - input.DealerPlayerId) % 4;
        int rank = input.Players.Count(p => p.Score > input.Players[0].Score ||
            p.Score == input.Players[0].Score && (p.PlayerId + ourInitialSeat) % 4 < ourInitialSeat);
        if (!applied.TryGetProperty("feature_schema", out var schema) || schema.GetInt32() != 2 ||
            !applied.TryGetProperty("match_context", out var context) ||
            context.GetProperty("actual_round_wind").GetInt32() != input.RoundWind ||
            context.GetProperty("actual_hand_number").GetInt32() != input.HandNumber ||
            Math.Abs(context.GetProperty("progress").GetDouble() - progress) > 1e-6 ||
            context.GetProperty("progress_row").GetInt32() != 27 ||
            applied.GetProperty("round_wind").GetInt32() != input.RoundWind ||
            applied.GetProperty("all_last").GetBoolean() != allLast || applied.GetProperty("rank").GetInt32() != rank)
            throw new AkochanException("MORTAL_APPLIED_RULES_MISMATCH");
        if (input.MatchRules is { MatchType: not null } rules &&
            (!context.GetProperty("confirmed_length").GetBoolean() ||
             context.GetProperty("match_type").GetString() != rules.MatchType ||
             context.GetProperty("scheduled_hands").GetInt32() != rules.ScheduledHands))
            throw new AkochanException("MORTAL_APPLIED_RULES_MISMATCH");
    }

    public void Dispose() => _ = DisposeAsync();
    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new(disposal ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel();
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            if (host is not null) await host.DisposeAsync().ConfigureAwait(false);
            host = null;
        }
        catch (EngineProcessException) { /* Never resume this disposed policy. */ }
        finally { serial.Release(); }
    }
}
