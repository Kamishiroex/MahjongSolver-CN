using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>Synthetic fixed-structure fixtures; no live pointers, native calls or semantic certification.</summary>
public sealed unsafe class PublicStatusReaderTests
{
    [Theory]
    [InlineData("Emj/38/12/2", "PlayerScore", "25000", 25000)]
    [InlineData("Emj/38/12/3", "PlayerScore", "25,000", 25000)]
    [InlineData("Emj/40/13/2", "PlayerScore", "30000", 30000)]
    [InlineData("Emj/42/13/3", "PlayerScore", "-1,000", -1000)]
    [InlineData("Emj/44/13/2", "PlayerScore", "0", 0)]
    [InlineData("Emj/38/9", "SeatWind", "东", 0)]
    [InlineData("Emj/40/10", "SeatWind", "南", 1)]
    [InlineData("Emj/42/10", "SeatWind", "西", 2)]
    [InlineData("Emj/44/10", "SeatWind", "北", 3)]
    [InlineData("Emj/22", "TopRedStickCount", "× 0", 0)]
    [InlineData("Emj/23", "TopBlackStickCount", "x3", 3)]
    [InlineData("Emj/105/2/2", "CenterCounterLeft", "6", 6)]
    [InlineData("Emj/105/3/2", "CenterCounterRight", "9", 9)]
    [InlineData("Emj/15", "RoundHeader", "东一局", 1)]
    public void Whitelisted_values_are_read_with_exact_ancestry_but_remain_candidates(string path, string field, string text, int number)
    {
        var fixture = new Fixture(path, text);
        var value = Assert.Single(fixture.Read());
        Assert.Equal("PUBLIC_STATUS_VALUE_CANDIDATE", value.Code);
        Assert.Equal(field, value.Field); Assert.Equal(number, value.Number);
        Assert.False(value.SemanticVerified); Assert.Equal("Candidate", value.MappingStatus);
        Assert.Equal(2, fixture.Reads.Count); // Utf8String metadata, then only its bounded current payload.
        Assert.True(fixture.Reads.All(x => x.Count <= PublicStatusReader.MaximumTextBytes + 1));
    }

    [Theory]
    [InlineData("Emj/38/5")]
    [InlineData("Emj/38/6")]
    [InlineData("Emj/40/6")]
    [InlineData("Emj/40/7")]
    [InlineData("Emj/42/6")]
    [InlineData("Emj/44/7")]
    [InlineData("Emj/2/3/2")]
    [InlineData("Emj/138/3")]
    [InlineData("Emj/038/12/2")]
    [InlineData("EmjL/38/12/2")]
    [InlineData("Emj/380001/12/2")]
    public void Name_chat_hidden_hand_and_noncanonical_paths_never_read_content(string path)
    {
        Assert.False(PublicStatusReader.TryRoute(path, out _));
        var reader = new PublicStatusReader((_, _, _) => throw new Exception("Unexpected memory read"),
            (_, _) => throw new Exception("Unexpected node read"));
        Assert.Empty(reader.Read([new(path, 2, 3, 0, 0, 0, 108, 20, 5)], new Dictionary<string, nint>()));
    }

    [Theory]
    [InlineData("score", "1,00")]
    [InlineData("score", "200001")]
    [InlineData("score", "NaN")]
    [InlineData("score", "Player25000")]
    [InlineData("score", "２５０００")]
    [InlineData("wind", "东风本人")]
    [InlineData("wind", "东家")]
    [InlineData("digit", "69")]
    [InlineData("count", "100")]
    [InlineData("count", "-1")]
    [InlineData("round", "东五局")]
    [InlineData("round", "Player 东一局")]
    [InlineData("result", "Player荣和")]
    [InlineData("dora", "Player")]
    public void Rejected_text_is_not_retained_or_turned_into_numbers(string grammar, string text)
    {
        Assert.False(PublicStatusReader.TryParse(grammar, Encoding.UTF8.GetBytes(text), out var number, out var value));
        Assert.Null(number); Assert.Null(value);
    }

    [Theory]
    [InlineData("Emj/27", "宝牌 （多玛式）")]
    [InlineData("Emj/27", "宝牌(传统式)")]
    [InlineData("Emj/55", "流局")]
    public void Finite_labels_are_candidates_not_settings_or_game_event_confirmation(string path, string text)
    {
        var candidate = Assert.Single(new Fixture(path, text).Read());
        Assert.Equal(text, candidate.Value); Assert.Null(candidate.Number);
        Assert.False(candidate.SemanticVerified);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("transparent")]
    [InlineData("rotation")]
    [InlineData("size")]
    [InlineData("owner")]
    [InlineData("wrong-addon-address")]
    [InlineData("wrong-component-address")]
    [InlineData("cycle")]
    public void Invalid_shape_or_parent_refuses_before_any_text_metadata_or_payload_read(string variation)
    {
        var f = new Fixture("Emj/38/12/2", "25000");
        var leaf = f.Nodes[f.Leaf];
        switch (variation)
        {
            case "hidden": leaf.NodeFlags &= ~NodeFlags.Visible; break;
            case "transparent": leaf.Color.A = 0; break;
            case "rotation": leaf.Rotation = 0.2f; break;
            case "size": leaf.Width = 208; break;
            case "owner": leaf.NodeId = 6; break;
            case "wrong-addon-address": f.Addresses["Emj/1"] += 8; break;
            case "wrong-component-address": f.Addresses["Emj/38/12"] += 8; break;
            case "cycle": leaf.ParentNode = (AtkResNode*)f.Leaf; break;
        }
        f.Nodes[f.Leaf] = leaf;
        Assert.NotEqual("PUBLIC_STATUS_VALUE_CANDIDATE", Assert.Single(f.Read()).Code);
        Assert.Empty(f.Reads);
    }

    [Fact]
    public void Omitted_runtime_template_roots_are_supported_but_no_other_ancestors_are_skipped()
    {
        var f = new Fixture("Emj/38/12/2", "25000", includeTemplateRoots: false);
        Assert.Equal(25000, Assert.Single(f.Read()).Number);
    }

    [Fact]
    public void Text_bounds_stop_before_payload_and_control_invalid_utf8_and_unterminated_text_reject()
    {
        var oversized = new Fixture("Emj/38/12/2", "25000");
        BinaryPrimitives.WriteInt64LittleEndian(oversized.Header.AsSpan(16), 66);
        Assert.Equal("STATUS_TEXT_BOUNDS_INVALID", Assert.Single(oversized.Read()).Code);
        Assert.Single(oversized.Reads);
        Assert.False(PublicStatusReader.TryParse("score", [0x32, 0x02, 0x13, 0x03], out _, out _));
        Assert.False(PublicStatusReader.TryParse("wind", [0xFF], out _, out _));
        var incomplete = new Fixture("Emj/38/12/2", "25000");
        incomplete.Payload[^1] = (byte)'x';
        Assert.Equal("STATUS_TEXT_TERMINATOR_INVALID", Assert.Single(incomplete.Read()).Code);
    }

    private sealed class Fixture
    {
        internal readonly Dictionary<nint, AtkResNode> Nodes = [];
        internal readonly Dictionary<string, nint> Addresses = [];
        internal readonly List<(nint Address, int Count)> Reads = [];
        internal readonly nint Leaf;
        internal readonly byte[] Header = new byte[24];
        internal readonly byte[] Payload;
        private readonly nint headerAddress;
        private readonly nint buffer = 0xF000;
        private readonly string path;
        private readonly PublicStatusRoute route;

        internal Fixture(string path, string text, bool includeTemplateRoots = true)
        {
            this.path = path;
            Assert.True(PublicStatusReader.TryRoute(path, out route));
            var expected = route.Chain.Where(x => includeTemplateRoots || !x.Optional).ToArray();
            for (int i = 0; i < expected.Length; i++)
            {
                nint address = 0x1000 + i * 0x200;
                var definition = expected[i];
                var value = new AtkResNode { NodeId = definition.Id, Type = (NodeType)definition.Type,
                    ScaleX = 1, ScaleY = 1, Width = (ushort)(i == 0 ? route.Width : 100),
                    Height = (ushort)(i == 0 ? route.Height : 100), NodeFlags = NodeFlags.Visible,
                    ParentNode = i + 1 < expected.Length ? (AtkResNode*)(address + 0x200) : null };
                value.Color.A = 255; Nodes[address] = value;
                if (i == expected.Length - 1) Addresses["Emj/1"] = address;
                if (definition.Type is 1025 or 1026 or 1016) Addresses["Emj/" + definition.Id] = address;
                if (definition.Type == 1047) Addresses[path[..path.LastIndexOf('/')]] = address;
                if (definition.Type == 1064) Addresses["Emj/105/" + definition.Id] = address;
            }
            Leaf = 0x1000; Addresses[path] = Leaf;
            int offset = route.Type == 5 ? (int)Marshal.OffsetOf<AtkCounterNode>(nameof(AtkCounterNode.NodeText))
                : (int)Marshal.OffsetOf<AtkTextNode>(nameof(AtkTextNode.NodeText));
            headerAddress = Leaf + offset;
            Payload = Encoding.UTF8.GetBytes(text + "\0");
            BinaryPrimitives.WriteInt64LittleEndian(Header, buffer);
            BinaryPrimitives.WriteInt64LittleEndian(Header.AsSpan(8), 64);
            BinaryPrimitives.WriteInt64LittleEndian(Header.AsSpan(16), Payload.Length);
        }

        internal IReadOnlyList<PublicStatusCandidate> Read() => new PublicStatusReader(ReadBytes, (address, _) => Nodes[address])
            .Read([new(path, Nodes[Leaf].NodeId, route.Type, 0, 0, 0, route.Width, route.Height, null)], Addresses);

        private byte[] ReadBytes(nint address, int count, string _)
        {
            Reads.Add((address, count));
            if (address == headerAddress && count == Header.Length) return Header.ToArray();
            if (address == buffer && count == Payload.Length) return Payload.ToArray();
            throw new InvalidOperationException("Unexpected read outside synthetic allowed text buffers");
        }
    }
}
