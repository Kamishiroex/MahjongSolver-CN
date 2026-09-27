using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>Synthetic transform chains; these tests do not establish a new CN field mapping.</summary>
public sealed class LowerHandGeometryTests
{
    [Theory]
    [InlineData(1f, 110f, 220f, 40f, 52f)]
    [InlineData(1.4f, 114f, 228f, 56f, 72.8f)]
    [InlineData(0.8f, 108f, 216f, 32f, 41.6f)]
    public void Root_scale_projects_local_size_into_screen_bounds(float scale, float x, float y, float width, float height)
    {
        var chain = Chain(scale);
        Assert.True(LowerHandGeometry.TryProject(chain, out var bounds, out var error), error);
        Assert.Null(error);
        Close(x, bounds.X); Close(y, bounds.Y); Close(width, bounds.Width); Close(height, bounds.Height);
    }

    [Fact]
    public void Nonzero_origin_is_applied_before_scaling_and_translation()
    {
        var chain = Chain(2);
        chain[1] = chain[1] with { OriginX = 50, OriginY = 30 };
        Assert.True(LowerHandGeometry.TryProject(chain, out var bounds, out var error), error);
        // Root origin is (150,230): (10,20) becomes (70,210).
        Assert.Equal(new LowerScreenBounds(70, 210, 80, 104), bounds);
    }

    [Fact]
    public void Nested_scales_use_local_transforms_once_not_each_cached_matrix()
    {
        LayoutTransform[] chain =
        [
            Node(4, 2, 1, 1.2f, 10, 20, 40, 52),
            Node(46, 1, 1.5f, 1.2f, 5, 7, 720, 700),
            Node(1, 1, 0.8f, 0.8f, 100, 200, 1260, 700),
        ];
        Assert.True(LowerHandGeometry.TryProject(chain, out var bounds, out var error), error);
        Close(116, bounds.X); Close(229.6f, bounds.Y); Close(48, bounds.Width); Close(62.4f, bounds.Height);
    }

    [Fact]
    public void Cache_rounding_tolerance_does_not_allow_different_transforms()
    {
        var chain = Chain(1.4f);
        chain[0] = chain[0] with { M11 = 1.4001f, M22 = 1.3999f };
        Assert.True(LowerHandGeometry.TryProject(chain, out _, out _));
        chain[0] = chain[0] with { M11 = 1.401f };
        Rejected(chain, "LOWER_GEOMETRY_MATRIX_MISMATCH");
    }

    [Fact]
    public void Parent_cache_mismatch_is_rejected_even_if_leaf_cache_matches()
    {
        var chain = Chain(1.4f);
        chain[1] = chain[1] with { M22 = 1 };
        Rejected(chain, "LOWER_GEOMETRY_MATRIX_MISMATCH");
    }

    [Theory]
    [InlineData("nonuniform")]
    [InlineData("negative")]
    [InlineData("zero")]
    [InlineData("rotation")]
    [InlineData("shear")]
    [InlineData("reflection")]
    public void Unsupported_transforms_are_not_relaxed(string variation)
    {
        var chain = Chain(1);
        chain[0] = variation switch
        {
            "nonuniform" => chain[0] with { ScaleY = 0.8f },
            "negative" => chain[0] with { ScaleX = -1, ScaleY = -1 },
            "zero" => chain[0] with { ScaleX = 0, ScaleY = 0 },
            "rotation" => chain[0] with { Rotation = 0.001f },
            "shear" => chain[0] with { M12 = 0.00001f },
            _ => chain[0] with { M11 = -1 },
        };
        Rejected(chain, "LOWER_GEOMETRY_TRANSFORM_UNSUPPORTED");
    }

    [Theory]
    [InlineData("scale")]
    [InlineData("rotation")]
    [InlineData("origin")]
    [InlineData("position")]
    [InlineData("matrix")]
    public void Nonfinite_numeric_fields_fail_closed(string field)
    {
        var chain = Chain(1);
        chain[0] = field switch
        {
            "scale" => chain[0] with { ScaleX = float.NaN },
            "rotation" => chain[0] with { Rotation = float.PositiveInfinity },
            "origin" => chain[0] with { OriginY = float.NaN },
            "position" => chain[0] with { LocalX = float.NegativeInfinity },
            _ => chain[0] with { M22 = float.NaN },
        };
        Rejected(chain, "LOWER_GEOMETRY_NONFINITE");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Missing_local_coordinates_do_not_become_zero(bool x)
    {
        var chain = Chain(1);
        chain[0] = x ? chain[0] with { LocalX = null } : chain[0] with { LocalY = null };
        Rejected(chain, "LOWER_GEOMETRY_CHAIN_INCOMPLETE");
    }

    [Fact]
    public void Unknown_or_excessive_chains_are_rejected()
    {
        Rejected(null, "LOWER_GEOMETRY_CHAIN_INVALID");
        Rejected([], "LOWER_GEOMETRY_CHAIN_INVALID");
        Rejected([Chain(1)[0]], "LOWER_GEOMETRY_CHAIN_INVALID");
        var chain = Chain(1);
        Rejected([chain[0] with { Type = 1 }, chain[1]], "LOWER_GEOMETRY_CHAIN_INVALID");
        Rejected([chain[0], chain[1] with { NodeId = 46 }], "LOWER_GEOMETRY_CHAIN_INVALID");
        Rejected(Enumerable.Repeat(chain[1], LowerHandGeometry.MaximumDepth + 1).ToArray(), "LOWER_GEOMETRY_CHAIN_INVALID");
    }

    [Fact]
    public void Missing_intermediate_node_is_rejected()
    {
        var chain = Chain(1);
        Rejected([chain[0], null!, chain[1]], "LOWER_GEOMETRY_CHAIN_INCOMPLETE");
    }

    [Theory]
    [InlineData(0, 52)]
    [InlineData(40, -1)]
    public void Leaf_dimensions_must_be_positive(int width, int height)
    {
        var chain = Chain(1);
        chain[0] = chain[0] with { Width = width, Height = height };
        Rejected(chain, "LOWER_GEOMETRY_SIZE_INVALID");
    }

    [Fact]
    public void Unrepresentable_screen_rectangles_fail_without_arbitrary_scale_limits()
    {
        var chain = Chain(float.MaxValue);
        Rejected(chain, "LOWER_GEOMETRY_BOUNDS_UNREPRESENTABLE");
        chain = Chain(1);
        chain[1] = chain[1] with { LocalX = float.MaxValue };
        Rejected(chain, "LOWER_GEOMETRY_BOUNDS_UNREPRESENTABLE");
    }

    private static LayoutTransform[] Chain(float scale) =>
    [
        Node(4, 2, 1, scale, 10, 20, 40, 52),
        Node(1, 1, scale, scale, 100, 200, 1260, 700),
    ];

    private static LayoutTransform Node(uint id, int type, float localScale, float cachedScale,
        float x, float y, int width, int height) =>
        new(id, type, localScale, localScale, 0, 0, 0, cachedScale, 0, 0, cachedScale, width, height, x, y);

    private static void Close(float expected, float actual) => Assert.InRange(Math.Abs(expected - actual), 0, 0.001f);

    private static void Rejected(IReadOnlyList<LayoutTransform>? chain, string code)
    {
        Assert.False(LowerHandGeometry.TryProject(chain, out var bounds, out var error));
        Assert.Equal(default, bounds);
        Assert.Equal(code, error);
    }
}
