using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game;
using Mahjong.Policy.Abstractions;
using RuntimePlugin = Mahjong.Plugin.Dalamud.Plugin;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Exercises the real dispatch entry points with no native client or addon memory.</summary>
public sealed class AutoPlayPendingPolicyTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DispatchContext Context = new(6, 14);
    private static readonly ActionChoice Pending = ActionChoice.Pass("AKOCHAN_PENDING: synthetic asynchronous calculation");
    private static StateSnapshot Snapshot => StateSnapshot.Empty with
    {
        Hand = Tiles.Parse("123456789m12344p"),
        AddonStateCode = 6,
        Legal = new(ActionFlags.Discard | ActionFlags.Riichi | ActionFlags.Pass, [], [], [], []),
    };

    [Fact]
    public void Captured_closed_riichi_menu_continues_with_latched_eight_sou_and_only_once()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "riichi-menu-closed-20260926.json")));
        var root = fixture.RootElement;
        var after = root.GetProperty("AfterRuntime");
        var snapshot = Snapshot with
        {
            Hand = after.GetProperty("HandIds").EnumerateArray().Select(x => Tile.FromId(x.GetInt32())).ToArray(),
            AddonStateCode = after.GetProperty("State").GetInt32(),
            Legal = new((ActionFlags)root.GetProperty("AfterLegalFlags").GetInt32(), [], [], [], []),
        };
        Assert.False(root.GetProperty("AfterMenu").GetProperty("Value").GetProperty("Visible").GetBoolean());
        Assert.Equal(ActionFlags.Discard, snapshot.Legal.Flags);
        var target = Tile.FromId(root.GetProperty("Submission").GetProperty("TileId").GetInt32());
        Assert.Equal(25, target.Id);
        using var host = new Host();
        host.SetInactiveConfiguration(); // Exercise routing without authorizing native game input.
        host.Fsm.LatchRiichiConfirm(target);
        Assert.True(host.Loop.CanContinuePendingRiichi("riichi", snapshot));
        Assert.False(host.Loop.CanContinuePendingRiichi("discard", snapshot));
        Assert.True(host.HandleRiichi(snapshot, new(snapshot.AddonStateCode, snapshot.Hand.Count), false));
        Assert.Equal(target, host.Fsm.RiichiConfirmTile);
        Assert.True(host.Fsm.NeedsRiichiDiscard);
        host.Fsm.MarkRiichiDiscardSubmitted();
        Assert.False(host.Loop.CanContinuePendingRiichi("riichi", snapshot));
        Assert.False(host.HandleRiichi(snapshot, new(snapshot.AddonStateCode, snapshot.Hand.Count), false));
        Assert.Equal(0, host.GameLookups);
        Assert.Equal(0, host.UnexpectedFrameworkCalls);
    }

    [Fact]
    public void Recorded_riichi_discard_then_new_draw_does_not_search_for_the_old_tile()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "riichi-discard-repeated-20260925.json")));
        var root = fixture.RootElement;
        using var host = new Host();
        var target = Tile.FromId(root.GetProperty("riichi-tsumogiri").GetProperty("TileId").GetInt32());
        Assert.Equal("Submitted", root.GetProperty("riichi-tsumogiri").GetProperty("Result").GetString());
        var later = root.GetProperty("laterDraw");
        var hand = later.GetProperty("handIds").EnumerateArray().Select(t => Tile.FromId(t.GetInt32())).ToArray();
        Assert.Equal(14, hand.Length);
        Assert.DoesNotContain(target, hand);
        host.Fsm.ObserveWall(root.GetProperty("afterDiscard").GetProperty("wall").GetInt32());
        host.Fsm.LatchRiichiConfirm(target);
        host.Fsm.MarkRiichiDiscardSubmitted();
        host.Fsm.ObserveWall(later.GetProperty("wall").GetInt32());
        // The stop's real menu labels are Riichi/Pass. Runtime Legal.Flags is captured
        // before menu merging; construct the corresponding merged flags explicitly.
        Assert.Equal("Riichi", root.GetProperty("stop").GetProperty("Menu").GetProperty("VisibleListLabels")[0].GetString());
        var snapshot = Snapshot with { Hand = hand, Legal = new(ActionFlags.Riichi | ActionFlags.Pass, [], [], [], []) };
        Assert.True(host.HandleRiichi(snapshot, new(15, hand.Length)));
        Assert.Contains("立直弃牌已提交", host.Loop.LastActionDescription);
        Assert.Equal(0, host.GameLookups);
        Assert.Equal(0, host.UnexpectedFrameworkCalls);
        Assert.True(host.Fsm.IsRiichiConfirmPending);
        Assert.False(host.Fsm.NeedsRiichiDiscard);

        Assert.True(root.GetProperty("winMenu").GetProperty("menu").GetProperty("Rows")[0].GetProperty("Enabled").GetBoolean());
        Assert.Equal(4, root.GetProperty("winMenu").GetProperty("menu").GetProperty("Rows")[0].GetProperty("Action").GetInt32());
        // Ron on the later public menu must return to the normal policy route.
        var ron = snapshot with { Legal = new(ActionFlags.Ron | ActionFlags.Pass, [], [], [], []) };
        Assert.False(host.HandleRiichi(ron, new(15, hand.Length)));
        Assert.Equal(0, host.GameLookups);
        Assert.Equal(0, host.UnexpectedFrameworkCalls);
    }

    [Theory]
    [InlineData(ActionFlags.Ron)]
    [InlineData(ActionFlags.Tsumo)]
    [InlineData(ActionFlags.AnKan)]
    [InlineData(ActionFlags.MinKan)]
    [InlineData(ActionFlags.ShouMinKan)]
    public void Outstanding_riichi_outcome_cannot_hide_new_win_or_kan_prompt(ActionFlags offered)
    {
        using var host = new Host();
        host.Fsm.LatchRiichiConfirm(Tile.FromId(25));
        foreach (var flags in new[] { offered | ActionFlags.Pass, offered | ActionFlags.Riichi | ActionFlags.Pass })
        {
            var snapshot = Snapshot with { Legal = new(flags, [], [], [], []) };
            Assert.True(host.Loop.CanContinuePendingRiichi("riichi", snapshot));
            Assert.True(host.Loop.CanContinuePendingRiichi("riichi-confirm", snapshot));
            Assert.False(host.HandleRiichi(snapshot, Context)); // Fresh policy handles win/kan, never the old discard.
            Assert.False(host.Loop.CanContinuePendingRiichi("riichi-tsumogiri", snapshot));
        }
        host.Fsm.MarkRiichiDiscardSubmitted();
        Assert.False(host.Loop.CanContinuePendingRiichi("riichi", Snapshot));
        Assert.Equal(0, host.GameLookups);
        Assert.Equal(0, host.UnexpectedFrameworkCalls);
    }

    [Theory]
    [InlineData(ActionFlags.Ron, false)]
    [InlineData(ActionFlags.Ron, true)]
    [InlineData(ActionFlags.Tsumo, false)]
    [InlineData(ActionFlags.Tsumo, true)]
    [InlineData(ActionFlags.AnKan, false)]
    [InlineData(ActionFlags.AnKan, true)]
    [InlineData(ActionFlags.MinKan, false)]
    [InlineData(ActionFlags.MinKan, true)]
    [InlineData(ActionFlags.ShouMinKan, false)]
    [InlineData(ActionFlags.ShouMinKan, true)]
    public void Win_and_kan_windows_bypass_riichi_discard_even_with_surviving_preview_flag(ActionFlags offered, bool submitted)
    {
        using var host = new Host();
        host.Fsm.LatchRiichiConfirm(Tile.FromId(9));
        if (submitted) host.Fsm.MarkRiichiDiscardSubmitted();
        var snapshot = Snapshot with { Legal = new(ActionFlags.Riichi | ActionFlags.Pass | offered, [], [], [], []) };
        Assert.False(host.HandleRiichi(snapshot, Context));
        Assert.Equal(0, host.GameLookups);
        Assert.Equal(0, host.UnexpectedFrameworkCalls);
        Assert.Equal(!submitted, host.Fsm.NeedsRiichiDiscard);
    }

    [Theory]
    [InlineData("DispatchPolicyChoice")]
    [InlineData("DispatchCallChoice")]
    public void Pending_result_cannot_enter_any_input_route_or_latch_an_unsent_context(string method)
    {
        using var host = new Host();
        host.Fsm.BeginDispatch(DateTime.UtcNow, Context);
        Assert.True(host.Fsm.ShouldSuppressForContext(Context, DateTime.UtcNow));

        host.Invoke(method, Snapshot, Pending);

        Assert.Equal(Pending.Reasoning, host.Loop.LastActionDescription);
        Assert.False(host.Fsm.ShouldSuppressForContext(Context, DateTime.UtcNow));
        // The original delayed callback still owns CompleteDispatch in its finally.
        // A policy result must not release an in-flight callback or queue a new one.
        Assert.True(host.Fsm.IsDispatchInFlight);
        Assert.Null(host.PendingOutcome);
        Assert.Equal(0, host.UnexpectedFrameworkCalls);
        Assert.Equal(0, host.GameLookups);
    }

    [Fact]
    public void Pending_riichi_probe_cannot_fall_through_to_real_pass()
    {
        using var host = new Host();
        var policy = new ProbePolicy();
        host.SetPolicy(policy);

        host.Invoke("DispatchCallChoice", Snapshot, ActionChoice.Pass("inspect riichi popup"));

        Assert.Equal(1, policy.Calls);
        Assert.Equal(ActionFlags.Discard | ActionFlags.Riichi, policy.LastFlags);
        Assert.Equal(Pending.Reasoning, host.Loop.LastActionDescription);
        Assert.False(host.Fsm.IsRiichiConfirmPending);
        Assert.Null(host.PendingOutcome);
        Assert.Equal(0, host.GameLookups);
        Assert.Equal(0, host.UnexpectedFrameworkCalls);
    }

    [Fact]
    public void Global_ai_none_does_not_run_legacy_riichi_probe_or_become_confirmation()
    {
        using var host = new Host();
        var policy = new ProbePolicy();
        host.SetPolicy(policy);
        host.Fsm.LatchRiichiConfirm(Tile.FromId(1));
        object?[] arguments = [Snapshot, ActionChoice.Pass("AKOCHAN_GLOBAL: native none"), null, null, false];
        bool accept = (bool)typeof(AutoPlayLoop).GetMethod("ResolveRiichiPopupAcceptance", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(host.Loop, arguments)!;
        Assert.False(accept);
        Assert.Equal(0, policy.Calls);
        Assert.Null(arguments[2]);
        Assert.False((bool)arguments[4]!);
        Assert.Equal(0, host.GameLookups);
        Assert.Equal(0, host.UnexpectedFrameworkCalls);
    }

    [Fact]
    public void Pending_retry_releases_only_unsent_debounce_and_waits_at_least_250ms()
    {
        using var host = new Host();
        host.Fsm.BeginDispatch(Now, Context);
        Assert.True(host.Loop.DeferPendingPolicyChoice(Pending, Now));
        host.Fsm.CompleteDispatch();

        Assert.False(host.Fsm.ShouldSuppressForContext(Context, Now));
        Assert.True(host.Loop.IsPolicyRefreshDeferred(Now));
        Assert.True(host.Loop.IsPolicyRefreshDeferred(Now.AddMilliseconds(249)));
        Assert.False(host.Loop.IsPolicyRefreshDeferred(Now.AddMilliseconds(250)));
        // Expiring the wait is eligibility only. No completion handler or input is queued.
        Assert.Equal(0, host.UnexpectedFrameworkCalls);
        Assert.Equal(0, host.GameLookups);

        var nextTick = Now.AddMilliseconds(251);
        host.Fsm.BeginDispatch(nextTick, Context);
        Assert.True(host.Loop.DeferPendingPolicyChoice(Pending, nextTick));
        Assert.True(host.Loop.IsPolicyRefreshDeferred(nextTick.AddMilliseconds(249)));
        Assert.False(host.Loop.IsPolicyRefreshDeferred(nextTick.AddMilliseconds(250)));
    }

    [Theory]
    [InlineData("ordinary policy pass")]
    [InlineData("AKOCHAN_PENDING")]
    [InlineData("akochan_pending: wrong case")]
    [InlineData("prefix AKOCHAN_PENDING: wrong position")]
    public void Ordinary_pass_keeps_existing_duplicate_input_protection(string reasoning)
    {
        using var host = new Host();
        host.Fsm.BeginDispatch(Now, Context);

        Assert.False(host.Loop.DeferPendingPolicyChoice(ActionChoice.Pass(reasoning), Now));

        Assert.True(host.Fsm.ShouldSuppressForContext(Context, Now));
        Assert.False(host.Loop.IsPolicyRefreshDeferred(Now));
    }

    [Fact]
    public void Ready_action_does_not_use_pending_reason_as_an_input_or_queue_bypass()
    {
        using var host = new Host();
        host.Fsm.BeginDispatch(Now, Context);
        var ready = ActionChoice.Discard(Tile.FromId(0), Pending.Reasoning);

        Assert.False(host.Loop.DeferPendingPolicyChoice(ready, Now));

        Assert.True(host.Fsm.ShouldSuppressForContext(Context, Now));
        Assert.Equal(0, host.UnexpectedFrameworkCalls);
        Assert.Equal(0, host.GameLookups);
    }

    [Fact]
    public void Stop_invalidates_existing_queue_and_clears_pending_retry_without_dispatching()
    {
        using var host = new Host();
        long generation = host.Queue.Capture();
        host.Fsm.BeginDispatch(Now, Context);
        host.Loop.DeferPendingPolicyChoice(Pending, Now);

        host.Loop.Stop();

        Assert.False(host.Loop.IsPolicyRefreshDeferred(Now));
        Assert.False(host.Fsm.IsDispatchInFlight);
        Assert.False(host.Fsm.ShouldSuppressForContext(Context, Now));
        bool dispatched = false;
        Assert.False(host.Queue.TryExecute(generation, () => true, () => dispatched = true));
        Assert.False(dispatched);
        Assert.Equal(0, host.GameLookups);
        Assert.Equal(0, host.UnexpectedFrameworkCalls);
    }

    private sealed class ProbePolicy : IPolicy
    {
        public int Calls { get; private set; }
        public ActionFlags LastFlags { get; private set; }
        public ActionChoice Choose(StateSnapshot state)
        {
            Calls++;
            LastFlags = state.Legal.Flags;
            return Pending;
        }
    }

    private sealed class Host : IDisposable
    {
        private readonly RuntimePlugin runtime = (RuntimePlugin)RuntimeHelpers.GetUninitializedObject(typeof(RuntimePlugin));
        public AutoPlayLoop Loop { get; }
        public int GameLookups { get; private set; }
        public int UnexpectedFrameworkCalls { get; private set; }
        public ActionStateMachine Fsm => Field<ActionStateMachine>("fsm");
        public QueuedActionGate Queue => Field<QueuedActionGate>("queuedActions");
        public object? PendingOutcome => typeof(AutoPlayLoop).GetField("pendingOutcome", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Loop);

        public Host()
        {
            var framework = RuntimeModeLifecycleTests.ServiceProxy.Create<IFramework>((method, _) =>
            {
                if (method.Name is "add_Update" or "remove_Update") return null;
                UnexpectedFrameworkCalls++;
                throw new InvalidOperationException("Pending policy must not schedule work or touch framework services.");
            });
            var gui = RuntimeModeLifecycleTests.ServiceProxy.Create<IGameGui>((_, _) =>
            {
                GameLookups++;
                throw new InvalidOperationException("Pending policy must not access native addons.");
            });
            var log = new StubPluginLog();
            Loop = new AutoPlayLoop(runtime, framework, log, new MahjongAddon(gui, log));
        }

        public void SetPolicy(IPolicy policy) => typeof(RuntimePlugin).GetField("policy", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, policy);
        public void SetInactiveConfiguration() => typeof(RuntimePlugin).GetField("<ConfigService>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, RuntimeModeLifecycleTests.ServiceProxy.Create<IConfigService<Mahjong.Plugin.Dalamud.Configuration>>((method, _) =>
                method.Name == "get_Current" ? new Mahjong.Plugin.Dalamud.Configuration() : throw new NotSupportedException(method.Name)));
        public bool HandleRiichi(StateSnapshot snapshot, DispatchContext context, bool isCallPrompt = true) => (bool)typeof(AutoPlayLoop)
            .GetMethod("TryHandleRiichiConfirmTsumogiri", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Loop, [snapshot, context, isCallPrompt])!;
        public void Invoke(string method, StateSnapshot snapshot, ActionChoice choice) => typeof(AutoPlayLoop)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Loop, [snapshot, choice]);
        private T Field<T>(string name) => (T)typeof(AutoPlayLoop).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Loop)!;
        public void Dispose() => Loop.Dispose();
    }
}
