using System.Reflection;
using System.Runtime.CompilerServices;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Tests.Replay;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game.Variants;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class StateCacheBoundaryTests
{
    [Fact]
    public void Background_policy_refreshes_unchanged_snapshot_without_inventing_game_events()
    {
        var policy = new AsyncPolicy();
        var aggregator = Cache(policy);
        int events = 0;
        aggregator.Changed += _ => events++;
        var state = StateSnapshot.Empty with { Legal = new(ActionFlags.Pass, [], [], [], []) };
        aggregator.ApplySnapshot(state);
        Assert.Equal("pending", aggregator.LastChoice!.Reasoning);
        policy.RequiresRefresh = true;
        aggregator.ApplySnapshot(state);
        Assert.Equal("ready", aggregator.LastChoice!.Reasoning);
        Assert.Equal(1, events);
        Assert.Equal(2, policy.Calls);
    }

    [Fact]
    public void Lost_snapshot_cancels_async_policy_and_clears_old_recommendation()
    {
        var policy = new AsyncPolicy();
        var aggregator = Cache(policy);
        aggregator.ApplySnapshot(StateSnapshot.Empty with { Legal = new(ActionFlags.Pass, [], [], [], []) });
        aggregator.ApplySnapshot(null);
        Assert.True(policy.Invalidated);
        Assert.Null(aggregator.LastChoice);
    }

    [Fact]
    public void End_of_action_window_cancels_background_policy()
    {
        var policy = new AsyncPolicy();
        var aggregator = Cache(policy);
        aggregator.ApplySnapshot(StateSnapshot.Empty with { Legal = new(ActionFlags.Pass, [], [], [], []) });
        aggregator.ApplySnapshot(StateSnapshot.Empty);
        Assert.True(policy.Invalidated);
        Assert.Null(aggregator.LastChoice);
    }

    private sealed class AsyncPolicy : IRefreshablePolicy
    {
        public bool RequiresRefresh { get; set; }
        public bool Invalidated { get; private set; }
        public int Calls { get; private set; }
        public ActionChoice Choose(StateSnapshot _) { Calls++; return ActionChoice.Pass(RequiresRefresh ? "ready" : "pending"); }
        public void Invalidate() => Invalidated = true;
    }

    [Fact]
    public void Same_candidate_count_but_changed_call_content_refreshes_snapshot_and_choice()
    {
        var policy = new TrackingPolicy();
        var aggregator = Cache(policy);
        var first = StateSnapshot.Empty with
        {
            Legal = new(ActionFlags.Pon, [], [Pon(4, 1)], [], []),
        };
        var next = first with { Legal = new(ActionFlags.Pon, [], [Pon(13, 2)], [], []) };
        aggregator.ApplySnapshot(first);
        var oldChoice = aggregator.LastChoice;
        aggregator.ApplySnapshot(next);
        Assert.Same(next, aggregator.Latest);
        Assert.NotSame(oldChoice, aggregator.LastChoice);
        Assert.Equal(13, aggregator.LastChoice!.Call!.Value.ClaimedTile.Id);
        Assert.Equal(2, policy.Calls);
    }

    [Fact]
    public void Seat_knowledge_and_discardable_content_changes_each_refresh_policy_cache()
    {
        var policy = new TrackingPolicy();
        var aggregator = Cache(policy);
        var first = StateSnapshot.Empty with
        {
            Legal = new(ActionFlags.Pass, [Tile.FromId(4)], [], [], []), SeatInfoKnown = false,
        };
        var seatKnown = first with { SeatInfoKnown = true };
        var differentAllowedTile = seatKnown with
        { Legal = seatKnown.Legal with { DiscardableTiles = [Tile.FromId(13)] } };
        foreach (var snapshot in new[] { first, seatKnown, differentAllowedTile })
        {
            aggregator.ApplySnapshot(snapshot);
            Assert.Same(snapshot, aggregator.Latest);
        }
        Assert.Equal(3, policy.Calls);
        aggregator.ApplySnapshot(differentAllowedTile);
        Assert.Equal(3, policy.Calls);
    }

    [Fact]
    public void Policy_failure_clears_prior_recommendation_and_reports_error_for_runtime_stop()
    {
        var policy = new TrackingPolicy();
        var aggregator = Cache(policy);
        var snapshot = StateSnapshot.Empty with { Legal = new(ActionFlags.Pass, [], [], [], []) };
        aggregator.ApplySnapshot(snapshot);
        Assert.NotNull(aggregator.LastChoice);
        policy.Throw = true;
        aggregator.ApplySnapshot(snapshot with { TurnIndex = snapshot.TurnIndex + 1 });
        Assert.Null(aggregator.LastChoice);
        Assert.Null(aggregator.LastScored);
        Assert.StartsWith("POLICY_FAILED: InvalidOperationException:", aggregator.LastScorerError);
    }

    [Fact]
    public void Upstream_result_state_clears_melds_and_does_not_seed_transition_hand_baseline()
    {
        var profile = JsonLayoutProfileLoader.Load(Path.Combine(TestPaths.LayoutsDir, "emj.json"));
        var tracker = new MeldTracker();
        tracker.Record(Meld.Pon(Tile.FromId(4), Tile.FromId(4), 1));
        tracker.ObserveWall(66); // A very early hand: next wall 70 would not satisfy the old >5 reset.
        var variant = new BaseEmjVariant(profile, new StubPluginLog(), Path.GetTempPath());
        var memory = new AddonMemoryBuilder(profile).WithScores(25000, 25000, 25000, 25000)
            .WithHand("1234m456p789s1234z").Build();
        var snapshot = variant.BuildSnapshotFromMemory(memory, [AtkValueRecord.OfInt(29)],
            new(tracker, null), false);
        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.OurMelds);
        Assert.Empty(tracker.Melds);
        Assert.Equal(-1, tracker.SerializeState().LastObservedWall);
        Assert.Equal(-1, tracker.SerializeState().PendingOppDiscardSeat);
        Assert.Null(typeof(MeldTracker).GetField("lastHand", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tracker));
        tracker.ObserveWall(70);
        Assert.Empty(tracker.Melds);
    }

    private static MeldCandidate Pon(int id, int seat) =>
        new(MeldKind.Pon, Tile.FromId(id), [Tile.FromId(id), Tile.FromId(id)], seat);

    private static StateAggregator Cache(IPolicy policy)
    {
        var aggregator = (StateAggregator)RuntimeHelpers.GetUninitializedObject(typeof(StateAggregator));
        typeof(StateAggregator).GetField("policy", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(aggregator, policy);
        return aggregator;
    }

    private sealed class TrackingPolicy : IPolicy
    {
        public int Calls { get; private set; }
        public bool Throw { get; set; }
        public ActionChoice Choose(StateSnapshot state)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("synthetic policy failure");
            return state.Legal.PonCandidates.Count > 0
                ? new(ActionKind.Pon, Call: state.Legal.PonCandidates[0])
                : ActionChoice.Pass("synthetic " + Calls);
        }
    }
}
