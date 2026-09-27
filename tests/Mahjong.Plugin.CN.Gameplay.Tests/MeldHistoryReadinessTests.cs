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

public sealed class MeldHistoryReadinessTests
{
    [Theory]
    [InlineData("123m456p789s11z", ActionFlags.Discard)]
    [InlineData("123m456p789s1z", ActionFlags.Chi | ActionFlags.Pass)]
    [InlineData("123m456p11z", ActionFlags.Tsumo | ActionFlags.Pass)]
    [InlineData("123m11z", ActionFlags.Discard)]
    [InlineData("11z", ActionFlags.Discard)]
    public void Missing_history_clears_old_advice_without_guessing_melds_or_choosing_pass(string tiles, ActionFlags flags)
    {
        var policy = new CountingPolicy();
        var cache = Cache(policy);
        cache.ApplySnapshot(StateSnapshot.Empty with { Legal = new(ActionFlags.Pass, [], [], [], []) });
        Assert.NotNull(cache.LastChoice);
        cache.ApplySnapshot(StateSnapshot.Empty with
        {
            Hand = Tiles.Parse(tiles), OurMelds = [], Legal = new(flags, [], [], [], []),
        });
        Assert.StartsWith("MELD_HISTORY_MISSING", cache.LastScorerError);
        Assert.Null(cache.LastScored);
        Assert.Null(cache.LastChoice);
        Assert.Equal(1, policy.Calls);
        Assert.Empty(cache.Latest!.OurMelds);
    }

    [Fact]
    public void Deferred_meld_does_not_score_until_the_observed_opponent_counter_arrives()
    {
        var profile = JsonLayoutProfileLoader.Load(Path.Combine(TestPaths.LayoutsDir, "emj.json"));
        var tracker = new MeldTracker();
        var variant = new BaseEmjVariant(profile, new StubPluginLog(), Path.GetTempPath());
        var policy = new CountingPolicy();
        var cache = Cache(policy);

        StateSnapshot Read(string hand, int otherDiscards) => variant.BuildSnapshotFromMemory(
            new AddonMemoryBuilder(profile).WithScores(25000, 25000, 25000, 25000)
                .WithHand(hand).WithDiscardCounts(0, otherDiscards, 0, 0).Build(),
            [AtkValueRecord.OfInt(6)], new(tracker, null), false)!;

        Read("123m456p789s1155z", 0); // 13 tiles, before a pon of the two white dragons.
        var pending = Read("123m456p789s11z", 0);
        Assert.Empty(pending.OurMelds);
        Assert.True(tracker.SerializeState().DeferredTicks > 0);
        Assert.Equal(ActionFlags.None, pending.Legal.Flags);
        cache.ApplySnapshot(pending);
        Assert.Null(cache.LastScorerError);
        Assert.Null(cache.LastChoice);
        Assert.Equal(0, policy.Calls);

        var complete = Read("123m456p789s11z", 1);
        Assert.Single(complete.OurMelds);
        Assert.Equal(MeldKind.Pon, complete.OurMelds[0].Kind);
        Assert.True(complete.Legal.Can(ActionFlags.Discard));
        cache.ApplySnapshot(complete);
        Assert.Null(cache.LastScorerError);
        Assert.NotNull(cache.LastChoice);
        Assert.NotEmpty(cache.LastScored!);
        Assert.Equal(1, policy.Calls);
    }

    private static StateAggregator Cache(IPolicy policy)
    {
        var cache = (StateAggregator)RuntimeHelpers.GetUninitializedObject(typeof(StateAggregator));
        typeof(StateAggregator).GetField("policy", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(cache, policy);
        return cache;
    }

    [Fact]
    public void Independent_public_policy_can_validate_melds_without_legacy_tracker_but_keeps_schema_and_scene_guards()
    {
        var policy = new PublicPolicy();
        var cache = Cache(policy);
        var state = StateSnapshot.Empty with { Hand = Tiles.Parse("123m456p789s11z"),
            OurMelds = [], Legal = new(ActionFlags.Discard, [], [], [], []) };
        cache.ApplySnapshot(state);
        Assert.Equal(1,policy.Calls);
        Assert.NotNull(cache.LastChoice);
        Assert.Null(cache.LastScored); // No legacy scorer with incomplete legacy meld history.
        Assert.Null(cache.LastScorerError);
        cache.ApplySnapshot(state with { SchemaVersion=StateSnapshot.CurrentSchemaVersion+1 });
        Assert.Null(cache.LastChoice);
        Assert.StartsWith("SCHEMA_MISMATCH",cache.LastScorerError);
        cache.ApplySnapshot(null);
        Assert.Null(cache.LastChoice);
        cache.ApplySnapshot(state with { Legal=LegalActions.None });
        Assert.Equal(1,policy.Calls);
        Assert.True(policy.Invalidations>=3);
    }

    private sealed class PublicPolicy : IIndependentPublicStatePolicy
    {
        internal int Calls, Invalidations;
        public bool RequiresRefresh => true;
        public void Invalidate() => Invalidations++;
        public ActionChoice Choose(StateSnapshot state) { Calls++; return ActionChoice.Pass("AKOCHAN_PENDING: verify own public melds"); }
    }

    private sealed class CountingPolicy : IPolicy
    {
        internal int Calls;
        public ActionChoice Choose(StateSnapshot state) { Calls++; return ActionChoice.Pass("synthetic test"); }
    }
}
