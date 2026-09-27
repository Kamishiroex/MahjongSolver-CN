using System.Collections.Immutable;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Synthetic public UI observations only; never copied from a real hand or capture.</summary>
public sealed class PublicMonitorSessionTests
{
    // Independent static-resource oracle from evidence/tile-resource-catalog.json, standard resolution.
    private static readonly IReadOnlyDictionary<uint, uint> Hashes = new Dictionary<uint, uint>
    {
        [76001] = 0x9A401A76, [76002] = 0xDDE060A6, [76003] = 0xE0804916,
        [76004] = 0x52A09506, [76005] = 0x6FC0BCB6, [76035] = 0xE954CE18,
    };

    private static HandFaceCandidate Face(int index, uint icon) =>
        new($"Emj/{1340001 + index}/9/4", 100 + index * 45, 600, 40, 52, icon,
            LowerHandProfile.VerifiedIconStatus, Hashes[icon]);

    private static HandFaceCandidate[] Faces(int count = 7) =>
        Enumerable.Range(0, count).Select(i => Face(i, 76001u + (uint)(i % 5))).ToArray();

    private static AddonProbe Probe(IReadOnlyList<HandFaceCandidate> faces) =>
        new("Emj", true, true, true, 50, [], null, faces);

    private static DiagnosticFrame Frame(long sequence, params AddonProbe[] addons) =>
        new(sequence, new DateTimeOffset(2026, 9, 24, 0, 0, 0, TimeSpan.Zero).AddSeconds(sequence),
            "synthetic", "synthetic public UI", addons);

    private static AddonProbe Tracked(LowerHandTracker tracker, long sequence, IReadOnlyList<HandFaceCandidate> faces)
    {
        var probe = Probe(faces);
        return probe with { LowerHandReading = tracker.Observe(sequence, probe) };
    }

    private static (PublicMonitorSession Monitor, LowerHandTracker Tracker, AddonProbe Stable) Prime(
        HandFaceCandidate[]? faces = null)
    {
        faces ??= Faces();
        var monitor = new PublicMonitorSession();
        var tracker = new LowerHandTracker();
        monitor.Start();
        monitor.Observe(Frame(1, Tracked(tracker, 1, faces)));
        Assert.Equal(Availability.Unknown, monitor.Current!.LowerVisibleFaces.Availability);
        var stable = Tracked(tracker, 2, faces.Select(f => f with { }).ToArray());
        monitor.Observe(Frame(2, stable));
        Assert.True(monitor.Current!.LowerVisibleFaces.IsConfirmed);
        return (monitor, tracker, stable);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(13)]
    [InlineData(14)]
    public void Two_independent_samples_confirm_only_lower_images_never_a_complete_hand(int count)
    {
        var (monitor, _, _) = Prime(Faces(count));
        var snapshot = monitor.Current!;
        Assert.Equal(count, snapshot.LowerVisibleFaces.Value.Length);
        Assert.Equal(SourceKind.Observed, snapshot.LowerVisibleFaces.SourceKind);
        Assert.Equal(MappingStatus.Validated, snapshot.LowerVisibleFaces.MappingStatus);
        Assert.Equal(StabilityState.Stable, snapshot.Stability);
        Assert.Equal(SynchronizationState.HistoryGap, snapshot.Synchronization);
        Assert.All(PublicSnapshotFields.Enumerate(snapshot).Where(x => x.Key != "LowerVisibleFaces"), field =>
        {
            Assert.Equal(Availability.Unknown, field.Value.Availability);
            Assert.False(field.Value.HasValue);
            Assert.False(string.IsNullOrWhiteSpace(field.Value.Reason));
        });
        Assert.Equal(2, snapshot.Observation!.Sequence);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Observation.Evidence));
        var readiness = ReadinessEvaluator.Evaluate(snapshot, ReadinessProfiles.CompleteDecision);
        Assert.False(readiness.IsReady);
        Assert.Contains(readiness.Issues, x => x.Path == "OwnHand" && x.Code == "unknown");
        Assert.Contains(readiness.Issues, x => x.Path == "RoundId" && x.Code == "unknown");
        Assert.Contains(readiness.Issues, x => x.Path == "Synchronization" && x.Code == "history-gap");
    }

    [Fact]
    public void Ordinary_and_red_fives_keep_distinct_revision_scoped_slots_without_persistent_identity()
    {
        HandFaceCandidate[] faces = [Face(0, 76005), Face(1, 76035), Face(2, 76005)];
        var (monitor, tracker, _) = Prime(faces);
        var previous = monitor.Current!;
        var previousTiles = previous.LowerVisibleFaces.Value;
        Assert.All(previousTiles, x => Assert.Equal(4, x.Tile.Id));
        Assert.Equal(new[] { false, true, false }, previousTiles.Select(x => x.Tile.Red));
        Assert.Equal(3, previousTiles.Select(x => x.Slot).Distinct().Count());
        Assert.Equal(new[] { 1, 2, 3 }, previousTiles.Select(x => x.Slot.DisplayPosition));
        monitor.Observe(Frame(3, Tracked(tracker, 3, faces)));
        var current = monitor.Current!;
        Assert.Equal(previous.SessionId, current.SessionId);
        Assert.True(current.StateRevision > previous.StateRevision);
        Assert.All(current.LowerVisibleFaces.Value, tile =>
        {
            Assert.Equal(current.StateRevision, tile.Slot.StateRevision);
            Assert.DoesNotContain(tile.Slot, previousTiles.Select(x => x.Slot));
        });
        Assert.False(current.RoundId.HasValue);
        Assert.False(current.HasDrawnTile.HasValue);
        Assert.False(current.DrawnTileSlot.HasValue);
    }

    [Theory]
    [InlineData("animation", "LOWER_LAYOUT_UNVERIFIED")]
    [InlineData("resource", "LOWER_TILE_UNVERIFIED")]
    [InlineData("status", "LOWER_RESOURCE_REJECTED")]
    public void Rejected_new_observation_removes_old_tiles_and_requires_two_fresh_samples(string failure, string code)
    {
        var faces = Faces();
        var (monitor, tracker, _) = Prime(faces);
        var changed = faces.ToArray();
        changed[0] = failure switch
        {
            "animation" => changed[0] with { X = changed[1].X },
            "resource" => changed[0] with { FacePathHash = 0x12345678 },
            _ => changed[0] with { DiagnosticStatus = "SHELL_REJECTED" },
        };
        monitor.Observe(Frame(3, Tracked(tracker, 3, changed)));
        AssertUnknown(monitor, code);
        monitor.Observe(Frame(4, Tracked(tracker, 4, faces)));
        AssertUnknown(monitor, "LOWER_STABILIZING");
        monitor.Observe(Frame(5, Tracked(tracker, 5, faces)));
        Assert.True(monitor.Current!.LowerVisibleFaces.IsConfirmed);
    }

    [Theory]
    [InlineData("alternate", "UNSUPPORTED_LAYOUT")]
    [InlineData("multiple", "MULTIPLE_TABLES")]
    [InlineData("error", "READ_ERROR")]
    [InlineData("hidden", "NO_READY_TABLE")]
    [InlineData("not-ready", "NO_READY_TABLE")]
    public void Ambiguous_or_unavailable_addon_cannot_keep_the_previous_visible_tiles(string failure, string code)
    {
        var (monitor, _, stable) = Prime();
        AddonProbe[] addons = failure switch
        {
            "alternate" => [stable with { Name = "EmjL" }],
            "multiple" => [stable, stable with { Name = "EmjL" }],
            "error" => [stable with { Error = "SYNTHETIC_READ_ERROR" }],
            "hidden" => [stable with { Visible = false }],
            _ => [stable with { Ready = false }],
        };
        monitor.Observe(Frame(3, addons));
        AssertUnknown(monitor, code);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Old_or_duplicate_sequence_invalidates_even_a_previously_valid_reading(long sequence)
    {
        var (monitor, _, stable) = Prime();
        monitor.Observe(Frame(sequence, stable));
        AssertUnknown(monitor, "STALE_OBSERVATION");
    }

    [Theory]
    [InlineData("count")]
    [InlineData("duplicate-path")]
    [InlineData("cross-resource")]
    [InlineData("red-identity")]
    [InlineData("display-position")]
    [InlineData("reversed-order")]
    public void Stable_flag_does_not_bypass_managed_projection_rechecks(string failure)
    {
        var (monitor, _, stable) = Prime();
        var reading = stable.LowerHandReading!;
        var tiles = reading.Tiles;
        var changed = failure switch
        {
            "count" => tiles.RemoveAt(tiles.Length - 1),
            "duplicate-path" => tiles.SetItem(1, tiles[1] with { Path = tiles[0].Path }),
            "cross-resource" => tiles.SetItem(0, tiles[0] with { FacePathHash = Hashes[76002] }),
            "display-position" => tiles.SetItem(0, tiles[0] with { DisplayPosition = 2 }),
            "reversed-order" => tiles.Reverse().Select((tile, index) => tile with { DisplayPosition = index + 1 }).ToImmutableArray(),
            _ => tiles.SetItem(0, tiles[0] with { RedFive = true }),
        };
        monitor.Observe(Frame(3, stable with { LowerHandReading = reading with { Tiles = changed } }));
        AssertUnknown(monitor, "LOWER_PROJECTION_CONFLICT");
    }

    [Fact]
    public void Region_evidence_counts_metadata_records_and_rejections_without_inventing_tiles()
    {
        var (monitor, _, stable) = Prime();
        PublicLayoutMetadata[] metadata =
        [
            new("Emj/118/4", "river-bottom", "PUBLIC_LAYOUT_METADATA_ONLY", []),
            new("Emj/118/5", "river-bottom", "PUBLIC_LAYOUT_METADATA_ONLY", []),
            new("Emj/1180001/4", "river-bottom", "PUBLIC_LAYOUT_PARENT_REJECTED", []),
            new("Emj/28/2", "dora-display", "PUBLIC_LAYOUT_METADATA_ONLY", []),
        ];
        monitor.Observe(Frame(3, stable with { PublicLayouts = metadata }));
        var river = Assert.Single(monitor.Evidence!.Regions, x => x.Area == "river-bottom");
        Assert.Equal(3, river.MetadataRecords);
        Assert.Equal(1, river.RejectedRecords);
        Assert.All(monitor.Current!.Players, x => Assert.False(x.River.HasValue));
        Assert.False(monitor.Current.DoraDisplay.HasValue);
        Assert.False(monitor.Current.DoraMode.HasValue);
    }

    [Fact]
    public void Stop_rejects_later_observations_and_restart_creates_a_new_observation_session()
    {
        var (monitor, _, stable) = Prime();
        var old = monitor.Current!;
        monitor.Stop("synthetic stop");
        monitor.Observe(Frame(3, stable));
        Assert.False(monitor.IsActive);
        Assert.Null(monitor.Current);
        Assert.Same(old, monitor.Last);
        Assert.Equal("synthetic stop", monitor.Status);
        monitor.Start();
        Assert.Null(monitor.Current);
        Assert.Null(monitor.Last);
        Assert.Null(monitor.Evidence);
        var tracker = new LowerHandTracker();
        monitor.Observe(Frame(1, Tracked(tracker, 1, Faces())));
        monitor.Observe(Frame(2, Tracked(tracker, 2, Faces())));
        Assert.NotEqual(old.SessionId, monitor.Current!.SessionId);
        Assert.False(monitor.Current.RoundId.HasValue);
    }

    [Fact]
    public void Initial_wait_is_allowed_but_observed_table_exit_stops_and_clears_live_state()
    {
        var monitor = new PublicMonitorSession();
        monitor.Start();
        monitor.Observe(Frame(1));
        Assert.True(monitor.IsActive);
        AssertUnknown(monitor, "NO_READY_TABLE");
        var tracker = new LowerHandTracker();
        monitor.Observe(Frame(2, Tracked(tracker, 2, Faces())));
        var stable = Tracked(tracker, 3, Faces());
        monitor.Observe(Frame(3, stable));
        Assert.True(monitor.Current!.LowerVisibleFaces.IsConfirmed);
        monitor.Observe(Frame(4, stable with { Present = false, Visible = false }));
        Assert.False(monitor.IsActive);
        Assert.Null(monitor.Current);
        Assert.StartsWith("SCENE_EXIT", monitor.Status);
        monitor.Observe(Frame(5, stable));
        Assert.Null(monitor.Current);
    }

    private static void AssertUnknown(PublicMonitorSession monitor, string code)
    {
        Assert.Equal(code, monitor.Evidence!.Code);
        Assert.NotNull(monitor.Current);
        var expectedAvailability = code.Contains("CONFLICT", StringComparison.Ordinal) || code == "MULTIPLE_TABLES"
            ? Availability.Conflict : Availability.Unknown;
        Assert.Equal(expectedAvailability, monitor.Current!.LowerVisibleFaces.Availability);
        Assert.False(monitor.Current.LowerVisibleFaces.HasValue);
        Assert.Equal(0, monitor.Evidence.AcceptedLowerFaces);
        Assert.False(ReadinessEvaluator.Evaluate(monitor.Current, ReadinessProfiles.CompleteDecision).IsReady);
    }
}
