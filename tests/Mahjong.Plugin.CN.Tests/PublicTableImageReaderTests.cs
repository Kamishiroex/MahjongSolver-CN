using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>Only synthetic memory; expected transforms use independent System.Numerics matrix composition.</summary>
public sealed unsafe class PublicTableImageReaderTests
{
    [Theory]
    [InlineData(1f, 51f, true)]
    [InlineData(1.4f, 51f, true)]
    [InlineData(2.75f, 51f, true)]
    [InlineData(1f, 50.9f, false)]
    public void Real_uld_right_meld_corner_reads_front_only_for_exact_pose(float scale, float sideX, bool accepted)
    {
        var f = new Fixture("Emj/113/3/4", scale); f.AddRightMeldCorner(sideX);
        var result = f.Probe(); Assert.Null(result.Error);
        var upright = Assert.Single(result.PublicTableFaces!, x => x.SlotPath == "Emj/113/3/4");
        Assert.Equal(accepted ? PublicTableImageReader.VerifiedResourceCode : "PUBLIC_OCCLUDED_OR_TRANSITION", upright.Code);
        if (!accepted) Assert.DoesNotContain(f.Memory.Reads, r => r.Address == f.Face && r.Count > sizeof(AtkResNode));
    }
    [Theory]
    [InlineData(0.5f)]
    [InlineData(1f)]
    [InlineData(1.4f)]
    [InlineData(2.75f)]
    public void Fixed_right_meld_corner_has_small_real_shell_overlap_at_any_uniform_scale(float s)
    {
        // emj.uld 1062: slot3 X26,Y2, scale .75; slot4 X51,Y35, rotation -90, scale .75.
        var face = new PublicTableProjection(28.55f*s, 2.54f*s, 23.4f*s, 30.42f*s, 0, 0, 0, false);
        var shell = new PublicTableProjection(26*s, 2*s, 25.5f*s, 33.75f*s, 0, 0, 0, false);
        var other = new PublicTableProjection(51*s, 9.5f*s, 33.75f*s, 25.5f*s, 0, 0, -90, false);
        Assert.True(PublicTableGeometry.Overlaps(face, other));
        Assert.False(PublicTableImageReader.AuditedSmallTileFringe(1023, face, shell, other));
        Assert.True(PublicTableImageReader.MatchesRightMeldPose(3, 26, 2, 4, 51, 35));
        Assert.True(PublicTableImageReader.RightMeldEdgeBounds(face, shell, other));
        Assert.False(PublicTableImageReader.RightMeldEdgeBounds(face, shell, other with { X = 50.9f*s }));
        Assert.False(PublicTableImageReader.RightMeldEdgeBounds(face, shell, shell));
        Assert.False(PublicTableImageReader.MatchesRightMeldPose(3, 26.1f, 2, 4, 51, 35));
        Assert.False(PublicTableImageReader.MatchesRightMeldPose(4, 26, 2, 5, 51, 35));
    }
    [Theory]
    [InlineData(1023)]
    [InlineData(1024)]
    public void Fixed_front_fringe_can_touch_a_previous_row_or_orthogonal_meld_shell_but_not_a_stack(int template)
    {
        var shell = new PublicTableProjection(0, 0, 34, 45, 0, 0, 0, false);
        var face = new PublicTableProjection(3.4f, 0.72f, 31.2f, 40.56f, 0, 0, 0, false);
        var next = new PublicTableProjection(34, 0, 45, 34, 0, 0, 90, false);
        Assert.True(PublicTableImageReader.AuditedSmallTileFringe(template, face, shell, next));
        Assert.False(PublicTableImageReader.AuditedSmallTileFringe(template, face, shell, next with { X = 33.9f }));
        Assert.False(PublicTableImageReader.AuditedSmallTileFringe(template, face with { X = 3.5f }, shell, next));
        Assert.False(PublicTableImageReader.AuditedSmallTileFringe(1021, face, shell, next));
        Assert.False(PublicTableImageReader.AuditedSmallTileFringe(template, face, shell, shell));
    }

    [Theory]
    [InlineData("Emj/112/5/9/4")]
    [InlineData("Emj/113/5/4")]
    public void Hidden_common_meld_visual_is_a_vacant_allocated_slot_and_does_not_read_its_face(string path)
    {
        var f = new Fixture(path); f.HideMeldVisual();
        Assert.Empty(f.Probe().PublicTableFaces!); f.AssertNoImageHeaders();
    }

    [Theory]
    [InlineData(0, 0, 0, "normal")]
    [InlineData(-75, -75, -75, "darkened")]
    [InlineData(0, -75, -75, "red-tinted")]
    [InlineData(-75, -150, -150, "darkened-red-tinted")]
    [InlineData(0, -76, -75, "unclassified")]
    public void Fixed_uld_node_tints_are_copied_without_claiming_called_or_tsumogiri_meaning(short r, short g, short b, string style)
    {
        var n = new AtkResNode { AddRed = r, AddGreen = g, AddBlue = b, MultiplyRed = 100, MultiplyGreen = 100, MultiplyBlue = 100 };
        var mark = PublicTableImageReader.ReadVisualMark(n);
        Assert.Equal(style, mark.Style); Assert.False(mark.CalledTileMeaningVerified); Assert.False(mark.TsumogiriMeaningVerified);
        n.MultiplyGreen = 99;
        Assert.Equal("unclassified", PublicTableImageReader.ReadVisualMark(n).Style);
    }

    [Theory]
    [InlineData("Emj/112/2/9/4")]
    [InlineData("Emj/112/4/9/4")]
    [InlineData("Emj/113/2/4")]
    [InlineData("Emj/113/4/4")]
    [InlineData("Emj/114/2/4")]
    [InlineData("Emj/114/4/4")]
    [InlineData("Emj/115/2/4")]
    [InlineData("Emj/115/4/4")]
    public void Verified_public_back_shell_never_authorizes_reading_its_hidden_face(string path)
    {
        var f = new Fixture(path, 1.4f); f.SetBack();
        var candidate = Assert.Single(f.Probe().PublicTableFaces!);
        Assert.Equal(PublicTableImageReader.VerifiedBackCode, candidate.Code);
        Assert.Null(candidate.IconId); Assert.Null(candidate.FacePathHash); f.AssertNoFaceHeader();
        f.Memory.Store(f.ShellHash, 123u);
        Assert.Equal("PUBLIC_BACK_HASH_MISMATCH", Assert.Single(f.Probe().PublicTableFaces!).Code);
        f.AssertNoFaceHeader();
    }

    [Theory]
    [InlineData(1023, "river-right")]
    [InlineData(1024, "river-top")]
    public void Only_fixed_adjacent_river_edge_overlap_is_allowed(int template, string area)
    {
        var a = new PublicTableFaceCandidate(area, area[6..], "a", "a", template, 0, 0, 0, 0, 0, false, null, null, "pending");
        var b = a with { SlotPath = "b" };
        var anchors = new Dictionary<string, PublicRiverAnchor> { ["a"] = new(1, 0, false), ["b"] = new(1, 34, false) };
        var face = template == 1023 ? new PublicTableProjection(0, 0, 40.56f, 31.2f, 0, 0, -90, false)
            : new PublicTableProjection(0, 0, 31.2f, 40.56f, 0, 0, 180, false);
        var shell = template == 1023 ? new PublicTableProjection(0, 30.6f, 45, 34, 0, 0, -90, false)
            : new PublicTableProjection(30.6f, 0, 34, 45, 0, 0, 180, false);
        Assert.True(PublicTableImageReader.NaturalRiverEdge(a, face, b, shell, anchors));
        Assert.False(PublicTableImageReader.NaturalRiverEdge(a, face, b, shell with { X = 0, Y = 0 }, anchors));
        Assert.False(PublicTableImageReader.NaturalRiverEdge(a, face, b with { Area = "meld-right" }, shell, anchors));
        anchors["b"] = new(1, 33, false);
        Assert.False(PublicTableImageReader.NaturalRiverEdge(a, face, b, shell, anchors));
        anchors["b"] = new(1, 34, true);
        Assert.False(PublicTableImageReader.NaturalRiverEdge(a, face, b, shell, anchors));
    }
    [Theory]
    [InlineData("Emj/118/4", "river-bottom", 1021)]
    [InlineData("Emj/117/4", "river-bottom", 1023)]
    [InlineData("Emj/1210015/4", "river-right", 1023)]
    [InlineData("Emj/120/4", "river-right", 1024)]
    [InlineData("Emj/124/4", "river-top", 1024)]
    [InlineData("Emj/123/4", "river-top", 1022)]
    [InlineData("Emj/127/4", "river-left", 1022)]
    [InlineData("Emj/126/4", "river-left", 1021)]
    [InlineData("Emj/1180030/4", "river-bottom", 1021)]
    [InlineData("Emj/112/2/9/4", "meld-bottom", 1010)]
    [InlineData("Emj/1120001/4/9/4", "meld-bottom", 1013)]
    [InlineData("Emj/112/5/9/4", "meld-bottom", 1013)]
    [InlineData("Emj/113/2/4", "meld-right", 1023)]
    [InlineData("Emj/113/4/4", "meld-right", 1024)]
    [InlineData("Emj/114/2/4", "meld-top", 1024)]
    [InlineData("Emj/114/5/4", "meld-top", 1022)]
    [InlineData("Emj/115/2/4", "meld-left", 1022)]
    [InlineData("Emj/115/4/4", "meld-left", 1021)]
    public void Fixed_public_templates_accept_current_front_face_with_scaled_rotated_mirrored_geometry(string path, string area, int template)
    {
        var f = new Fixture(path, scale: 1.4f);
        var result = Assert.Single(f.Probe().PublicTableFaces!);
        Assert.Equal(PublicTableImageReader.VerifiedResourceCode, result.Code);
        Assert.Equal(area, result.Area); Assert.Equal(template, result.Template);
        Assert.Equal(76035u, result.IconId); Assert.True(result.Width > 0); Assert.True(result.Height > 0);
        Assert.Contains(f.Memory.Reads, x => x.Address == f.Face && x.Count == sizeof(AtkImageNode));
        Assert.DoesNotContain(f.Memory.Reads, x => x.Address == f.FaceResource && x.Count > sizeof(uint));
        Assert.Equal(3, Fixture.FacePartIndex); // Only this selected part exists in synthetic memory.
    }

    [Theory]
    [InlineData("Emj/138/9/4")]
    [InlineData("Emj/141/9/4")]
    [InlineData("Emj/144/9/4")]
    [InlineData("Emj/129/4")]
    [InlineData("Emj/28/2")]
    [InlineData("Emj/0118/4")]
    [InlineData("Emj/1180000/4")]
    [InlineData("Emj/1180257/4")]
    [InlineData("EmjL/118/4")]
    [InlineData("Emj/112/2/4")]
    [InlineData("Emj/113/2/9/4")]
    public void Hidden_hands_other_regions_noncanonical_and_unbounded_clone_paths_are_not_in_scope(string path)
        => Assert.False(PublicTableImageReader.TryRoute(path, out _));

    [Theory]
    [InlineData("owner")]
    [InlineData("template")]
    [InlineData("wrapper")]
    [InlineData("matrix")]
    [InlineData("screen-cache")]
    [InlineData("rotation")]
    [InlineData("reflection")]
    [InlineData("nonuniform")]
    public void Invalid_layout_stops_before_any_image_header(string variation)
    {
        var f = new Fixture("Emj/121/4");
        switch (variation)
        {
            case "owner": f.Alter(f.Owner, n => { n.NodeId = 137; return n; }); break;
            case "template": f.Alter(f.Root, n => { n.Type = (NodeType)1058; return n; }); break;
            case "wrapper": f.Alter(f.Wrapper, n => { n.NodeId = 99; return n; }); break;
            case "matrix": f.Alter(f.Face, n => { n.Transform.M11 = 42; return n; }); break;
            case "screen-cache": f.Alter(f.Face, n => { n.ScreenX += 3; return n; }); break;
            case "rotation": f.Alter(f.Root, n => { n.Rotation += 0.2f; return n; }); break;
            case "reflection": f.Alter(f.Face, n => { n.ScaleX = -n.ScaleX; return n; }); break;
            case "nonuniform": f.Alter(f.Owner, n => { n.ScaleX = 2; return n; }); break;
        }
        var result = f.Probe(); Assert.Null(result.Error);
        Assert.All(result.PublicTableFaces!, x => Assert.NotEqual(PublicTableImageReader.VerifiedResourceCode, x.Code));
        f.AssertNoImageHeaders();
    }

    [Theory]
    [InlineData("Emj/118/4", 22)]
    [InlineData("Emj/121/4", 21)]
    [InlineData("Emj/112/2/9/4", 6)]
    [InlineData("Emj/112/4/9/4", 6)]
    public void A_selected_back_shell_never_reads_face_header_or_resources(string path, ushort back)
    {
        var f = new Fixture(path); f.ShellPart = back; f.StoreImages();
        Assert.Equal("PUBLIC_SHELL_NOT_FRONT", Assert.Single(f.Probe().PublicTableFaces!).Code);
        f.AssertNoFaceHeader(); Assert.DoesNotContain(f.Memory.Reads, x => x.Address == f.FaceResource);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("rectangle")]
    [InlineData("asset")]
    [InlineData("flip")]
    public void Shell_identity_failures_precede_face_access(string kind)
    {
        var f = new Fixture("Emj/124/4");
        if (kind == "hash") f.Memory.Store(f.ShellHash, 0xE8DCBE7Fu);
        if (kind == "rectangle") f.StoreShellPart(wrong: true);
        if (kind == "asset") f.Memory.Store(f.ShellAsset, new AtkUldAsset { Id = 99 });
        if (kind == "flip") { f.ShellFlags = ImageNodeFlags.FlipH; f.StoreImages(); }
        Assert.StartsWith("PUBLIC_SHELL_", Assert.Single(f.Probe().PublicTableFaces!).Code);
        f.AssertNoFaceHeader();
    }

    [Fact]
    public void Face_hash_and_icon_must_identify_same_catalog_resource()
    {
        var f = new Fixture("Emj/118/4");
        f.Memory.Store(f.FaceResource + (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash)), 42u);
        var result = Assert.Single(f.Probe().PublicTableFaces!);
        Assert.Equal("PUBLIC_TILE_CATALOG_MISMATCH", result.Code); Assert.Null(result.IconId); Assert.Null(result.FacePathHash);
    }

    [Fact]
    public void New_public_face_path_is_off_by_default_and_metadata_opt_in_does_not_enable_it()
    {
        var f = new Fixture("Emj/118/4");
        var result = f.Memory.Reader().Probe("Emj", true, capturePublicLayout: true);
        Assert.Null(result.Error); Assert.Null(result.PublicTableFaces); f.AssertNoFaceHeader();
    }

    [Fact]
    public void Region_presence_and_owner_are_required_before_reporting_an_empty_candidate()
    {
        var f = new Fixture("Emj/118/4");
        var original = f.Probe();
        var bottom = Assert.Single(original.PublicTableAreas!, x => x.Area == "river-bottom");
        Assert.True(bottom.EnumerationCompleted); Assert.True(bottom.ContainerVerified); Assert.False(bottom.ObservedEmptyCandidate);
        Assert.Equal("PUBLIC_AREA_OWNER_UNAVAILABLE", Assert.Single(original.PublicTableAreas!, x => x.Area == "river-right").Code);
        f.Alter(f.Owner, n => { n.NodeFlags = 0; return n; });
        var hidden = f.Probe(); Assert.Empty(hidden.PublicTableFaces!);
        bottom = Assert.Single(hidden.PublicTableAreas!, x => x.Area == "river-bottom");
        Assert.False(bottom.ObservedEmptyCandidate); Assert.False(bottom.ContainerVisible); Assert.True(bottom.EnumerationCompleted);
        Assert.False(bottom.Stable); // Raw reader does not manufacture a second independent observation.
    }

    [Fact]
    public void Empty_meld_directions_require_declared_fixed_roots_visible_shared_owner_and_complete_enumeration()
    {
        var f = new Fixture("Emj/112/2/9/4"); f.AddHiddenMeldDirections();
        var result = f.Probe(); Assert.Null(result.Error);
        foreach (string area in new[] { "meld-right", "meld-top", "meld-left" })
        {
            var a = Assert.Single(result.PublicTableAreas!, x => x.Area == area);
            Assert.True(a.ContainerVerified); Assert.True(a.ContainerVisible); Assert.True(a.EnumerationCompleted);
            Assert.True(a.ObservedEmptyCandidate); Assert.Equal(0, a.VisibleGroups); Assert.Equal(0, a.VisibleSlots);
            Assert.Equal(1, a.VerifiedRegionRoots); Assert.Equal("PUBLIC_AREA_EMPTY_CANDIDATE", a.Code);
        }
        var bottom = Assert.Single(result.PublicTableAreas!, x => x.Area == "meld-bottom");
        Assert.False(bottom.ObservedEmptyCandidate); Assert.Equal(1, bottom.VisibleGroups);
        f.Alter(f.Owner, n => { n.NodeFlags = 0; return n; });
        Assert.All(f.Probe().PublicTableAreas!.Where(x => x.Area.StartsWith("meld-", StringComparison.Ordinal)), a =>
        { Assert.False(a.ObservedEmptyCandidate); Assert.False(a.ContainerVisible); });
    }

    [Fact]
    public void Shared_meld_owner_without_direction_declaration_does_not_prove_no_meld()
    {
        var f = new Fixture("Emj/112/2/9/4");
        var area = Assert.Single(f.Probe().PublicTableAreas!, x => x.Area == "meld-right");
        Assert.False(area.ContainerVerified); Assert.False(area.EnumerationCompleted); Assert.False(area.ObservedEmptyCandidate);
        Assert.Equal("PUBLIC_AREA_REGION_UNAVAILABLE", area.Code);
    }


    [Fact]
    public void Collapsed_or_stale_empty_region_is_unknown_instead_of_a_recovery_anchor()
    {
        var f = new Fixture("Emj/118/4");
        f.Alter(f.Owner, n => { n.NodeFlags = 0; n.ScaleX = n.ScaleY = 0; return n; });
        var bottom = Assert.Single(f.Probe().PublicTableAreas!, x => x.Area == "river-bottom");
        Assert.Equal("PUBLIC_AREA_GEOMETRY_UNVERIFIED", bottom.Code);
        Assert.False(bottom.ContainerVerified); Assert.False(bottom.ObservedEmptyCandidate);
        f.AssertNoImageHeaders();
    }

    [Fact]
    public void A_visible_meld_tile_with_missing_face_is_an_unknown_slot_not_a_smaller_complete_group()
    {
        var f = new Fixture("Emj/112/5/9/4");
        f.Alter(f.Face, n => { n.NodeFlags = 0; return n; });
        var result = Assert.Single(f.Probe().PublicTableFaces!);
        Assert.Equal("PUBLIC_FACE_NOT_VISIBLE", result.Code); Assert.Null(result.IconId); f.AssertNoFaceHeader();
    }

    [Fact]
    public void Overlapping_live_slots_stop_before_either_face_or_shell_header_is_read()
    {
        var f = new Fixture("Emj/118/4"); f.DuplicateRiverSlot();
        var result = f.Probe(); Assert.Null(result.Error);
        Assert.Equal(2, result.PublicTableFaces!.Count);
        Assert.All(result.PublicTableFaces, x => Assert.Equal("PUBLIC_OCCLUDED_OR_TRANSITION", x.Code));
        f.AssertNoImageHeaders();
        Assert.DoesNotContain(f.Memory.Reads, x => (x.Address == 0x213000 || x.Address == 0x214000) && x.Count > sizeof(AtkResNode));
    }

    [Theory]
    [InlineData("EmjL", true, true)]
    [InlineData("Emj", false, true)]
    [InlineData("Emj", true, false)]
    public void EmjL_hidden_and_not_ready_never_enter_new_resource_path(string name, bool visible, bool ready)
    {
        var f = new Fixture("Emj/118/4"); f.Memory.Addon(f.Manager, visible: visible, ready: ready);
        var result = f.Memory.Reader().Probe(name, true, capturePublicFaces: true);
        Assert.Null(result.PublicTableFaces); f.AssertNoImageHeaders();
    }

    private sealed class Fixture
    {
        internal readonly SyntheticMemory Memory = new();
        private readonly Dictionary<nint, AtkResNode> nodes = [];
        private readonly Dictionary<nint, List<nint>> lists = [];
        private readonly PublicTableRoute route;
        private nint next = 0x100000;
        internal nint Face, Shell, Root, Owner, Wrapper, Display;
        internal const int FacePartIndex = 3;
        internal readonly nint FaceResource = 0xF10000, ShellAsset = 0xF20000, ShellHash;
        private const nint ShellList = 0xF30000, ShellParts = 0xF40000, ShellResource = 0xF50000;
        private const nint FaceList = 0xF60000, FaceParts = 0xF70000, FaceAsset = 0xF80000;
        internal ushort ShellPart;
        internal ImageNodeFlags ShellFlags;
        internal AtkUldManager Manager;

        internal Fixture(string path, float scale = 1)
        {
            Assert.True(PublicTableImageReader.TryRoute(path, out route!));
            ShellHash = ShellResource + (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash));
            nint outer = Add(1, 1, 0); Set(outer, n => { n.ScaleX = n.ScaleY = scale; return n; });
            nint fortySix = Add(46, 1, outer); Owner = Add(route.Owner, 1, fortySix);
            uint rootId = uint.Parse(route.RootPath.Split('/')[1]);
            Root = Add(rootId, route.RootType, Owner, route.RootRotation);
            Set(Root, n => { n.X = 500; n.Y = 300; return n; });
            nint tile = Root;
            if (route.TilePath != route.RootPath)
            {
                tile = Add(uint.Parse(route.TilePath.Split('/')[2]), route.TileType, Root, route.TileRotation);
                Set(tile, n => { n.ScaleX = route.TileScaleX; n.ScaleY = route.TileScaleY; return n; });
                lists[Root] = [tile];
            }
            nint display = tile;
            if (route.DisplayPath != route.TilePath) { display = Add(9, route.DisplayType, tile); lists[tile] = [display]; }
            Display = display;
            nint two = Add(2, 1, display); Wrapper = Add(3, 1, two);
            bool large = route.DisplayType is 1010 or 1013;
            Face = Add(4, 2, Wrapper); Shell = Add(5, 2, Wrapper, route.DisplayType == 1023 ? 180 : 0);
            Set(Face, n =>
            {
                n.Width = 40; n.Height = 52; n.ScaleX = route.DisplayType == 1013 ? -1 : large ? 1 : 0.78f;
                n.ScaleY = large ? 1 : 0.78f; n.OriginX = large ? 18 : 20; n.OriginY = large ? 23 : 26;
                n.X = large ? route.DisplayType == 1013 ? 4 : 0 : route.DisplayType is 1021 or 1022 ? -4 : -1;
                n.Y = large ? 0 : route.DisplayType is 1021 or 1023 ? -5 : -2; return n;
            });
            Set(Shell, n =>
            {
                n.Width = (ushort)(large ? 42 : 34); n.Height = (ushort)(large ? 55 : 45);
                n.ScaleX = route.DisplayType == 1024 ? -1 : 1; n.OriginX = large ? 21 : 17; n.OriginY = large ? 27 : 22;
                n.Y = route.DisplayType == 1023 ? 1 : 0; return n;
            });
            lists[display] = [two, Wrapper, Face, Shell];
            foreach (var pair in nodes.ToArray())
            {
                Matrix3x2 world = World(pair.Key); var n = pair.Value; var origin = Vector2.Transform(Vector2.Zero, world);
                n.ScreenX = origin.X; n.ScreenY = origin.Y;
                n.Transform = new() { M11 = world.M11, M12 = world.M12, M21 = world.M21, M22 = world.M22 };
                nodes[pair.Key] = n;
            }
            foreach (var pair in nodes) Memory.Store(pair.Key, pair.Value);
            foreach (var pair in lists)
            {
                nint component = pair.Key + 0x500000, pointers = pair.Key + 0x600000;
                Memory.StoreComponentNode(pair.Key, nodes[pair.Key], component);
                Memory.Store(component, new AtkComponentBase { UldManager = Memory.NodeListAt(pointers, pair.Value.ToArray()) });
            }
            ShellPart = large ? (ushort)0 : route.DisplayType == 1021 ? (ushort)18 : (ushort)20;
            StoreImages(); StoreShellPart();
            Memory.Store(ShellList, new AtkUldPartsList { Id = 18, PartCount = 23, Parts = (AtkUldPart*)ShellParts });
            Memory.Store(ShellAsset, new AtkUldAsset { Id = 21, AtkTexture = new() { TextureType = TextureType.Resource, Resource = (AtkTextureResource*)ShellResource } });
            Memory.Store(ShellHash, LowerHandImageReader.ClientTexturePathHash("ui/uld/EmjTile_hr1.tex"));
            Memory.Store(FaceList, new AtkUldPartsList { Id = 0, PartCount = 5, Parts = (AtkUldPart*)FaceParts });
            Memory.Store(FaceParts + FacePartIndex * sizeof(AtkUldPart), new AtkUldPart { UldAsset = (AtkUldAsset*)FaceAsset, Width = 40, Height = 52 });
            Memory.Store(FaceAsset, new AtkUldAsset { AtkTexture = new() { TextureType = TextureType.Resource, Resource = (AtkTextureResource*)FaceResource } });
            Memory.Store(FaceResource + (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash)),
                LowerHandImageReader.ClientTexturePathHash("ui/icon/076000/076035.tex"));
            Memory.Store(FaceResource + (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.IconId)), 76035u);
            Manager = Memory.NodeList(Root, Owner, fortySix, outer); Memory.Addon(Manager);
        }
        private nint Add(uint id, int type, nint parent, float rotation = 0)
        {
            nint address = next; next += 0x1000; var n = SyntheticMemory.Node(id, parent, (NodeType)type);
            n.ScaleX = n.ScaleY = 1; n.Rotation = rotation * MathF.PI / 180; nodes.Add(address, n); return address;
        }
        private void Set(nint address, Func<AtkResNode, AtkResNode> change) => nodes[address] = change(nodes[address]);
        internal void AddRightMeldCorner(float sideX)
        {
            Set(Display, n => { n.X = 26; n.Y = 2; return n; });
            nint tile = Add(4, 1024, Root, -90);
            Set(tile, n => { n.X = sideX; n.Y = 35; n.ScaleX = n.ScaleY = .75f; return n; });
            nint two = Add(2, 1, tile), three = Add(3, 1, two);
            nint face = Add(4, 2, three), shell = Add(5, 2, three);
            Set(face, n => { n.X = -1; n.Y = -2; n.Width = 40; n.Height = 52; n.OriginX = 20; n.OriginY = 26;
                n.ScaleX = n.ScaleY = .78f; return n; });
            Set(shell, n => { n.Width = 34; n.Height = 45; n.OriginX = 17; n.OriginY = 22; n.ScaleX = -1; return n; });
            lists[Root].Add(tile); lists[tile] = [two, three, face, shell];
            foreach (var pair in nodes.ToArray())
            {
                var world = World(pair.Key); var origin = Vector2.Transform(Vector2.Zero, world); var n = pair.Value;
                n.ScreenX = origin.X; n.ScreenY = origin.Y;
                n.Transform = new() { M11 = world.M11, M12 = world.M12, M21 = world.M21, M22 = world.M22 };
                nodes[pair.Key] = n; Memory.Store(pair.Key, n);
            }
            foreach (var pair in lists)
            {
                nint component = pair.Key + 0x500000, pointers = pair.Key + 0x600000;
                Memory.StoreComponentNode(pair.Key, nodes[pair.Key], component);
                Memory.Store(component, new AtkComponentBase { UldManager = Memory.NodeListAt(pointers, pair.Value.ToArray()) });
            }
            StoreImages();
            Memory.Store(face, new AtkImageNode { AtkResNode = nodes[face], PartId = FacePartIndex, PartsList = (AtkUldPartsList*)FaceList });
            Memory.Store(shell, new AtkImageNode { AtkResNode = nodes[shell], PartId = 20, PartsList = (AtkUldPartsList*)ShellList });
        }
        private Matrix3x2 World(nint address)
        {
            if (address == 0) return Matrix3x2.Identity;
            var n = nodes[address]; var origin = new Vector2(n.OriginX, n.OriginY);
            return Matrix3x2.CreateScale(n.ScaleX, n.ScaleY, origin) * Matrix3x2.CreateRotation(n.Rotation, origin) *
                Matrix3x2.CreateTranslation(n.X, n.Y) * World((nint)n.ParentNode);
        }
        internal void Alter(nint address, Func<AtkResNode, AtkResNode> change)
        {
            Set(address, change);
            if (address == Face || address == Shell) StoreImages();
            else if (lists.ContainsKey(address)) Memory.StoreComponentNode(address, nodes[address], address + 0x500000);
            else Memory.Store(address, nodes[address]);
        }
        internal void StoreImages()
        {
            Memory.Store(Face, new AtkImageNode { AtkResNode = nodes[Face], PartId = FacePartIndex, PartsList = (AtkUldPartsList*)FaceList });
            Memory.Store(Shell, new AtkImageNode { AtkResNode = nodes[Shell], PartId = ShellPart, PartsList = (AtkUldPartsList*)ShellList, Flags = ShellFlags });
        }
        internal void StoreShellPart(bool wrong = false)
        {
            bool large = route.DisplayType is 1010 or 1013;
            Memory.Store(ShellParts + ShellPart * sizeof(AtkUldPart), new AtkUldPart
            { U = (ushort)(large ? 0 : route.DisplayType == 1021 ? 97 : 132), V = (ushort)(wrong ? 1 : 0),
                Width = (ushort)(large ? 42 : 34), Height = (ushort)(large ? 55 : 45), UldAsset = (AtkUldAsset*)ShellAsset });
        }
        internal void SetBack()
        {
            bool large = route.DisplayType is 1010 or 1013;
            Set(Face, n => { n.NodeFlags = 0; return n; });
            Set(Shell, n =>
            {
                n.Rotation = route.DisplayType is 1022 or 1024 ? MathF.PI : 0;
                n.ScaleX = route.DisplayType is 1021 or 1024 ? -1 : 1;
                n.Y = route.DisplayType is 1022 or 1024 ? 1 : 0; return n;
            });
            Matrix3x2 world = World(Shell); var origin = Vector2.Transform(Vector2.Zero, world);
            Set(Shell, n => { n.ScreenX = origin.X; n.ScreenY = origin.Y;
                n.Transform = new() { M11 = world.M11, M12 = world.M12, M21 = world.M21, M22 = world.M22 }; return n; });
            ShellPart = large ? (ushort)6 : (ushort)21; StoreImages();
            Memory.Store(ShellParts + ShellPart * sizeof(AtkUldPart), new AtkUldPart
            { U = (ushort)(large ? 0 : 132), V = (ushort)(large ? 56 : 46), Width = (ushort)(large ? 42 : 34),
                Height = (ushort)(large ? 55 : 45), UldAsset = (AtkUldAsset*)ShellAsset });
        }
        internal void DuplicateRiverSlot()
        {
            Assert.Equal("river-bottom", route.Area);
            var root = nodes[Root]; root.NodeId = 1180001;
            Memory.StoreComponentNode(0x210000, root, 0xD00000);
            var two = nodes[Wrapper]; two.NodeId = 2; two.ParentNode = (AtkResNode*)0x210000;
            var three = nodes[Wrapper]; three.ParentNode = (AtkResNode*)0x211000;
            var face = nodes[Face]; face.ParentNode = (AtkResNode*)0x212000;
            var shell = nodes[Shell]; shell.ParentNode = (AtkResNode*)0x212000;
            Memory.Store(0x211000, two); Memory.Store(0x212000, three);
            Memory.Store(0x213000, new AtkImageNode { AtkResNode = face, PartId = FacePartIndex, PartsList = (AtkUldPartsList*)FaceList });
            Memory.Store(0x214000, new AtkImageNode { AtkResNode = shell, PartId = ShellPart, PartsList = (AtkUldPartsList*)ShellList });
            Memory.Store(0xD00000, new AtkComponentBase { UldManager = Memory.NodeListAt(0xD01000, 0x211000, 0x212000, 0x213000, 0x214000) });
            var owner = nodes[Owner]; nint fortySix = (nint)owner.ParentNode, outer = (nint)nodes[fortySix].ParentNode;
            Manager = Memory.NodeList(Root, 0x210000, Owner, fortySix, outer); Memory.Addon(Manager);
        }
        internal void AddHiddenMeldDirections()
        {
            var extra = new List<nint>();
            foreach (var (id, type) in new (uint, int)[] { (113, 1062), (114, 1063), (115, 1061) })
            {
                nint address = Add(id, type, Owner); Set(address, n => { n.NodeFlags = 0; return n; });
                Memory.Store(address, nodes[address]); extra.Add(address);
            }
            nint fortySix = (nint)nodes[Owner].ParentNode, outer = (nint)nodes[fortySix].ParentNode;
            Manager = Memory.NodeList(new[] { Root, Owner, fortySix, outer }.Concat(extra).ToArray()); Memory.Addon(Manager);
        }
        internal void HideMeldVisual() => Alter(route.Area == "meld-bottom" ? Display : Wrapper,
            n => { n.NodeFlags = 0; return n; });
        internal AddonProbe Probe() => Memory.Reader().Probe("Emj", true, capturePublicFaces: true);
        internal void AssertNoFaceHeader() => Assert.DoesNotContain(Memory.Reads, x => x.Address == Face && x.Count > sizeof(AtkResNode));
        internal void AssertNoImageHeaders() => Assert.DoesNotContain(Memory.Reads, x => (x.Address == Face || x.Address == Shell) && x.Count > sizeof(AtkResNode));
    }
}
