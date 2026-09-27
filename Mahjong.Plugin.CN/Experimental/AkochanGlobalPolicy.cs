using System.Collections.Immutable;
using Mahjong.Cn.Engines;

namespace Mahjong.Plugin.CN.Experimental;

internal sealed record GlobalAiTrace(string Phase, string? InputSha256,
    AkochanGlobalSnapshot? Input, AkochanGlobalDecision? Decision, ActionChoice? Choice, string? Error);

/// <summary>
/// Real akochan decisions from the current public table. No legacy policy picks wins,
/// calls or discards before this engine. Game input remains owned by the guarded loop.
/// Partial-history assumptions are in the request and journal, never promoted to facts.
/// </summary>
internal sealed class AkochanGlobalPolicy : IIndependentPublicStatePolicy, IDisposable
{
    private readonly Func<StateSnapshot, AkochanGlobalProjection> project;
    private readonly Action<string> report;
    private readonly Action<GlobalAiTrace> trace;
    private readonly Func<AkochanGlobalSnapshot, CancellationToken, Task<AkochanGlobalDecision>> analyze;
    private readonly Func<DateTimeOffset> clock;
    private readonly string expectedCommit;
    private readonly string engineLabel;
    private readonly Action? cleanup;
    private readonly Task? preparation;
    private readonly Func<string>? preparationStatus;
    private readonly Func<bool>? accessValid;
    private int accessRevoked;
    private bool HasAccess()
    {
        if (Volatile.Read(ref accessRevoked) != 0) return false;
        if (accessValid?.Invoke() != false) return true;
        Interlocked.Exchange(ref accessRevoked, 1);
        return false; // Renewal cannot revive this policy or its late worker results.
    }
    private CancellationTokenSource? cancellation;
    private Task<AkochanGlobalDecision>? pending;
    private string? key;
    private string? inputHash;
    private ActionChoice? completed;
    private DateTimeOffset requestStarted;
    private long lastChooseTicks;
    private bool disposed;
    private string? lastErrorCode;
    internal Task? PendingWork => pending;
    public bool RequiresRefresh => !disposed && clock().UtcTicks - lastChooseTicks >= TimeSpan.TicksPerMillisecond * 100;

    public AkochanGlobalPolicy(string directory, Func<StateSnapshot, AkochanGlobalProjection> project,
        Action<string> report, Action<GlobalAiTrace> trace, Func<bool>? accessValid = null)
        : this(project, report, trace, async (snapshot, token) =>
        {
            var installation = await AkochanInstallation.LoadAsync(directory, token).ConfigureAwait(false);
            return await new AkochanGlobalEngine().AnalyzeAsync(installation, snapshot,
                TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        }, accessValid: accessValid) { }

    internal AkochanGlobalPolicy(Func<StateSnapshot, AkochanGlobalProjection> project, Action<string> report,
        Action<GlobalAiTrace> trace,
        Func<AkochanGlobalSnapshot, CancellationToken, Task<AkochanGlobalDecision>> analyze,
        Func<DateTimeOffset>? clock = null, string? expectedCommit = null, string engineLabel = "akochan v5", Action? cleanup = null,
        Task? preparation = null, Func<string>? preparationStatus = null, Func<bool>? accessValid = null)
    {
        this.project = project; this.report = report; this.trace = trace; this.analyze = analyze;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.expectedCommit = expectedCommit ?? AkochanInstallation.ExpectedCommit; this.engineLabel = engineLabel;
        this.cleanup = cleanup;
        this.accessValid = accessValid;
        this.preparation = preparation; this.preparationStatus = preparationStatus;
    }

    public ActionChoice Choose(StateSnapshot state)
    {
        if (!HasAccess()) { Invalidate(); return ActionChoice.Pass("AKOCHAN_BLOCKED: BETA_ACCESS_REVOKED"); }
        lastChooseTicks = clock().UtcTicks;
        if (disposed) return ActionChoice.Pass("AKOCHAN_BLOCKED: 全局 AI 已停止。");
        if (preparation is not null && !preparation.IsCompletedSuccessfully)
        {
            Invalidate();
            if (preparation.IsCompleted)
            {
                try { preparation.GetAwaiter().GetResult(); }
                catch (Exception ex) { return Block(ErrorCode(ex), null); }
            }
            report(preparationStatus?.Invoke() ?? "凡夫正在预热，尚未开始当前牌局推理。");
            return ActionChoice.Pass("AKOCHAN_PENDING: 模型预热中，请等待预热完成后开始对局。");
        }
        if (state.Legal.Flags == ActionFlags.None) { Invalidate(); return ActionChoice.Pass("等待合法操作窗口。"); }
        var projection = project(state);
        if (projection.Snapshot is not { } current)
        {
            Invalidate();
            string code = projection.Error ?? "GLOBAL_INPUT_UNAVAILABLE";
            string detail = projection.ErrorDetail is { Length: > 0 } reason ? "；" + reason : "";
            report("全局 AI 等待读取：" + code + detail);
            return ActionChoice.Pass("AKOCHAN_PENDING: " + code + detail);
        }
        var age = clock() - current.Utc;
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(1) ||
            !current.Hand.Select(t => t.Id).Order().SequenceEqual(state.Hand.Select(t => (int)t.Id).Order()))
        {
            Invalidate();
            report("全局 AI 等待新鲜、一致的当前手牌；不会沿用旧建议。");
            return ActionChoice.Pass("AKOCHAN_PENDING: GLOBAL_INPUT_STALE_OR_HAND_CHANGED");
        }
        string hash;
        // The captured post-riichi draw still advertises Riichi|Pass while the game
        // advances its forced draw/discard. There is no legal manual discard here.
        // Await a fresh window, retaining wins/kans and ordinary declaration decisions.
        if (current.Players.Length == 4 && current.Players[0].RiichiDeclared &&
            current.Trigger is { Type: "tsumo", Actor: 0 } &&
            current.LegalActions == (ActionFlags.Riichi | ActionFlags.Pass) &&
            state.Legal.Flags == current.LegalActions)
        {
            Invalidate();
            report("已立直：等待游戏摸切或新的合法操作窗口。");
            return ActionChoice.Pass("AKOCHAN_PENDING: GLOBAL_WAIT_RIICHI_DRAW");
        }
        try { hash = AkochanGlobalEngine.ComputeInputSha256(current); }
        catch (Exception ex) { Invalidate(); return Block(ErrorCode(ex), current); }
        var legalState = state with { Legal = (projection.ResponseLegal ?? state.Legal) with { Flags = current.LegalActions } };
        string nextKey = hash + "|" + state.AddonStateCode + "|" + (int)legalState.Legal.Flags + "|" +
            string.Join(',', legalState.Legal.DiscardableTiles.Select(t => t.Id)) + "|" +
            Candidates(legalState.Legal.PonCandidates) + "|" + Candidates(legalState.Legal.ChiCandidates) + "|" + Candidates(legalState.Legal.KanCandidates);
        if (key != nextKey)
        {
            Invalidate();
            key = nextKey; inputHash = hash; requestStarted = clock(); lastErrorCode = null;
            cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = cancellation.Token;
            trace(new("request", hash, current, null, null, null));
            pending = Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!HasAccess()) throw new OperationCanceledException("BETA_ACCESS_REVOKED");
                return analyze(current, token);
            }, token);
            _ = pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        if (!HasAccess()) { Invalidate(); return ActionChoice.Pass("AKOCHAN_BLOCKED: BETA_ACCESS_REVOKED"); }
        if (completed is not null) return completed;
        if (clock() - requestStarted >= TimeSpan.FromSeconds(10))
        {
            cancellation?.Cancel();
            return completed = Block("GLOBAL_AI_TIMEOUT", current);
        }
        if (pending is null || !pending.IsCompleted)
        {
            report($"{engineLabel} 计算中：牌河 {string.Join('/', current.Players.Select(p => p.River.Length))}，" +
                $"副露 {string.Join('/', current.Players.Select(p => p.Melds.Length))}。");
            return ActionChoice.Pass("AKOCHAN_PENDING: 全局 AI 正在分析公开牌局。");
        }
        try
        {
            var result = pending.GetAwaiter().GetResult(); // IsCompleted: never blocks the game thread.
            if (!HasAccess()) { Invalidate(); return ActionChoice.Pass("AKOCHAN_BLOCKED: BETA_ACCESS_REVOKED"); }
            if (result.InputSha256 != inputHash || result.EngineCommit != expectedCommit)
                return completed = Block("GLOBAL_AI_RESPONSE_IDENTITY_MISMATCH", current);
            bool winOffered = legalState.Legal.Can(ActionFlags.Ron) || legalState.Legal.Can(ActionFlags.Tsumo);
            foreach (var candidate in result.Candidates.OrderByDescending(c => c.Score))
            {
                var mapped = AkochanGlobalActionMapper.Map(candidate.Moves, legalState, current);
                if (mapped.Reasoning.StartsWith("AKOCHAN_BLOCKED:", StringComparison.Ordinal)) continue;
                // Never silently discard/pass/call over a currently legal win. Require
                // an actual native hora candidate with matching target/tile; an engine
                // disagreement is recorded, not disguised as an AI choice to pass.
                if (winOffered && mapped.Kind is not (ActionKind.Ron or ActionKind.Tsumo)) continue;
                completed = mapped with
                {
                    Reasoning = $"AKOCHAN_GLOBAL: {engineLabel}；引擎评分 {candidate.Score:F2}，" +
                        $"耗时 {result.StartToResponseMilliseconds:F0} 毫秒。" + mapped.Reasoning,
                };
                report($"{engineLabel} 已返回 {mapped.Kind}（{result.StartToResponseMilliseconds:F0}毫秒）；" +
                    "已分析四家公开牌局。");
                trace(new("decision", inputHash, current, result, completed, null));
                return completed;
            }
            return completed = Block(winOffered ? "GLOBAL_AI_WIN_OPTION_MISSING" : "GLOBAL_AI_NO_MATCHING_LEGAL_ACTION", current, result);
        }
        catch (Exception ex) { return completed = Block(ErrorCode(ex), current); }
    }

    private ActionChoice Block(string code, AkochanGlobalSnapshot? input, AkochanGlobalDecision? decision = null)
    {
        string detail = code == "GLOBAL_AI_WIN_OPTION_MISSING"
            ? "；游戏提供和牌，但引擎未返回匹配的和牌候选，已阻止放弃和其他操作。" : "；未切回上游策略。";
        report("全局 AI 已暂停：" + code + detail);
        var choice = ActionChoice.Pass("AKOCHAN_BLOCKED: " + code);
        if (lastErrorCode != code) trace(new("error", inputHash, input, decision, choice, code));
        lastErrorCode = code;
        return choice;
    }

    private static string Candidates(IReadOnlyList<MeldCandidate> candidates) => string.Join(';', candidates.Select(c =>
        $"{c.Kind}:{c.ClaimedTile.Id}:{c.FromSeat}:{string.Join(',', c.HandTiles.Select(t => t.Id))}"));
    private static string ErrorCode(Exception exception) => exception switch
    {
        OperationCanceledException => "GLOBAL_AI_TIMEOUT_OR_CANCELLED",
        AkochanException ako => ako.Code,
        AkochanProtocolException protocol => $"GLOBAL_AI_PROTOCOL_{protocol.Code}@{protocol.EventIndex}:{protocol.Position}",
        EngineProcessException process => "GLOBAL_AI_PROCESS_" + process.Code,
        _ => "GLOBAL_AI_" + exception.GetType().Name,
    };
    public void Invalidate()
    {
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        pending = null; key = inputHash = null; completed = null;
    }
    public void Dispose() { if (disposed) return; disposed = true; Invalidate(); cleanup?.Invoke(); }
}
