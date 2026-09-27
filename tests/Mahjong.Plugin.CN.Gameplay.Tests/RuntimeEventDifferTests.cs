using Mahjong.Core;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Synthetic managed public snapshots, not native CN play or complete mjai history.</summary>
public sealed class RuntimeEventDifferTests
{
    [Fact]
    public void First_observation_is_incomplete_baseline_with_explicit_candidate_provenance()
    {
        var change = Assert.Single(new RuntimeEventDiffer().Observe(Guid.NewGuid(), 1, Snapshot()));
        Assert.Equal("observation_baseline", change.Type);
        Assert.True(change.HistoryGap);
        Assert.False(change.HistoryComplete);
        Assert.Equal("LegacyAssumption", change.SourceKind);
        Assert.Equal("Candidate", change.MappingStatus);
        Assert.Equal("StateSnapshot/public-runtime-diff", change.Source);
        Assert.Empty(change.TileIds);
    }

    [Fact]
    public void Unique_closed_hand_addition_reports_candidate_draw_and_red_delta()
    {
        var (differ, session, start) = Start();
        var next = start with { Hand = start.Hand.Concat([Tile.FromId(4)]).ToArray(), AkaDora = 1 };
        var draw = Assert.Single(differ.Observe(session, 2, next));
        Assert.Equal("draw", draw.Type);
        Assert.Equal(0, draw.RelativePlayer);
        Assert.Equal(4, Assert.Single(draw.TileIds));
        Assert.Equal(1, draw.RedCountDelta);
        Assert.False(draw.HistoryComplete);
    }

    [Fact]
    public void Hand_growth_with_multiple_changed_tiles_does_not_invent_draw_identity()
    {
        var (differ, session, start) = Start();
        var changed = start.Hand.Skip(1).Concat([Tile.FromId(29), Tile.FromId(30)]).ToArray();
        var events = differ.Observe(session, 2, start with { Hand = changed });
        var draw = Assert.Single(events, e => e.Type == "draw_unknown_tile");
        Assert.Empty(draw.TileIds);
        Assert.True(draw.HistoryGap);
        Assert.Contains(events, e => e.Type == "history_gap");
        Assert.DoesNotContain(events, e => e.Type == "draw");
    }

    [Fact]
    public void Sorting_hand_is_not_draw_discard_or_gap()
    {
        var (differ, session, start) = Start();
        Assert.Empty(differ.Observe(session, 2, start with { Hand = start.Hand.Reverse().ToArray() }));
    }

    [Fact]
    public void Complete_public_river_append_reports_tile_without_guessing_red_or_tedashi()
    {
        var (differ, session, start) = Start();
        var next = River(start, 2, 1, [Tile.FromId(13)]);
        var discard = Assert.Single(differ.Observe(session, 2, next));
        Assert.Equal("discard", discard.Type);
        Assert.Equal(2, discard.RelativePlayer);
        Assert.Equal(13, Assert.Single(discard.TileIds));
        Assert.Null(discard.RedCountDelta);
    }

    [Fact]
    public void Own_unique_removed_tile_and_count_increment_supports_candidate_when_river_is_unresolved()
    {
        var start = Snapshot() with { Hand = Tiles.Parse("123m456p789s11234z") };
        var differ = new RuntimeEventDiffer(); var session = Guid.NewGuid();
        differ.Observe(session, 1, start);
        var next = River(start with { Hand = start.Hand.Take(13).ToArray() }, 0, 1, []);
        var discard = Assert.Single(differ.Observe(session, 2, next));
        Assert.Equal("discard", discard.Type);
        Assert.Equal(start.Hand[^1].Id, Assert.Single(discard.TileIds));
        Assert.Equal(0, discard.RelativePlayer);
    }

    [Fact]
    public void Unknown_opponent_river_face_stays_unknown_and_marks_gap()
    {
        var (differ, session, start) = Start();
        var events = differ.Observe(session, 2, River(start, 1, 1, []));
        var discard = Assert.Single(events, e => e.Type == "discard_unknown_tile");
        Assert.Equal(1, discard.RelativePlayer);
        Assert.Empty(discard.TileIds);
        Assert.True(discard.HistoryGap);
        Assert.Contains(events, e => e.Type == "history_gap");
    }

    [Fact]
    public void Multiple_missed_discards_are_one_count_change_not_fabricated_ordered_actions()
    {
        var (differ, session, start) = Start();
        var events = differ.Observe(session, 2, River(start, 1, 2, Tiles.Parse("12z")));
        var change = Assert.Single(events, e => e.Type == "discard_count_changed");
        Assert.Equal(2, change.CountDelta);
        Assert.Empty(change.TileIds);
        Assert.DoesNotContain(events, e => e.Type == "discard");
    }

    [Fact]
    public void Legal_tsumo_ron_and_call_menus_never_become_actions_or_win_events()
    {
        var (differ, session, start) = Start();
        var menu = start with { Legal = new(ActionFlags.Ron | ActionFlags.Tsumo | ActionFlags.Pon, [], [], [], []), AddonStateCode = 6 };
        Assert.Empty(differ.Observe(session, 2, menu));
    }

    [Fact]
    public void Result_state_never_claims_specific_win_type_and_repeated_result_is_deduplicated()
    {
        var (differ, session, start) = Start();
        var result = StateSnapshot.Empty with { AddonStateCode = 29 };
        var events = differ.Observe(session, 2, result);
        Assert.Contains(events, e => e.Type == "round_end_candidate");
        Assert.DoesNotContain(events, e => e.Type is "ron" or "tsumo" or "hora" or "ryukyoku");
        Assert.Empty(differ.Observe(session, 3, result));
        Assert.Equal("observation_baseline", Assert.Single(differ.Observe(session, 4, start)).Type);
    }

    [Fact]
    public void Riichi_and_score_deltas_do_not_imply_payment_acceptance_or_a_win()
    {
        var (differ, session, start) = Start();
        var seats = start.Seats.ToArray(); seats[1] = seats[1] with { Riichi = true };
        var events = differ.Observe(session, 2, start with { Seats = seats, Scores = [25000, 24000, 25000, 25000] });
        Assert.Equal(2, events.Length);
        Assert.Contains(events, e => e.Type == "riichi_status_changed" && e.RelativePlayer == 1);
        var score = Assert.Single(events, e => e.Type == "score_changed");
        Assert.Equal(25000, score.ScoreBefore); Assert.Equal(24000, score.ScoreAfter);
        Assert.DoesNotContain(events, e => e.Type is "reach_accepted" or "hora");
    }

    [Fact]
    public void Submitted_or_restored_meld_inventory_does_not_acknowledge_call()
    {
        var (differ, session, start) = Start();
        var meld = Meld.Pon(Tile.FromId(33), Tile.FromId(33), 1);
        var next = start with { Hand = start.Hand.Take(10).ToArray(), OurMelds = [meld] };
        var events = differ.Observe(session, 2, next);
        var change = Assert.Single(events, e => e.Type == "meld_inventory_changed");
        Assert.True(change.HistoryGap);
        Assert.Null(change.MeldKind);
        Assert.DoesNotContain(events, e => e.Type is "pon" or "chi" or "kan");
    }

    [Fact]
    public void Wall_decrease_alone_cannot_invent_an_opponent_draw()
    {
        var (differ, session, start) = Start();
        Assert.Empty(differ.Observe(session, 2, start with { WallRemaining = 49 }));
    }

    [Fact]
    public void Simultaneous_observed_actions_keep_order_unknown()
    {
        var (differ, session, start) = Start();
        var next = River(start with { Hand = start.Hand.Concat([Tile.FromId(31)]).ToArray() }, 3, 1, [Tile.FromId(32)]);
        var events = differ.Observe(session, 2, next);
        Assert.Contains(events, e => e.Type == "draw");
        Assert.Contains(events, e => e.Type == "discard");
        Assert.Contains(events, e => e.Type == "history_gap");
        Assert.All(events, e => Assert.False(e.HistoryComplete));
    }

    [Fact]
    public void Missing_or_reversed_sequences_do_not_bridge_unobserved_history()
    {
        var (differ, session, start) = Start();
        var next = River(start, 1, 1, [Tile.FromId(33)]);
        var skipped = differ.Observe(session, 3, next);
        Assert.Contains(skipped, e => e.Type == "history_gap");
        Assert.Contains(skipped, e => e.Type == "observation_baseline");
        Assert.DoesNotContain(skipped, e => e.Type == "discard");
        Assert.Equal("history_gap", Assert.Single(differ.Observe(session, 2, start)).Type);
        Assert.Equal("observation_baseline", Assert.Single(differ.Observe(session, 4, next)).Type);
    }

    [Fact]
    public void Session_reset_and_round_boundary_drop_previous_baseline()
    {
        var (differ, session, start) = Start();
        Assert.Equal("round_boundary_candidate", Assert.Single(differ.Observe(session, 2, start with { WallRemaining = 70 })).Type);
        Assert.Equal("observation_baseline", Assert.Single(differ.Observe(Guid.NewGuid(), 1, start)).Type);
        differ.Reset();
        Assert.Equal("observation_baseline", Assert.Single(differ.Observe(session, 1, start)).Type);
    }

    [Fact]
    public void Mutable_input_arrays_are_copied_and_invalid_snapshot_breaks_baseline()
    {
        var (differ, session, start) = Start();
        ((Tile[])start.Hand)[0] = Tile.FromId(32);
        Assert.Empty(differ.Observe(session, 2, Snapshot()));
        Assert.Equal("history_gap", Assert.Single(differ.Observe(session, 3, StateSnapshot.Empty)).Type);
        Assert.Equal("observation_baseline", Assert.Single(differ.Observe(session, 4, Snapshot())).Type);
    }

    [Fact]
    public void Batch_size_remains_bounded_under_many_concurrent_changes()
    {
        var (differ, session, start) = Start();
        var seats = start.Seats.Select(s => s with { DiscardCount = 40, Discards = [], Riichi = true }).ToArray();
        var next = start with { Seats = seats, Scores = [0, 0, 0, 0], DoraIndicators = Tiles.Parse("12345m") };
        var events = differ.Observe(session, 2, next);
        Assert.InRange(events.Length, 1, RuntimeEventDiffer.MaximumEventsPerObservation);
        Assert.Contains(events, e => e.Type == "history_gap");
    }

    private static (RuntimeEventDiffer Differ, Guid Session, StateSnapshot Snapshot) Start()
    {
        var differ = new RuntimeEventDiffer(); var session = Guid.NewGuid(); var snapshot = Snapshot();
        differ.Observe(session, 1, snapshot); return (differ, session, snapshot);
    }
    private static StateSnapshot Snapshot() => StateSnapshot.Empty with { Hand = Tiles.Parse("123m456p789s1123z"), WallRemaining = 50 };
    private static StateSnapshot River(StateSnapshot snapshot, int player, int count, Tile[] tiles)
    {
        var seats = snapshot.Seats.ToArray(); seats[player] = seats[player] with { DiscardCount = count, Discards = tiles };
        return snapshot with { Seats = seats };
    }
}
