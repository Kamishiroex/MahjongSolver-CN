using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Core;
using Xunit;

namespace Mahjong.Cn.Tests;

/// <summary>All fixtures here are synthetic. No fixture is a capture from the CN client.</summary>
public sealed class CnCoreTests
{
    private const string Version = "synthetic-test-only";
    private static VisibleSnapshot Sample(string hand = "123m456p789s234s5z6z")
    {
        var tiles = Tiles.Parse(hand).Select((t, i) => new HandTile(i, new(t.Id))).ToImmutableArray();
        return new()
        {
            RoundId = "synthetic.round.1", RoundWind = 0, HandNumber = 1, Honba = 0, RiichiSticks = 0,
            OurSeat = 1, WallRemaining = 69, TurnIndex = 0, OurDoubleRiichi = false,
            Hand = tiles, DrawnTileKnown = true, DrawnSlot = tiles.Length - 1,
            Seats = Enumerable.Range(0, 4).Select(i => new VisibleSeat
            {
                Wind = i, Score = 25000, Riichi = false, Ippatsu = false, RiichiDiscardIndex = -1, River = [], Melds = [],
            }).ToImmutableArray(),
            DoraIndicators = [new(27)], LegalFlags = ActionFlags.Discard,
            DiscardableSlots = tiles.Select(t => t.Slot).ToImmutableArray(),
        };
    }
    private static CnSession Session(bool verified = true) => new(
        new(Version, 99, verified ? "synthetic unit-test evidence; NOT CN validation" : null, verified), Version, 99);
    private static ReadObservation Active(long id, VisibleSnapshot s) => new(id, ObservationPhase.Active, s);
    private static void Valid(VisibleSnapshot s) => Assert.True(SnapshotValidator.Validate(s).IsValid,
        string.Join("; ", SnapshotValidator.Validate(s).Issues.Select(x => x.Path + ":" + x.Message)));
    private static void Invalid(VisibleSnapshot s, string code) => Assert.Contains(SnapshotValidator.Validate(s).Issues, x => x.Code == code);

    [Fact] public void Synthetic_fixture_is_complete_and_valid() => Valid(Sample());
    [Fact] public void Missing_fields_are_unknown_not_zero()
    {
        var result = SnapshotValidator.Validate(new());
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, x => x.Path == "OurSeat" && x.Code == "unknown");
    }
    [Fact] public void Empty_river_and_unknown_river_are_distinct()
    {
        var s = Sample(); Valid(s);
        Invalid(s with { Seats = s.Seats.SetItem(0, s.Seats[0] with { River = default }) }, "unknown");
    }
    [Fact] public void Fifth_public_copy_is_rejected()
    {
        var s = Sample("1111m456p789s234s");
        Invalid(s with { DoraIndicators = [new(0)] }, "copies");
    }
    [Fact] public void Red_and_ordinary_fives_share_four_copy_limit()
    {
        var s = Sample("5555m456p789s234s");
        s = s with { Hand = s.Hand.SetItem(0, new(0, new(4, true))), DoraIndicators = [new(4)] };
        Invalid(s, "copies");
    }
    [Fact] public void Unknown_score_is_rejected()
    {
        var s = Sample(); Invalid(s with { Seats = s.Seats.SetItem(1, s.Seats[1] with { Score = null }) }, "unknown");
    }
    [Fact] public void Score_spread_cannot_overflow_upstream_arithmetic()
    {
        var s = Sample();
        Invalid(s with { Seats = s.Seats.SetItem(0, s.Seats[0] with { Score = int.MaxValue }).SetItem(1, s.Seats[1] with { Score = -1 }) }, "score-range");
    }
    [Fact] public void Out_of_range_tile_is_rejected()
    {
        var s = Sample(); Invalid(s with { Hand = s.Hand.SetItem(0, new(0, new(34))) }, "tile-id");
    }
    [Theory] [InlineData(4)] [InlineData(13)] [InlineData(22)]
    public void Red_five_remains_same_34_kind(int id)
    {
        var s = Sample("123m456p789s234s55m");
        s = s with { Hand = s.Hand.SetItem(12, new(12, new(id, true))) };
        Valid(s);
        var mapped = CnSuggestionBridge.ToEngineSnapshot(s);
        Assert.Equal(1, mapped.AkaDora);
        Assert.Equal(id, mapped.Hand[12].Id);
    }
    [Fact] public void Red_honor_is_rejected()
    {
        var s = Sample(); Invalid(s with { DoraIndicators = [new(27, true)] }, "red-five");
    }
    [Fact] public void Missing_draw_identity_blocks_validation() => Invalid(Sample() with { DrawnTileKnown = false }, "unknown");
    [Fact] public void Draw_slot_must_exist() => Invalid(Sample() with { DrawnSlot = 42 }, "draw-slot");
    [Fact] public void Draw_is_last_in_engine_but_ui_slot_is_preserved()
    {
        var s = Sample() with { DrawnSlot = 0 };
        var engine = CnSuggestionBridge.ToEngineSnapshot(s);
        Assert.Equal(s.Hand[0].Tile.Id, engine.Hand[^1].Id);
        Assert.Equal(0, s.Hand[0].Slot);
        Assert.Equal(13, s.Hand[^1].Slot);
    }
    [Fact] public void Sort_changes_preserve_slot_identity()
    {
        var s = Sample();
        var reversed = s with { Hand = s.Hand.Reverse().ToImmutableArray() };
        Assert.Equal(CnSuggestionBridge.ToEngineSnapshot(s).Hand, CnSuggestionBridge.ToEngineSnapshot(reversed).Hand);
    }
    [Fact] public void Duplicate_hand_slots_are_rejected()
    {
        var s = Sample(); Invalid(s with { Hand = s.Hand.SetItem(1, s.Hand[1] with { Slot = 0 }) }, "slot");
    }
    [Fact] public void Illegal_discard_slot_is_rejected() => Invalid(Sample() with { DiscardableSlots = [42] }, "slot");
    [Fact] public void Unknown_action_bits_are_rejected() => Invalid(Sample() with { LegalFlags = (ActionFlags)(1 << 15) }, "flags");
    [Fact] public void Reaction_and_own_turn_flags_cannot_coexist() => Invalid(Sample() with { LegalFlags = ActionFlags.Discard | ActionFlags.Ron }, "legal");
    [Fact] public void Tsumo_requires_known_draw_slot() => Invalid(Sample() with { LegalFlags = ActionFlags.Discard | ActionFlags.Tsumo, DrawnSlot = null }, "draw-slot");
    [Theory] [InlineData("{}")][InlineData("{\"Id\":4}")][InlineData("{\"Red\":false}")]
    public void Json_tile_missing_identity_or_red_status_cannot_become_one_man(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<VisibleTile>(json));
    [Fact] public void Discard_flag_requires_legal_slots() => Invalid(Sample() with { DiscardableSlots = [] }, "legal");
    [Fact] public void Mismatched_riichi_is_rejected()
    {
        var s = Sample(); Invalid(s with { Seats = s.Seats.SetItem(1, s.Seats[1] with { Riichi = true }) }, "riichi");
    }

    private static VisibleSnapshot WithOwnMeld(MeldKind kind = MeldKind.Pon)
    {
        var s = Sample("123m456p789s12p") with { DrawnSlot = null };
        var source = s.Seats[0] with { River = [new(new(33), true, true)] };
        var tiles = Enumerable.Repeat(new VisibleTile(33), kind == MeldKind.Pon ? 3 : 4).ToImmutableArray();
        var us = s.Seats[1] with { Melds = [new(kind, tiles, kind == MeldKind.AnKan ? null : 0,
            kind == MeldKind.AnKan ? null : 0, kind == MeldKind.AnKan ? null : new(33))] };
        return s with { Seats = s.Seats.SetItem(0, kind == MeldKind.AnKan ? s.Seats[0] : source).SetItem(1, us) };
    }

    [Fact] public void Claimed_river_tile_is_counted_once() => Valid(WithOwnMeld());
    [Fact] public void Own_pon_then_discard_has_eleven_closed_tiles() => Valid(WithOwnMeld());
    [Theory] [InlineData(MeldKind.AnKan)] [InlineData(MeldKind.MinKan)] [InlineData(MeldKind.ShouMinKan)]
    public void All_kans_validate_four_physical_tiles_and_three_effective(MeldKind kind) => Valid(WithOwnMeld(kind));
    [Fact] public void Kan_engine_limitation_is_reported()
    {
        Assert.Throws<NotSupportedException>(() => CnSuggestionBridge.Evaluate(WithOwnMeld(MeldKind.AnKan)));
    }
    [Fact] public void Invalid_meld_shape_is_rejected()
    {
        var s = WithOwnMeld(); var us = s.Seats[1];
        Invalid(s with { Seats = s.Seats.SetItem(1, us with { Melds = [us.Melds[0] with { Tiles = [new(33), new(32), new(33)] }] }) }, "meld-shape");
    }
    [Fact] public void Claimed_river_requires_matching_meld()
    {
        var s = Sample(); Invalid(s with { Seats = s.Seats.SetItem(0, s.Seats[0] with { River = [new(new(33), true, true)] }) }, "orphan-claim");
    }
    [Fact] public void Evidence_gate_rejects_stable_synthetic_state_in_unverified_profile()
    {
        var session = Session(false); var s = Sample(); session.Observe(Active(1, s)); session.Observe(Active(2, s));
        Assert.Equal("profile-unverified", session.Status.Code); Assert.Null(session.CurrentSuggestion);
    }
    [Fact] public void Verified_test_profile_requires_two_distinct_captures()
    {
        var session = Session(); var s = Sample(); session.Observe(Active(1, s));
        Assert.Equal("stabilizing", session.Status.Code); Assert.Null(session.CurrentSuggestion);
        session.Observe(Active(2, s)); Assert.True(session.Status.Ready); Assert.NotNull(session.CurrentSuggestion);
    }
    [Fact] public void Repeated_capture_id_cannot_create_stability()
    {
        var session = Session(); var s = Sample(); session.Observe(Active(1, s)); session.Observe(Active(1, s));
        Assert.Equal("stale-capture", session.Status.Code); Assert.Null(session.CurrentSuggestion);
    }
    [Fact] public void Duplicate_snapshot_does_not_accumulate_public_tiles()
    {
        var session = Session(); var s = Sample();
        for (int i = 1; i <= 4; i++) session.Observe(Active(i, s));
        Assert.True(session.Status.Ready); Assert.Equal(14, session.CurrentSnapshot!.Hand.Length);
    }
    [Theory] [InlineData(ObservationPhase.OutsideTable)] [InlineData(ObservationPhase.Transition)] [InlineData(ObservationPhase.ReadError)]
    public void Nonactive_phase_clears_previous_suggestion(ObservationPhase phase)
    {
        var session = Session(); var s = Sample(); session.Observe(Active(1, s)); session.Observe(Active(2, s));
        session.Observe(new(3, phase, Error: "synthetic read error"));
        Assert.Null(session.CurrentSuggestion); Assert.Null(session.CurrentSnapshot); Assert.False(session.AutomationEnabled);
        session.Observe(Active(4, s)); Assert.Equal("stabilizing", session.Status.Code);
    }
    [Fact] public void New_round_needs_new_stability_and_clears_old_advice()
    {
        var session = Session(); var s = Sample(); session.Observe(Active(1, s)); session.Observe(Active(2, s));
        session.Observe(Active(3, s with { RoundId = "synthetic.round.2", HandNumber = 2 }));
        Assert.Null(session.CurrentSuggestion); Assert.Equal("stabilizing", session.Status.Code);
    }
    [Fact] public void Invalid_state_clears_old_suggestion()
    {
        var session = Session(); var s = Sample(); session.Observe(Active(1, s)); session.Observe(Active(2, s));
        session.Observe(Active(3, s with { OurSeat = null }));
        Assert.Null(session.CurrentSuggestion); Assert.Equal("invalid-state", session.Status.Code);
    }
    [Fact] public void Retired_round_cannot_return_after_new_round()
    {
        var session = Session(); var old = Sample();
        session.Observe(Active(1, old)); session.Observe(Active(2, old with { RoundId = "synthetic.round.2", HandNumber = 2 }));
        session.Observe(Active(3, old)); session.Observe(Active(4, old));
        Assert.Equal("retired-round", session.Status.Code); Assert.Null(session.CurrentSuggestion);
    }
    [Fact] public void Same_round_wall_increase_is_rejected()
    {
        var session = Session(); var s = Sample();
        session.Observe(Active(1, s with { WallRemaining = 60, TurnIndex = 9 }));
        session.Observe(Active(2, s));
        Assert.Equal("state-regression", session.Status.Code); Assert.Null(session.CurrentSuggestion);
    }
    [Fact] public void Torn_score_update_clears_snapshot_and_suggestion()
    {
        var session = Session(); var s = Sample();
        session.Observe(Active(1, s)); session.Observe(Active(2, s));
        session.Observe(Active(3, s with { Seats = s.Seats.SetItem(1, s.Seats[1] with { Score = 24000 }) }));
        Assert.Equal("score-inconsistent", session.Status.Code); Assert.Null(session.CurrentSnapshot); Assert.Null(session.CurrentSuggestion);
    }
    [Fact] public void Score_debit_and_matching_riichi_deposit_preserve_total()
    {
        var session = Session(); var s = Sample(); session.Observe(Active(1, s));
        var reached = s with
        {
            RiichiSticks = 1, DiscardableSlots = [13],
            Seats = s.Seats.SetItem(1, s.Seats[1] with
            {
                Score = 24000, Riichi = true, RiichiDiscardIndex = 0, River = [new(new(30), true, false)],
            }),
        };
        session.Observe(Active(2, reached)); session.Observe(Active(3, reached));
        Assert.True(session.Status.Ready); Assert.NotNull(session.CurrentSuggestion);
    }
    [Fact] public void Unsupported_engine_state_clears_both_snapshot_and_suggestion()
    {
        var session = Session(); var s = WithOwnMeld(MeldKind.AnKan);
        session.Observe(Active(1, s)); session.Observe(Active(2, s));
        Assert.Equal("engine-unavailable", session.Status.Code); Assert.Null(session.CurrentSnapshot); Assert.Null(session.CurrentSuggestion);
    }
    [Fact] public void Version_mismatch_blocks_even_verified_profile()
    {
        var session = new CnSession(new(Version, 99, "synthetic", true), "other", 99);
        session.Observe(Active(1, Sample())); Assert.Equal("version-mismatch", session.Status.Code);
    }
    [Fact] public void Stop_is_sticky_until_explicit_read_only_resume()
    {
        var session = Session(); session.Stop(); session.Observe(Active(1, Sample()));
        Assert.Equal("stopped", session.Status.Code); session.ResumeReadOnly(); session.Observe(Active(1, Sample()));
        Assert.Equal("stabilizing", session.Status.Code); Assert.False(session.AutomationEnabled);
    }
    [Fact] public void Every_action_remains_disabled()
    {
        var adapter = new DisabledActionAdapter();
        Assert.Equal(Enum.GetValues<CnAction>().Length, adapter.Capabilities.Length);
        foreach (var capability in adapter.Capabilities)
        {
            Assert.False(capability.Enabled); Assert.False(adapter.Execute(capability.Action, Sample(), 0).Executed);
        }
    }
    [Fact] public void Engine_mutation_does_not_change_public_snapshot()
    {
        var s = WithOwnMeld(); var engine = CnSuggestionBridge.ToEngineSnapshot(s);
        engine.OurMelds[0].Tiles[0] = new Tile(0);
        Assert.Equal(33, s.Seats[1].Melds[0].Tiles[0].Id);
    }
    [Fact] public void Upstream_synthetic_example_gives_legal_honor_discard_and_chinese_reason()
    {
        var s = Sample(); var advice = CnSuggestionBridge.Evaluate(s);
        Assert.True(advice.DiscardTile!.Value.Id is 31 or 32);
        Assert.Contains(advice.DiscardSlot!.Value, s.DiscardableSlots);
        Assert.Contains("向听", advice.Reason); Assert.NotEmpty(advice.Alternatives);
    }
    [Fact] public void Riichi_tsumogiri_restriction_filters_engine_candidates()
    {
        var s = Sample();
        s = s with
        {
            DiscardableSlots = [13],
            Seats = s.Seats.SetItem(1, s.Seats[1] with { Riichi = true, RiichiDiscardIndex = 0, River = [new(new(30), true, false)] }),
        };
        var advice = CnSuggestionBridge.Evaluate(s);
        Assert.Equal(13, advice.DiscardSlot); Assert.Single(advice.Alternatives);
    }
    [Fact] public void Same_kind_prefers_ordinary_five_and_preserves_red_slot()
    {
        var s = Sample("123m456p789s234s55m");
        s = s with { Hand = s.Hand.SetItem(12, new(12, new(4, true))), DiscardableSlots = [12, 13] };
        var advice = CnSuggestionBridge.Evaluate(s);
        Assert.Equal(13, advice.DiscardSlot); Assert.False(advice.DiscardTile!.Value.Red);
    }
    [Fact] public void Red_only_legal_slot_is_not_confused_with_ordinary_five()
    {
        var s = Sample("123m456p789s234s55m");
        s = s with { Hand = s.Hand.SetItem(12, new(12, new(4, true))), DiscardableSlots = [12] };
        var advice = CnSuggestionBridge.Evaluate(s);
        Assert.Equal(12, advice.DiscardSlot); Assert.True(advice.DiscardTile!.Value.Red);
    }
}
