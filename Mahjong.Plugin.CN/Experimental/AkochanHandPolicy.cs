using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.Engines;
using Mahjong.Policy.Efficiency;
using Mahjong.Rules.Rulesets;

namespace Mahjong.Plugin.CN.Experimental;

internal sealed record HandAiObservation(string ContextKey, DateTimeOffset Utc,
    bool Stable, ImmutableArray<VisibleTile> Tiles);

/// <summary>
/// Nonblocking adapter for an explicitly synthetic hand experiment. Never treats the
/// hypothetical engine replay as observed game history or dispatches its non-discard actions.
/// All Choose calls and public observations stay on the framework thread; the worker owns
/// only immutable tiles. Completion cannot send game input.
/// </summary>
internal sealed class AkochanHandPolicy : IRefreshablePolicy, IDisposable
{
    private readonly IPolicy fallback;
    private readonly Func<bool> enabled;
    private readonly Func<HandAiObservation?> observe;
    private readonly Action<string> report;
    private readonly Func<AkochanHandScenario, CancellationToken, Task<AkochanDecision>> analyze;
    private readonly Func<DateTimeOffset> clock;
    private CancellationTokenSource? cancellation;
    private Task<AkochanDecision>? pending;
    private AkochanHandScenario? scenario;
    private string? key;
    private ActionChoice? completed;
    private long lastChooseTicks;
    private DateTimeOffset requestStarted;
    private bool disposed;
    internal Task? PendingWork => pending;
    public bool RequiresRefresh => !disposed && clock().UtcTicks - lastChooseTicks >= TimeSpan.TicksPerMillisecond * 100;

    public AkochanHandPolicy(string engineDirectory, Func<bool> enabled,
        Func<HandAiObservation?> observe, Action<string> report)
        : this(new EfficiencyPolicy(new DomanRuleSet()), enabled, observe, report, async (input, ct) =>
        {
            var installation = await AkochanInstallation.LoadAsync(engineDirectory, ct).ConfigureAwait(false);
            return await new AkochanDecisionEngine().AnalyzeOfflineAsync(installation,
                input.ScenarioReplay, TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        }) { }

    internal AkochanHandPolicy(IPolicy fallback, Func<bool> enabled, Func<HandAiObservation?> observe,
        Action<string> report, Func<AkochanHandScenario, CancellationToken, Task<AkochanDecision>> analyze,
        Func<DateTimeOffset>? clock = null)
    {
        this.fallback = fallback; this.enabled = enabled; this.observe = observe;
        this.report = report; this.analyze = analyze; this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public ActionChoice Choose(StateSnapshot state)
    {
        lastChooseTicks = clock().UtcTicks;
        if (disposed) return ActionChoice.Pass("AKOCHAN_STOPPED：已停止。");
        var original = fallback.Choose(state);
        if (!enabled()) { Invalidate(); return original; }
        // Wins, riichi, calls, kan and open-hand discards retain the existing real-game policy.
        if (original.Kind != ActionKind.Discard || state.OurRiichi || state.OurMelds.Count != 0 ||
            state.Hand.Count != 14 || !state.Legal.Can(ActionFlags.Discard))
        {
            Invalidate();
            report("当前动作使用原策略；手牌 AI 仅处理门清14张的普通弃牌。");
            return original;
        }
        var visible = observe();
        var age = visible is null ? TimeSpan.MaxValue : clock() - visible.Utc;
        if (visible is not { Stable: true, Tiles.Length: 14 } || age < TimeSpan.Zero || age > TimeSpan.FromSeconds(1) ||
            !visible.Tiles.Select(x => x.Id).Order().SequenceEqual(state.Hand.Select(x => (int)x.Id).Order()))
        {
            Invalidate();
            report("等待当前手牌稳定并与运行时一致；暂停弃牌建议与输入。");
            return ActionChoice.Pass("AKOCHAN_PENDING: 等待新鲜、一致的可见手牌。");
        }
        string requestKey = visible.ContextKey + "|" + state.WallRemaining + "|" + state.TurnIndex + "|" +
            state.AddonStateCode + "|" + (int)state.Legal.Flags + "|" +
            string.Join(',', visible.Tiles.Select(MjaiTileCodec.Encode)) + "|" +
            string.Join(',', state.Legal.DiscardableTiles.Select(t => t.Id));
        if (key != requestKey)
        {
            Invalidate();
            key = requestKey;
            try
            {
                scenario = AkochanHandScenario.Create(visible.Tiles);
                requestStarted = clock();
                cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var input = scenario;
                var token = cancellation.Token;
                pending = Task.Run(() => analyze(input, token), token);
                // Observe faults even when a later frame retires this request before completion.
                _ = pending.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                return completed = Fallback(original, ErrorCode(ex));
            }
        }
        if (completed is not null) return completed;
        if (clock() - requestStarted >= TimeSpan.FromSeconds(8))
        {
            cancellation?.Cancel();
            return completed = Fallback(original, "AKOCHAN_TIMEOUT");
        }
        if (pending is null || !pending.IsCompleted)
        {
            report("真实 akochan 正在计算；仅手牌分析，未使用完整牌局历史。");
            return ActionChoice.Pass("AKOCHAN_PENDING: 正在计算手牌弃牌；尚未发送操作。");
        }
        try
        {
            var decision = pending.GetAwaiter().GetResult(); // Already complete; never blocks framework.
            if (!enabled()) { Invalidate(); return original; }
            if (!scenario!.TryGetDiscard(decision, out var tile))
                return completed = Fallback(original, "AKOCHAN_NO_HAND_DISCARD");
            if (state.Legal.DiscardableTiles.Count > 0 && !state.Legal.DiscardableTiles.Any(t => t.Id == tile.Id))
                return completed = Fallback(original, "AKOCHAN_DISCARD_NOT_LEGAL");
            // The legacy dispatcher selects by kind, not physical red identity. Never claim it
            // will honor an engine choice between a red and ordinary five of the same kind.
            if (visible.Tiles.Any(t => t.Id == tile.Id && t.Red != tile.Red))
                return completed = Fallback(original, "AKOCHAN_RED_SLOT_AMBIGUOUS");
            report($"akochan 建议打出{tile.ChineseName}（{decision.StartToResponseMilliseconds:F0}毫秒）；仅手牌分析。");
            return completed = ActionChoice.Discard(new Tile((byte)tile.Id),
                "AKOCHAN_HAND_ONLY：真实 akochan 的实验手牌弃牌。" + string.Join(" ", scenario.Assumptions) +
                " 原生引擎未返回候选分数或自然语言理由；页面进张/备选由原牌效引擎另算。");
        }
        catch (Exception ex) { return completed = Fallback(original, ErrorCode(ex)); }
    }

    private ActionChoice Fallback(ActionChoice original, string code)
    {
        report("手牌 AI 回退原策略：" + code);
        return original with { Reasoning = "AKOCHAN_FALLBACK：" + code + "；" + original.Reasoning };
    }

    private static string ErrorCode(Exception ex) => ex switch
    {
        OperationCanceledException => "AKOCHAN_TIMEOUT_OR_CANCELLED",
        EngineProcessException process => "AKOCHAN_PROCESS_" + process.Code,
        AkochanException ako => ako.Message,
        _ => "AKOCHAN_" + ex.GetType().Name,
    };

    public void Invalidate()
    {
        cancellation?.Cancel();
        cancellation?.Dispose(); cancellation = null;
        key = null; pending = null; scenario = null; completed = null;
    }
    public void Dispose() { disposed = true; Invalidate(); }
}
