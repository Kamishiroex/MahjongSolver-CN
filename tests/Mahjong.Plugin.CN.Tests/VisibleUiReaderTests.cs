using System.Reflection;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed unsafe class VisibleUiReaderTests
{
    [Fact]
    public void Struct_identity_matches_the_audited_CN_binary()
    {
        string? actual = typeof(AtkUnitBase).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.Equal("1.0.0+af18b1116ddd23d1eddfc345f8eef6974d8f84d3", actual);
        Assert.Equal(0x238, sizeof(AtkUnitBase));
        Assert.Equal(0xC0, sizeof(AtkResNode));
    }

    [Fact]
    public void Missing_addon_does_not_read_any_memory()
    {
        var memory = new SyntheticMemory();
        var result = memory.Reader(lookup: _ => 0).Probe("Emj", true);
        Assert.False(result.Present);
        Assert.Null(result.Error);
        Assert.Empty(memory.Reads);
    }

    [Theory]
    [InlineData(false, true, 255, true)]
    [InlineData(true, false, 255, true)]
    [InlineData(true, true, 0, true)]
    [InlineData(true, true, 255, false)]
    public void Hidden_unready_or_summary_only_addon_never_reads_its_nodes(bool visible, bool ready, int alpha, bool detail)
    {
        var memory = new SyntheticMemory();
        AtkUldManager unmapped = default;
        unmapped.NodeList = (AtkResNode**)0xBAD0000;
        unmapped.NodeListCount = unmapped.NodeListSize = 1;
        memory.Addon(unmapped, visible, ready, (byte)alpha);
        var result = memory.Reader().Probe("Emj", detail);
        Assert.Null(result.Error);
        Assert.Empty(result.VisibleNodes);
        Assert.Single(memory.Reads);
    }

    [Fact]
    public void Visible_ancestor_chain_emits_layout_and_reuses_cached_parent_headers()
    {
        var memory = new SyntheticMemory();
        memory.Store(0x30000, SyntheticMemory.Node(1));
        memory.Store(0x31000, SyntheticMemory.Node(2, 0x30000));
        memory.Addon(memory.NodeList(0x31000, 0x30000));
        var result = memory.Reader().Probe("Emj", true);
        Assert.Null(result.Error);
        Assert.Equal(2, result.VisibleNodes.Count);
        Assert.Equal("Emj/2", result.VisibleNodes[0].Path);
        Assert.Equal(100f, result.VisibleNodes[0].X);
        Assert.Single(memory.Reads, r => r.Address == 0x30000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Hidden_ancestor_suppresses_child_and_its_payload(int kind)
    {
        var memory = new SyntheticMemory();
        var parent = SyntheticMemory.Node(1);
        if (kind == 0) parent.NodeFlags = 0;
        if (kind == 1) parent.Color.A = 0;
        if (kind == 2) parent.IsDrawDisabled = true;
        memory.Store(0x30000, parent);
        // Text length/payload deliberately absent: hidden descendants must not be parsed.
        memory.Store(0x31000, SyntheticMemory.Node(2, 0x30000, NodeType.Text));
        memory.Addon(memory.NodeList(0x31000));
        var result = memory.Reader().Probe("Emj", true);
        Assert.Null(result.Error);
        Assert.Empty(result.VisibleNodes);
        Assert.DoesNotContain(memory.Reads, r => r.Address == 0x31000 + VisibleUiReader.TextLengthOffset);
    }

    [Fact]
    public void Collision_clipping_selection_flag_is_not_a_hidden_flag()
    {
        var memory = new SyntheticMemory();
        var node = SyntheticMemory.Node(7);
        node.IsCollisionClipped = true;
        memory.Store(0x30000, node);
        memory.Addon(memory.NodeList(0x30000));
        var result = memory.Reader().Probe("Emj", true);
        Assert.Null(result.Error);
        Assert.Single(result.VisibleNodes);
    }

    [Fact]
    public void Null_and_duplicate_node_entries_are_skipped_without_double_counting()
    {
        var memory = new SyntheticMemory();
        memory.Store(0x30000, SyntheticMemory.Node(7));
        memory.Addon(memory.NodeList(0, 0x30000, 0x30000));
        var result = memory.Reader().Probe("Emj", true);
        Assert.Null(result.Error);
        Assert.Single(result.VisibleNodes);
        Assert.Single(memory.Reads, r => r.Address == 0x30000);
    }

    [Fact]
    public void Parent_cycle_returns_specific_error_and_no_usable_addon_state()
    {
        var memory = new SyntheticMemory();
        memory.Store(0x30000, SyntheticMemory.Node(1, 0x31000));
        memory.Store(0x31000, SyntheticMemory.Node(2, 0x30000));
        memory.Addon(memory.NodeList(0x30000));
        var result = memory.Reader().Probe("Emj", true);
        Assert.Contains("node.parent: cycle", result.Error);
        Assert.False(result.Ready);
        Assert.False(result.Visible);
        Assert.Empty(result.VisibleNodes);
        Assert.Equal(4, memory.Reads.Count);
    }

    [Fact]
    public void Unreadable_parent_returns_error_at_parent_field()
    {
        var memory = new SyntheticMemory();
        memory.Store(0x30000, SyntheticMemory.Node(1, 0xBAD0000));
        memory.Addon(memory.NodeList(0x30000));
        Assert.Contains("node.parent: unreadable", memory.Reader().Probe("Emj", true).Error);
    }

    [Fact]
    public void Empty_node_list_may_have_null_pointer_without_failure()
    {
        var memory = new SyntheticMemory();
        memory.Addon(default);
        var result = memory.Reader().Probe("Emj", true);
        Assert.Null(result.Error);
        Assert.Empty(result.VisibleNodes);
        Assert.Single(memory.Reads);
    }

    [Fact]
    public void Nonempty_node_list_requires_readable_pointer()
    {
        var memory = new SyntheticMemory();
        AtkUldManager manager = default;
        manager.NodeListCount = manager.NodeListSize = 1;
        memory.Addon(manager);
        Assert.Contains("Emj.nodes: unreadable", memory.Reader().Probe("Emj", true).Error);
        Assert.Single(memory.Reads); // Address zero rejected before injected reader.
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(2049, 2049)]
    public void Invalid_node_count_is_rejected_before_list_read(int count, int capacity)
    {
        var memory = new SyntheticMemory();
        AtkUldManager manager = default;
        manager.NodeListCount = (ushort)count;
        manager.NodeListSize = (ushort)capacity;
        manager.NodeList = (AtkResNode**)0xBAD0000;
        memory.Addon(manager);
        Assert.Contains("invalid node list/depth", memory.Reader().Probe("Emj", true).Error);
        Assert.Single(memory.Reads);
    }

    [Fact]
    public void Component_child_traversal_and_repeated_component_reference_terminate()
    {
        var memory = new SyntheticMemory();
        var header = SyntheticMemory.Node(10, type: (NodeType)1000);
        memory.StoreComponentNode(0x30000, header, 0x40000);
        memory.Store(0x31000, SyntheticMemory.Node(20, 0x30000));
        AtkComponentBase component = default;
        component.UldManager = memory.NodeListAt(0x21000, 0x31000, 0x30000);
        memory.Store(0x40000, component);
        memory.Addon(memory.NodeList(0x30000));
        var result = memory.Reader().Probe("Emj", true);
        Assert.Null(result.Error);
        Assert.Equal(new[] { "Emj/10", "Emj/10/20" }, result.VisibleNodes.Select(n => n.Path));
    }

    [Fact]
    public void Text_and_image_nodes_never_read_text_payload_texture_or_atkvalues()
    {
        var memory = new SyntheticMemory();
        memory.Store(0x30000, SyntheticMemory.Node(1, type: NodeType.Text));
        memory.Store(0x30000 + VisibleUiReader.TextLengthOffset, 13L);
        memory.Store(0x31000, SyntheticMemory.Node(2, type: NodeType.Image));
        // No AtkTextNode full buffer, StringPtr, inline text, AtkImageNode/parts/texture or AtkValues exists.
        memory.Addon(memory.NodeList(0x30000, 0x31000));
        var result = memory.Reader().Probe("Emj", true);
        Assert.Null(result.Error);
        Assert.Equal(12, result.VisibleNodes[0].TextByteLength);
        Assert.Null(result.VisibleNodes[1].TextByteLength);
        Assert.Equal(new[] {
            ((nint)SyntheticMemory.AddonAddress, sizeof(AtkUnitBase)),
            ((nint)SyntheticMemory.ListAddress, 2 * IntPtr.Size),
            ((nint)0x30000, sizeof(AtkResNode)),
            ((nint)(0x30000 + VisibleUiReader.TextLengthOffset), sizeof(long)),
            ((nint)0x31000, sizeof(AtkResNode)),
        }, memory.Reads);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.VisibleNodes[0]));
        Assert.Equal(new[] { "Height", "Id", "Path", "Rotation", "TextByteLength", "Type", "Width", "X", "Y" },
            json.RootElement.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4098)]
    public void Invalid_text_length_stays_unknown_without_following_any_text_pointer(long length)
    {
        var memory = new SyntheticMemory();
        memory.Store(0x30000, SyntheticMemory.Node(1, type: NodeType.Text));
        memory.Store(0x30000 + VisibleUiReader.TextLengthOffset, length);
        memory.Addon(memory.NodeList(0x30000));
        var result = memory.Reader().Probe("Emj", true);
        Assert.Null(result.Error);
        Assert.Null(Assert.Single(result.VisibleNodes).TextByteLength);
    }

    [Theory]
    [InlineData(1L, 0)]
    [InlineData(4097L, 4096)]
    public void Text_length_uses_the_pinned_Utf8String_Length_contract(long used, int length)
    {
        var memory = new SyntheticMemory();
        memory.Store(0x30000, SyntheticMemory.Node(1, type: NodeType.Text));
        // Match the live finding: StringLength is zero despite a different BufUsed.
        int storedLengthOffset = checked((int)System.Runtime.InteropServices.Marshal.OffsetOf<AtkTextNode>(nameof(AtkTextNode.NodeText)) +
            (int)System.Runtime.InteropServices.Marshal.OffsetOf<FFXIVClientStructs.FFXIV.Client.System.String.Utf8String>("StringLength"));
        memory.Store(0x30000 + storedLengthOffset, 0L);
        memory.Store(0x30000 + VisibleUiReader.TextLengthOffset, used);
        memory.Addon(memory.NodeList(0x30000));
        var result = memory.Reader().Probe("Emj", true);
        Assert.Null(result.Error);
        Assert.Equal(length, Assert.Single(result.VisibleNodes).TextByteLength);
        Assert.DoesNotContain(memory.Reads, r => r.Address == 0x30000 + storedLengthOffset);
    }

    [Fact]
    public void Nonfinite_coordinates_fail_before_export()
    {
        var memory = new SyntheticMemory();
        var node = SyntheticMemory.Node(1);
        node.ScreenX = float.NaN;
        memory.Store(0x30000, node);
        memory.Addon(memory.NodeList(0x30000));
        var result = memory.Reader().Probe("Emj", true);
        Assert.Contains("invalid coordinates", result.Error);
        Assert.Empty(result.VisibleNodes);
    }

    [Fact]
    public void Short_injected_read_is_rejected_without_deserializing_partial_header()
    {
        var reader = new VisibleUiReader(_ => 0x10000, (_, count) => new byte[count - 1], clockMilliseconds: () => 0);
        Assert.Contains("addon.header: unreadable", reader.Probe("Emj", true).Error);
    }

    [Fact]
    public void Node_budget_is_shared_across_addons_and_reset_only_at_new_sample()
    {
        var memory = new SyntheticMemory();
        memory.Store(0x30000, SyntheticMemory.Node(1));
        memory.Addon(memory.NodeList(0x30000));
        var reader = memory.Reader(new UiReadLimits(MaxNodes: 1));
        Assert.Null(reader.Probe("Emj", true).Error);
        var second = reader.Probe("EmjL", true);
        Assert.Contains("BUDGET_EXCEEDED nodes", second.Error);
        Assert.False(second.Ready);
        reader.BeginSample();
        Assert.Null(reader.Probe("EmjL", true).Error);
    }

    [Fact]
    public void Read_budget_prevents_additional_memory_reads_and_is_shared_across_addons()
    {
        var memory = new SyntheticMemory();
        memory.Store(0x30000, SyntheticMemory.Node(1));
        memory.Addon(memory.NodeList(0x30000));
        var reader = memory.Reader(new UiReadLimits(MaxReads: 4));
        Assert.Null(reader.Probe("Emj", true).Error); // Header, list, node = 3.
        Assert.Contains("BUDGET_EXCEEDED reads/time", reader.Probe("EmjL", true).Error);
        Assert.Equal(4, memory.Reads.Count); // Second header only, list read rejected.
    }

    [Fact]
    public void Time_budget_uses_injected_clock_without_JIT_or_wall_clock_flakiness()
    {
        var memory = new SyntheticMemory();
        memory.Addon(default);
        double now = 100;
        var reader = memory.Reader(clock: () => now);
        now = 121;
        Assert.Contains("BUDGET_EXCEEDED reads/time", reader.Probe("Emj", true).Error);
        Assert.Empty(memory.Reads);
        reader.BeginSample();
        Assert.Null(reader.Probe("Emj", true).Error);
    }

    [Fact]
    public void A_new_probe_cannot_leak_nodes_from_an_earlier_addon()
    {
        var memory = new SyntheticMemory();
        memory.Store(0x30000, SyntheticMemory.Node(1));
        memory.Addon(memory.NodeList(0x30000));
        var reader = memory.Reader(lookup: name => name == "Emj" ? (nint)SyntheticMemory.AddonAddress : 0);
        Assert.Single(reader.Probe("Emj", true).VisibleNodes);
        Assert.Empty(reader.Probe("EmjL", true).VisibleNodes);
    }
}
