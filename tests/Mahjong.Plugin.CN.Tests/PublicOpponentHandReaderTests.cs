using System.Numerics;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed unsafe class PublicOpponentHandReaderTests
{
    [Theory]
    [InlineData("left", 144, 0, -27)]
    [InlineData("left", 145, 0, -32)]
    [InlineData("right", 138, 0, 27)]
    [InlineData("right", 139, 0, 32)]
    [InlineData("top", 141, 26, 0)]
    [InlineData("top", 142, 32, 0)]
    public void Fixed_uld_compacted_row_endpoints_remain_visible_backs(string side, uint id, float x, float y)
    {
        var f = new Fixture(side, 1.4f); f.Add(id);
        f.Change(f.Wrapper, n => { n.X = x; n.Y = y; return n; });
        Assert.True(f.Read().CountComplete); f.AssertNoFaceResourceRead();
    }

    [Fact]
    public void Preallocated_component_with_hidden_common_wrapper_is_not_an_extra_back_or_unknown_slot()
    {
        var f = new Fixture("top", 1.4f); f.Add(141); f.Add(1410001);
        f.Change(f.Wrapper, n => { n.Color.A = 0; return n; });
        var reading = f.Read(); Assert.True(reading.CountComplete); Assert.Equal(1, reading.VisibleSlots);
        Assert.Equal(1, reading.VerifiedBackCount); Assert.False(reading.SeparateDrawSlotVisible);
        f.AssertNoFaceResourceRead();
    }

    [Theory]
    [InlineData(138, 139, "right")]
    [InlineData(141, 142, "top")]
    [InlineData(144, 145, "left")]
    public void All_three_public_back_rows_count_current_slots_and_separate_draw_without_face_resources(int ordinary, int draw, string side)
    {
        var f = new Fixture(side, 1.4f); f.Add((uint)ordinary);
        for (int i = 1; i < 13; i++) f.Add((uint)(ordinary * 10000 + i));
        var initial = f.Read();
        Assert.True(initial.CountComplete); Assert.Equal(13, initial.VerifiedBackCount);
        Assert.False(initial.SeparateDrawSlotVisible); Assert.Equal("Candidate", initial.Quality);
        f.Add((uint)draw);
        var next = f.Read(); Assert.True(next.CountComplete); Assert.Equal(14, next.VerifiedBackCount);
        Assert.True(next.SeparateDrawSlotVisible);
        Assert.All(next.Slots, x => Assert.Equal(PublicOpponentHandReader.VerifiedCode, x.Code));
        f.AssertNoFaceResourceRead();
    }

    [Theory]
    [InlineData("Emj/1380001/3")]
    [InlineData("Emj/1390001/4")]
    [InlineData("Emj/1380257/4")]
    [InlineData("Emj/134/4")]
    [InlineData("EmjL/138/4")]
    [InlineData("Emj/0138/4")]
    public void Opponent_route_never_admits_dynamic_faces_or_unknown_families(string path) =>
        Assert.False(PublicOpponentHandReader.TryRoute(path, out _));

    [Theory]
    [InlineData("part")]
    [InlineData("hash")]
    [InlineData("animation")]
    [InlineData("owner")]
    [InlineData("hidden")]
    public void Unknown_shells_and_transitions_make_count_and_draw_state_incomplete(string variation)
    {
        var f = new Fixture("right", 1); f.Add(138);
        if (variation == "part") f.PartOverride = 15;
        if (variation == "hash") f.HashOverride = 1;
        if (variation == "animation") f.Change(f.Wrapper, n => { n.X = 6; return n; });
        if (variation == "owner") f.Change(f.Root, n => { n.ParentNode = (AtkResNode*)0; return n; });
        if (variation == "hidden") f.HiddenOwner = true;
        var status = f.Read(); Assert.False(status.CountComplete); Assert.Null(status.SeparateDrawSlotVisible);
        f.AssertNoFaceResourceRead();
    }

    [Fact]
    public void Duplicate_visual_positions_and_fifteen_slot_animation_do_not_claim_a_complete_hand_count()
    {
        var overlap = new Fixture("top", 1); overlap.Add(141); overlap.Add(1410001);
        overlap.Change(overlap.Root, n => { n.X = 0; return n; });
        Assert.All(overlap.Read().Slots, x => Assert.Equal("OPPONENT_BACK_OVERLAP", x.Code));
        var excess = new Fixture("top", 1);
        for (int i = 0; i < 15; i++) excess.Add(i == 0 ? 141u : (uint)(1410000 + i));
        Assert.False(excess.Read().CountComplete); Assert.Null(excess.Read().SeparateDrawSlotVisible);
        excess.AssertNoFaceResourceRead();
    }

    private sealed class Fixture
    {
        private readonly SyntheticMemory memory = new();
        private readonly Dictionary<nint, AtkResNode> ns = [];
        private readonly Dictionary<string, nint> addresses = new(StringComparer.Ordinal);
        private readonly Dictionary<string, nint> containers = new(StringComparer.Ordinal);
        private readonly List<(string Path, nint Root, nint Shell, nint Face, PublicOpponentBackRoute Route)> slots = [];
        private readonly string side;
        private readonly nint outer = 0x100000, fortySix = 0x101000, owner = 0x102000;
        internal nint Root, Wrapper;
        internal ushort? PartOverride;
        internal uint? HashOverride;
        internal bool HiddenOwner;
        internal Fixture(string side, float scale)
        {
            this.side = side;
            uint ownerId = side switch { "right" => 137u, "top" => 140u, _ => 143u };
            ns[outer] = N(1, 1, 0); ns[fortySix] = N(46, 1, outer); ns[owner] = N(ownerId, 1, fortySix);
            Change(outer, n => { n.ScaleX = n.ScaleY = scale; n.X = 100; n.Y = 200; return n; });
            foreach (var pair in ns) { addresses["Emj/" + pair.Value.NodeId] = pair.Key; containers["Emj/" + pair.Value.NodeId] = pair.Key; }
        }
        internal void Add(uint id)
        {
            string path = "Emj/" + id; Assert.True(PublicOpponentHandReader.TryRoute(path + "/4", out var route));
            Root = 0x200000 + slots.Count * 0x10000; Wrapper = Root + 0x1000;
            nint shell = Root + 0x2000, face = Root + 0x3000;
            ns[Root] = N(id, route.Template, owner);
            Change(Root, n => { if (side == "top") n.X = slots.Count * 40; else n.Y = slots.Count * 40; return n; });
            ns[Wrapper] = N(2, 1, Root); ns[shell] = N(4, 2, Wrapper); ns[face] = N(3, 2, Wrapper);
            Change(shell, n => { n.Width = route.Width; n.Height = route.Height; return n; });
            addresses[path] = Root; addresses[path + "/2"] = Wrapper; addresses[path + "/4"] = shell; addresses[path + "/3"] = face;
            containers[path + "/2"] = Wrapper;
            slots.Add((path, Root, shell, face, route));
        }
        internal void Change(nint address, Func<AtkResNode, AtkResNode> fn) => ns[address] = fn(ns[address]);
        private static AtkResNode N(uint id, int type, nint parent)
        {
            var n = SyntheticMemory.Node(id, parent, (NodeType)type); n.ScaleX = n.ScaleY = 1; return n;
        }
        private Matrix3x2 World(nint address)
        {
            if (address == 0) return Matrix3x2.Identity;
            var n = ns[address]; return Matrix3x2.CreateScale(n.ScaleX, n.ScaleY) * Matrix3x2.CreateTranslation(n.X, n.Y) * World((nint)n.ParentNode);
        }
        internal PublicOpponentHandStatus Read()
        {
            foreach (var (addr, value) in ns.ToArray())
            {
                var n = value; var world = World(addr); var origin = Vector2.Transform(Vector2.Zero, world);
                n.ScreenX = origin.X; n.ScreenY = origin.Y;
                n.Transform = new() { M11 = world.M11, M12 = world.M12, M21 = world.M21, M22 = world.M22 };
                ns[addr] = n; memory.Store(addr, n);
            }
            foreach (var s in slots)
            {
                nint list = s.Root + 0x4000, parts = s.Root + 0x5000, asset = s.Root + 0x6000, texture = s.Root + 0x7000;
                memory.Store(s.Shell, new AtkImageNode { AtkResNode = ns[s.Shell], PartId = PartOverride ?? s.Route.Part, PartsList = (AtkUldPartsList*)list });
                memory.Store(list, new AtkUldPartsList { Id = 18, PartCount = 23, Parts = (AtkUldPart*)parts });
                memory.Store(parts + s.Route.Part * sizeof(AtkUldPart), new AtkUldPart { U = s.Route.U, V = s.Route.V,
                    Width = s.Route.Width, Height = s.Route.Height, UldAsset = (AtkUldAsset*)asset });
                memory.Store(asset, new AtkUldAsset { Id = 21, AtkTexture = new() { TextureType = TextureType.Resource, Resource = (AtkTextureResource*)texture } });
                memory.Store(texture + (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash)),
                    HashOverride ?? LowerHandImageReader.ClientTexturePathHash("ui/uld/EmjTile_hr1.tex"));
            }
            var visibleAddresses = new Dictionary<string, nint>(addresses);
            foreach (var s in slots.Where(s => ns[s.Root + 0x1000].Color.A == 0))
                foreach (string key in visibleAddresses.Keys.Where(k => k.StartsWith(s.Path + "/", StringComparison.Ordinal)).ToArray())
                    visibleAddresses.Remove(key);
            if (HiddenOwner) visibleAddresses.Remove("Emj/" + ns[owner].NodeId);
            var visible = visibleAddresses.Select(x => new UiNode(x.Key, ns[x.Value].NodeId, (int)ns[x.Value].Type,
                ns[x.Value].ScreenX, ns[x.Value].ScreenY, 0, ns[x.Value].Width, ns[x.Value].Height, null)).ToArray();
            var reader = new PublicOpponentHandReader((a, n, _) => memory.Read(a, n) ?? throw new InvalidOperationException("unmapped"),
                (a, _) => MemoryMarshal.Read<AtkResNode>(memory.Read(a, sizeof(AtkResNode))!));
            return Assert.Single(reader.Read(visible, visibleAddresses, containers), x => x.ScreenDirection == side);
        }
        internal void AssertNoFaceResourceRead()
        {
            foreach (var slot in slots) Assert.DoesNotContain(memory.Reads, x => x.Address == slot.Face);
            // No IconId is requested even from the public shell texture resource.
            Assert.DoesNotContain(memory.Reads, x => slots.Any(s => x.Address == s.Root + 0x7000 +
                (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.IconId))));
        }
    }
}
