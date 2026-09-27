using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>Exact routing tests only; they do not certify public ownership, parent pointers, or Mahjong semantics.</summary>
public sealed class PublicTableMetadataProfileTests
{
    [Theory]
    [InlineData(118, 1021, 116, "river-bottom")]
    [InlineData(1180005, 1021, 116, "river-bottom")]
    [InlineData(1180014, 1021, 116, "river-bottom")]
    [InlineData(121, 1023, 119, "river-right")]
    [InlineData(1210005, 1023, 119, "river-right")]
    [InlineData(1210015, 1023, 119, "river-right")]
    [InlineData(124, 1024, 122, "river-top")]
    [InlineData(1240005, 1024, 122, "river-top")]
    [InlineData(1240014, 1024, 122, "river-top")]
    [InlineData(127, 1022, 125, "river-left")]
    [InlineData(1270006, 1022, 125, "river-left")]
    [InlineData(1270014, 1022, 125, "river-left")]
    public void Observed_river_roots_have_exact_type_and_screen_owner_chain(uint root, int type, uint owner, string area)
    {
        Assert.True(PublicTableMetadataProfile.TryMatch($"Emj/{root}", type, out var route));
        Assert.Equal(area, route.Area); Assert.Equal($"Emj/{root}", route.RootPath); Assert.Equal(type, route.RootType);
        Assert.Equal(new uint[] { owner, 46, 1 }, route.OwnerIds);
        Assert.False(route.ReadShellPart); Assert.Null(route.RequiredPathTypes);
    }

    [Fact]
    public void Every_observed_clone_and_only_its_shell_leaf_can_request_selected_part_metadata()
    {
        foreach (var (root, lastClone) in new[] { (118, 14), (121, 15), (124, 14), (127, 14) })
            for (int clone = 0; clone <= lastClone; clone++)
            {
                string rootPath = "Emj/" + (clone == 0 ? root : root * 10000 + clone);
                Assert.True(PublicTableMetadataProfile.TryMatch(rootPath + "/4", 2, out var face));
                Assert.False(face.ReadShellPart);
                Assert.True(PublicTableMetadataProfile.TryMatch(rootPath + "/5", 2, out var shell));
                Assert.True(shell.ReadShellPart);
                Assert.Equal(new[] { new PublicPathType(rootPath + "/2", 1), new PublicPathType(rootPath + "/3", 1) }, shell.RequiredPathTypes);
            }
    }

    [Theory]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    public void Static_dora_display_roots_allow_only_transforms_and_distinguish_image_from_ninegrid(uint root)
    {
        string path = $"Emj/{root}";
        Assert.True(PublicTableMetadataProfile.TryMatch(path, 1006, out var area));
        Assert.Equal("dora-display", area.Area); Assert.Equal(new uint[] { 26, 21, 1 }, area.OwnerIds);
        foreach (var (suffix, type) in new[] { ("", 1006), ("/2", 2), ("/3", 4), ("/4", 4) })
        {
            Assert.True(PublicTableMetadataProfile.TryMatch(path + suffix, type, out var route));
            Assert.False(route.ReadShellPart); Assert.Null(route.RequiredPathTypes);
        }
        Assert.False(PublicTableMetadataProfile.TryMatch(path + "/4", 2, out _));
        Assert.False(PublicTableMetadataProfile.TryMatch(path + "/5", 2, out _));
    }

    [Theory]
    [InlineData("Emj/118", 1023)]
    [InlineData("Emj/121", 1024)]
    [InlineData("Emj/124", 1021)]
    [InlineData("Emj/127", 1023)]
    [InlineData("Emj/118/4", 4)]
    [InlineData("Emj/118/5", 4)]
    [InlineData("Emj/28/2", 4)]
    [InlineData("Emj/28/3", 2)]
    public void Template_or_leaf_type_mismatch_rejects_route(string path, int type)
        => Assert.False(PublicTableMetadataProfile.TryMatch(path, type, out _));

    [Theory]
    [InlineData("Emj/117/5")]
    [InlineData("Emj/120/5")]
    [InlineData("Emj/123/5")]
    [InlineData("Emj/126/5")]
    [InlineData("Emj/1180000/5")]
    [InlineData("Emj/1180015/5")]
    [InlineData("Emj/1210016/5")]
    [InlineData("Emj/1240015/5")]
    [InlineData("Emj/1270015/5")]
    [InlineData("Emj/134/9/5")]
    [InlineData("Emj/137/5")]
    [InlineData("Emj/138/4")]
    [InlineData("Emj/140/5")]
    [InlineData("Emj/141/4")]
    [InlineData("Emj/143/5")]
    [InlineData("Emj/144/4")]
    [InlineData("Emj/270001/2")]
    [InlineData("Emj/280001/2")]
    [InlineData("Emj/33/2")]
    public void Unobserved_clones_alternate_roots_and_all_closed_hand_regions_remain_excluded(string path)
        => Assert.False(PublicTableMetadataProfile.TryMatch(path, 2, out _));

    [Theory]
    [InlineData("EmjL/118/5")]
    [InlineData("emj/118/5")]
    [InlineData("Emj/0118/5")]
    [InlineData("Emj/+118/5")]
    [InlineData("Emj/118 /5")]
    [InlineData("Emj/118/05")]
    [InlineData("Emj/118/5/child")]
    [InlineData("Emj/118/5suffix")]
    [InlineData("Emj/11800010/5")]
    [InlineData("Emj/118//5")]
    [InlineData("Emj/118/5 ")]
    [InlineData("Emj\\118\\5")]
    [InlineData("")]
    public void Prefix_spoofing_or_noncanonical_path_never_grants_metadata_access(string path)
        => Assert.False(PublicTableMetadataProfile.TryMatch(path, 2, out _));

    [Fact]
    public void Ancestor_container_paths_are_not_promoted_to_broad_resource_read_scopes()
    {
        foreach (string path in new[] { "Emj/116", "Emj/119", "Emj/122", "Emj/125", "Emj/118/2", "Emj/118/3", "Emj/26", "Emj/21" })
            Assert.False(PublicTableMetadataProfile.TryMatch(path, 1, out _));
    }

    [Fact]
    public void Caller_mutation_of_one_route_cannot_change_future_whitelist_contracts()
    {
        Assert.True(PublicTableMetadataProfile.TryMatch("Emj/118/5", 2, out var first));
        first.OwnerIds[0] = 137;
        first.RequiredPathTypes![0] = new("Emj/138", 1058);
        Assert.True(PublicTableMetadataProfile.TryMatch("Emj/118/5", 2, out var second));
        Assert.Equal(116u, second.OwnerIds[0]);
        Assert.Equal(new PublicPathType("Emj/118/2", 1), second.RequiredPathTypes![0]);
    }

    [Fact]
    public void Newly_observed_clones_preserve_strict_root_and_leaf_types_and_ancestry_requirements()
    {
        foreach (var (root, firstNew, lastNew, type, owner) in new[]
                 { (118, 6, 14, 1021, 116), (121, 6, 15, 1023, 119),
                   (124, 6, 14, 1024, 122), (127, 7, 14, 1022, 125) })
            for (var clone = firstNew; clone <= lastNew; clone++)
            {
                var path = $"Emj/{root * 10000 + clone}";
                Assert.True(PublicTableMetadataProfile.TryMatch(path, type, out var actualRoot));
                Assert.Equal(new uint[] { (uint)owner, 46, 1 }, actualRoot.OwnerIds);
                Assert.False(actualRoot.ReadShellPart);
                Assert.False(PublicTableMetadataProfile.TryMatch(path, type + 1, out _));
                Assert.True(PublicTableMetadataProfile.TryMatch(path + "/4", 2, out var face));
                Assert.False(face.ReadShellPart);
                Assert.True(PublicTableMetadataProfile.TryMatch(path + "/5", 2, out var shell));
                Assert.Equal(type, shell.RootType);
                Assert.Equal(new[] { new PublicPathType(path + "/2", 1), new PublicPathType(path + "/3", 1) }, shell.RequiredPathTypes);
                Assert.False(PublicTableMetadataProfile.TryMatch(path + "/5", 4, out _));
                Assert.False(PublicTableMetadataProfile.TryMatch(path + "/6", 2, out _));
            }
    }
}
