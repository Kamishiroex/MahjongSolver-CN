using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>Tests scope isolation only, not live ownership, rendering or meld semantics.</summary>
public sealed class LowerMeldMetadataProfileTests
{
    [Fact]
    public void Only_two_observed_groups_and_four_static_tile_branches_are_in_scope()
    {
        foreach (var group in new[] { 112, 1120001 })
        foreach (var tile in new[] { 2, 3, 4, 5 })
        foreach (var image in new[] { 4, 5 })
        {
            var path = $"Emj/{group}/{tile}/9/{image}";
            Assert.True(LowerMeldMetadataProfile.TryMatch(path, 2, out var route));
            Assert.Equal("lower-meld-candidate", route.Area);
            Assert.Equal($"Emj/{group}", route.RootPath);
            Assert.Equal(1060, route.RootType);
            Assert.Equal(new uint[] { 111, 46, 1 }, route.OwnerIds);
            Assert.Equal(image == 5, route.ReadShellPart);
            Assert.Equal(new[] { new PublicPathType($"Emj/{group}", 1060),
                new PublicPathType($"Emj/{group}/{tile}", tile is 2 or 3 ? 1055 : 1056),
                new PublicPathType($"Emj/{group}/{tile}/9", tile is 2 or 3 ? 1010 : 1013) },
                route.RequiredPathTypes);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EmjL/112/2/9/5")]
    [InlineData("emj/112/2/9/5")]
    [InlineData("Emj/1120000/2/9/5")]
    [InlineData("Emj/1120002/2/9/5")]
    [InlineData("Emj/11200010/2/9/5")]
    [InlineData("Emj/113/2/9/5")]
    [InlineData("Emj/114/2/9/5")]
    [InlineData("Emj/115/2/9/5")]
    [InlineData("Emj/134/2/9/5")]
    [InlineData("Emj/112/1/9/5")]
    [InlineData("Emj/112/6/9/5")]
    [InlineData("Emj/112/2/8/5")]
    [InlineData("Emj/112/2/9/6")]
    [InlineData("Emj/112/2/9")]
    [InlineData("Emj/112/2/9/5/child")]
    [InlineData("Emj/0112/2/9/5")]
    [InlineData("Emj/+112/2/9/5")]
    [InlineData("Emj/112/02/9/5")]
    [InlineData("Emj/112/2/09/5")]
    [InlineData("Emj/112/2/9/05")]
    [InlineData("Emj/112/2/9/5 ")]
    [InlineData("Emj/112/2/9/5suffix")]
    [InlineData("Emj//112/2/9/5")]
    [InlineData("Emj\\112\\2\\9\\5")]
    public void Unobserved_groups_opponents_and_noncanonical_paths_are_rejected(string? path)
    {
        Assert.False(LowerMeldMetadataProfile.TryMatch(path, 2, out _));
        Assert.False(LowerMeldMetadataProfile.HasExpectedComponentTypes(path, 1055, 1010));
        Assert.False(LowerMeldMetadataProfile.HasExpectedComponentTypes(path, 1056, 1013));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(1010)]
    [InlineData(1060)]
    public void A_matching_path_never_permits_a_non_image_node(int nodeType)
        => Assert.False(LowerMeldMetadataProfile.TryMatch("Emj/112/2/9/5", nodeType, out _));

    [Theory]
    [InlineData(2, 1055, 1010)]
    [InlineData(3, 1055, 1010)]
    [InlineData(4, 1056, 1013)]
    [InlineData(5, 1056, 1013)]
    public void Exact_intermediate_templates_are_required(int tile, int tileType, int buttonType)
    {
        var path = $"Emj/112/{tile}/9/5";
        Assert.True(LowerMeldMetadataProfile.HasExpectedComponentTypes(path, tileType, buttonType));
        Assert.False(LowerMeldMetadataProfile.HasExpectedComponentTypes(path, tileType + 1, buttonType));
        Assert.False(LowerMeldMetadataProfile.HasExpectedComponentTypes(path, tileType, buttonType + 1));
        Assert.False(LowerMeldMetadataProfile.HasExpectedComponentTypes(path, 0, 0));
    }

    [Fact]
    public void Face_routes_never_authorize_selected_part_access()
    {
        Assert.True(LowerMeldMetadataProfile.TryMatch("Emj/112/4/9/4", 2, out var route));
        Assert.False(route.ReadShellPart);
        Assert.False(LowerMeldMetadataProfile.HasExpectedComponentTypes("Emj/112/4/9/4", 1055, 1010));
    }

    [Fact]
    public void Owner_id_array_is_not_shared_between_calls()
    {
        Assert.True(LowerMeldMetadataProfile.TryMatch("Emj/112/2/9/5", 2, out var first));
        first.OwnerIds[0] = 137;
        Assert.True(LowerMeldMetadataProfile.TryMatch("Emj/112/2/9/5", 2, out var second));
        Assert.Equal(new uint[] { 111, 46, 1 }, second.OwnerIds);
    }
}
