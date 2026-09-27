using System.Globalization;
using System.Text.Json;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>
/// The scalar oracle is the separately extracted, visually reviewed static resource catalog.
/// Tracker inputs are synthetic visible-image observations, never a complete/live Mahjong hand.
/// </summary>
public sealed class LowerHandReadingTests
{
    private sealed record CatalogResource(uint IconId, uint Hash, int? Kind34, bool? RedFive,
        string? ChineseName, string Path, bool SemanticConfirmed);

    private static readonly CatalogResource[] Catalog = ReadCatalog();

    private static CatalogResource[] ReadCatalog()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "tile-resource-catalog.json")));
        return document.RootElement.GetProperty("Resources").EnumerateArray().Select(row => new CatalogResource(
            row.GetProperty("IconId").GetUInt32(),
            uint.Parse(row.GetProperty("StandardCrc32").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            row.GetProperty("CandidateTileKind34").ValueKind == JsonValueKind.Null ? null : row.GetProperty("CandidateTileKind34").GetInt32(),
            row.GetProperty("CandidateRedFive").ValueKind == JsonValueKind.Null ? null : row.GetProperty("CandidateRedFive").GetBoolean(),
            row.GetProperty("ChineseName").GetString(), row.GetProperty("Path").GetString()!,
            row.GetProperty("MahjongSemanticVisuallyConfirmed").GetBoolean())).ToArray();
    }

    public static IEnumerable<object[]> ReviewedResources() => Catalog.Where(x => x.Kind34.HasValue)
        .Select(x => new object[] { x.IconId, x.Hash, x.Kind34!.Value, x.RedFive!.Value, x.ChineseName! });

    private static uint Hash(uint icon, bool highResolution = false) => Catalog.Single(x =>
        x.IconId == icon && x.Path.EndsWith("_hr1.tex", StringComparison.Ordinal) == highResolution).Hash;

    private static HandFaceCandidate Face(int index, uint? icon = null)
    {
        uint id = icon ?? (76001u + (uint)index);
        return new($"Emj/{1340001 + index}/9/4", 100 + 45 * index, 600, 40, 52, id,
            LowerHandProfile.VerifiedIconStatus, Hash(id));
    }

    private static HandFaceCandidate[] Faces(int count) => Enumerable.Range(0, count).Select(i => Face(i)).ToArray();
    private static AddonProbe Probe(IReadOnlyList<HandFaceCandidate>? faces, int atkValueCount = 50) =>
        new("Emj", true, true, true, atkValueCount, [], null, faces);

    [Fact]
    public void Scale_change_invalidates_old_faces_and_requires_two_new_identical_observations()
    {
        var tracker = new LowerHandTracker();
        var original = Faces(13);
        Prime(tracker, original);
        var scaled = original.Select(face => face with
        { X = face.X * 1.4f, Y = face.Y * 1.4f, Width = face.Width * 1.4f, Height = face.Height * 1.4f }).ToArray();
        AssertEmpty(tracker.Observe(12, Probe(scaled)), "LOWER_STABILIZING");
        // Separate diagnostic arrays must not become part of face equality.
        var next = Probe(scaled.Select(face => face with { }).ToArray()) with
        { PublicLayouts = [new("Emj/134/9/4", "lower-row-geometry", "PUBLIC_LAYOUT_METADATA_ONLY", [])] };
        Assert.True(tracker.Observe(13, next).Stable);
        AssertEmpty(tracker.Observe(14, Probe(original)), "LOWER_STABILIZING");
    }

    private static void AssertEmpty(LowerHandReading reading, string code)
    {
        Assert.Equal(code, reading.Code);
        Assert.False(reading.Stable);
        Assert.False(reading.CompleteGameState);
        Assert.Empty(reading.Tiles);
    }

    private static void Prime(LowerHandTracker tracker, HandFaceCandidate[] faces)
    {
        AssertEmpty(tracker.Observe(10, Probe(faces)), "LOWER_STABILIZING");
        var stable = tracker.Observe(11, Probe(faces));
        Assert.True(stable.Stable);
        Assert.Equal(faces.Length, stable.Tiles.Length);
        Assert.False(stable.CompleteGameState);
    }

    private static void AssertRequiresTwoFreshCaptures(LowerHandTracker tracker, HandFaceCandidate[] faces)
    {
        AssertEmpty(tracker.Observe(13, Probe(faces)), "LOWER_STABILIZING");
        Assert.True(tracker.Observe(14, Probe(faces)).Stable);
    }

    [Fact]
    public void Catalog_oracle_contains_exactly_two_reviewed_sets_at_both_resolutions()
    {
        var known = Catalog.Where(x => x.Kind34.HasValue).ToArray();
        Assert.Equal(222, Catalog.Length);
        Assert.Equal(148, known.Length);
        Assert.Equal(74, known.Select(x => x.IconId).Distinct().Count());
        Assert.All(known, row =>
        {
            Assert.True(row.SemanticConfirmed);
            Assert.True(row.IconId is >= 76001 and <= 76037 or >= 76041 and <= 76077);
            Assert.InRange(row.Kind34!.Value, 0, 33);
            Assert.False(string.IsNullOrWhiteSpace(row.ChineseName));
        });
        Assert.All(known.GroupBy(x => x.IconId), group => Assert.Equal(2, group.Count()));
    }

    [Theory]
    [MemberData(nameof(ReviewedResources))]
    public void Both_scalar_fields_decode_to_the_independently_reviewed_Chinese_tile_identity(
        uint icon, uint hash, int kind, bool red, string chineseName)
    {
        Assert.True(LowerTileCatalog.TryDecode(icon, hash, out var tile));
        Assert.Equal(kind, tile.Kind34);
        Assert.Equal(red, tile.RedFive);
        Assert.Equal(chineseName, tile.ChineseName);
    }

    [Fact]
    public void Null_zero_unknown_and_another_icons_valid_hash_are_never_decoded()
    {
        foreach (var resource in Catalog.Where(x => x.Kind34.HasValue))
        {
            Assert.False(LowerTileCatalog.TryDecode(null, resource.Hash, out _));
            Assert.False(LowerTileCatalog.TryDecode(0, resource.Hash, out _));
            Assert.False(LowerTileCatalog.TryDecode(999999, resource.Hash, out _));
            Assert.False(LowerTileCatalog.TryDecode(resource.IconId, null, out _));
            Assert.False(LowerTileCatalog.TryDecode(resource.IconId, 0, out _));
            Assert.False(LowerTileCatalog.TryDecode(resource.IconId, 0x12345678, out _));
            Assert.False(LowerTileCatalog.TryDecode(resource.IconId, ~resource.Hash, out _));
            uint otherIcon = resource.IconId == 76001 ? 76002u : 76001u;
            Assert.False(LowerTileCatalog.TryDecode(otherIcon, resource.Hash, out _));
        }
    }

    [Fact]
    public void Every_third_style_resource_remains_unknown_even_with_its_exact_catalog_hash()
    {
        var unreviewed = Catalog.Where(x => !x.Kind34.HasValue).ToArray();
        Assert.Equal(74, unreviewed.Length);
        Assert.All(unreviewed, resource =>
        {
            Assert.False(resource.SemanticConfirmed);
            Assert.False(LowerTileCatalog.TryDecode(resource.IconId, resource.Hash, out _));
        });
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(14)]
    public void Two_independent_equal_captures_expose_only_partial_visible_faces(int count)
    {
        var tracker = new LowerHandTracker();
        var faces = Faces(count);
        AssertEmpty(tracker.Observe(1, Probe(faces)), "LOWER_STABILIZING");
        var reading = tracker.Observe(2, Probe(faces.Select(x => x with { }).ToArray()));
        Assert.Equal("STABLE_VISIBLE_FACES", reading.Code);
        Assert.True(reading.Stable);
        Assert.Equal(count, reading.Tiles.Length);
        Assert.False(reading.CompleteGameState);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("y")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("path")]
    [InlineData("tile")]
    [InlineData("resolution")]
    public void Changed_geometry_path_or_resource_invalidates_old_value_until_repeated(string change)
    {
        var tracker = new LowerHandTracker();
        var faces = Faces(7);
        Prime(tracker, faces);
        var changed = faces.ToArray();
        changed[0] = change switch
        {
            "x" => changed[0] with { X = 95 },
            "y" => changed[0] with { Y = 605 },
            "width" => changed[0] with { Width = 39 },
            "height" => changed[0] with { Height = 51 },
            "path" => changed[0] with { Path = "Emj/1340016/9/4" },
            "tile" => changed[0] with { IconId = 76008, FacePathHash = Hash(76008) },
            "resolution" => changed[0] with { FacePathHash = Hash(76001, highResolution: true) },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        AssertEmpty(tracker.Observe(12, Probe(changed)), "LOWER_STABILIZING");
        Assert.True(tracker.Observe(13, Probe(changed)).Stable);
    }

    [Theory]
    [InlineData(73)]
    [InlineData(109)]
    public void Interface_phase_change_needs_new_stability_and_never_claims_a_playable_turn(int atkValueCount)
    {
        var tracker = new LowerHandTracker();
        var faces = Faces(7);
        Prime(tracker, faces);
        AssertEmpty(tracker.Observe(12, Probe(faces, atkValueCount)), "LOWER_STABILIZING");
        var result = tracker.Observe(13, Probe(faces, atkValueCount));
        Assert.True(result.Stable);
        Assert.False(result.CompleteGameState);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(11)]
    public void Repeated_or_older_sequence_clears_old_values_without_advancing_stability(long sequence)
    {
        var tracker = new LowerHandTracker();
        var faces = Faces(7);
        Prime(tracker, faces);
        AssertEmpty(tracker.Observe(sequence, Probe(faces)), "STALE_CAPTURE");
        AssertEmpty(tracker.Observe(11, Probe(faces)), "STALE_CAPTURE");
        AssertEmpty(tracker.Observe(12, Probe(faces)), "LOWER_STABILIZING");
        AssertEmpty(tracker.Observe(12, Probe(faces)), "STALE_CAPTURE");
        AssertRequiresTwoFreshCaptures(tracker, faces);
    }

    [Theory]
    [InlineData("status", "LOWER_RESOURCE_REJECTED")]
    [InlineData("null-icon", "LOWER_TILE_UNVERIFIED")]
    [InlineData("zero-icon", "LOWER_TILE_UNVERIFIED")]
    [InlineData("unknown-icon", "LOWER_TILE_UNVERIFIED")]
    [InlineData("null-hash", "LOWER_TILE_UNVERIFIED")]
    [InlineData("zero-hash", "LOWER_TILE_UNVERIFIED")]
    [InlineData("unknown-hash", "LOWER_TILE_UNVERIFIED")]
    [InlineData("cross-icon-hash", "LOWER_TILE_UNVERIFIED")]
    [InlineData("third-style", "LOWER_TILE_UNVERIFIED")]
    public void One_rejected_resource_discards_every_tile_and_clears_previous_capture(string failure, string code)
    {
        var tracker = new LowerHandTracker();
        var faces = Faces(7);
        Prime(tracker, faces);
        var invalid = faces.ToArray();
        var unknownStyle = Catalog.First(x => !x.Kind34.HasValue);
        invalid[3] = failure switch
        {
            "status" => invalid[3] with { DiagnosticStatus = "SHELL_REJECTED" },
            "null-icon" => invalid[3] with { IconId = null },
            "zero-icon" => invalid[3] with { IconId = 0 },
            "unknown-icon" => invalid[3] with { IconId = 999999 },
            "null-hash" => invalid[3] with { FacePathHash = null },
            "zero-hash" => invalid[3] with { FacePathHash = 0 },
            "unknown-hash" => invalid[3] with { FacePathHash = 0x12345678 },
            "cross-icon-hash" => invalid[3] with { FacePathHash = Hash(76001) },
            "third-style" => invalid[3] with { IconId = unknownStyle.IconId, FacePathHash = unknownStyle.Hash },
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        AssertEmpty(tracker.Observe(12, Probe(invalid)), code);
        AssertRequiresTwoFreshCaptures(tracker, faces);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("duplicate-path")]
    [InlineData("overlap")]
    [InlineData("opponent-path")]
    public void Missing_or_rejected_layout_never_reuses_the_last_decoded_tiles(string failure)
    {
        var tracker = new LowerHandTracker();
        var faces = Faces(7);
        Prime(tracker, faces);
        HandFaceCandidate[]? invalid = failure switch
        {
            "null" => null,
            "empty" => [],
            "too-many" => Faces(15),
            "duplicate-path" => [faces[0], faces[1] with { Path = faces[0].Path }],
            "overlap" => [faces[0], faces[1] with { X = faces[0].X }],
            "opponent-path" => [faces[0] with { Path = "Emj/137/9/4" }],
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        AssertEmpty(tracker.Observe(12, Probe(invalid)), "LOWER_LAYOUT_UNVERIFIED");
        AssertRequiresTwoFreshCaptures(tracker, faces);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("different-addon")]
    [InlineData("absent")]
    [InlineData("hidden")]
    [InlineData("not-ready")]
    [InlineData("read-error")]
    public void Leaving_or_losing_the_ready_addon_discards_previous_faces(string failure)
    {
        var tracker = new LowerHandTracker();
        var faces = Faces(7);
        Prime(tracker, faces);
        var probe = Probe(faces);
        AddonProbe? invalid = failure switch
        {
            "missing" => null,
            "different-addon" => probe with { Name = "EmjL" },
            "absent" => probe with { Present = false },
            "hidden" => probe with { Visible = false },
            "not-ready" => probe with { Ready = false },
            "read-error" => probe with { Error = "MEMORY_READ_FAILURE" },
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        AssertEmpty(tracker.Observe(12, invalid), "NO_VISIBLE_LOWER_ROW");
        AssertRequiresTwoFreshCaptures(tracker, faces);
    }

    [Fact]
    public void Clear_requires_new_observations_and_permits_a_new_session_sequence()
    {
        var tracker = new LowerHandTracker();
        var faces = Faces(7);
        Prime(tracker, faces);
        tracker.Clear();
        AssertEmpty(tracker.Observe(1, Probe(faces)), "LOWER_STABILIZING");
        Assert.True(tracker.Observe(2, Probe(faces)).Stable);
        tracker.Clear();
        tracker.Clear();
        AssertEmpty(tracker.Observe(1, Probe(faces)), "LOWER_STABILIZING");
    }

    [Theory]
    [InlineData(76005u, 76035u, 76045u)]
    [InlineData(76014u, 76036u, 76054u)]
    [InlineData(76023u, 76037u, 76063u)]
    public void Red_and_normal_fives_of_both_styles_share_the_four_tile_limit(uint normal, uint red, uint secondStyle)
    {
        var tracker = new LowerHandTracker();
        var four = new[] { Face(0, normal), Face(1, normal), Face(2, secondStyle), Face(3, red) };
        Prime(tracker, four);
        var five = four.Append(Face(4, secondStyle)).ToArray();
        AssertEmpty(tracker.Observe(12, Probe(five)), "LOWER_TILE_COUNT_CONFLICT");
        AssertRequiresTwoFreshCaptures(tracker, four);
    }

    [Fact]
    public void Screen_order_is_stable_across_input_enumeration_and_keeps_exact_source_paths()
    {
        var faces = new[]
        {
            Face(0) with { Path = "Emj/134/9/4", X = 200, Y = 700 },
            Face(1) with { Path = "Emj/135/9/4", X = 100, Y = 600 },
            Face(2) with { Path = "Emj/1340016/9/4", X = 200, Y = 600 },
        };
        var tracker = new LowerHandTracker();
        AssertEmpty(tracker.Observe(1, Probe(faces)), "LOWER_STABILIZING");
        var reading = tracker.Observe(2, Probe(faces.Reverse().ToArray()));
        Assert.True(reading.Stable);
        Assert.Equal(new[] { "Emj/135/9/4", "Emj/1340016/9/4", "Emj/134/9/4" }, reading.Tiles.Select(x => x.Path));
        Assert.Equal(new[] { 1, 2, 3 }, reading.Tiles.Select(x => x.DisplayPosition));
        Assert.Equal(new uint[] { 76002, 76003, 76001 }, reading.Tiles.Select(x => x.IconId));
        Assert.False(reading.CompleteGameState);

        // DisplayPosition follows geometry even when clone/root IDs disagree with it.
        // The exposed record carries no inferred operation slot or callback argument.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(reading.Tiles[0]));
        Assert.Equal(new[] { "Path", "DisplayPosition", "IconId", "FacePathHash", "Kind34", "RedFive", "ChineseName" },
            json.RootElement.EnumerateObject().Select(x => x.Name));
    }
}
