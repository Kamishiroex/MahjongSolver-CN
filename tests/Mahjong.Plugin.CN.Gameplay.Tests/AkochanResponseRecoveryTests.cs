using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.Engines;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Experimental;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Synthetic response-window evidence; never assumes a complete historical event prefix.</summary>
public sealed class AkochanResponseRecoveryTests
{
    private const string TopSlot = "Emj/124/4";
    private const string LeftSlot = "Emj/127/4";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
    private static readonly Guid Session = Guid.Parse("918d2940-e2db-4621-aae2-0e0b34a3c55d");
    private static readonly int[] Hand = [0, 1, 2, 9, 10, 11, 12, 12, 20, 27, 27, 31, 31];
    private static ObservationReference Ref(long sample) => new(sample, Start.AddMilliseconds(sample * 100), "synthetic", "response window fixture");
    private static Field<T> Known<T>(T value, long sample = 10) => Field<T>.Known(value, Ref(sample));
    private static PublicImageTile Image(int tile, string slot, string group = "", bool stable = true, int order = 1) =>
        new(new(tile), slot, group, 1, 2, 40, 50, 0, false, stable)
        { DisplayPosition = new(1, order, order, false), WasClaimed = Known(false), Tsumogiri = Known(false) };
    private static PublicImageInventory Inventory(params PublicImageTile[] tiles) =>
        new(tiles.ToImmutableArray(), true, true, true, tiles.Length == 0, tiles.Length, 0, []);
    private static PublicSnapshot Snapshot(long sample) => new()
    {
        SessionId = Session, Observation = Ref(sample), Stability = StabilityState.Stable,
        Synchronization = SynchronizationState.HistoryGap,
        LowerVisibleFaces = Known(Hand.Select((kind, index) => new PublicHandTile(new(kind), new($"hand/{index}", index, sample))).ToImmutableArray(), sample),
        Players = Enumerable.Range(0, 4).Select(actor => new PlayerPublicState((ScreenPosition)actor)
        {
            RiverImages = Known(actor == 2 ? Inventory(Image(12, TopSlot)) : Inventory(), sample),
            MeldImages = Known(Inventory(), sample),
        }).ToImmutableArray(),
    };
    private static PublicSnapshot River(PublicSnapshot snapshot, int actor, PublicImageInventory inventory) =>
        snapshot with { Players = snapshot.Players.SetItem(actor, snapshot.Players[actor] with { RiverImages = Known(inventory, snapshot.Observation!.Sequence) }) };
    private static PublicSnapshot Melds(PublicSnapshot snapshot, int actor, Field<PublicImageInventory> inventory) =>
        snapshot with { Players = snapshot.Players.SetItem(actor, snapshot.Players[actor] with { MeldImages = inventory }) };
    private static PublicImageInventory Pon() => Inventory(Image(5, "Emj/113/2/4", "Emj/113"),
        Image(5, "Emj/113/3/4", "Emj/113"), Image(5, "Emj/113/4/4", "Emj/113")) with
    { Groups = [new("Emj/113", 3, 3, 0, true, "PUBLIC_PON_THREE_FACE_PATTERN", null)] };
    private static (AkochanGlobalEvent Action, string Slot) Discard(long sequence = 9, int actor = 2, int tile = 12,
        string slot = TopSlot) => (new(sequence, "dahai", actor, null, new(tile), []), slot);
    private static AddonProbe Probe(PublicSnapshot snapshot, string action = "Pon") => new("Emj", true, true, true, 109, [], null,
        PublicTableFaces: snapshot.Players.SelectMany(player => player.RiverImages.Value?.Tiles.Select(tile =>
        {
            string direction = new[] { "bottom", "right", "top", "left" }[(int)player.Position];
            uint icon = 76001u + (uint)tile.Tile.Id;
            return new PublicTableFaceCandidate("river-" + direction, direction, tile.SlotPath, tile.GroupPath, 1021,
                tile.X, tile.Y, tile.Width, tile.Height, tile.RotationDegrees, tile.Mirrored, icon,
                LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex"), PublicTableImageReader.VerifiedResourceCode,
                new(1, tile.DisplayPosition!.VisibleColumn, tile.DisplayPosition.ReadOrder, false));
        }) ?? []).ToArray(),
        PublicActionMenu: new("ACTION_MENU_VISIBLE_CANDIDATE", true, true,
            [new("Emj/104/3/3", 0, action, true, "ACTION_MENU_LABEL_CANDIDATE"),
                new("Emj/104/3/4", 30, "Pass", true, "ACTION_MENU_LABEL_CANDIDATE")]));

    [Fact]
    public void Fresh_top_discard_conflicting_with_chi_menu_cannot_fall_back_to_old_left_tail()
    {
        var window = new AkochanResponseWindow();
        var snapshot = River(River(Snapshot(10), 2, Inventory(Image(5, TopSlot))), 3, Inventory(Image(12, LeftSlot)));
        window.Observe(snapshot, Probe(snapshot, "Chi"), [Discard(10, 2, 5)]);
        Assert.Null(window.Trigger); Assert.Null(window.Assumption);
    }

    [Fact]
    public void Old_own_discard_before_new_window_does_not_forbid_unique_current_left_tail_inference()
    {
        var window = new AkochanResponseWindow();
        var snapshot = River(Snapshot(40), 3, Inventory(Image(12, LeftSlot)));
        window.Observe(snapshot, Probe(snapshot, "Chi"), [Discard(5, 0, 0, "Emj/118/4")]);
        Assert.NotNull(window.Trigger); Assert.Equal(3, window.Trigger.Actor); Assert.Equal(12, window.Trigger.Tile!.Value.Id);
        Assert.NotNull(window.Assumption);
    }

    [Fact]
    public void Simultaneous_old_actions_before_popup_allow_unique_current_tail_inference_without_picking_an_old_event()
    {
        var window = new AkochanResponseWindow();
        var snapshot = River(Snapshot(100), 3, Inventory(Image(12, LeftSlot)));
        // Neither old event is the current left-hand response; their mutual order is unknown.
        (AkochanGlobalEvent Action, string Slot)[] oldActions =
            [Discard(5, 0, 0, "Emj/118/4"), Discard(5, 2, 12)];
        window.Observe(snapshot, Probe(snapshot, "Chi"), oldActions);
        Assert.NotNull(window.Trigger);
        Assert.Equal(3, window.Trigger.Actor);
        Assert.Equal(12, window.Trigger.Tile!.Value.Id);
        Assert.Contains("唯一匹配推导", window.Assumption);
        Assert.Contains("未捕获该弃牌事件", window.Assumption);
    }

    [Fact]
    public void Simultaneous_actions_in_current_popup_stay_ambiguous_after_100_more_observations()
    {
        var window = new AkochanResponseWindow();
        (AkochanGlobalEvent Action, string Slot)[] simultaneous =
            [Discard(10, 3, 12, LeftSlot), Discard(10, 2, 12)];
        // The menu alone would uniquely suggest the left tail, but observed concurrent
        // actions belong to this same popup and cannot become unambiguous merely by age.
        for (long sample = 10; sample <= 110; sample++)
        {
            var snapshot = River(Snapshot(sample), 3, Inventory(Image(12, LeftSlot)));
            window.Observe(snapshot, Probe(snapshot, "Chi"), simultaneous);
            Assert.Null(window.Trigger);
            Assert.Null(window.Assumption);
            Assert.Contains("同次采样", window.Diagnostic);
        }
    }

    [Fact]
    public void Unrelated_preexisting_river_temporarily_unreadable_does_not_block_observed_response_source()
    {
        var window = new AkochanResponseWindow();
        var initial = River(Snapshot(10), 1, Inventory(Image(4, "Emj/121/4")));
        window.Observe(initial, Probe(initial), [Discard()]);
        Assert.NotNull(window.Trigger);
        var current = River(Snapshot(11), 1, Inventory(Image(4, "Emj/121/4", stable: false)) with
        { Stable = false, AllVisibleSlotsDecoded = false });
        window.Observe(current, Probe(current), [Discard()]);
        Assert.NotNull(window.Trigger); Assert.Equal(2, window.Trigger.Actor); Assert.Null(window.Assumption);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Missing_or_partial_meld_read_does_not_permanently_retire_same_response(bool unknown)
    {
        var window = new AkochanResponseWindow();
        var initial = Melds(Snapshot(10), 1, Known(Pon()));
        window.Observe(initial, Probe(initial), [Discard()]); Assert.NotNull(window.Trigger);
        var interrupted = Melds(Snapshot(11), 1, unknown ? Field<PublicImageInventory>.Unknown("temporary read gap") :
            Known(Pon() with { Tiles = [], AllVisibleSlotsDecoded = false, Stable = false }));
        window.Observe(interrupted, Probe(interrupted), [Discard()]);
        var restored = Melds(Snapshot(12), 1, Known(Pon(), 12));
        window.Observe(restored, Probe(restored), [Discard()]);
        Assert.NotNull(window.Trigger); Assert.Equal(2, window.Trigger.Actor); Assert.Null(window.Assumption);
    }

    [Fact]
    public void Genuine_complete_meld_change_retires_old_response_even_if_same_menu_stays_visible()
    {
        var window = new AkochanResponseWindow();
        var initial = Snapshot(10);
        window.Observe(initial, Probe(initial), [Discard()]); Assert.NotNull(window.Trigger);
        var called = Melds(Snapshot(11), 1, Known(Pon(), 11));
        window.Observe(called, Probe(called), [Discard()]);
        Assert.Null(window.Trigger);
        var repeated = Melds(Snapshot(12), 1, Known(Pon(), 12));
        window.Observe(repeated, Probe(repeated), [Discard()]);
        Assert.Null(window.Trigger);
    }

    [Fact]
    public void Temporarily_missing_tail_order_suspends_without_retiring_observed_source()
    {
        var window = new AkochanResponseWindow();
        var prior = Image(5, "Emj/1240001/4", order: 1);
        var tail = Image(12, TopSlot, order: 2);
        var initial = River(Snapshot(10), 2, Inventory(prior, tail));
        window.Observe(initial, Probe(initial), [Discard()]); Assert.NotNull(window.Trigger);
        var missing = River(Snapshot(11), 2, Inventory(prior, tail with { DisplayPosition = null }));
        // The raw resource was read; only its positional interpretation is unavailable.
        window.Observe(missing, Probe(initial), [Discard()]);
        Assert.Null(window.Trigger);
        var restored = River(Snapshot(12), 2, Inventory(prior, tail));
        window.Observe(restored, Probe(restored), [Discard()]);
        Assert.NotNull(window.Trigger); Assert.Equal(12, window.Trigger.Tile!.Value.Id);
    }
}
