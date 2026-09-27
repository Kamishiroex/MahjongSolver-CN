using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed unsafe class RoundTitleResourceReaderTests
{
    [Fact]
    public void Reads_only_selected_header_part_and_two_resource_scalars()
    {
        var f = new Fixture();
        var value = f.Read();
        Assert.Equal("ROUND_TITLE_RESOURCE_CANDIDATE", value.Code);
        Assert.Equal(9876u, value.IconId); Assert.Equal(0x12345678u, value.TexturePathHash);
        Assert.False(value.SemanticVerified); Assert.Equal("Candidate", value.MappingStatus);
        Assert.Equal(6, f.Reads.Count);
        Assert.DoesNotContain(f.Reads, x => x.Address == Fixture.Resource && x.Count > 4);
        Assert.DoesNotContain(f.Reads, x => x.Address == Fixture.Parts); // Only selected part 3 is present.
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("wrong-parent")]
    [InlineData("background20")]
    [InlineData("wrong-size")]
    [InlineData("wrong-scale")]
    public void Header_identity_rejection_happens_before_resource_reads(string variation)
    {
        var f = new Fixture(); var image = f.Nodes[Fixture.Image];
        switch (variation)
        {
            case "hidden": image.NodeFlags &= ~NodeFlags.Visible; break;
            case "wrong-parent": image.ParentNode = (AtkResNode*)Fixture.Root; break;
            case "background20": image.NodeId = 20; break;
            case "wrong-size": image.Width = 300; break;
            case "wrong-scale": image.ScaleX = 0.5f; image.ScaleY = 0.5f; break;
        }
        f.Nodes[Fixture.Image] = image;
        Assert.NotEqual("ROUND_TITLE_RESOURCE_CANDIDATE", f.Read().Code);
        Assert.Empty(f.Reads);
    }

    [Fact]
    public void Kernel_texture_does_not_dereference_unknown_resource_pointer()
    {
        var f = new Fixture();
        f.Store(Fixture.Asset, new AtkUldAsset { Id = 9, AtkTexture = new AtkTexture { TextureType = TextureType.KernelTexture, KernelTexture = (FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture*)Fixture.Resource } });
        Assert.Equal("ROUND_TITLE_NOT_RESOURCE_TEXTURE", f.Read().Code);
        Assert.DoesNotContain(f.Reads, x => x.Address == Fixture.Resource || x.Address == Fixture.Resource + 4);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(65u)]
    public void Invalid_parts_count_never_reads_array_or_asset(uint count)
    {
        var f = new Fixture(); f.Store(Fixture.List, new AtkUldPartsList { Id = 0, PartCount = count, Parts = (AtkUldPart*)Fixture.Parts });
        Assert.Equal("ROUND_TITLE_PART_INVALID", f.Read().Code);
        Assert.Equal(2, f.Reads.Count);
    }

    private sealed class Fixture
    {
        internal const nint Image = 0x1000, Owner = 0x2000, Root = 0x3000, List = 0x4000, Parts = 0x5000, Asset = 0x6000, Resource = 0x7000;
        internal readonly Dictionary<nint, AtkResNode> Nodes = [];
        private readonly Dictionary<nint, byte[]> memory = [];
        internal readonly List<(nint Address, int Count)> Reads = [];
        internal Fixture()
        {
            Nodes[Image] = Node(19, NodeType.Image, Owner, 1);
            Nodes[Owner] = Node(16, NodeType.Res, Root, 0.5f);
            Nodes[Root] = Node(1, NodeType.Res, 0, 1.4f);
            Store(Image, new AtkImageNode { PartsList = (AtkUldPartsList*)List, PartId = 3 });
            Store(List, new AtkUldPartsList { Id = 0, PartCount = 4, Parts = (AtkUldPart*)Parts });
            Store(Parts + 3 * sizeof(AtkUldPart), new AtkUldPart { U = 0, V = 0, Width = 640, Height = 80, UldAsset = (AtkUldAsset*)Asset });
            Store(Asset, new AtkUldAsset { Id = 9, AtkTexture = new AtkTexture { TextureType = TextureType.Resource, Resource = (AtkTextureResource*)Resource } });
            Store(Resource, 0x12345678u); Store(Resource + 4, 9876u);
        }
        internal RoundTitleResourceCandidate Read() => new RoundTitleResourceReader(ReadBytes, (address, _) => Nodes[address])
            .Read([new("Emj/19", 19, 2, 0, 0, 0, 640, 80, null)], new Dictionary<string, nint> { ["Emj/19"] = Image, ["Emj/16"] = Owner, ["Emj/1"] = Root });
        internal void Store<T>(nint address, T value) where T : unmanaged
        { var bytes = new byte[sizeof(T)]; MemoryMarshal.Write(bytes, in value); memory[address] = bytes; }
        private byte[] ReadBytes(nint address, int count, string _)
        { Reads.Add((address, count)); var bytes = memory[address]; Assert.Equal(count, bytes.Length); return bytes; }
        private static AtkResNode Node(uint id, NodeType type, nint parent, float scale)
        {
            var node = new AtkResNode { NodeId = id, Type = type, ParentNode = (AtkResNode*)parent, Width = 640, Height = 80,
                ScaleX = scale, ScaleY = scale, NodeFlags = NodeFlags.Visible };
            node.Color.A = 255; return node;
        }
    }
}
