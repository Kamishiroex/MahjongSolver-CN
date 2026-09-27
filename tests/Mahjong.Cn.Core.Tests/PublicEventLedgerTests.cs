using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Cn.Events;
using Mahjong.Cn.PublicState;
using Mahjong.Core;
using Xunit;

namespace Mahjong.Cn.Tests;

// Synthetic managed public observations: these tests establish no live CN behavior.
public sealed class PublicEventLedgerTests
{
    private static readonly Guid Session = Guid.Parse("e4508b71-0502-497c-b45a-d2ea7c851a0e");
    private static ObservationReference Obs(long sequence) => new(sequence,
        DateTimeOffset.Parse("2026-09-24T00:00:00Z").AddSeconds(sequence), "synthetic", "synthetic only");
    private static PublicSnapshot Snapshot(long revision, params VisibleTile[] tiles) => new()
    {
        SessionId = Session, StateRevision = revision, Observation = Obs(revision), Stability = StabilityState.Stable,
        Synchronization = SynchronizationState.HistoryGap,
        OwnHand = Field<ImmutableArray<PublicHandTile>>.Known(tiles.Select((t, index) =>
            new PublicHandTile(t, new($"lower/{index}", index, revision))).ToImmutableArray(), Obs(revision)),
    };

    [Fact] public void Sampling_metadata_does_not_duplicate_identical_observation_but_sort_and_red_changes_are_logged()
    {
        var ledger = new PublicEventLedger(Session);
        ledger.Observe(Snapshot(1, new VisibleTile(4, true), new VisibleTile(1)));
        Assert.True(ledger.Observe(Snapshot(2, new VisibleTile(4, true), new VisibleTile(1))).Duplicate);
        Assert.False(ledger.Observe(Snapshot(3, new VisibleTile(1), new VisibleTile(4, true))).Duplicate);
        Assert.False(ledger.Observe(Snapshot(4, new VisibleTile(1), new VisibleTile(4))).Duplicate);
        Assert.Equal(3, ledger.Entries.Count(e => e.Kind == GameEventKind.SnapshotObserved));
        Assert.DoesNotContain(ledger.Entries, e => e.Kind is GameEventKind.Draw or GameEventKind.Discard);
    }

    [Fact] public void Submitted_actions_and_transitions_never_become_game_events()
    {
        var ledger = new PublicEventLedger(Session);
        ledger.Observe(Snapshot(1, new VisibleTile(4)));
        ledger.RecordSubmitted(new() { Action = "discard", Tile = new VisibleTile(4), ActorPosition = ScreenPosition.Lower }, Obs(2));
        ledger.Observe(Snapshot(3) with { Stability = StabilityState.Transition });
        Assert.Null(ledger.LatestSnapshot);
        Assert.DoesNotContain(ledger.Entries, e => e.Kind is GameEventKind.Discard or GameEventKind.Draw);
        Assert.Single(ledger.Entries.Where(e => e.Kind == GameEventKind.ActionSubmitted));
    }

    [Fact] public void Current_round_sample_reference_and_river_pulse_do_not_repeat_full_snapshots()
    {
        PublicSnapshot Frame(long sample, short brightness, string style = "unclassified", bool claimed = false,
            long opening = 1, bool highlight = true)
        {
            var observation = Obs(sample);
            var tile = new PublicImageTile(new VisibleTile(4), "river/0", "river", 1, 2, 40, 56, 0, false, false)
            {
                Style = new(brightness, brightness, brightness, 100, 100, 100, style) { ResponseHighlight = highlight },
                WasClaimed = Field<bool>.Known(claimed, observation),
            };
            return Snapshot(sample, new VisibleTile(1)) with
            {
                RoundId = Field<string>.Known("round-one", observation with
                {
                    DerivationInputs = [$"confirmed-opening-observation:{opening}", $"current-identity-observation:{sample}"],
                }),
                Players = [new(ScreenPosition.Lower)
                {
                    RiverImages = Field<PublicImageInventory>.Known(new([tile], false, true, true, false, 1, 0, []), observation),
                }],
            };
        }
        var ledger = new PublicEventLedger(Session);
        ledger.Observe(Frame(1, 1));
        for (int sample = 2; sample <= 600; sample++)
        {
            short brightness = (short)((sample % 4) switch { 0 => 0, 1 => 23, 2 => 45, _ => 0 });
            Assert.True(ledger.Observe(Frame(sample, brightness, brightness == 0 ? "normal" : "unclassified")).Duplicate);
        }
        Assert.Single(ledger.Entries.Where(e => e.Kind == GameEventKind.SnapshotObserved));
        Assert.Equal(600, ledger.LatestSnapshot!.Observation!.Sequence);
        Assert.False(ledger.Observe(Frame(601, 0, "normal", highlight: false)).Duplicate);
        Assert.False(ledger.Observe(Frame(602, 0, "red-tinted", claimed: true, highlight: false)).Duplicate);
        Assert.False(ledger.Observe(Frame(603, 0, "red-tinted", claimed: true, opening: 9, highlight: false)).Duplicate);

        // Semantic equality is only for deduplication. Durable replay still detects any
        // raw evidence modification, including the supposedly unimportant pulse value.
        var entry = ledger.Entries.First(e => e.Snapshot is not null);
        var changed = entry with { Snapshot = Frame(1, 99) };
        var replay = PublicEventLedger.Replay(ledger.Entries.Select(x => x == entry ? changed : x));
        Assert.Contains(replay.Issues, issue => issue.Code == "snapshot-digest-mismatch");
    }

    [Fact] public void Fingerprint_never_hides_unidentified_brightness_genuine_tint_or_multiplier_changes()
    {
        static string Hash(PublicImageStyle style) => PublicObservationFingerprint.Compute(JsonSerializer.SerializeToElement(new { Style = style }));
        var raw = new PublicImageStyle(23, 23, 23, 100, 100, 100, "unclassified");
        Assert.NotEqual(Hash(raw), Hash(raw with { AddRed = 45, AddGreen = 45, AddBlue = 45 }));
        var highlight = raw with { ResponseHighlight = true };
        Assert.Equal(Hash(highlight), Hash(highlight with { AddRed = 0, AddGreen = 0, AddBlue = 0, Name = "normal" }));
        Assert.NotEqual(Hash(highlight), Hash(highlight with { AddRed = -75, AddGreen = -75, AddBlue = -75, Name = "darkened" }));
        Assert.NotEqual(Hash(highlight), Hash(highlight with { AddRed = 0, AddGreen = -75, AddBlue = -75, Name = "red-tinted" }));
        Assert.NotEqual(Hash(highlight), Hash(highlight with { MultiplyRed = 99 }));
        Assert.NotEqual(Hash(highlight), Hash(highlight with { ResponseHighlight = false }));
    }

    [Theory] [InlineData(GameEventKind.Draw)] [InlineData(GameEventKind.Discard)]
    [InlineData(GameEventKind.Chi)] [InlineData(GameEventKind.Pon)] [InlineData(GameEventKind.OpenKan)]
    [InlineData(GameEventKind.ClosedKan)] [InlineData(GameEventKind.AddedKan)] [InlineData(GameEventKind.RiichiDeclared)]
    [InlineData(GameEventKind.RiichiEstablished)] [InlineData(GameEventKind.Ron)] [InlineData(GameEventKind.Tsumo)]
    [InlineData(GameEventKind.ExhaustiveDraw)] [InlineData(GameEventKind.RoundStarted)] [InlineData(GameEventKind.RoundEnded)]
    public void Explicit_happened_events_need_source_validation_and_are_separate_from_snapshots(GameEventKind kind)
    {
        var ledger = new PublicEventLedger(Session);
        VisibleMeld meld = kind switch
        {
            GameEventKind.Chi => new(MeldKind.Chi, [new VisibleTile(0), new VisibleTile(1), new VisibleTile(2)], 1, 0, new VisibleTile(0)),
            GameEventKind.ClosedKan => new(MeldKind.AnKan, [new VisibleTile(27), new VisibleTile(27), new VisibleTile(27), new VisibleTile(27)], null, null, null),
            GameEventKind.OpenKan => new(MeldKind.MinKan, [new VisibleTile(27), new VisibleTile(27), new VisibleTile(27), new VisibleTile(27)], 1, 0, new VisibleTile(27)),
            GameEventKind.AddedKan => new(MeldKind.ShouMinKan, [new VisibleTile(27), new VisibleTile(27), new VisibleTile(27), new VisibleTile(27)], 1, 0, new VisibleTile(27)),
            _ => new(MeldKind.Pon, [new VisibleTile(27), new VisibleTile(27), new VisibleTile(27)], 1, 0, new VisibleTile(27)),
        };
        var details = new GameEventDetails { ActorPosition = ScreenPosition.Lower, Tile = new VisibleTile(27), Meld = meld };
        var candidate = new ConfirmedGameEvent(kind, Obs(1), details);
        Assert.Empty(ledger.RecordOccurred(candidate).Entries);
        var actual = candidate with { MappingStatus = MappingStatus.Validated };
        Assert.Equal(kind, Assert.Single(ledger.RecordOccurred(actual).Entries).Kind);
        Assert.True(ledger.RecordOccurred(actual).Duplicate);
        Assert.Null(ledger.LatestSnapshot);
        Assert.False(ledger.HistoryComplete);
    }

    [Fact] public void Pause_preserves_current_state_but_actual_gap_and_round_boundaries_invalidate_it()
    {
        var ledger = new PublicEventLedger(Session);
        ledger.Observe(Snapshot(1, new VisibleTile(4)));
        ledger.RecordControl(GameEventKind.Paused, Obs(2));
        Assert.NotNull(ledger.LatestSnapshot);
        ledger.RecordControl(GameEventKind.Resumed, Obs(3));
        Assert.NotNull(ledger.LatestSnapshot);
        ledger.MarkGap("reader-failed", Obs(4));
        Assert.Null(ledger.LatestSnapshot);
        ledger.Observe(Snapshot(5, new VisibleTile(5)));
        ledger.RecordOccurred(new(GameEventKind.RoundEnded, Obs(6), new()) { MappingStatus = MappingStatus.Validated });
        Assert.Null(ledger.LatestSnapshot);
        Assert.True(ledger.HasGap);
    }

    [Fact] public void Old_session_out_of_order_and_reused_sequences_do_not_replace_live_state()
    {
        var ledger = new PublicEventLedger(Session);
        ledger.Observe(Snapshot(3, new VisibleTile(4)));
        Assert.Contains(ledger.Observe(Snapshot(2)).Issues, x => x.Code == "observation-out-of-order");
        Assert.Contains(ledger.Observe(Snapshot(4) with { SessionId = Guid.NewGuid() }).Issues, x => x.Code == "session-mismatch");
        ledger.Observe(Snapshot(5, new VisibleTile(4)));
        Assert.Contains(ledger.Observe(Snapshot(5, new VisibleTile(5))).Issues, x => x.Code == "observation-sequence-reused");
        Assert.Null(ledger.LatestSnapshot);
    }

    [Fact] public void Json_roundtrip_replay_keeps_known_empty_unknown_and_red_identity_and_marks_partial_tail()
    {
        var ledger = new PublicEventLedger(Session);
        ledger.Observe(Snapshot(1, new VisibleTile(4, true)));
        ledger.Checkpoint(Obs(2));
        var parsed = JsonSerializer.Deserialize<PublicGameEvent[]>(JsonSerializer.Serialize(ledger.Entries))!;
        var replay = PublicEventLedger.Replay(parsed, partialTail: true);
        Assert.True(replay.HasGap);
        Assert.False(replay.HistoryComplete);
        Assert.Contains(replay.Issues, i => i.Code == "partial-tail");
        Assert.True(replay.LatestSnapshot!.OwnHand.Value[0].Tile.Red);
        Assert.False(replay.LatestSnapshot.Players[0].Melds.HasValue);
        Assert.All(replay.Entries, entry => Assert.Equal(Session, entry.SessionId));
    }

    [Fact] public void Digest_tampering_sequence_gap_and_bounded_retention_are_detected()
    {
        var ledger = new PublicEventLedger(Session, 4);
        ledger.Observe(Snapshot(1, new VisibleTile(4)));
        var original = ledger.Entries;
        var tampered = original.SetItem(1, original[1] with { Snapshot = Snapshot(1, new VisibleTile(5)) });
        Assert.Contains(PublicEventLedger.Replay(tampered).Issues, i => i.Code == "snapshot-digest-mismatch");
        Assert.Contains(PublicEventLedger.Replay(original.Skip(1)).Issues, i => i.Code == "sequence-gap");
        for (int i = 2; i < 20; i++) ledger.Observe(Snapshot(i, new VisibleTile(i % 34)));
        Assert.True(ledger.Entries.Length <= 4);
        Assert.Contains(ledger.Entries, e => e.Code == "retained-prefix-truncated");
        Assert.True(ledger.HasGap);
        Assert.True(ledger.Entries.Zip(ledger.Entries.Skip(1)).All(pair => pair.First.Sequence < pair.Second.Sequence));
    }

    [Fact] public void Captured_entries_are_immutable_and_errors_are_not_gameplay_events()
    {
        var ledger = new PublicEventLedger(Session);
        ledger.Observe(Snapshot(1, new VisibleTile(4)));
        var first = ledger.Entries;
        var failure = ledger.RecordOccurred(new(GameEventKind.ActionSubmitted, Obs(2), new()) { MappingStatus = MappingStatus.Validated });
        Assert.NotEmpty(failure.Issues);
        Assert.Empty(failure.Entries);
        ledger.Observe(Snapshot(3, new VisibleTile(5)));
        Assert.Equal(2, first.Length);
        Assert.Equal(new VisibleTile(4), first[1].Snapshot!.OwnHand.Value[0].Tile);
        Assert.DoesNotContain(ledger.Entries, e => e.Code == "event-unverified");
    }
}
