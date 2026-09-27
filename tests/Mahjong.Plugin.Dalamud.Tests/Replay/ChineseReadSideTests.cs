using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Game.Addon.Lifecycle;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game.Variants;
using Mahjong.Policy.Abstractions;
using Mahjong.Policy.Efficiency;

namespace Mahjong.Plugin.Dalamud.Tests.Replay;

/// <summary>Synthetic upstream-layout buffers/cache tests; no game process, logger hooks or CN-offset certification.</summary>
public sealed class ChineseReadSideTests
{
    private static readonly LayoutProfile Profile = JsonLayoutProfileLoader.Load(Path.Combine(TestPaths.LayoutsDir, "emj.json"));

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(28)]
    [InlineData(99)]
    public void Fourteen_tiles_without_known_discard_state_do_not_grant_discard(int state)
    {
        var snapshot = Read(state, callModal: false);
        Assert.NotNull(snapshot);
        Assert.Equal(ActionFlags.None, snapshot.Legal.Flags);
    }

    [Theory]
    [InlineData(30, "1234m456p789s1234z")]
    [InlineData(6, "1234m456p789s1234z")]
    [InlineData(6, "123m456p789s12z")]
    public void Explicit_upstream_discard_surfaces_remain_usable(int state, string hand)
    {
        var snapshot = Read(state, callModal: false, hand: hand);
        Assert.Equal(ActionFlags.Discard, snapshot!.Legal.Flags);
    }

    [Fact]
    public void Missing_state_slot_with_fourteen_tiles_does_not_fill_in_our_turn()
    {
        var memory = Memory("1234m456p789s1234z");
        var variant = new BaseEmjVariant(Profile, new StubPluginLog(), Path.GetTempPath());
        var snapshot = variant.BuildSnapshotFromMemory(memory, [], new(new MeldTracker(), null), false);
        Assert.Equal(-1, snapshot!.AddonStateCode);
        Assert.Equal(ActionFlags.None, snapshot.Legal.Flags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exact_chinese_win_button_is_parsed_from_atk_or_visible_list_with_no_logger(bool fromList)
    {
        var root = Path.Combine(Path.GetTempPath(), "mjcn-no-read-log-" + Guid.NewGuid().ToString("N"));
        var variant = new BaseEmjVariant(Profile, new StubPluginLog(), root);
        var values = fromList ? new[] { AtkValueRecord.OfInt(15) } :
            new[] { AtkValueRecord.OfInt(15), AtkValueRecord.OfInt(0), AtkValueRecord.OfString("和牌") };
        var snapshot = variant.BuildSnapshotFromMemory(Memory("123m456p789s1234z"), values,
            new(new MeldTracker(), null), true, fromList ? ["和牌", "放弃"] : null, enableDiagnosticLogging: true);
        Assert.Equal(ActionFlags.Ron | ActionFlags.Pass, snapshot!.Legal.Flags);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void Chinese_declaration_text_is_not_an_action_button()
    {
        var snapshot = Read(15, callModal: true, label: "和牌！", hand: "123m456p789s1234z");
        Assert.Equal(ActionFlags.Pass, snapshot!.Legal.Flags);
    }

    [Fact]
    public void Schema_failure_clears_snapshot_policy_scores_and_content_hash_then_accepts_same_valid_state()
    {
        // Exercise only the managed cache boundary without constructing framework/native adapters.
        var aggregator = (StateAggregator)RuntimeHelpers.GetUninitializedObject(typeof(StateAggregator));
        var good = StateSnapshot.Empty;
        var notifications = 0;
        aggregator.Changed += _ => notifications++;
        aggregator.ApplySnapshot(good);
        SetProperty(aggregator, nameof(aggregator.LastScored), Array.Empty<ScoredDiscard>());
        SetProperty(aggregator, nameof(aggregator.LastChoice), ActionChoice.Discard(Tile.FromId(0)));
        aggregator.ApplySnapshot(good with { SchemaVersion = StateSnapshot.CurrentSchemaVersion + 1 });
        Assert.Null(aggregator.Latest);
        Assert.Null(aggregator.LastScored);
        Assert.Null(aggregator.LastChoice);
        Assert.StartsWith("SCHEMA_MISMATCH:", aggregator.LastScorerError);
        aggregator.ApplySnapshot(good);
        Assert.Same(good, aggregator.Latest);
        Assert.Null(aggregator.LastScorerError);
        Assert.Equal(2, notifications);
        aggregator.ApplySnapshot(null);
        Assert.Null(aggregator.Latest);
        Assert.Null(aggregator.LastChoice);
    }

    [Fact]
    public void PreFinalize_clears_meld_history_and_policy_cache_without_rebuilding_dying_addon()
    {
        var tracker = new MeldTracker();
        tracker.Record(Meld.Pon(Tile.FromId(4), Tile.FromId(4), 1));
        tracker.ObserveWall(30);
        var reader = (AddonEmjReader)RuntimeHelpers.GetUninitializedObject(typeof(AddonEmjReader));
        typeof(AddonEmjReader).GetField("meldTracker", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(reader, tracker);
        SetProperty(reader, nameof(reader.LastObservation), AddonEmjObservation.Empty);
        SetProperty(reader, nameof(reader.ActiveLayout), Profile);
        var aggregator = (StateAggregator)RuntimeHelpers.GetUninitializedObject(typeof(StateAggregator));
        aggregator.ApplySnapshot(StateSnapshot.Empty);
        reader.ObservationChanged += observation => typeof(StateAggregator)
            .GetMethod("OnObservationChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(aggregator, [observation]);
        // No AddonArgs is dereferenced with findings disabled; no pointer/host access is performed.
        typeof(AddonEmjReader).GetMethod("OnPreFinalize", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(reader, [AddonEvent.PreFinalize, null]);
        Assert.Empty(tracker.Melds);
        Assert.Equal(-1, tracker.SerializeState().LastObservedWall);
        Assert.Null(reader.ActiveLayout);
        Assert.False(reader.LastObservation.Present);
        Assert.Null(aggregator.Latest);
        Assert.Null(aggregator.LastChoice);
    }

    private static StateSnapshot? Read(int state, bool callModal, string? label = null,
        string hand = "1234m456p789s1234z")
    {
        var variant = new BaseEmjVariant(Profile, new StubPluginLog(), Path.GetTempPath());
        var values = label is null ? new[] { AtkValueRecord.OfInt(state) } :
            new[] { AtkValueRecord.OfInt(state), AtkValueRecord.OfInt(0), AtkValueRecord.OfString(label) };
        return variant.BuildSnapshotFromMemory(Memory(hand), values, new(new MeldTracker(), null), callModal);
    }

    private static byte[] Memory(string hand) => new AddonMemoryBuilder(Profile)
        .WithScores(25000, 25000, 25000, 25000).WithHand(hand).Build();

    private static void SetProperty(object target, string name, object? value) =>
        target.GetType().GetProperty(name)!.SetValue(target, value);
}
