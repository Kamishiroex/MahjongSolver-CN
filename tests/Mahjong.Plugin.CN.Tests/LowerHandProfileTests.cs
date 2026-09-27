using System.Text.Json;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>Only path/template/geometry guards are tested; these examples are not live tile samples.</summary>
public sealed class LowerHandProfileTests
{
    private static HandFaceCandidate Face(int index = 0) => new($"Emj/{1340001 + index}/9/4",
        100 + 40 * index, 640, 40, 60, (uint)(1000 + index), LowerHandProfile.VerifiedIconStatus);

    [Theory]
    [InlineData("Emj/134/9/4", 134, 4)]
    [InlineData("Emj/135/9/5", 135, 5)]
    [InlineData("Emj/1340001/9/4", 1340001, 4)]
    [InlineData("Emj/1340016/9/5", 1340016, 5)]
    public void Exact_evidenced_routes_are_accepted(string path, int root, int image)
    {
        Assert.True(LowerHandProfile.TryMatchPath(path, out var route));
        Assert.Equal(root, route.RootId); Assert.Equal(image, route.ImageNodeId);
        Assert.Equal(image == 4, route.IsFace); Assert.Equal(image == 5, route.IsShell);
    }

    [Fact]
    public void Entire_observed_clone_interval_is_allowed_without_broad_prefix_matching()
    {
        for (int id = 1340001; id <= 1340016; id++)
        {
            Assert.True(LowerHandProfile.TryMatchPath($"Emj/{id}/9/4", out _));
            Assert.True(LowerHandProfile.TryMatchPath($"Emj/{id}/9/5", out _));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EmjL/134/9/4")]
    [InlineData("NotEmj/134/9/4")]
    [InlineData("Emj/133/9/4")]
    [InlineData("Emj/137/9/4")]
    [InlineData("Emj/140/9/4")]
    [InlineData("Emj/143/9/4")]
    [InlineData("Emj/1340000/9/4")]
    [InlineData("Emj/1340017/9/4")]
    [InlineData("Emj/13400010/9/4")]
    [InlineData("Emj/1340001suffix/9/4")]
    [InlineData("Emj/134/9/4/child")]
    [InlineData("Emj/134/9/4suffix")]
    [InlineData("Emj/134/9/3")]
    [InlineData("Emj/134/8/4")]
    [InlineData("emj/134/9/4")]
    [InlineData("Emj/0134/9/4")]
    [InlineData("Emj/+134/9/4")]
    [InlineData("Emj/ 134/9/4")]
    [InlineData("Emj/134/09/4")]
    [InlineData("Emj/134/9/04")]
    [InlineData("Emj/134/9/4 ")]
    [InlineData("Emj//134/9/4")]
    [InlineData("Emj\\134\\9\\4")]
    public void Every_noncanonical_or_unlisted_route_is_rejected(string? path)
    {
        Assert.False(LowerHandProfile.TryMatchPath(path, out _));
        Assert.False(LowerHandProfile.CanReadResource(path, 1055, 1010, 2, 2));
    }

    [Theory]
    [InlineData(1054, 1010, 2, 2)]
    [InlineData(1055, 1009, 2, 2)]
    [InlineData(1055, 1010, 3, 2)]
    [InlineData(1055, 1010, 2, 3)]
    [InlineData(0, 0, 0, 0)]
    public void All_four_template_types_must_match_before_resource_access(int root, int button, int face, int shell)
        => Assert.False(LowerHandProfile.CanReadResource("Emj/134/9/4", root, button, face, shell));

    [Fact]
    public void Both_allowed_leaves_require_the_entire_matched_template()
    {
        Assert.True(LowerHandProfile.CanReadResource("Emj/134/9/4", 1055, 1010, 2, 2));
        Assert.True(LowerHandProfile.CanReadResource("Emj/134/9/5", 1055, 1010, 2, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(14)]
    public void Verified_nonoverlapping_candidates_can_be_shown_for_manual_comparison(int count)
    {
        var faces = Enumerable.Range(0, count).Select(Face).ToArray();
        int resourceChecks = 0;
        var result = LowerHandProfile.CheckPreview(faces, id => { resourceChecks++; return id is >= 1000 and <= 1013; });
        Assert.True(result.Eligible); Assert.Equal(count, resourceChecks);
        Assert.Contains("禁止用作引擎输入", result.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    public void Candidate_count_alone_never_proves_a_hand_and_out_of_bounds_preview_is_refused(int count)
    {
        int queries = 0;
        var result = LowerHandProfile.CheckPreview(Enumerable.Range(0, count).Select(Face).ToArray(),
            _ => { queries++; return true; });
        Assert.False(result.Eligible); Assert.Equal(0, queries);
    }

    [Fact]
    public void Unknown_or_zero_icon_never_reaches_resource_checker()
    {
        foreach (uint? id in new uint?[] { null, 0 })
        {
            int queries = 0;
            Assert.False(LowerHandProfile.CheckPreview([Face() with { IconId = id }], _ => { queries++; return true; }).Eligible);
            Assert.Equal(0, queries);
        }
    }

    [Fact]
    public void Positive_icon_id_is_not_valid_without_resource_confirmation()
        => Assert.False(LowerHandProfile.CheckPreview([Face()], _ => false).Eligible);

    [Theory]
    [InlineData("UNVERIFIED")]
    [InlineData("READ_ERROR")]
    [InlineData("")]
    public void Stale_icon_accompanying_unknown_or_failed_diagnostic_status_is_not_previewed(string status)
        => Assert.False(LowerHandProfile.CheckPreview([Face() with { DiagnosticStatus = status }], _ => true).Eligible);

    [Fact]
    public void Shell_is_not_mistaken_for_a_face_even_when_the_resource_exists()
        => Assert.False(LowerHandProfile.CheckPreview([Face() with { Path = "Emj/134/9/5" }], _ => true).Eligible);

    [Fact]
    public void Duplicate_path_is_rejected_even_at_different_screen_coordinates()
        => Assert.False(LowerHandProfile.CheckPreview([Face(), Face() with { X = 500 }], _ => true).Eligible);

    [Fact]
    public void Same_position_clone_and_partial_overlap_are_rejected()
    {
        Assert.False(LowerHandProfile.CheckPreview([Face(), Face(1) with { X = 100 }], _ => true).Eligible);
        Assert.False(LowerHandProfile.CheckPreview([Face(), Face(1) with { X = 139 }], _ => true).Eligible);
        Assert.True(LowerHandProfile.CheckPreview([Face(), Face(1)], _ => true).Eligible);
    }

    [Theory]
    [InlineData(float.NaN, 640, 40, 60)]
    [InlineData(100, float.PositiveInfinity, 40, 60)]
    [InlineData(-1, 640, 40, 60)]
    [InlineData(100, 0, 40, 60)]
    [InlineData(100, 640, 0, 60)]
    [InlineData(100, 640, 40, -1)]
    [InlineData(float.MaxValue, 640, int.MaxValue, 60)]
    public void Screen_rectangle_must_be_positive_finite_and_representable(float x, float y, int width, int height)
        => Assert.False(LowerHandProfile.CheckPreview([Face() with { X = x, Y = y, Width = width, Height = height }], _ => true).Eligible);

    [Fact]
    public void Entire_layout_is_validated_before_any_icon_resource_query()
    {
        int queries = 0;
        Assert.False(LowerHandProfile.CheckPreview([Face(), Face(1) with { Path = "Emj/137/9/4" }],
            _ => { queries++; return true; }).Eligible);
        Assert.Equal(0, queries);
    }

    [Fact]
    public void Geometry_gate_can_run_before_icons_or_resource_status_are_known()
    {
        var candidates = new[] { Face() with { IconId = null, DiagnosticStatus = "NOT_READ" },
            Face(1) with { IconId = null, DiagnosticStatus = "LAYOUT_PENDING" } };
        Assert.True(LowerHandProfile.CheckLayout(candidates).Eligible);
        Assert.False(LowerHandProfile.CheckPreview(candidates, _ => true).Eligible);
    }

    [Fact]
    public void Geometry_gate_rejects_transition_clones_and_excess_count_without_any_icon_input()
    {
        Assert.False(LowerHandProfile.CheckLayout([Face() with { IconId = null }, Face(1) with { X = 100, IconId = null }]).Eligible);
        Assert.False(LowerHandProfile.CheckLayout(Enumerable.Range(0, 15).Select(i => Face(i) with { IconId = null }).ToArray()).Eligible);
        Assert.False(LowerHandProfile.CheckLayout([Face() with { Path = "Emj/137/9/4", IconId = null }]).Eligible);
    }

    [Fact]
    public void Resource_failure_returns_diagnostic_reason_without_exception_payload()
    {
        var result = LowerHandProfile.CheckPreview([Face()], _ => throw new InvalidOperationException("private payload"));
        Assert.False(result.Eligible); Assert.Contains("InvalidOperationException", result.Reason);
        Assert.DoesNotContain("private payload", result.Reason);
    }

    [Fact]
    public void Candidate_export_has_only_the_requested_diagnostic_fields()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(Face()));
        Assert.Equal(new[] { "Path", "X", "Y", "Width", "Height", "IconId", "DiagnosticStatus", "FacePathHash" },
            json.RootElement.EnumerateObject().Select(x => x.Name));
    }

    [Fact]
    public void Fractional_screen_extents_roundtrip_and_detect_overlap_without_integer_rounding()
    {
        var face = Face() with { Width = 56f, Height = 72.8f };
        var restored = JsonSerializer.Deserialize<HandFaceCandidate>(JsonSerializer.Serialize(face));
        Assert.Equal(face, restored);
        Assert.False(LowerHandProfile.CheckLayout([face, Face(1) with { X = face.X + 55.9f }]).Eligible);
        Assert.True(LowerHandProfile.CheckLayout([face, Face(1) with { X = face.X + 56f }]).Eligible);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    [InlineData(0x12345678u)]
    public void Export_preserves_unread_zero_and_known_hash_as_distinct_diagnostic_values(uint? hash)
    {
        var candidate = Face() with { FacePathHash = hash };
        var restored = JsonSerializer.Deserialize<HandFaceCandidate>(JsonSerializer.Serialize(candidate));
        Assert.Equal(candidate, restored);
        Assert.Equal(hash, restored!.FacePathHash);
    }
}
