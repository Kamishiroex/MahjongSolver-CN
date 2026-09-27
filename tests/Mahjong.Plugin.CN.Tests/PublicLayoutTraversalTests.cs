using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Common.Math;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>Artificial layout buffers exercise metadata read boundaries, not live meld semantics.</summary>
public sealed unsafe class PublicLayoutTraversalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Later_river_rows_fit_bounded_capture_and_still_require_each_owner(bool corruptBottomOwner)
    {
        // Synthetic buffers use only the exact root ranges observed in the fourth capture.
        // 61 river slots produce 183 metadata rows, exceeding the old 128-row ceiling.
        var memory = new SyntheticMemory();
        var roots = new List<nint>();
        memory.Store(0x30000, SyntheticMemory.Node(1, type: NodeType.Res));
        memory.Store(0x31000, SyntheticMemory.Node(46, 0x30000, NodeType.Res));
        int index = 0;
        foreach (var (root, clones, template, owner) in new (uint, int, int, uint)[]
            { (118, 14, 1021, 116), (121, 15, 1023, 119), (124, 14, 1024, 122), (127, 14, 1022, 125) })
        {
            nint ownerAddress = 0x40000 + (nint)(owner * 0x100);
            memory.Store(ownerAddress, SyntheticMemory.Node(corruptBottomOwner && owner == 116 ? 999u : owner,
                0x31000, NodeType.Res));
            for (int clone = 0; clone <= clones; clone++, index++)
            {
                nint address = 0x100000 + index * 0x10000;
                roots.Add(address);
                uint id = clone == 0 ? root : root * 10000 + (uint)clone;
                memory.StoreComponentNode(address, SyntheticMemory.Node(id, ownerAddress, (NodeType)template), address + 0x1000);
                memory.Store(address + 0x2000, SyntheticMemory.Node(2, address, NodeType.Res));
                memory.Store(address + 0x3000, SyntheticMemory.Node(3, address + 0x2000, NodeType.Res));
                memory.Store(address + 0x4000, SyntheticMemory.Node(4, address + 0x3000)); // No face header bytes.
                ushort partId = template == 1021 ? (ushort)18 : (ushort)20;
                memory.Store(address + 0x5000, new AtkImageNode
                {
                    AtkResNode = SyntheticMemory.Node(5, address + 0x3000), PartId = partId,
                    PartsList = (AtkUldPartsList*)(address + 0x7000),
                });
                memory.Store(address + 0x7000, new AtkUldPartsList
                    { Id = 18, PartCount = 23, Parts = (AtkUldPart*)(address + 0x8000) });
                memory.Store(address + 0x8000 + partId * sizeof(AtkUldPart), new AtkUldPart
                {
                    U = template == 1021 ? (ushort)97 : (ushort)132, V = 0, Width = 34, Height = 45,
                    UldAsset = (AtkUldAsset*)0x7FFE0000, // Unmapped: metadata must never follow this pointer.
                });
                memory.Store(address + 0x1000, new AtkComponentBase
                {
                    UldManager = memory.NodeListAt(address + 0x6000,
                        address + 0x2000, address + 0x3000, address + 0x4000, address + 0x5000),
                });
            }
        }
        memory.Addon(memory.NodeList(roots.ToArray()));
        var result = memory.Reader().Probe("Emj", true, capturePublicLayout: true);
        Assert.Null(result.Error);
        Assert.Equal(183, result.PublicLayouts!.Count);
        Assert.Equal(corruptBottomOwner ? 45 : 0,
            result.PublicLayouts.Count(x => x.Status == "PUBLIC_OWNER_UNVERIFIED"));
        Assert.Equal(corruptBottomOwner ? 46 : 61,
            result.PublicLayouts.Count(x => x.ShellPart is not null));
        Assert.DoesNotContain(memory.Reads, x => x.Address >= 0x7FFE0000);
        Assert.All(roots, address => Assert.DoesNotContain(memory.Reads,
            x => x.Address == address + 0x4000 && x.Count > sizeof(AtkResNode)));
    }

    [Fact]
    public void Public_metadata_capture_is_off_by_default_even_for_matching_paths()
    {
        var f = new Fixture();
        var result = f.Memory.Reader().Probe("Emj", true);
        Assert.Null(result.Error);
        Assert.Null(result.PublicLayouts);
        f.AssertNoImageHeaders();
        f.AssertNoAssets();
    }

    [Theory]
    [InlineData("EmjL", true, true, true)]
    [InlineData("Emj", false, true, true)]
    [InlineData("Emj", true, false, true)]
    [InlineData("Emj", true, true, false)]
    public void Only_detailed_ready_visible_Emj_can_enter_the_metadata_branch(string name, bool detail,
        bool visible, bool ready)
    {
        var f = new Fixture();
        f.Memory.Addon(f.Manager, visible: visible, ready: ready);
        var result = f.Memory.Reader().Probe(name, detail, capturePublicLayout: true);
        Assert.Null(result.Error);
        Assert.Null(result.PublicLayouts);
        f.AssertNoImageHeaders();
        f.AssertNoAssets();
    }

    [Fact]
    public void Face_stays_at_base_header_and_shell_reads_only_its_selected_part_metadata()
    {
        var f = new Fixture();
        var result = f.Memory.Reader().Probe("Emj", true, capturePublicLayout: true);
        Assert.Null(result.Error);
        var layouts = result.PublicLayouts!;
        Assert.Equal(2, layouts.Count);
        var face = Assert.Single(layouts, x => x.Path.EndsWith("/9/4", StringComparison.Ordinal));
        var shell = Assert.Single(layouts, x => x.Path.EndsWith("/9/5", StringComparison.Ordinal));
        Assert.Equal("PUBLIC_LAYOUT_METADATA_ONLY", face.Status);
        Assert.Equal("PUBLIC_LAYOUT_METADATA_ONLY", shell.Status);
        Assert.Null(face.ShellPart);
        Assert.Equal(new SelectedShellPart(1, 18, 3, 42, 55, 42, 55), shell.ShellPart);
        Assert.Equal(new uint[] { 5, 9, 2, 112, 111, 46, 1 }, shell.NodeAndParents.Select(x => x.NodeId));
        Assert.Contains(f.Memory.Reads, x => x.Address == Fixture.ShellAddress && x.Count == sizeof(AtkImageNode));
        var partReads = f.Memory.Reads.Where(x => x.Address >= Fixture.PartsAddress &&
            x.Address < Fixture.PartsAddress + 3 * sizeof(AtkUldPart)).ToArray();
        Assert.Equal((Fixture.PartsAddress + sizeof(AtkUldPart), sizeof(AtkUldPart)), Assert.Single(partReads));
        f.AssertNoFaceHeader();
        f.AssertNoAssets();
    }

    [Fact]
    public void Public_opt_in_does_not_make_meld_candidates_eligible_for_lower_face_resources()
    {
        var f = new Fixture();
        var result = f.Memory.Reader().Probe("Emj", true, captureLowerHand: true, capturePublicLayout: true);
        Assert.Null(result.Error);
        Assert.Empty(result.LowerHandFaces!);
        Assert.Equal(2, result.PublicLayouts!.Count);
        f.AssertNoFaceHeader();
        f.AssertNoAssets();
    }

    [Fact]
    public void Lower_face_opt_in_alone_does_not_enable_public_shell_metadata()
    {
        var f = new Fixture();
        var result = f.Memory.Reader().Probe("Emj", true, captureLowerHand: true);
        Assert.Null(result.Error);
        Assert.Empty(result.LowerHandFaces!);
        Assert.Null(result.PublicLayouts);
        f.AssertNoImageHeaders();
        f.AssertNoAssets();
    }

    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("wrong-owner-type")]
    [InlineData("wrong-root-type")]
    [InlineData("wrong-tile-type")]
    [InlineData("wrong-button-type")]
    [InlineData("bypass-group")]
    [InlineData("extra-root-ancestor")]
    [InlineData("interposed-template-parent")]
    public void Wrong_or_noncanonical_parent_chain_rejects_before_any_shell_image_header(string failure)
    {
        var f = new Fixture(failure);
        var result = f.Memory.Reader().Probe("Emj", true, capturePublicLayout: true);
        Assert.Null(result.Error);
        Assert.Equal(2, result.PublicLayouts!.Count);
        Assert.All(result.PublicLayouts, row =>
        {
            Assert.NotEqual("PUBLIC_LAYOUT_METADATA_ONLY", row.Status);
            Assert.StartsWith("PUBLIC_", row.Status);
            Assert.Null(row.ShellPart);
        });
        f.AssertNoImageHeaders();
        f.AssertNoAssets();
    }

    [Fact]
    public void Unlisted_clone_is_not_promoted_to_a_public_scope_by_matching_suffix()
    {
        var f = new Fixture("unlisted-clone");
        var result = f.Memory.Reader().Probe("Emj", true, capturePublicLayout: true);
        Assert.Null(result.Error);
        Assert.Empty(result.PublicLayouts!);
        f.AssertNoImageHeaders();
        f.AssertNoAssets();
    }

    [Fact]
    public void Hidden_ancestor_prevents_descendant_public_metadata_and_shell_reads()
    {
        var f = new Fixture("hidden-owner");
        var result = f.Memory.Reader().Probe("Emj", true, capturePublicLayout: true);
        Assert.Null(result.Error);
        Assert.Empty(result.PublicLayouts!);
        f.AssertNoImageHeaders();
        f.AssertNoAssets();
    }

    [Fact]
    public void Finite_nonidentity_transform_is_exported_as_metadata_without_reading_a_face()
    {
        var f = new Fixture("scaled-owner");
        var result = f.Memory.Reader().Probe("Emj", true, capturePublicLayout: true);
        Assert.Null(result.Error);
        Assert.All(result.PublicLayouts!, row =>
        {
            Assert.Equal("PUBLIC_LAYOUT_METADATA_ONLY", row.Status);
            Assert.Equal(1.5f, Assert.Single(row.NodeAndParents, x => x.NodeId == 111).ScaleX);
        });
        f.AssertNoFaceHeader();
        f.AssertNoAssets();
    }

    [Fact]
    public void Public_chain_retains_local_offsets_separately_from_screen_coordinates()
    {
        var f = new Fixture("local-offsets");
        var result = f.Memory.Reader().Probe("Emj", true, capturePublicLayout: true);
        Assert.Null(result.Error);
        Assert.All(result.PublicLayouts!, row =>
        {
            var owner = Assert.Single(row.NodeAndParents, x => x.NodeId == 111);
            Assert.Equal(27f, owner.LocalX);
            Assert.Equal(-13f, owner.LocalY);
        });
        f.AssertNoFaceHeader();
        f.AssertNoAssets();
    }

    [Theory]
    [InlineData("nonfinite-owner")]
    [InlineData("nonfinite-local-offset")]
    public void Nonfinite_parent_transform_stops_the_probe_before_shell_metadata(string variation)
    {
        var f = new Fixture(variation);
        var result = f.Memory.Reader().Probe("Emj", true, capturePublicLayout: true);
        Assert.Contains("publicTransform: invalid numeric value", result.Error);
        Assert.False(result.Ready);
        Assert.Null(result.PublicLayouts);
        f.AssertNoImageHeaders();
        f.AssertNoAssets();
    }

    [Fact]
    public void Invalid_selected_part_is_reported_without_following_part_or_asset_memory()
    {
        var f = new Fixture("invalid-selected-part");
        var result = f.Memory.Reader().Probe("Emj", true, capturePublicLayout: true);
        Assert.Null(result.Error);
        var shell = Assert.Single(result.PublicLayouts!, x => x.Path.EndsWith("/9/5", StringComparison.Ordinal));
        Assert.Equal("PUBLIC_SHELL_PART_INVALID", shell.Status);
        Assert.Null(shell.ShellPart);
        Assert.DoesNotContain(f.Memory.Reads, x => x.Address >= Fixture.PartsAddress);
        f.AssertNoFaceHeader();
        f.AssertNoAssets();
    }

    [Fact]
    public void Unreadable_selected_part_stops_the_probe_without_leaking_partial_metadata_or_assets()
    {
        var f = new Fixture("unreadable-selected-part");
        var result = f.Memory.Reader().Probe("Emj", true, capturePublicLayout: true);
        Assert.Contains("publicShell.selectedPart: unreadable", result.Error);
        Assert.False(result.Ready);
        Assert.Null(result.PublicLayouts);
        f.AssertNoFaceHeader();
        f.AssertNoAssets();
    }

    private sealed class Fixture
    {
        internal const nint FaceAddress = 0x35000;
        internal const nint ShellAddress = 0x36000;
        internal const nint PartsAddress = 0x52000;
        private const nint AssetAddress = 0x7FFE0000;
        internal readonly SyntheticMemory Memory = new();
        internal readonly AtkUldManager Manager;

        internal Fixture(string? variation = null)
        {
            Memory.Store(0x33000, Node(1, variation == "extra-root-ancestor" ? 0x39000 : 0, NodeType.Res));
            Memory.Store(0x39000, Node(999, 0, NodeType.Res));
            Memory.Store(0x32000, Node(46, 0x33000, NodeType.Res));
            var owner = Node(variation == "wrong-owner" ? 54u : 111u, 0x32000,
                variation == "wrong-owner-type" ? NodeType.Image : NodeType.Res);
            if (variation == "hidden-owner") owner.NodeFlags = 0;
            if (variation == "scaled-owner") owner.ScaleX = 1.5f;
            if (variation == "nonfinite-owner") owner.ScaleX = float.NaN;
            if (variation == "local-offsets") { owner.X = 27; owner.Y = -13; }
            if (variation == "nonfinite-local-offset") owner.X = float.NaN;
            Memory.Store(0x31000, owner);
            Memory.StoreComponentNode(0x30000, Node(variation == "unlisted-clone" ? 1120002u : 112u, 0x31000,
                (NodeType)(variation == "wrong-root-type" ? 1055 : 1060)), 0x41000);
            Memory.StoreComponentNode(0x37000, Node(2, variation == "bypass-group" ? 0x31000 : 0x30000,
                (NodeType)(variation == "wrong-tile-type" ? 1056 : 1055)), 0x43000);
            Memory.StoreComponentNode(0x34000, Node(9, 0x37000,
                (NodeType)(variation == "wrong-button-type" ? 1013 : 1010)), 0x42000);
            Memory.Store(0x3A000, Node(999, 0x34000, NodeType.Res));
            nint leafParent = variation == "interposed-template-parent" ? 0x3A000 : 0x34000;
            var face = Node(4, leafParent, NodeType.Image);
            face.Width = 40; face.Height = 52;
            Memory.Store(FaceAddress, face); // Deliberately no AtkImageNode bytes for the face.
            var shell = new AtkImageNode
            {
                AtkResNode = Node(5, leafParent, NodeType.Image),
                PartId = variation == "invalid-selected-part" ? (ushort)3 : (ushort)1,
                PartsList = (AtkUldPartsList*)0x51000,
            };
            Memory.Store(ShellAddress, shell);
            Memory.Store(0x51000, new AtkUldPartsList { Id = 18, PartCount = 3, Parts = (AtkUldPart*)PartsAddress });
            if (variation != "unreadable-selected-part")
                Memory.Store(PartsAddress + sizeof(AtkUldPart), new AtkUldPart
                {
                    UldAsset = (AtkUldAsset*)AssetAddress, U = 42, V = 55, Width = 42, Height = 55,
                });
            AtkComponentBase group = default;
            group.UldManager = Memory.NodeListAt(0x21000, 0x37000);
            AtkComponentBase tile = default;
            tile.UldManager = Memory.NodeListAt(0x23000, 0x34000);
            AtkComponentBase button = default;
            button.UldManager = Memory.NodeListAt(0x22000, FaceAddress, ShellAddress);
            Memory.Store(0x41000, group); Memory.Store(0x43000, tile); Memory.Store(0x42000, button);
            Manager = Memory.NodeList(0x30000, 0x31000, 0x32000, 0x33000);
            Memory.Addon(Manager);
        }

        private static AtkResNode Node(uint id, nint parent, NodeType type)
        {
            var node = SyntheticMemory.Node(id, parent, type);
            node.ScaleX = node.ScaleY = 1;
            node.Transform = Matrix2x2.Identity;
            return node;
        }

        internal void AssertNoFaceHeader() => Assert.DoesNotContain(Memory.Reads,
            x => x.Address == FaceAddress && x.Count > sizeof(AtkResNode));

        internal void AssertNoImageHeaders() => Assert.DoesNotContain(Memory.Reads,
            x => (x.Address == FaceAddress || x.Address == ShellAddress) && x.Count > sizeof(AtkResNode));

        internal void AssertNoAssets() => Assert.DoesNotContain(Memory.Reads,
            x => x.Address >= AssetAddress);
    }
}
