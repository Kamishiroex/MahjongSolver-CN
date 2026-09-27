using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Managed inventory fixtures; no claim of CN memory or live round validation.</summary>
public sealed class MeldRecoveryCheckpointTests
{
    [Fact]
    public void Export_is_bounded_and_does_not_share_mutable_meld_hand_or_counter_arrays()
    {
        var tracker = Seeded();
        var checkpoint = Assert.IsType<MeldTrackerCheckpoint>(tracker.ExportCheckpoint());
        Assert.Equal(1, checkpoint.SchemaVersion);
        Assert.Single(checkpoint.Melds);
        checkpoint.Melds[0].Tiles[0] = Tile.FromId(0);
        checkpoint.LastHand[0] = Tile.FromId(33);
        checkpoint.DiscardCounts[0] = 24;
        var again = Assert.IsType<MeldTrackerCheckpoint>(tracker.ExportCheckpoint());
        Assert.Equal(33, again.Melds[0].Tiles[0].Id);
        Assert.Equal(0, again.LastHand[0].Id);
        Assert.Equal(2, again.DiscardCounts[0]);
    }

    [Fact]
    public void Reconciled_restore_uses_current_baseline_and_drops_pending_old_inference()
    {
        var tracker = new MeldTracker();
        var hand = Tiles.Parse("123m456p789s12z");
        tracker.ObserveWall(60);
        tracker.ObserveSnapshot(hand.Concat(new[] { Tile.FromId(33), Tile.FromId(33) }).ToArray(), [0, 0, 0, 0], 0);
        tracker.ObserveSnapshot(hand, [0, 0, 0, 0], 0);
        Assert.Equal(1, tracker.SerializeState().DeferredTicks);
        var meld = Meld.Pon(Tile.FromId(33), Tile.FromId(33), 3);
        Assert.True(tracker.TryRebaseFromReconciledState([meld], 0, hand, [2, 3, 4, 5], 0, 56, out _));
        Assert.Equal(0, tracker.SerializeState().DeferredTicks);
        Assert.Equal(-1, tracker.SerializeState().PendingOppDiscardSeat);
        Assert.Null(tracker.ObserveSnapshot(hand, [2, 3, 4, 5], 0));
        Assert.Single(tracker.Melds);
        Assert.Equal(56, tracker.ExportCheckpoint()!.LastObservedWall);
    }

    [Fact]
    public void Invalid_recovery_does_not_partially_replace_existing_history()
    {
        var tracker = Seeded();
        var invalid = Meld.Pon(Tile.FromId(0), Tile.FromId(0), 0); // self cannot be an open-meld source
        Assert.False(tracker.TryRebaseFromReconciledState([invalid], 0, Tiles.Parse("123m456p789s1z"), [0, 0, 0, 0], 0, 40, out var error));
        Assert.Equal("RECOVERY_INVALID_TRACKER_STATE", error);
        Assert.Equal(33, Assert.Single(tracker.Melds).Tiles[0].Id);
        Assert.Equal(50, tracker.SerializeState().LastObservedWall);
    }

    [Fact]
    public void Physical_fifth_copy_and_invented_red_are_rejected()
    {
        var tracker = new MeldTracker();
        var pon = Meld.Pon(Tile.FromId(33), Tile.FromId(33), 1);
        Assert.False(tracker.TryRebaseFromReconciledState([pon], 0, Tiles.Parse("123m456p78s77z"), [0, 0, 0, 0], 0, 40, out _));
        Assert.False(tracker.TryRebaseFromReconciledState([pon], 1, Tiles.Parse("123m456p789s1z"), [0, 0, 0, 0], 0, 40, out _));
        Assert.Empty(tracker.Melds);
    }

    [Fact]
    public void Kan_counts_four_actual_tiles_but_three_toward_closed_hand_shape()
    {
        var tracker = new MeldTracker();
        var kan = Meld.AnKan(Tile.FromId(33));
        Assert.True(tracker.TryRebaseFromReconciledState([kan], 0, Tiles.Parse("123m456p789s1z"), [0, 0, 0, 0], 0, 40, out _));
        Assert.Equal(4, Assert.Single(tracker.ExportCheckpoint()!.Melds).Tiles.Length);
        Assert.False(tracker.TryRebaseFromReconciledState([kan], 0, Tiles.Parse("123m456p789s7z"), [0, 0, 0, 0], 0, 40, out _));
    }

    [Fact]
    public void Deferred_or_incomplete_tracker_is_not_exported_as_an_empty_known_meld_list()
    {
        var tracker = new MeldTracker();
        Assert.Null(tracker.ExportCheckpoint());
        tracker.ObserveWall(50);
        tracker.ObserveSnapshot(Tiles.Parse("123m456p789s1z"), [2, 3, 4, 5], 0);
        Assert.Null(tracker.ExportCheckpoint());
    }

    [Fact]
    public void Restoring_copies_caller_owned_arrays()
    {
        var tracker = new MeldTracker();
        var hand = Tiles.Parse("123m456p789s1z");
        var counts = new[] { 2, 3, 4, 5 };
        var meld = Meld.Pon(Tile.FromId(33), Tile.FromId(33), 1);
        Assert.True(tracker.TryRebaseFromReconciledState([meld], 0, hand, counts, 0, 50, out _));
        hand[0] = Tile.FromId(33); counts[0] = 24; meld.Tiles[0] = Tile.FromId(0);
        var checkpoint = tracker.ExportCheckpoint()!;
        Assert.Equal(0, checkpoint.LastHand[0].Id);
        Assert.Equal(2, checkpoint.DiscardCounts[0]);
        Assert.Equal(33, checkpoint.Melds[0].Tiles[0].Id);
    }

    private static MeldTracker Seeded()
    {
        var tracker = new MeldTracker();
        tracker.Record(Meld.Pon(Tile.FromId(33), Tile.FromId(33), 1));
        tracker.ObserveWall(50);
        tracker.ObserveSnapshot(Tiles.Parse("123m456p789s1z"), [2, 3, 4, 5], 0);
        return tracker;
    }
}
