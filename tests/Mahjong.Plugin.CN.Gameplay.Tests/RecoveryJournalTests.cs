using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Cn;
using Mahjong.Cn.Events;
using Mahjong.Core;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Journaling;
using Mahjong.Plugin.Dalamud.GameState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Synthetic public image fixtures use the pinned resource catalog. No live mapping is asserted.</summary>
public sealed class RecoveryJournalTests
{
    private static readonly RuntimeIdentity Identity = new(RuntimeIdentity.TargetGame, 15, "synthetic",
        RuntimeIdentity.TargetDalamud, RuntimeIdentity.TargetStructs, "ChineseSimplified", "synthetic", null);
    private static readonly Guid Session = Guid.Parse("9f8c3422-548a-4206-aa72-d44f2ce77bc8");
    private static Tile[] Hand => new[] { 0, 1, 2, 3, 4, 6, 7, 8, 9, 10 }.Select(Tile.FromId).ToArray();
    private static Meld Pon => Meld.Pon(Tile.FromId(27), Tile.FromId(27), 1);
    private static StateSnapshot Snapshot => StateSnapshot.Empty with
    {
        Hand = Hand, OurMelds = [Pon], AkaDora = 1, AddonStateCode = 30, WallRemaining = 50,
        DoraIndicators = [Tile.FromId(18)],
        Seats = StateSnapshot.Empty.Seats.Select((s, i) => i == 0 ? s with
        { DiscardCount = 1, Discards = [Tile.FromId(31)], DiscardIsTedashi = [true] } : s).ToArray(),
    };
    private static SavedRecoveryState Saved => new(1, Identity, "synthetic", Session, 12, Snapshot,
        new(1, [Pon], 0, Hand, [1, 0, 0, 0], 1, 50), 8, Lower(Hand), Table());
    private static RecoveryCandidate Candidate => new(Saved, DateTimeOffset.UtcNow.AddSeconds(-10), "synthetic", false);

    private static LowerHandReading Lower(IEnumerable<Tile> hand) => new("LOWER_STABLE", "synthetic only", true,
        hand.Select((tile, index) =>
        {
            uint icon = tile.Id == 4 ? 76035u : 76001u + tile.Id;
            uint hash = LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex");
            Assert.True(LowerTileCatalog.TryDecode(icon, hash, out var decoded));
            return new DecodedLowerFace($"Emj/134/{index}/3", index, icon, hash,
                decoded.Kind34, decoded.RedFive, decoded.ChineseName);
        }).ToImmutableArray());

    private static PublicTableReading Table(bool extendedRiver = false, bool fourthMeldTile = false)
    {
        static PublicTableFaceCandidate Face(string path, string group, string area, int id) =>
            new(area, "bottom", path, group, 1021, 100, 200, 40, 56, 0, false, 76001u + (uint)id,
                LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{76001 + id:D6}.tex"), PublicTableImageReader.VerifiedResourceCode);
        var faces = new List<PublicTableFaceCandidate> { Face("Emj/118/4", "Emj/118", "river-bottom", 31) };
        if (extendedRiver) faces.Add(Face("Emj/1180001/4", "Emj/1180001", "river-bottom", 0));
        for (int i = 2; i < (fourthMeldTile ? 6 : 5); i++) faces.Add(Face($"Emj/112/{i}/9/4", "Emj/112", "meld-bottom", 27));
        var areas = new[] { "bottom", "right", "top", "left" }.Select(direction =>
            new PublicTableAreaStatus("river-" + direction, "synthetic-enumerated", true, true, true,
                direction != "bottom", direction == "bottom" ? (extendedRiver ? 2 : 1) : 0, 0)).ToArray();
        var probe = new AddonProbe("Emj", true, true, true, 109, [], null, PublicTableFaces: faces, PublicTableAreas: areas);
        var tracker = new PublicTableTracker();
        tracker.Observe(1, probe);
        return tracker.Observe(2, probe);
    }

    [Fact]
    public async Task Independent_checkpoint_recovers_without_scanning_corrupt_or_oversized_history()
    {
        using var folder = new Folder();
        var journal = new GameJournal(folder.Path);
        journal.Event("discard", new { Tile = 3 });
        journal.UpdateRecovery(Saved);
        await journal.CompleteAsync();
        // Would fail the legacy full-stream scan before even looking for the checkpoint.
        await File.WriteAllTextAsync(Path.Combine(journal.DirectoryPath, "events.jsonl"), "corrupt history\n");
        var result = await RecoveryJournal.LoadAsync(folder.Path, Identity);
        var candidate = Assert.Single(result.Candidates);
        Assert.False(candidate.State.AutomaticEnabled); Assert.False(candidate.State.FullHistoryVerified);
        Assert.True(RecoveryEvidenceBuilder.TryBuild(candidate, Snapshot with { OurMelds = [] }, 0,
            Lower(Hand), Table(), Guid.NewGuid(), 2, out var reconciliation, out _, out _));
        Assert.False(reconciliation.HistoryComplete);
    }

    [Fact]
    public async Task Independent_checkpoint_from_another_record_is_rejected()
    {
        using var folder = new Folder();
        var original = new GameJournal(folder.Path); original.UpdateRecovery(Saved); await original.CompleteAsync();
        var other = new GameJournal(folder.Path); other.Event("discard", new { Tile = 3 }); await other.CompleteAsync();
        File.Copy(Path.Combine(original.DirectoryPath, "recovery-latest.jsonl"), Path.Combine(other.DirectoryPath, "recovery-latest.jsonl"));
        var result = await RecoveryJournal.LoadAsync(folder.Path, Identity, excludeDirectory: original.DirectoryPath);
        Assert.Empty(result.Candidates); Assert.Contains("RECOVERY_SESSION_MISMATCH", result.Errors);
    }

    [Fact]
    public async Task Atomic_latest_checkpoint_takes_priority_over_older_rotated_history_without_arming()
    {
        using var folder = new Folder();
        var journal = new GameJournal(folder.Path, 2048);
        journal.Event("recovery_checkpoint", Saved);
        for (int i = 0; i < 10; i++) { journal.Event("discard", new { Index = i, Text = new string('x', 300) }); await journal.Completion; }
        journal.UpdateRecovery(Saved with { ObservationSequence = 200, RuntimeSequence = 100 });
        await journal.CompleteAsync(); Assert.Null(journal.Fault);
        Assert.True(GameJournal.StreamFiles(journal.DirectoryPath, "events.jsonl").Length > 1);
        var loaded = await RecoveryJournal.LoadAsync(folder.Path, Identity);
        Assert.Empty(loaded.Errors);
        Assert.Equal(200, loaded.Candidates[0].State.ObservationSequence);
        Assert.False(loaded.Candidates[0].State.AutomaticEnabled);
        Assert.False(loaded.Candidates[0].State.FullHistoryVerified);
    }

    [Fact]
    public async Task Persisted_checkpoint_roundtrip_preserves_tile_ids_red_sidechannels_sources_and_candidate_quality()
    {
        using var folder = new Folder();
        var journal = new GameJournal(folder.Path);
        Assert.True(journal.Event("recovery_checkpoint", Saved));
        await journal.CompleteAsync();
        Assert.Null(journal.Fault);
        var loaded = await RecoveryJournal.LoadAsync(folder.Path, Identity);
        Assert.Empty(loaded.Errors);
        var result = Assert.Single(loaded.Candidates).State;
        Assert.Equal(Hand.Select(t => t.Id), result.Snapshot.Hand.Select(t => t.Id));
        Assert.Equal(1, result.Snapshot.AkaDora);
        Assert.Equal(1, result.Tracker.LastAkadora);
        Assert.Equal(0, result.Tracker.MeldAkadora);
        Assert.Equal(new byte[] { 27, 27, 27 }, Assert.Single(result.Tracker.Melds).Tiles.Select(t => t.Id));
        Assert.Equal(1, result.Tracker.Melds[0].ClaimedFromSeat);
        Assert.Equal(27, result.Tracker.Melds[0].ClaimedTile!.Value.Id);
        Assert.True(result.Lower!.Tiles.Single(t => t.Kind34 == 4).RedFive);
        Assert.Equal("LegacyAssumption", result.Mapping);
        Assert.False(result.AutomaticEnabled);
        Assert.False(result.FullHistoryVerified);
        Assert.All(result.PublicTable!.Tiles, t => Assert.Equal("Candidate", t.Quality));
        Assert.False(result.PublicTable.HistoryComplete);
    }

    [Fact]
    public async Task Partial_crash_tail_keeps_checkpoint_candidate_but_never_arms_or_verifies_history()
    {
        using var folder = new Folder();
        var journal = new GameJournal(folder.Path);
        journal.Event("recovery_checkpoint", Saved);
        await journal.CompleteAsync();
        await File.AppendAllTextAsync(System.IO.Path.Combine(journal.DirectoryPath, "events.jsonl"), "{\"Entry\":{");
        var loaded = await RecoveryJournal.LoadAsync(folder.Path, Identity);
        var candidate = Assert.Single(loaded.Candidates);
        Assert.True(candidate.IncompleteTail);
        Assert.False(candidate.State.FullHistoryVerified);
        Assert.False(candidate.State.AutomaticEnabled);
        Assert.True(RecoveryEvidenceBuilder.TryBuild(candidate, Snapshot with { OurMelds = [] }, 0,
            Lower(Hand), Table(), Guid.NewGuid(), 2, out var result, out _, out _));
        Assert.True(result.CanRestoreExperimentalMelds);
        Assert.False(result.CanRestoreOwnMelds);
        Assert.False(result.HistoryComplete);
    }

    [Theory]
    [InlineData("schema")] [InlineData("snapshot-schema")] [InlineData("sequence")]
    [InlineData("tile")] [InlineData("enabled")] [InlineData("mapping")] [InlineData("version")]
    public async Task Invalid_or_promoted_saved_records_are_not_recovery_candidates(string variation)
    {
        using var folder = new Folder();
        var saved = variation switch
        {
            "schema" => Saved with { Schema = 2 },
            "snapshot-schema" => Saved with { Snapshot = Snapshot with { SchemaVersion = 3 } },
            "sequence" => Saved with { ObservationSequence = -1 },
            "tile" => Saved with { Snapshot = Snapshot with { Hand = [Tile.FromId(40)] } },
            "enabled" => Saved with { AutomaticEnabled = true },
            "mapping" => Saved with { Mapping = "Observed" },
            "version" => Saved with { Identity = Identity with { GameVersion = "old" } },
            _ => throw new InvalidOperationException(),
        };
        var journal = new GameJournal(folder.Path);
        Assert.True(journal.Event("recovery_checkpoint", saved));
        await journal.CompleteAsync();
        Assert.Empty((await RecoveryJournal.LoadAsync(folder.Path, Identity)).Candidates);
    }

    [Fact]
    public void Invalid_runtime_seat_is_rejected_before_a_computed_json_property_can_index_it()
    {
        Assert.False(RecoveryJournal.ValidSnapshot(Snapshot with { OurSeat = -1 }));
        Assert.False(RecoveryJournal.ValidSnapshot(Snapshot with { OurSeat = 4 }));
        Assert.True(RecoveryJournal.ValidSnapshot(Snapshot));
    }

    [Fact]
    public void Actual_candidate_builder_and_reconciler_match_complete_visible_meld_without_promoting_quality()
    {
        var saved = Candidate;
        Assert.True(RecoveryEvidenceBuilder.TryBuild(saved, Snapshot with { OurMelds = [] }, 0,
            Lower(Hand), Table(), Guid.NewGuid(), 2, out var result, out var current, out var code));
        Assert.Equal("RECOVERY_EXPERIMENTAL_MATCH", code);
        Assert.True(result.CanRestoreExperimentalMelds);
        Assert.False(result.CanRestoreOwnMelds);
        Assert.False(result.HistoryComplete);
        Assert.Equal(Hand.Select(t => (int)t.Id).Order(), current.OwnHand.Select(t => t.Id).Order());
        Assert.Equal(1, current.OwnHand.Count(t => t.Red));
        Assert.Equal("LegacyAssumption", saved.State.Mapping);
        Assert.All(saved.State.PublicTable!.Tiles, t => Assert.Equal("Candidate", t.Quality));
    }

    [Fact]
    public void Interrupted_draw_discard_progress_rebuilds_inventory_without_filling_missing_timing()
    {
        var hand = Hand.Select(t => t.Id == 0 ? Tile.FromId(11) : t).ToArray();
        var snapshot = Snapshot with { Hand = hand, OurMelds = [], WallRemaining = 48,
            Seats = Snapshot.Seats.Select((s, i) => i == 0 ? s with { DiscardCount = 2, Discards = [Tile.FromId(31), Tile.FromId(0)] } : s).ToArray() };
        Assert.True(RecoveryEvidenceBuilder.TryBuild(Candidate, snapshot, 0, Lower(hand), Table(extendedRiver: true),
            Guid.NewGuid(), 3, out var result, out _, out _));
        Assert.True(result.CanRestoreExperimentalMelds);
        Assert.False(result.HistoryComplete);
        Assert.False(result.CanRestoreOwnMelds);
    }

    [Theory]
    [InlineData("empty-proof")] [InlineData("missing-area")] [InlineData("hidden-area")]
    [InlineData("partial-group")] [InlineData("group-slot-mismatch")] [InlineData("added-kan")]
    [InlineData("red-mismatch")] [InlineData("wall-reset")] [InlineData("stale")]
    public void Incomplete_desktop_or_conflicting_saved_evidence_cannot_restore(string variation)
    {
        var table = Table();
        var saved = Candidate;
        var lower = Lower(Hand);
        var current = Snapshot with { OurMelds = [] };
        switch (variation)
        {
            case "empty-proof": table = table with { Areas = table.Areas.SetItem(1, table.Areas[1] with { ObservedEmptyCandidate = false }) }; break;
            case "missing-area": table = table with { Areas = [] }; break;
            case "hidden-area": table = table with { Areas = table.Areas.SetItem(1, table.Areas[1] with { ContainerVisible = false }) }; break;
            case "partial-group": table = table with { MeldGroups = [table.MeldGroups[0] with { AllVisibleSlotsDecoded = false }] }; break;
            case "group-slot-mismatch": table = table with { MeldGroups = [table.MeldGroups[0] with { SlotPaths = ["unrelated"] }] }; break;
            case "added-kan": table = Table(fourthMeldTile: true); break;
            case "red-mismatch": lower = lower with { Tiles = lower.Tiles.SetItem(4, lower.Tiles[4] with { RedFive = false }) }; break;
            case "wall-reset": current = current with { WallRemaining = 60 }; break;
            case "stale": saved = saved with { Utc = DateTimeOffset.UtcNow.AddHours(-7) }; break;
        }
        Assert.False(RecoveryEvidenceBuilder.TryBuild(saved, current, 0, lower, table, Guid.NewGuid(), 3, out _, out _, out _));
    }

    [Fact]
    public void Ordinary_json_preserves_closed_kan_null_claim_and_four_physical_tiles()
    {
        var snapshot = Snapshot with { OurMelds = [Meld.AnKan(Tile.FromId(33))] };
        var parsed = JsonSerializer.Deserialize<StateSnapshot>(JsonSerializer.Serialize(snapshot))!;
        var meld = Assert.Single(parsed.OurMelds);
        Assert.Equal(MeldKind.AnKan, meld.Kind);
        Assert.Equal(4, meld.Tiles.Length);
        Assert.All(meld.Tiles, t => Assert.Equal(33, t.Id));
        Assert.Equal(-1, meld.ClaimedFromSeat);
        Assert.Null(meld.ClaimedTile);
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(33, true)]
    [InlineData(4, false)]
    [InlineData(13, false)]
    [InlineData(22, false)]
    public void Public_two_back_closed_kan_can_restore_only_when_red_identity_is_unambiguous(int kind, bool expected)
    {
        var closedKan = Meld.AnKan(Tile.FromId(kind));
        var table = ClosedTable(kind);
        var state = Saved with
        {
            Snapshot = Snapshot with { OurMelds = [closedKan] },
            Tracker = Saved.Tracker with { Melds = [closedKan] },
            PublicTable = table,
        };
        Assert.Equal(expected, RecoveryEvidenceBuilder.TryBuild(Candidate with { State = state },
            state.Snapshot with { OurMelds = [] }, 0, Lower(Hand), table, Guid.NewGuid(), 20,
            out var result, out _, out _));
        if (expected)
        {
            Assert.False(result.HistoryComplete);
            Assert.False(result.CanRestoreOwnMelds);
            var restored = Assert.Single(result.ExperimentalMelds);
            Assert.Equal(MeldKind.AnKan, restored.Kind);
            Assert.Null(restored.FromRelativePlayer);
            Assert.Equal(4, restored.TileIds.Length);
            Assert.Equal(2, table.Tiles.Count(t => t.Area == "meld-bottom"));
        }
    }

    [Theory]
    [InlineData("one-back")]
    [InlineData("wrong-shape")]
    [InlineData("different-face")]
    [InlineData("unbound-slot")]
    [InlineData("open-kan")]
    public void Closed_kan_recovery_rejects_incomplete_or_incompatible_public_pattern(string fault)
    {
        var kan = Meld.AnKan(Tile.FromId(16));
        var original = ClosedTable(16);
        var current = original;
        var state = Saved with { Snapshot = Snapshot with { OurMelds = [kan] },
            Tracker = Saved.Tracker with { Melds = [kan] }, PublicTable = original };
        var group = original.MeldGroups[0];
        switch (fault)
        {
            case "one-back": current = current with { MeldGroups = [group with { VerifiedBackSlots = 1 }] }; break;
            case "wrong-shape": current = current with { MeldGroups = [group with { ShapeCode = "unknown" }] }; break;
            case "different-face": current = current with { Tiles = current.Tiles.SetItem(1, current.Tiles[1] with { Kind34 = 15 }) }; break;
            case "unbound-slot": current = current with { MeldGroups = [group with { SlotPaths = group.SlotPaths.SetItem(2, "unrelated") }] }; break;
            case "open-kan":
                var open = new Meld(MeldKind.MinKan, kan.Tiles, Tile.FromId(16), 1);
                state = state with { Snapshot = state.Snapshot with { OurMelds = [open] }, Tracker = state.Tracker with { Melds = [open] } };
                break;
        }
        Assert.False(RecoveryEvidenceBuilder.TryBuild(Candidate with { State = state },
            state.Snapshot with { OurMelds = [] }, 0, Lower(Hand), current, Guid.NewGuid(), 20, out _, out _, out _));
    }

    private static PublicTableReading ClosedTable(int kind)
    {
        var original = Table();
        var template = original.Tiles.First(t => t.Area == "meld-bottom");
        var slots = Enumerable.Range(2, 4).Select(i => $"Emj/112/{i}/9/4").ToImmutableArray();
        return original with
        {
            Tiles = original.Tiles.Where(t => t.Area != "meld-bottom").Concat(slots.Skip(2).Select(path =>
                template with { SlotPath = path, Kind34 = kind, RedFive = false })).ToImmutableArray(),
            MeldGroups = [new("Emj/112", "bottom", 4, 2, false, true, slots, [], 2,
                "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN", kind)],
        };
    }

    private sealed class Folder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mjcn-recovery-test-" + Guid.NewGuid().ToString("N"));
        internal Folder() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            if (!resolved.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !System.IO.Path.GetFileName(resolved).StartsWith("mjcn-recovery-test-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test directory.");
            Directory.Delete(resolved, true);
        }
    }
}
