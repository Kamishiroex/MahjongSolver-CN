using System.Collections.Immutable;
using System.Threading.Channels;
using Mahjong.Cn;
using Mahjong.Cn.Engines;
using Mahjong.Core;
using Mahjong.Plugin.CN.Experimental;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class AkochanHandPolicyTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private sealed record Request(AkochanHandScenario Scenario, CancellationToken Token,
        TaskCompletionSource<AkochanDecision> Completion)
    {
        internal AkochanDecision Discard(int kind, bool red = false) => new(
            [new("dahai", 0, null, new VisibleTile(kind, red), [], false)], Scenario.ScenarioReplay.InputSha256,
            Scenario.ScenarioReplay.SourceLabel, true, AkochanInstallation.ExpectedCommit, "test-manifest", 42, null, null);
    }

    private sealed class StubPolicy : IPolicy
    {
        internal ActionChoice Choice = ActionChoice.Discard(Tile.FromId(1), "original-policy");
        internal int Calls;
        public ActionChoice Choose(StateSnapshot state) { Calls++; return Choice; }
    }

    private sealed class Harness : IDisposable
    {
        internal DateTimeOffset Now = new(2026, 9, 24, 9, 20, 0, TimeSpan.Zero);
        internal bool Enabled = true;
        internal HandAiObservation? Observation;
        internal StateSnapshot State;
        internal readonly StubPolicy Fallback = new();
        internal readonly List<string> Reports = [];
        internal readonly AkochanHandPolicy Policy;
        private readonly Channel<Request> requests = Channel.CreateUnbounded<Request>();

        internal Harness()
        {
            VisibleTile[] tiles = [new(1), new(5), new(6), new(7), new(8), new(8), new(9), new(10),
                new(10), new(19), new(25), new(26), new(28), new(30)];
            Observation = new("table-one/round-one/window-one", Now, true, tiles.ToImmutableArray());
            State = StateSnapshot.Empty with
            {
                Hand = tiles.Select(t => Tile.FromId(t.Id)).ToArray(), WallRemaining = 69, TurnIndex = 1, AddonStateCode = 30,
                Legal = new(ActionFlags.Discard, tiles.Select(t => Tile.FromId(t.Id)).ToArray(), [], [], []),
            };
            Policy = new(Fallback, () => Enabled, () => Observation, Reports.Add, (scenario, token) =>
            {
                var request = new Request(scenario, token, new(TaskCreationOptions.RunContinuationsAsynchronously));
                requests.Writer.TryWrite(request);
                // Deliberately ignore cancellation to model a worker which finishes late.
                return request.Completion.Task;
            }, () => Now);
        }

        internal ActionChoice Choose() => Policy.Choose(State);
        internal async Task<Request> Next() => await requests.Reader.ReadAsync().AsTask().WaitAsync(TestTimeout);
        internal void Freshen() => Observation = Observation! with { Utc = Now };
        internal async Task Complete(Request request, int tile = 28, bool red = false)
        {
            Task pending = Policy.PendingWork!;
            request.Completion.SetResult(request.Discard(tile, red));
            await pending.WaitAsync(TestTimeout);
        }
        public void Dispose() => Policy.Dispose();
    }

    [Fact]
    public async Task Pending_never_dispatches_then_matching_result_becomes_discard_and_refresh_is_throttled()
    {
        using var h = new Harness();
        var pending = h.Choose();
        Assert.Equal(ActionKind.Pass, pending.Kind);
        Assert.StartsWith("AKOCHAN_PENDING", pending.Reasoning);
        var request = await h.Next();
        Assert.False(h.Policy.RequiresRefresh);
        h.Now = h.Now.AddMilliseconds(100); h.Freshen();
        Assert.True(h.Policy.RequiresRefresh);
        await h.Complete(request);
        var result = h.Choose();
        Assert.Equal(ActionKind.Discard, result.Kind);
        Assert.Equal(28, result.DiscardTile!.Value.Id);
        Assert.StartsWith("AKOCHAN_HAND_ONLY", result.Reasoning);
        Assert.Same(result, h.Choose());
        Assert.False(h.Policy.RequiresRefresh);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("hand")]
    [InlineData("legal")]
    [InlineData("turn")]
    public async Task Changed_request_cancels_old_worker_and_late_completion_cannot_supply_new_discard(string change)
    {
        using var h = new Harness();
        h.Choose(); var old = await h.Next(); Task oldWork = h.Policy.PendingWork!;
        if (change == "context") h.Observation = h.Observation! with { ContextKey = "table-two/round-one/window-one" };
        if (change == "hand")
        {
            h.Observation = h.Observation! with { Tiles = h.Observation.Tiles.SetItem(13, new(31)) };
            h.State = h.State with { Hand = h.Observation.Tiles.Select(t => Tile.FromId(t.Id)).ToArray() };
        }
        if (change == "legal") h.State = h.State with { Legal = h.State.Legal with { DiscardableTiles = [Tile.FromId(1), Tile.FromId(28)] } };
        if (change == "turn") h.State = h.State with { TurnIndex = h.State.TurnIndex + 1 };
        Assert.Equal(ActionKind.Pass, h.Choose().Kind);
        var next = await h.Next();
        Assert.True(old.Token.IsCancellationRequested);
        old.Completion.SetResult(old.Discard(1));
        await oldWork.WaitAsync(TestTimeout);
        Assert.Equal(ActionKind.Pass, h.Choose().Kind);
        await h.Complete(next, 28);
        Assert.Equal(28, h.Choose().DiscardTile!.Value.Id);
    }

    [Fact]
    public async Task Disabled_during_pending_cancels_work_and_keeps_original_result_after_late_completion()
    {
        using var h = new Harness();
        h.Choose(); var request = await h.Next(); Task work = h.Policy.PendingWork!;
        h.Enabled = false;
        Assert.Same(h.Fallback.Choice, h.Choose());
        Assert.True(request.Token.IsCancellationRequested);
        request.Completion.SetResult(request.Discard(28)); await work.WaitAsync(TestTimeout);
        Assert.Same(h.Fallback.Choice, h.Choose());
        Assert.Null(h.Policy.PendingWork);
    }

    [Fact]
    public async Task Engine_failure_returns_explicit_fallback_without_retry_loop_for_same_window()
    {
        using var h = new Harness();
        h.Choose(); var request = await h.Next(); Task work = h.Policy.PendingWork!;
        request.Completion.SetException(new AkochanException("AKOCHAN_TEST_FAILURE"));
        await Assert.ThrowsAsync<AkochanException>(async () => await work.WaitAsync(TestTimeout));
        var result = h.Choose();
        Assert.Equal(h.Fallback.Choice.DiscardTile, result.DiscardTile);
        Assert.Contains("AKOCHAN_FALLBACK", result.Reasoning);
        Assert.Contains("AKOCHAN_TEST_FAILURE", result.Reasoning);
        Assert.Same(result, h.Choose());
    }

    [Theory]
    [InlineData(ActionKind.Tsumo)]
    [InlineData(ActionKind.Ron)]
    [InlineData(ActionKind.Riichi)]
    [InlineData(ActionKind.Pon)]
    [InlineData(ActionKind.Chi)]
    [InlineData(ActionKind.AnKan)]
    public async Task Existing_wins_calls_and_declarations_take_priority_over_pending_hand_analysis(ActionKind kind)
    {
        using var h = new Harness();
        h.Choose(); var request = await h.Next(); Task work = h.Policy.PendingWork!;
        h.Fallback.Choice = new(kind, Reasoning: "original-priority");
        Assert.Same(h.Fallback.Choice, h.Choose());
        Assert.True(request.Token.IsCancellationRequested);
        request.Completion.SetResult(request.Discard(28)); await work.WaitAsync(TestTimeout);
        Assert.Same(h.Fallback.Choice, h.Choose());
    }

    [Theory]
    [InlineData("meld")]
    [InlineData("riichi")]
    [InlineData("count")]
    [InlineData("no-discard")]
    public void Unsupported_current_state_uses_original_policy_without_starting_worker(string condition)
    {
        using var h = new Harness();
        if (condition == "meld") h.State = h.State with { OurMelds = [Meld.Pon(Tile.FromId(0), Tile.FromId(0), 1)] };
        if (condition == "riichi") h.State = h.State with { OurRiichi = true };
        if (condition == "count") h.State = h.State with { Hand = h.State.Hand.Take(11).ToArray() };
        if (condition == "no-discard") h.State = h.State with { Legal = LegalActions.None };
        Assert.Same(h.Fallback.Choice, h.Choose());
        Assert.Null(h.Policy.PendingWork);
    }

    [Theory]
    [InlineData("old")]
    [InlineData("future")]
    [InlineData("unstable")]
    [InlineData("different")]
    [InlineData("missing")]
    public async Task Unfresh_or_conflicting_observation_cancels_request_and_stops_discard(string condition)
    {
        using var h = new Harness();
        h.Choose(); var request = await h.Next(); Task work = h.Policy.PendingWork!;
        if (condition == "old") h.Now = h.Now.AddMilliseconds(1001);
        if (condition == "future") h.Observation = h.Observation! with { Utc = h.Now.AddMilliseconds(1) };
        if (condition == "unstable") h.Observation = h.Observation! with { Stable = false };
        if (condition == "different") h.Observation = h.Observation! with { Tiles = h.Observation.Tiles.SetItem(0, new(31)) };
        if (condition == "missing") h.Observation = null;
        var stopped = h.Choose();
        Assert.Equal(ActionKind.Pass, stopped.Kind);
        Assert.StartsWith("AKOCHAN_PENDING", stopped.Reasoning);
        Assert.True(request.Token.IsCancellationRequested);
        request.Completion.SetResult(request.Discard(28)); await work.WaitAsync(TestTimeout);
        Assert.Equal(ActionKind.Pass, h.Choose().Kind);
    }

    [Fact]
    public async Task Red_and_ordinary_same_kind_cannot_be_dispatched_as_precise_engine_red_choice()
    {
        using var h = new Harness();
        h.Observation = h.Observation! with { Tiles = h.Observation.Tiles.SetItem(1, new(4, true)).SetItem(2, new(4, false)) };
        h.State = h.State with
        {
            Hand = h.Observation.Tiles.Select(t => Tile.FromId(t.Id)).ToArray(),
            Legal = h.State.Legal with { DiscardableTiles = [Tile.FromId(1), Tile.FromId(4)] },
        };
        h.Choose(); var request = await h.Next(); await h.Complete(request, 4, red: true);
        var result = h.Choose();
        Assert.Equal(h.Fallback.Choice.DiscardTile, result.DiscardTile);
        Assert.Contains("AKOCHAN_RED_SLOT_AMBIGUOUS", result.Reasoning);
    }

    [Fact]
    public async Task Engine_discard_outside_current_allowlist_falls_back()
    {
        using var h = new Harness();
        h.State = h.State with { Legal = h.State.Legal with { DiscardableTiles = [Tile.FromId(1)] } };
        h.Choose(); var request = await h.Next(); await h.Complete(request, 28);
        Assert.Contains("AKOCHAN_DISCARD_NOT_LEGAL", h.Choose().Reasoning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Eight_second_deadline_rejects_pending_or_late_completed_result_even_with_fresh_hand(bool alreadyCompleted)
    {
        using var h = new Harness();
        h.Choose(); var request = await h.Next(); Task work = h.Policy.PendingWork!;
        h.Now = h.Now.AddSeconds(9); h.Freshen();
        if (alreadyCompleted) await h.Complete(request);
        var result = h.Choose();
        Assert.Equal(h.Fallback.Choice.DiscardTile, result.DiscardTile);
        Assert.Contains("AKOCHAN", result.Reasoning);
        Assert.Contains("TIMEOUT", result.Reasoning);
        if (!alreadyCompleted) { request.Completion.SetResult(request.Discard(28)); await work.WaitAsync(TestTimeout); }
    }

    [Fact]
    public async Task Disposed_policy_never_applies_late_worker_result()
    {
        using var h = new Harness();
        h.Choose(); var request = await h.Next(); Task work = h.Policy.PendingWork!;
        h.Policy.Dispose();
        Assert.True(request.Token.IsCancellationRequested);
        request.Completion.SetResult(request.Discard(28)); await work.WaitAsync(TestTimeout);
        Assert.Equal(ActionKind.Pass, h.Choose().Kind);
        Assert.StartsWith("AKOCHAN_STOPPED", h.Choose().Reasoning);
        Assert.False(h.Policy.RequiresRefresh);
    }
}
