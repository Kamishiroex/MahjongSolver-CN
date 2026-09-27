using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Cn.PublicState;
using Mahjong.Core;
using Xunit;

namespace Mahjong.Cn.Tests;

/// <summary>Every fixture in this file is synthetic; none establishes a CN UI mapping.</summary>
public sealed class PublicStateTests
{
    private static readonly ObservationReference Observation = new(17, DateTimeOffset.Parse("2026-09-24T00:00:00Z"),
        "synthetic/public-observation", "synthetic-test-only");
    private static Field<T> Known<T>(T value) => Field<T>.Known(value, Observation);
    private static PublicSnapshot ReadyBoundary() => new()
    {
        SessionId = Guid.Parse("358561c0-803f-4223-83cb-8cfb941dc036"), StateRevision = 17,
        Observation = Observation, Stability = StabilityState.Stable,
        Synchronization = SynchronizationState.Synchronized,
    };
    private static ReadinessProfile Profile(params string[] fields) => new("synthetic-backend", fields.ToImmutableArray());
    private static T RoundTrip<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    [Fact] public void Unknown_riichi_melds_and_dealer_are_not_negative_observations()
    {
        var snapshot = new PublicSnapshot();
        Assert.Equal(4, snapshot.Players.Length);
        Assert.Equal(4, snapshot.Players.Select(p => p.Position).Distinct().Count());
        Assert.All(snapshot.Players, player =>
        {
            Assert.Equal(Availability.Unknown, player.RiichiEstablished.Availability);
            Assert.False(player.RiichiEstablished.IsConfirmed);
            Assert.False(player.RiichiEstablished.HasValue);
            Assert.Equal(Availability.Unknown, player.Melds.Availability);
            Assert.False(player.Melds.HasValue);
        });
        Assert.Equal(Availability.Unknown, snapshot.DealerPlayerId.Availability);
        Assert.False(snapshot.DealerPlayerId.IsConfirmed);
        var report = ReadinessEvaluator.Evaluate(snapshot, ReadinessProfiles.CompleteDecision);
        Assert.Contains(report.Issues, i => i.Path == "DealerPlayerId" && i.Code == "unknown");
        Assert.Contains(report.Issues, i => i.Path == "Players.Lower.RiichiEstablished" && i.Code == "unknown");
        Assert.Contains(report.Issues, i => i.Path == "Players.Lower.Melds" && i.Code == "unknown");
    }

    [Fact] public void Missing_new_json_fields_remain_unknown()
    {
        var snapshot = JsonSerializer.Deserialize<PublicSnapshot>("{\"StateRevision\":4}")!;
        Assert.Equal(4, snapshot.StateRevision);
        Assert.Equal(Availability.Unknown, snapshot.DealerPlayerId.Availability);
        Assert.Equal(Availability.Unknown, snapshot.DoraMode.Availability);
        Assert.All(snapshot.Players, p => Assert.False(p.Melds.IsConfirmed));
        Assert.Equal(SynchronizationState.Unknown, snapshot.Synchronization);
    }

    [Fact] public void Complete_default_snapshot_roundtrips_without_fabricating_values()
    {
        var result = RoundTrip(new PublicSnapshot());
        Assert.All(PublicSnapshotFields.Enumerate(result), entry =>
        {
            Assert.Equal(Availability.Unknown, entry.Value.Availability);
            Assert.False(entry.Value.HasValue);
        });
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void Explicit_bool_values_roundtrip(bool value)
    {
        var result = RoundTrip(Known(value));
        Assert.True(result.IsConfirmed);
        Assert.True(result.HasValue);
        Assert.Equal(value, result.Value);
    }

    [Theory] [InlineData(0)] [InlineData(25000)]
    public void Explicit_numeric_values_roundtrip(int value)
    {
        var result = RoundTrip(Known(value));
        Assert.True(result.IsConfirmed);
        Assert.Equal(value, result.Value);
    }

    [Fact] public void Known_empty_and_unknown_default_arrays_remain_distinct_after_json()
    {
        var empty = RoundTrip(Known(ImmutableArray<VisibleMeld>.Empty));
        var unknown = RoundTrip(Field<ImmutableArray<VisibleMeld>>.Unknown("synthetic missing data"));
        var invalidKnown = RoundTrip(Known(default(ImmutableArray<VisibleMeld>)));
        Assert.True(empty.IsConfirmed);
        Assert.True(empty.Value.IsEmpty);
        Assert.False(unknown.HasValue);
        Assert.Equal(Availability.Unknown, unknown.Availability);
        Assert.False(invalidKnown.IsConfirmed);
        Assert.False(invalidKnown.HasValue);
    }

    [Fact] public void Known_metadata_without_value_does_not_invent_zero()
    {
        var json = JsonSerializer.Serialize(Known(25000));
        using var document = JsonDocument.Parse(json);
        var metadataOnly = "{" + string.Join(",", document.RootElement.EnumerateObject()
            .Where(p => p.Name != "Value").Select(p => JsonSerializer.Serialize(p.Name) + ":" + p.Value.GetRawText())) + "}";
        var field = JsonSerializer.Deserialize<Field<int>>(metadataOnly)!;
        Assert.False(field.IsConfirmed);
        var report = ReadinessEvaluator.Evaluate(ReadyBoundary() with { DealerPlayerId = field }, Profile("DealerPlayerId"));
        Assert.Contains(report.Issues, i => i.Code == "value-missing");
    }

    [Fact] public void Camel_case_json_respects_presence_of_explicit_false()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var json = JsonSerializer.Serialize(Known(false), options);
        Assert.Contains("\"value\":false", json);
        Assert.True(JsonSerializer.Deserialize<Field<bool>>(json, options)!.IsConfirmed);
    }

    [Fact] public void Legacy_migration_preserves_values_without_inventing_fixed_identity()
    {
        var original = new VisibleSnapshot
        {
            RoundId = "synthetic.old.round", OurSeat = 2, TurnIndex = 3,
            Honba = 0, RoundWind = 1, OurDoubleRiichi = false,
            Hand = [new(7, new(4, true)), new(8, new(4))],
            DrawnTileKnown = true, DrawnSlot = 7,
            DoraIndicators = [new(27)], LegalFlags = ActionFlags.Discard, DiscardableSlots = [7, 8],
            Seats = [new() { Wind = 0, Score = 25000, Riichi = false, River = [], Melds = [] }],
        };
        var snapshot = LegacySnapshotAdapter.Adapt(original, Guid.NewGuid(), 17, Observation);
        Assert.Equal("synthetic.old.round", snapshot.RoundId.Value);
        Assert.Equal(0, snapshot.Honba.Value);
        Assert.Equal(SourceKind.LegacyAssumption, snapshot.Honba.SourceKind);
        Assert.Equal(MappingStatus.Candidate, snapshot.Honba.MappingStatus);
        Assert.False(snapshot.Honba.IsConfirmed);
        Assert.Equal(2, snapshot.OwnHand.Value.Length);
        Assert.True(snapshot.OwnHand.Value[0].Tile.Red);
        Assert.False(snapshot.OwnHand.Value[1].Tile.Red);
        Assert.Equal(7, snapshot.DrawnTileSlot.Value!.DisplayPosition);
        Assert.Equal(17, snapshot.DrawnTileSlot.Value.StateRevision);
        Assert.Equal(original.Seats[0], Assert.Single(snapshot.LegacySeats).Seat);
        Assert.Equal(2, snapshot.LegacyOurSeat.Value);
        Assert.All(snapshot.Players, player =>
        {
            Assert.Equal(Availability.Unknown, player.PlayerId.Availability);
            Assert.Equal(Availability.Unknown, player.SeatWind.Availability);
        });
        Assert.Equal(Availability.Unknown, snapshot.OurPlayerId.Availability);
        Assert.Equal(Availability.Unknown, snapshot.DealerPlayerId.Availability);
        Assert.Equal(Availability.Unknown, snapshot.LowerVisibleFaces.Availability);
        var roundtrip = RoundTrip(snapshot);
        Assert.Equal(snapshot.OwnHand.Value.ToArray(), roundtrip.OwnHand.Value.ToArray());
        Assert.False(ReadinessEvaluator.Evaluate(snapshot, Profile("OwnHand")).IsReady);
    }

    [Fact] public void Old_empty_json_migration_keeps_unknowns_and_missing_melds()
    {
        var old = JsonSerializer.Deserialize<VisibleSnapshot>("{}")!;
        var result = LegacySnapshotAdapter.Adapt(old, Guid.NewGuid(), 1, Observation);
        Assert.Equal(Availability.Unknown, result.OwnHand.Availability);
        Assert.Equal(Availability.Unknown, result.Honba.Availability);
        Assert.Equal(Availability.Unknown, result.DoraMode.Availability);
        Assert.Equal(Availability.Unknown, result.HasDrawnTile.Availability);
        Assert.All(result.Players, p => Assert.Equal(Availability.Unknown, p.Melds.Availability));
        Assert.False(ReadinessEvaluator.Evaluate(result, ReadinessProfiles.CompleteDecision).IsReady);
    }

    [Fact] public void Legacy_partial_seats_roundtrip_preserves_unknown_river_and_false_riichi()
    {
        var old = new VisibleSnapshot { Seats = [new() { Score = 0, Riichi = false }] };
        var result = RoundTrip(LegacySnapshotAdapter.Adapt(old, Guid.NewGuid(), 1, Observation));
        Assert.True(result.LegacySeatsWerePresent);
        var seat = Assert.Single(result.LegacySeats).Seat;
        Assert.Equal(0, seat.Score);
        Assert.Equal(false, seat.Riichi);
        Assert.True(seat.River.IsDefault);
        Assert.True(seat.Melds.IsDefault);
        Assert.False(result.Players[0].RiichiEstablished.IsConfirmed);
    }

    [Fact] public void Stable_visible_faces_do_not_become_a_complete_hand_or_synchronized_history()
    {
        var snapshot = new PublicSnapshot
        {
            Stability = StabilityState.Stable,
            LowerVisibleFaces = Known(ImmutableArray.Create(new PublicHandTile(new(4, true), new("synthetic/slot/1", 0, 17)))),
        };
        Assert.True(snapshot.LowerVisibleFaces.IsConfirmed);
        Assert.False(snapshot.OwnHand.IsConfirmed);
        Assert.False(ReadinessEvaluator.Evaluate(snapshot, ReadinessProfiles.CompleteDecision).IsReady);
        Assert.True(ReadinessEvaluator.Evaluate(snapshot, ReadinessProfiles.Diagnostics).IsReady);
    }

    [Fact] public void Backend_required_fields_are_scoped_to_that_backend()
    {
        var snapshot = ReadyBoundary() with { RoundWind = Known(0) };
        Assert.True(ReadinessEvaluator.Evaluate(snapshot, Profile("RoundWind")).IsReady);
        Assert.False(ReadinessEvaluator.Evaluate(snapshot, Profile("RoundWind", "DealerPlayerId")).IsReady);
        Assert.True(ReadinessEvaluator.Evaluate(new PublicSnapshot(), ReadinessProfiles.Diagnostics).IsReady);
    }

    [Theory] [InlineData(SourceKind.Observed, MappingStatus.Candidate, "mapping-candidate")]
    [InlineData(SourceKind.LegacyAssumption, MappingStatus.Validated, "legacy-assumption")]
    public void Stable_candidate_and_legacy_fields_are_still_blocked(SourceKind source, MappingStatus mapping, string code)
    {
        var snapshot = ReadyBoundary() with { RoundWind = Field<int>.Known(0, Observation, source, mapping) };
        Assert.Contains(ReadinessEvaluator.Evaluate(snapshot, Profile("RoundWind")).Issues, i => i.Code == code);
    }

    [Fact] public void Conflict_blocks_relevant_backend_but_diagnostics_remain_available()
    {
        var snapshot = ReadyBoundary() with { RoundWind = Field<int>.Conflict(0, Observation, "synthetic conflicting resources") };
        Assert.Contains(ReadinessEvaluator.Evaluate(snapshot, Profile("RoundWind")).Issues, i => i.Code == "conflict");
        Assert.True(ReadinessEvaluator.Evaluate(snapshot, ReadinessProfiles.Diagnostics).IsReady);
    }

    [Fact] public void Animation_and_history_have_independent_readiness_reasons()
    {
        var snapshot = ReadyBoundary() with { Stability = StabilityState.Transition, Synchronization = SynchronizationState.HistoryGap };
        var report = ReadinessEvaluator.Evaluate(snapshot, Profile());
        Assert.Contains(report.Issues, i => i.Code == "unstable");
        Assert.Contains(report.Issues, i => i.Code == "history-gap");
    }

    [Fact] public void Mixed_observation_boundaries_do_not_form_a_trusted_snapshot()
    {
        var snapshot = ReadyBoundary() with
        {
            RoundWind = Field<int>.Known(0, Observation with { Sequence = 16 }),
        };
        Assert.Contains(ReadinessEvaluator.Evaluate(snapshot, Profile("RoundWind")).Issues,
            i => i.Code == "observation-mismatch");
        Assert.True(ReadinessEvaluator.Evaluate(snapshot, ReadinessProfiles.Diagnostics).IsReady);
    }

    [Fact] public void Derived_values_require_documented_input_references()
    {
        var snapshot = ReadyBoundary() with { RoundWind = Field<int>.Known(0, Observation, SourceKind.Derived) };
        Assert.Contains(ReadinessEvaluator.Evaluate(snapshot, Profile("RoundWind")).Issues, i => i.Code == "derivation-inputs-missing");
        snapshot = snapshot with { RoundWind = Field<int>.Known(0, Observation with { DerivationInputs = ["synthetic/input/17"] }, SourceKind.Derived) };
        Assert.True(ReadinessEvaluator.Evaluate(snapshot, Profile("RoundWind")).IsReady);
    }

    [Fact] public void Empty_evidence_cannot_be_presented_as_confirmed()
    {
        var field = Field<int>.Known(0, Observation with { Evidence = null });
        Assert.False(field.IsConfirmed);
        Assert.Contains(ReadinessEvaluator.Evaluate(ReadyBoundary() with { RoundWind = field }, Profile("RoundWind")).Issues,
            i => i.Code == "evidence-missing");
    }

    [Fact] public void Unsupported_rules_block_only_the_declaring_backend()
    {
        var snapshot = ReadyBoundary() with { Rules = new() { RuleSetId = Known("synthetic-rule-A") } };
        var profile = Profile("Rules.RuleSetId") with { SupportedRuleSetIds = ["synthetic-rule-B"] };
        Assert.Contains(ReadinessEvaluator.Evaluate(snapshot, profile).Issues, i => i.Code == "rules-unsupported");
        Assert.True(ReadinessEvaluator.Evaluate(snapshot, profile with { SupportedRuleSetIds = ["synthetic-rule-A"] }).IsReady);
    }

    [Fact] public void Red_and_ordinary_fives_keep_different_entities_and_revision_scoped_slots()
    {
        var first = new UiTileSlot("synthetic/hand/1", 0, 17);
        var second = new UiTileSlot("synthetic/hand/2", 1, 17);
        var snapshot = ReadyBoundary() with
        {
            OwnHand = Known(ImmutableArray.Create(new PublicHandTile(new(4, true), first), new PublicHandTile(new(4), second))),
            DiscardableSlots = Known(ImmutableArray.Create(first, second)), DrawnTileSlot = Known(second),
        };
        Assert.NotEqual(snapshot.OwnHand.Value[0], snapshot.OwnHand.Value[1]);
        Assert.True(ReadinessEvaluator.Evaluate(snapshot, Profile("OwnHand", "DiscardableSlots")).IsReady);
        Assert.Contains(ReadinessEvaluator.Evaluate(snapshot with { StateRevision = 18 }, Profile("OwnHand")).Issues,
            i => i.Code == "stale-slot");
    }

    [Fact] public void Sorting_must_relocate_current_draw_and_discard_slots()
    {
        var current = new UiTileSlot("synthetic/hand/2", 0, 17);
        var stale = new UiTileSlot("synthetic/hand/1", 0, 16);
        var snapshot = ReadyBoundary() with
        {
            OwnHand = Known(ImmutableArray.Create(new PublicHandTile(new(4), current))),
            DrawnTileSlot = Known(stale), DiscardableSlots = Known(ImmutableArray.Create(stale)),
        };
        var report = ReadinessEvaluator.Evaluate(snapshot, Profile("OwnHand", "DiscardableSlots"));
        Assert.Contains(report.Issues, i => i.Code == "drawn-slot-missing");
        Assert.Contains(report.Issues, i => i.Code == "discard-slot-missing");
    }

    [Fact] public void Drawn_slot_is_required_only_when_a_draw_is_confirmed_present()
    {
        var profile = Profile("HasDrawnTile") with { RequireDrawnIdentity = true };
        var snapshot = ReadyBoundary() with { HasDrawnTile = Known(false) };
        Assert.True(ReadinessEvaluator.Evaluate(snapshot, profile).IsReady);
        snapshot = snapshot with { HasDrawnTile = Known(true) };
        Assert.Contains(ReadinessEvaluator.Evaluate(snapshot, profile).Issues, i => i.Path == "DrawnTileSlot");
    }

    [Fact] public void Immutable_snapshot_does_not_change_after_input_array_or_builder_mutation()
    {
        var input = new[] { new PublicHandTile(new(4, true), new("synthetic/hand/1", 0, 17)) };
        var snapshot = ReadyBoundary() with { OwnHand = Known(input.ToImmutableArray()) };
        input[0] = new(new(4), new("synthetic/hand/other", 0, 18));
        var builder = snapshot.OwnHand.Value.ToBuilder();
        builder.Clear();
        Assert.True(Assert.Single(snapshot.OwnHand.Value).Tile.Red);
        Assert.Equal(17, snapshot.OwnHand.Value[0].Slot.StateRevision);
    }

    [Fact] public void Screen_position_does_not_change_when_wind_rotates()
    {
        var player = new PlayerPublicState(ScreenPosition.Left) { PlayerId = Known(2), SeatWind = Known(0) };
        var nextRound = player with { SeatWind = Known(3) };
        Assert.Equal(player.PlayerId, nextRound.PlayerId);
        Assert.Equal(ScreenPosition.Left, nextRound.Position);
        Assert.NotEqual(player.SeatWind, nextRound.SeatWind);
    }

    [Fact] public void Explicit_unknown_dora_mode_is_not_accepted_as_known_indicator_mode()
    {
        var snapshot = ReadyBoundary() with { DoraMode = Known(DoraDisplayMode.Unknown) };
        Assert.Contains(ReadinessEvaluator.Evaluate(snapshot, Profile("DoraMode")).Issues, i => i.Code == "unknown");
    }

    [Fact] public void Missing_or_null_nested_data_returns_readiness_issues_instead_of_throwing()
    {
        var snapshot = ReadyBoundary() with { Rules = null!, DealerPlayerId = null!, Players = [null!] };
        var report = ReadinessEvaluator.Evaluate(snapshot, ReadinessProfiles.CompleteDecision);
        Assert.False(report.IsReady);
        Assert.Contains(report.Issues, i => i.Path == "DealerPlayerId" && i.Code == "field-unavailable");
        Assert.Contains(report.Issues, i => i.Code == "screen-positions");
    }
}
