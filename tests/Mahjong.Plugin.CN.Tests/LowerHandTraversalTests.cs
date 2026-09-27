using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.FFXIV.Common.Math;
using System.Text.Json;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed unsafe class LowerHandTraversalTests
{
    [Fact]
    public void Default_capture_never_reads_even_allowlisted_image_payloads()
    {
        var f = new Fixture();
        var result = f.Memory.Reader().Probe("Emj", true);
        Assert.Null(result.Error);
        Assert.Null(result.LowerHandFaces);
        f.AssertNoImagePayload();
    }

    [Theory]
    [InlineData(137)]
    [InlineData(140)]
    [InlineData(143)]
    [InlineData(1340017)]
    public void Opponent_and_unknown_roots_cannot_reach_any_image_payload(uint root)
    {
        var f = new Fixture(root);
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        Assert.Empty(result.LowerHandFaces!);
        f.AssertNoImagePayload();
    }

    [Fact]
    public void Hidden_shell_prevents_face_read_even_when_face_is_flag_visible()
    {
        var f = new Fixture(hideShell: true);
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        Assert.Equal("LOWER_ROW_SHELL_NOT_VISIBLE", Assert.Single(result.LowerHandFaces!).DiagnosticStatus);
        f.AssertNoImagePayload();
    }

    [Fact]
    public void Parent_outside_the_pinned_lower_row_cannot_read_image_payloads()
    {
        var f = new Fixture(containerId: 54);
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        Assert.Equal("LOWER_ROW_LAYOUT_UNVERIFIED", Assert.Single(result.LowerHandFaces!).DiagnosticStatus);
        f.AssertNoImagePayload();
    }

    [Fact]
    public void Ancestor_scaling_blocks_payload_before_screen_rectangle_is_assumed()
    {
        var f = new Fixture(ancestorScale: 1.5f);
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        Assert.StartsWith("LOWER_ROW_TRANSFORM_UNVERIFIED", Assert.Single(result.LowerHandFaces!).DiagnosticStatus);
        f.AssertNoImagePayload();
    }

    [Fact]
    public void Verified_scope_still_checks_back_shell_before_face_payload()
    {
        var f = new Fixture();
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        Assert.StartsWith("SHELL_NOT_FRONT", Assert.Single(result.LowerHandFaces!).DiagnosticStatus);
        Assert.Contains(f.Memory.Reads, x => x.Address == Fixture.ShellAddress && x.Count == sizeof(AtkImageNode));
        Assert.DoesNotContain(f.Memory.Reads, x => x.Address == Fixture.FaceAddress && x.Count > sizeof(AtkResNode));
    }

    [Theory]
    [InlineData(1.4f)]
    [InlineData(0.8f)]
    [InlineData(1.25f)]
    [InlineData(1.0f)]
    public void Uniform_root_scale_reaches_shell_guard_without_reading_face_payload(float scale)
    {
        var f = new Fixture(rootScale: scale);
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        var candidate = Assert.Single(result.LowerHandFaces!);
        Assert.StartsWith("SHELL_NOT_FRONT", candidate.DiagnosticStatus);
        Assert.Equal(100f, candidate.X, 3);
        Assert.Equal(200f, candidate.Y, 3);
        Assert.Equal(40f * scale, candidate.Width, 3);
        Assert.Equal(52f * scale, candidate.Height, 3);
        Assert.Null(result.PublicLayouts); // Extra geometry metadata remains explicitly opt-in.
        Assert.Contains(f.Memory.Reads, x => x.Address == Fixture.ShellAddress && x.Count == sizeof(AtkImageNode));
        Assert.DoesNotContain(f.Memory.Reads, x => x.Address == Fixture.FaceAddress && x.Count > sizeof(AtkResNode));
    }

    [Fact]
    public void Verified_res_wrappers_preserve_owned_scaled_chain()
    {
        var f = new Fixture(rootScale: 1.4f, includeWrappers: true);
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        Assert.StartsWith("SHELL_NOT_FRONT", Assert.Single(result.LowerHandFaces!).DiagnosticStatus);
        Assert.DoesNotContain(f.Memory.Reads, x => x.Address == Fixture.FaceAddress && x.Count > sizeof(AtkResNode));
    }

    [Theory]
    [InlineData("nonuniform")]
    [InlineData("negative")]
    [InlineData("rotation")]
    [InlineData("matrix")]
    [InlineData("position")]
    public void Unverified_transform_or_cached_position_never_reads_image_payload(string invalid)
    {
        var f = new Fixture(rootScale: 1.4f);
        switch (invalid)
        {
            case "nonuniform":
                f.Change(Fixture.RootAddress, n => { n.ScaleY = 1.25f; return n; });
                break;
            case "negative":
                f.Change(Fixture.RootAddress, n => { n.ScaleX = n.ScaleY = -1.4f; return n; });
                break;
            case "rotation":
                f.Change(Fixture.TableAddress, n => { n.Rotation = 0.2f; return n; });
                break;
            case "matrix":
                f.Change(Fixture.TableAddress, n => { n.Transform.M11 = 1.0f; return n; });
                break;
            case "position":
                // Move both cached image positions so the older face/shell equality
                // check still passes. Their real parent transforms still project x=100.
                f.Change(Fixture.FaceAddress, n => { n.ScreenX += 2; return n; });
                f.Change(Fixture.ShellAddress, n => { n.ScreenX += 2; return n; });
                break;
        }
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        Assert.StartsWith("LOWER_ROW_TRANSFORM_UNVERIFIED", Assert.Single(result.LowerHandFaces!).DiagnosticStatus);
        f.AssertNoImagePayload();
    }

    [Fact]
    public void Unknown_intermediate_parent_cannot_read_image_payloads()
    {
        var f = new Fixture(rootScale: 1.4f, includeWrappers: true);
        f.Change(Fixture.LeafWrapperAddress, n => { n.NodeId = 8; return n; });
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        Assert.StartsWith("LOWER_ROW_TRANSFORM_UNVERIFIED", Assert.Single(result.LowerHandFaces!).DiagnosticStatus);
        f.AssertNoImagePayload();
    }

    [Fact]
    public void Face_list_membership_cannot_bypass_actual_button_parent()
    {
        var f = new Fixture(rootScale: 1.4f);
        f.Change(Fixture.FaceAddress, n => { n.ParentNode = (AtkResNode*)Fixture.TileRootAddress; return n; });
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        Assert.StartsWith("LOWER_ROW_TRANSFORM_UNVERIFIED", Assert.Single(result.LowerHandFaces!).DiagnosticStatus);
        f.AssertNoImagePayload();
    }

    [Fact]
    public void Extra_parent_above_a_matching_root_does_not_make_a_verified_chain()
    {
        var f = new Fixture(rootScale: 1.4f, extraRootParent: true);
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        Assert.Equal("LOWER_ROW_LAYOUT_UNVERIFIED", Assert.Single(result.LowerHandFaces!).DiagnosticStatus);
        f.AssertNoImagePayload();
    }

    [Fact]
    public void Scaled_overlap_blocks_all_payloads_even_when_unscaled_width_would_fit()
    {
        var f = new Fixture(rootScale: 1.4f, secondTileScreenGap: 50f);
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        var candidates = result.LowerHandFaces!;
        Assert.Equal(2, candidates.Count);
        Assert.All(candidates, c => Assert.Equal("LOWER_ROW_TRANSITION_OR_LAYOUT_UNVERIFIED", c.DiagnosticStatus));
        Assert.Equal(50f, candidates[1].X - candidates[0].X, 3);
        Assert.Equal(56f, candidates[0].Width, 3);
        Assert.True(candidates[0].X + 40 < candidates[1].X); // Old unscaled-width test would miss it.
        Assert.True(candidates[0].X + candidates[0].Width > candidates[1].X);
        f.AssertNoImagePayload();
    }

    [Fact]
    public void Face_and_shell_scale_disagreement_blocks_both_payloads()
    {
        var f = new Fixture(rootScale: 1.4f);
        f.Change(Fixture.FaceAddress, n =>
        {
            n.ScaleX = n.ScaleY = 1.1f;
            n.Transform.M11 = n.Transform.M22 = 1.4f * 1.1f;
            return n;
        });
        var result = f.Memory.Reader().Probe("Emj", true, true);
        Assert.Null(result.Error);
        Assert.StartsWith("LOWER_ROW_TRANSFORM_UNVERIFIED", Assert.Single(result.LowerHandFaces!).DiagnosticStatus);
        f.AssertNoImagePayload();
    }

    [Fact]
    public void Explicit_public_capture_exports_two_scalar_geometry_chains_without_addresses()
    {
        var f = new Fixture(rootScale: 1.4f, includeWrappers: true);
        var result = f.Memory.Reader().Probe("Emj", true, true, true);
        Assert.Null(result.Error);
        var geometry = result.PublicLayouts!.Where(x => x.Area == "lower-row-geometry").ToArray();
        Assert.Equal(2, geometry.Length);
        Assert.Equal(new[] { "Emj/134/9/4", "Emj/134/9/5" }, geometry.Select(x => x.Path).ToArray());
        Assert.All(geometry, entry =>
        {
            Assert.Equal("PUBLIC_LAYOUT_METADATA_ONLY", entry.Status);
            Assert.Null(entry.ShellPart);
            Assert.InRange(entry.NodeAndParents.Length, 2, 16);
            Assert.All(entry.NodeAndParents, node =>
            {
                Assert.Equal(1.4f, node.M11, 3);
                Assert.Equal(1.4f, node.M22, 3);
            });
            Assert.Equal(100f, entry.NodeAndParents[^1].LocalX);
            Assert.Equal(200f, entry.NodeAndParents[^1].LocalY);
        });
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(geometry));
        string[] scalarFields = ["NodeId", "Type", "ScaleX", "ScaleY", "Rotation", "OriginX", "OriginY",
            "M11", "M12", "M21", "M22", "Width", "Height", "LocalX", "LocalY"];
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            Assert.Equal(new[] { "Path", "Area", "Status", "NodeAndParents", "ShellPart" },
                entry.EnumerateObject().Select(p => p.Name).ToArray());
            foreach (var node in entry.GetProperty("NodeAndParents").EnumerateArray())
            {
                Assert.Equal(scalarFields, node.EnumerateObject().Select(p => p.Name).ToArray());
                Assert.All(node.EnumerateObject(), p => Assert.Equal(JsonValueKind.Number, p.Value.ValueKind));
            }
        }
        Assert.DoesNotContain(f.Memory.Reads, x => x.Address == Fixture.FaceAddress && x.Count > sizeof(AtkResNode));
    }

    [Theory]
    [InlineData(137, 133)]
    [InlineData(134, 54)]
    public void Geometry_metadata_opt_in_does_not_bypass_lower_root_scope(uint root, uint container)
    {
        var f = new Fixture(rootId: root, containerId: container, rootScale: 1.4f);
        var result = f.Memory.Reader().Probe("Emj", true, true, true);
        Assert.Null(result.Error);
        Assert.DoesNotContain(result.PublicLayouts!, x => x.Area == "lower-row-geometry");
        f.AssertNoImagePayload();
    }

    private sealed class Fixture
    {
        internal const nint RootAddress = 0x33000;
        internal const nint TableAddress = 0x32000;
        internal const nint TileRootAddress = 0x30000;
        internal const nint LeafWrapperAddress = 0x37000;
        internal const nint FaceAddress = 0x35000;
        internal const nint ShellAddress = 0x36000;
        internal readonly SyntheticMemory Memory = new();
        private readonly Dictionary<nint, AtkResNode> headers = [];
        private readonly Dictionary<nint, nint> components = [];
        private readonly HashSet<nint> faceAddresses = [];
        private readonly HashSet<nint> shellAddresses = [];
        private readonly float rootScale;

        internal Fixture(uint rootId = 134, bool hideShell = false, uint containerId = 133,
            float ancestorScale = 1, float rootScale = 1, bool includeWrappers = false,
            bool extraRootParent = false, float? secondTileScreenGap = null)
        {
            this.rootScale = rootScale;
            var root = Node(1, extraRootParent ? 0x3B000 : 0, NodeType.Res);
            root.X = 100; root.Y = 200;
            root.ScaleX = root.ScaleY = rootScale;
            Store(RootAddress, root);
            if (extraRootParent) Store(0x3B000, Node(1, 0, NodeType.Res));
            var table = Node(46, RootAddress, NodeType.Res);
            table.ScaleX = ancestorScale;
            Store(TableAddress, table);
            Store(0x31000, Node(containerId, TableAddress, NodeType.Res));
            AddTile(rootId, TileRootAddress, 0x34000, FaceAddress, ShellAddress, 0x41000, 0x42000,
                0x21000, 0x22000, 0, hideShell, includeWrappers);
            if (secondTileScreenGap is { } gap)
            {
                AddTile(1340001, 0x50000, 0x54000, 0x55000, 0x56000, 0x61000, 0x62000,
                    0x71000, 0x72000, gap, false, false);
                Memory.Addon(Memory.NodeList(TileRootAddress, 0x50000, 0x31000, TableAddress, RootAddress));
            }
            else Memory.Addon(Memory.NodeList(TileRootAddress, 0x31000, TableAddress, RootAddress));
        }

        private void AddTile(uint id, nint rootAddress, nint buttonAddress, nint faceAddress, nint shellAddress,
            nint outerAddress, nint innerAddress, nint outerList, nint innerList,
            float screenOffset, bool hideShell, bool includeWrappers)
        {
            var root = Node(id, 0x31000, (NodeType)1055, screenOffset);
            root.X = screenOffset / rootScale;
            components[rootAddress] = outerAddress;
            Store(rootAddress, root);
            nint buttonParent = rootAddress, imageParent = buttonAddress;
            if (includeWrappers)
            {
                Store(0x3A000, Node(1, rootAddress, NodeType.Res, screenOffset));
                buttonParent = 0x3A000;
                Store(0x39000, Node(1, buttonAddress, NodeType.Res, screenOffset));
                Store(0x38000, Node(2, 0x39000, NodeType.Res, screenOffset));
                Store(LeafWrapperAddress, Node(3, 0x38000, NodeType.Res, screenOffset));
                imageParent = LeafWrapperAddress;
            }
            components[buttonAddress] = innerAddress;
            Store(buttonAddress, Node(9, buttonParent, (NodeType)1010, screenOffset));
            var face = Node(4, imageParent, NodeType.Image, screenOffset); face.Width = 40; face.Height = 52;
            faceAddresses.Add(faceAddress);
            Store(faceAddress, face); // Face header only: any payload access would fail.
            var shell = Node(5, imageParent, NodeType.Image, screenOffset); shell.Width = 42; shell.Height = 55;
            if (hideShell) shell.NodeFlags = 0;
            shellAddresses.Add(shellAddress);
            Store(shellAddress, shell);
            AtkComponentBase outer = default; outer.UldManager = Memory.NodeListAt(outerList, buttonAddress);
            AtkComponentBase inner = default; inner.UldManager = Memory.NodeListAt(innerList, faceAddress, shellAddress);
            Memory.Store(outerAddress, outer); Memory.Store(innerAddress, inner);
        }

        private AtkResNode Node(uint id, nint parent, NodeType type, float screenOffset = 0)
        {
            var node = SyntheticMemory.Node(id, parent, type);
            node.X = node.Y = 0;
            node.ScreenX = 100 + screenOffset; node.ScreenY = 200;
            node.ScaleX = node.ScaleY = 1;
            node.Transform = Matrix2x2.Identity;
            node.Transform.M11 = node.Transform.M22 = rootScale;
            return node;
        }

        internal void Change(nint address, Func<AtkResNode, AtkResNode> change) => Store(address, change(headers[address]));

        private void Store(nint address, AtkResNode header)
        {
            headers[address] = header;
            if (components.TryGetValue(address, out var component)) Memory.StoreComponentNode(address, header, component);
            else if (shellAddresses.Contains(address))
            {
                AtkImageNode shell = default; shell.AtkResNode = header; shell.PartId = 6;
                Memory.Store(address, shell);
            }
            else Memory.Store(address, header);
        }

        internal void AssertNoImagePayload() => Assert.DoesNotContain(Memory.Reads,
            x => (faceAddresses.Contains(x.Address) || shellAddresses.Contains(x.Address)) && x.Count > sizeof(AtkResNode));
    }
}
