using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed unsafe class PublicRiichiReaderTests
{
    [Fact]
    public void Explicitly_hidden_fixed_image_header_is_negative_without_texture_access()
    {
        var f = new Fixture(100); var image = f.Nodes[Fixture.Image]; image.NodeFlags &= ~NodeFlags.Visible;
        f.Nodes[Fixture.Image] = image;
        Assert.Single(f.ReadHidden(), x => x.StickVisible == false); Assert.Equal(0, f.Reads);
    }
    [Theory]
    [InlineData(100u)]
    [InlineData(101u)]
    [InlineData(102u)]
    [InlineData(103u)]
    public void Explicitly_hidden_fixed_owner_proves_negative_without_reading_resources(uint root)
    {
        var f = new Fixture(root);
        var owner = f.Nodes[Fixture.Owner]; owner.NodeFlags &= ~NodeFlags.Visible; f.Nodes[Fixture.Owner] = owner;
        var value = Assert.Single(f.ReadHidden(), x => x.StickVisible == false);
        Assert.Equal("PUBLIC_RIICHI_STICK_HIDDEN_OWNER", value.Code);
        Assert.Equal(0, f.Reads);
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("hidden-table")]
    [InlineData("wrong-owner")]
    [InlineData("wrong-type")]
    public void Missing_image_or_fading_or_invalid_table_is_not_negative(string variation)
    {
        var f = new Fixture(100); var owner = f.Nodes[Fixture.Owner];
        owner.NodeFlags &= ~NodeFlags.Visible;
        switch (variation)
        {
            case "alpha": owner.NodeFlags |= NodeFlags.Visible; owner.Color.A = 0; break;
            case "hidden-table": var table = f.Nodes[Fixture.Table]; table.NodeFlags &= ~NodeFlags.Visible; f.Nodes[Fixture.Table] = table; break;
            case "wrong-owner": owner.ParentNode = (AtkResNode*)Fixture.Root; break;
            case "wrong-type": owner.Type = NodeType.Res; break;
        }
        f.Nodes[Fixture.Owner] = owner;
        Assert.All(f.ReadHidden(), x => Assert.Null(x.StickVisible)); Assert.Equal(0, f.Reads);
    }
    [Theory]
    [InlineData(100u, "bottom")]
    [InlineData(101u, "right")]
    [InlineData(102u, "top")]
    [InlineData(103u, "left")]
    public void Only_current_selected_public_stick_is_recognized(uint root, string direction)
    {
        var f = new Fixture(root); var values = f.Read();
        var value = Assert.Single(values, x => x.StickVisible == true);
        Assert.Equal(direction, value.ScreenDirection); Assert.Equal("PUBLIC_RIICHI_STICK_CANDIDATE", value.Code);
        Assert.False(value.DeclarationTimingVerified); Assert.Equal("Candidate", value.MappingStatus);
        Assert.All(values.Where(x => x != value), x => Assert.Null(x.StickVisible));
        Assert.Equal(5, f.Reads);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("alpha")]
    [InlineData("wrong-side-pose")]
    [InlineData("wrong-owner")]
    [InlineData("cycle")]
    public void Broken_visibility_or_owner_prevents_all_resource_reads(string variation)
    {
        var f = new Fixture(101); var owner = f.Nodes[Fixture.Owner];
        switch (variation)
        {
            case "hidden": owner.NodeFlags &= ~NodeFlags.Visible; break;
            case "alpha": owner.Color.A = 0; break;
            case "wrong-side-pose": owner.Rotation = 0; break;
            case "wrong-owner": owner.ParentNode = (AtkResNode*)Fixture.Root; break;
            case "cycle": owner.ParentNode = (AtkResNode*)Fixture.Owner; break;
        }
        f.Nodes[Fixture.Owner] = owner;
        Assert.All(f.Read(), x => Assert.Null(x.StickVisible)); Assert.Equal(0, f.Reads);
    }

    [Theory]
    [InlineData("wrong-part")]
    [InlineData("flip")]
    [InlineData("wrong-count")]
    [InlineData("wrong-rect")]
    [InlineData("wrong-asset")]
    [InlineData("kernel")]
    [InlineData("wrong-hash")]
    public void Other_stick_or_texture_cannot_be_claimed_as_riichi(string variation)
    {
        var f = new Fixture(100);
        switch (variation)
        {
            case "wrong-part": f.Store(Fixture.Image, new AtkImageNode { PartId = 5, PartsList = (AtkUldPartsList*)Fixture.List }); break;
            case "flip": f.Store(Fixture.Image, new AtkImageNode { PartId = 4, PartsList = (AtkUldPartsList*)Fixture.List, Flags = ImageNodeFlags.FlipV }); break;
            case "wrong-count": f.Store(Fixture.List, new AtkUldPartsList { Id = 14, PartCount = 31, Parts = (AtkUldPart*)Fixture.Parts }); break;
            case "wrong-rect": f.Store(Fixture.Parts + 4 * sizeof(AtkUldPart), new AtkUldPart { U = 0, V = 48, Width = 78, Height = 12, UldAsset = (AtkUldAsset*)Fixture.Asset }); break;
            case "wrong-asset": f.Store(Fixture.Asset, new AtkUldAsset { Id = 21, AtkTexture = new AtkTexture { TextureType = TextureType.Resource, Resource = (AtkTextureResource*)Fixture.Resource } }); break;
            case "kernel": f.Store(Fixture.Asset, new AtkUldAsset { Id = 17, AtkTexture = new AtkTexture { TextureType = TextureType.KernelTexture } }); break;
            case "wrong-hash": f.Store(Fixture.Resource, 0u); break;
        }
        Assert.All(f.Read(), x => Assert.Null(x.StickVisible));
    }

    [Fact]
    public void Missing_visible_nodes_leave_every_player_unknown_without_reading_hidden_resources()
    {
        var r = new PublicRiichiReader((_, _, _) => throw new Exception("Unexpected resource"), (_, _) => throw new Exception("Unexpected node"));
        var values = r.Read(new Dictionary<string, nint> { ["Emj/100"] = 1, ["Emj/117/2"] = 2 });
        Assert.Equal(4, values.Count); Assert.All(values, x => Assert.Null(x.StickVisible));
    }

    private sealed class Fixture
    {
        internal const nint Image = 0x1000, Owner = 0x2000, Group = 0x3000, Table = 0x4000,
            Root = 0x5000, List = 0x6000, Parts = 0x7000, Asset = 0x8000, Resource = 0x9000;
        internal readonly Dictionary<nint, AtkResNode> Nodes = [];
        private readonly Dictionary<string, nint> addresses = [];
        private readonly Dictionary<nint, byte[]> memory = [];
        internal int Reads;
        internal Fixture(uint root)
        {
            Add("Emj/" + root + "/2", Image, 2, 2, Owner, 78, 12);
            Add("Emj/" + root, Owner, root, 1037, Group, 78, 12);
            if (root is 101 or 103) { var n = Nodes[Owner]; n.ScaleY = -1; n.Rotation = MathF.PI / 2; Nodes[Owner] = n; }
            Add("Emj/99", Group, 99, 1, Table, 160, 156);
            Add("Emj/46", Table, 46, 1, Root, 720, 700);
            Add("Emj/1", Root, 1, 1, 0, 1260, 700);
            Store(Image, new AtkImageNode { PartId = 4, PartsList = (AtkUldPartsList*)List });
            Store(List, new AtkUldPartsList { Id = 14, PartCount = 32, Parts = (AtkUldPart*)Parts });
            Store(Parts + 4 * sizeof(AtkUldPart), new AtkUldPart { U = 0, V = 36, Width = 78, Height = 12, UldAsset = (AtkUldAsset*)Asset });
            Store(Asset, new AtkUldAsset { Id = 17, AtkTexture = new AtkTexture { TextureType = TextureType.Resource, Resource = (AtkTextureResource*)Resource } });
            Store(Resource, LowerHandImageReader.ClientTexturePathHash("ui/uld/EmjParts_hr1.tex"));
        }
        internal void Store<T>(nint address, T value) where T : unmanaged
        { var b = new byte[sizeof(T)]; MemoryMarshal.Write(b, in value); memory[address] = b; }
        internal IReadOnlyList<PublicRiichiCandidate> Read() => new PublicRiichiReader((p, n, _) =>
        { Reads++; Assert.Equal(n, memory[p].Length); return memory[p]; }, (p, _) => Nodes[p]).Read(addresses);
        internal IReadOnlyList<PublicRiichiCandidate> ReadHidden() => new PublicRiichiReader((_, _, _) =>
            throw new Exception("Hidden owner must not read image resources"), (p, _) => Nodes[p])
            .Read(new Dictionary<string, nint>(), addresses);
        private void Add(string path, nint address, uint id, int type, nint parent, ushort width, ushort height)
        {
            var n = new AtkResNode { NodeId = id, Type = (NodeType)type, ParentNode = (AtkResNode*)parent, Width = width, Height = height,
                ScaleX = 1, ScaleY = 1, NodeFlags = NodeFlags.Visible }; n.Color.A = 255; Nodes[address] = n; addresses[path] = address;
        }
    }
}
