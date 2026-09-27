using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed unsafe class PublicDoraReaderTests
{
    [Theory]
    [InlineData(28u, 0)]
    [InlineData(29u, 1)]
    [InlineData(30u, 2)]
    [InlineData(31u, 3)]
    [InlineData(32u, 4)]
    public void Only_five_public_slots_read_current_selected_full_tile_resource(uint root, int slot)
    {
        var f = new Fixture(root); var dora = Assert.Single(f.Read());
        Assert.Equal("PUBLIC_DORA_FACE_CANDIDATE", dora.Code); Assert.Equal(slot, dora.Slot);
        Assert.Equal(27, dora.Kind34); Assert.False(dora.RedFive);
        Assert.Equal((ushort)40, dora.SelectedPart!.Width);
        Assert.Equal(6, f.Reads.Count);
        Assert.DoesNotContain(f.Reads, r => r.Address == Fixture.Resource && r.Count > 4);
        Assert.DoesNotContain(f.Reads, r => r.Address == Fixture.Parts);
    }

    [Theory]
    [InlineData("leaf-size")]
    [InlineData("hidden")]
    [InlineData("wrong-owner")]
    [InlineData("opponent-hand")]
    [InlineData("wrong-component-address")]
    [InlineData("infinite-scale")]
    [InlineData("zero-alpha")]
    [InlineData("unsupported-owner-scale")]
    public void Invalid_visibility_and_public_ownership_reject_before_resource_reads(string variation)
    {
        var f = new Fixture(28); var face = f.Nodes[Fixture.Face];
        switch (variation)
        {
            case "leaf-size": face.Width = 39; break;
            case "hidden": face.NodeFlags &= ~NodeFlags.Visible; break;
            case "wrong-owner": face.ParentNode = (AtkResNode*)Fixture.AddonRoot; break;
            case "opponent-hand": var owner = f.Nodes[Fixture.Component]; owner.Type = (NodeType)1058; f.Nodes[Fixture.Component] = owner; break;
            case "wrong-component-address": f.Addresses["Emj/28"] += 8; break;
            case "infinite-scale": face.ScaleX = float.PositiveInfinity; break;
            case "zero-alpha": face.Color.A = 0; break;
            case "unsupported-owner-scale": var component = f.Nodes[Fixture.Component]; component.ScaleX = component.ScaleY = 1; f.Nodes[Fixture.Component] = component; break;
        }
        f.Nodes[Fixture.Face] = face;
        Assert.Equal("DORA_OWNER_UNVERIFIED", Assert.Single(f.Read()).Code); Assert.Empty(f.Reads);
    }

    [Theory]
    [InlineData("flipped")]
    [InlineData("cropped")]
    [InlineData("empty-rectangle")]
    [InlineData("unsupported-size")]
    [InlineData("kernel")]
    [InlineData("wrong-icon")]
    [InlineData("wrong-hash")]
    public void Changed_render_or_resource_identity_cannot_become_a_tile(string variation)
    {
        var f = new Fixture(28);
        switch (variation)
        {
            case "flipped": f.Store(Fixture.Face, new AtkImageNode { PartsList = (AtkUldPartsList*)Fixture.List, PartId = 2, Flags = ImageNodeFlags.FlipH }); break;
            case "cropped": f.StorePart(1, 0, 40, 52); break;
            case "empty-rectangle": f.StorePart(0, 0, 0, 0); break;
            case "unsupported-size": f.StorePart(0, 0, 80, 104); break;
            case "kernel": f.Store(Fixture.Asset, new AtkUldAsset { AtkTexture = new AtkTexture { TextureType = TextureType.KernelTexture } }); break;
            case "wrong-icon": f.Store(Fixture.Resource + 4, 76029u); break;
            case "wrong-hash": f.Store(Fixture.Resource, 0u); break;
        }
        var value = Assert.Single(f.Read()); Assert.NotEqual("PUBLIC_DORA_FACE_CANDIDATE", value.Code); Assert.Null(value.Kind34);
        if (variation is "flipped" or "cropped" or "empty-rectangle" or "unsupported-size" or "kernel")
            Assert.DoesNotContain(f.Reads, r => r.Address == Fixture.Resource || r.Address == Fixture.Resource + 4);
    }

    [Fact]
    public void Unknown_hidden_or_ura_regions_are_not_read_and_missing_dora_is_not_empty_known_array()
    {
        var reader = new PublicDoraReader((_, _, _) => throw new Exception("Unexpected read"), (_, _) => throw new Exception("Unexpected node"));
        Assert.Empty(reader.Read(new Dictionary<string, nint> { ["Emj/79/2"] = 1, ["Emj/138/3"] = 2 }));
    }

    private sealed class Fixture
    {
        internal const nint Face = 0x1000, Component = 0x2000, Group = 0x3000, Top = 0x4000, AddonRoot = 0x5000,
            List = 0x6000, Parts = 0x7000, Asset = 0x8000, Resource = 0x9000;
        internal readonly Dictionary<nint, AtkResNode> Nodes = [];
        internal readonly Dictionary<string, nint> Addresses = [];
        internal readonly List<(nint Address, int Count)> Reads = [];
        private readonly Dictionary<nint, byte[]> memory = [];
        internal Fixture(uint root)
        {
            Add("Emj/" + root + "/2", Face, 2, 2, Component, 40, 52);
            Add("Emj/" + root, Component, root, 1006, Group, 50, 60);
            var component = Nodes[Component]; component.ScaleX = component.ScaleY = 0.75f; Nodes[Component] = component;
            Add("Emj/26", Group, 26, 1, Top, 320, 82);
            Add("Emj/21", Top, 21, 1, AddonRoot, 320, 82);
            Add("Emj/1", AddonRoot, 1, 1, 0, 1260, 700);
            Store(Face, new AtkImageNode { PartsList = (AtkUldPartsList*)List, PartId = 2 });
            Store(List, new AtkUldPartsList { Id = 0, PartCount = 3, Parts = (AtkUldPart*)Parts }); StorePart(0, 0, 40, 52);
            Store(Asset, new AtkUldAsset { AtkTexture = new AtkTexture { TextureType = TextureType.Resource, Resource = (AtkTextureResource*)Resource } });
            Store(Resource, LowerHandImageReader.ClientTexturePathHash("ui/icon/076000/076028_hr1.tex")); Store(Resource + 4, 76028u);
        }
        internal void StorePart(ushort u, ushort v, ushort width, ushort height) => Store(Parts + 2 * sizeof(AtkUldPart),
            new AtkUldPart { U = u, V = v, Width = width, Height = height, UldAsset = (AtkUldAsset*)Asset });
        internal void Store<T>(nint address, T value) where T : unmanaged
        { var data = new byte[sizeof(T)]; MemoryMarshal.Write(data, in value); memory[address] = data; }
        internal IReadOnlyList<PublicDoraCandidate> Read() => new PublicDoraReader(ReadBytes, (p, _) => Nodes[p]).Read(Addresses);
        private byte[] ReadBytes(nint address, int length, string _)
        { Reads.Add((address, length)); var data = memory[address]; Assert.Equal(length, data.Length); return data; }
        private void Add(string path, nint address, uint id, int type, nint parent, ushort width, ushort height)
        {
            var node = new AtkResNode { NodeId = id, Type = (NodeType)type, ParentNode = (AtkResNode*)parent, Width = width, Height = height,
                ScaleX = 1, ScaleY = 1, NodeFlags = NodeFlags.Visible }; node.Color.A = 255;
            Nodes[address] = node; Addresses[path] = address;
        }
    }
}
