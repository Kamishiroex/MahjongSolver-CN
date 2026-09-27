using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed unsafe class PublicActionMenuReaderTests
{
    [Theory]
    [InlineData("碰", "Pon")]
    [InlineData("吃", "Chi")]
    [InlineData("杠", "Kan")]
    [InlineData("立直", "Riichi")]
    [InlineData("和牌", "Ron")]
    [InlineData("自摸", "Tsumo")]
    [InlineData("放弃", "Pass")]
    [InlineData("取消", "Cancel")]
    public void Public_menu_labels_and_row_enabled_bit_are_candidates_only(string label, string expected)
    {
        var f = new Fixture(); f.AddRow(2, label, 30, enabled: true);
        var menu = f.Read(); var row = Assert.Single(menu.Rows);
        Assert.True(menu.Visible); Assert.True(menu.AllVisibleRowsDecoded);
        Assert.Equal(expected, row.Action); Assert.True(row.Enabled);
        Assert.False(menu.CompleteLegalActions); Assert.False(menu.ActionOccurred);
        Assert.Equal("Candidate", menu.MappingStatus);
        Assert.Equal(2, f.TextReadCount);
    }

    [Fact]
    public void Disabled_row_remains_disabled_and_is_not_dropped_from_visible_menu()
    {
        var f = new Fixture(); f.AddRow(2, "立直", 0, enabled: false);
        var row = Assert.Single(f.Read().Rows);
        Assert.Equal("Riichi", row.Action); Assert.False(row.Enabled);
    }

    [Fact]
    public void Absent_menu_does_not_infer_pass_or_any_legal_action()
    {
        var f = new Fixture(); f.Addresses.Remove("Emj/104");
        var menu = f.Read(); Assert.False(menu.Visible); Assert.False(menu.CompleteLegalActions);
        Assert.False(menu.AllVisibleRowsDecoded); Assert.Empty(menu.Rows); Assert.Empty(f.Reads);
    }

    [Theory]
    [InlineData("碰！")]
    [InlineData("Player Name")]
    [InlineData("下一局")]
    [InlineData("确定")]
    [InlineData("立直设置")]
    public void Nonbutton_declarations_names_next_and_confirmation_do_not_become_call_actions(string text)
    {
        var f = new Fixture(); f.AddRow(2, text, 0, true);
        var menu = f.Read(); Assert.False(menu.AllVisibleRowsDecoded);
        var row = Assert.Single(menu.Rows); Assert.Null(row.Action);
        Assert.Equal("ACTION_MENU_UNKNOWN_LABEL", row.Code);
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("hidden")]
    [InlineData("wrong-template")]
    [InlineData("wrong-leaf")]
    [InlineData("wrong-component-address")]
    [InlineData("wrong-size")]
    [InlineData("cycle")]
    public void Wrong_or_hidden_row_does_not_read_text(string variation)
    {
        var f = new Fixture(); var (row, leaf) = f.AddRow(2, "放弃", 0, true);
        var item = f.Nodes[leaf];
        switch (variation)
        {
            case "parent": item.ParentNode = (AtkResNode*)Fixture.Host; break;
            case "hidden": item.NodeFlags &= ~NodeFlags.Visible; break;
            case "wrong-template": var r = f.Nodes[row]; r.Type = (NodeType)1025; f.Nodes[row] = r; break;
            case "wrong-leaf": item.NodeId = 6; break;
            case "wrong-component-address": f.Addresses["Emj/104/3/2"] += 1; break;
            case "wrong-size": item.Width = 208; break;
            case "cycle": item.ParentNode = (AtkResNode*)leaf; break;
        }
        f.Nodes[leaf] = item;
        Assert.False(f.Read().AllVisibleRowsDecoded); Assert.Equal(0, f.TextReadCount);
    }

    [Fact]
    public void Sorted_render_order_is_preserved_without_interpreting_callback_index()
    {
        var f = new Fixture(); f.AddRow(2, "放弃", 100, true); f.AddRow(20001, "碰", 40, true);
        var menu = f.Read(); Assert.True(menu.AllVisibleRowsDecoded);
        Assert.Equal(new[] { "Pon", "Pass" }, menu.Rows.Select(x => x.Action));
        Assert.Equal(new[] { 40f, 100f }, menu.Rows.Select(x => x.ScreenY));
    }

    [Fact]
    public void Overlapping_rows_and_missing_or_extra_unreadable_rows_keep_menu_partial()
    {
        var f = new Fixture(); f.AddRow(2, "放弃", 100, true); f.AddRow(20001, "碰", 100, true);
        Assert.False(f.Read().AllVisibleRowsDecoded);
        var missing = new Fixture(); missing.AddRow(2, "放弃", 0, true); missing.AddRow(20001, "碰", 40, true);
        missing.Addresses.Remove("Emj/104/3/20001/4");
        Assert.False(missing.Read().AllVisibleRowsDecoded);
    }

    [Fact]
    public void More_than_eight_rows_fail_before_any_text_payload()
    {
        var f = new Fixture();
        for (uint i = 2; i < 11; i++) f.AddRow(i, "碰", i * 40, true);
        Assert.Equal("ACTION_MENU_ROW_LIMIT", f.Read().Code); Assert.Equal(0, f.TextReadCount);
    }

    [Fact]
    public void Active_list_membership_excludes_five_preallocated_rows_even_if_their_labels_are_valid()
    {
        var f = new Fixture();
        f.AddRow(2, "立直", 100, true); f.AddRow(21001, "放弃", 132, true);
        for (uint id = 21002; id <= 21006; id++) f.AddRow(id, "碰", 100, true);
        f.ListData.ListLength = 2;
        var menu = f.Read();
        Assert.True(menu.AllVisibleRowsDecoded);
        Assert.Equal(new[] { "Riichi", "Pass" }, menu.Rows.Select(x => x.Action));
        Assert.Equal(4, f.TextReadCount);
        Assert.Equal(7, menu.ListState!.AllocatedRendererCount);
        Assert.Equal(2, menu.ListState.ListLength);
    }

    [Theory]
    [InlineData("item")]
    [InlineData("list")]
    public void Item_disabled_or_list_interaction_disabled_overrides_enabled_node(string source)
    {
        var f = new Fixture(); f.AddRow(2, "自摸", 0, true);
        if (source == "item") f.Items[0] = f.Items[0] with { IsDisabled = true };
        else f.ListData.IsItemInteractionEnabled = false;
        var row = Assert.Single(f.Read().Rows);
        Assert.True(row.RendererEnabled); Assert.False(row.Enabled);
        Assert.Equal(source == "item", row.ListItemDisabled);
    }

    [Theory]
    [InlineData("index")]
    [InlineData("owner")]
    [InlineData("pointer")]
    [InlineData("duplicate")]
    public void Renderer_binding_must_match_item_index_owner_and_component(string mismatch)
    {
        var f = new Fixture(); f.AddRow(2, "碰", 0, true); f.AddRow(21001, "放弃", 32, true);
        if (mismatch == "index") f.Renderers[0] = f.Renderers[0] with { ListItemIndex = 1 };
        if (mismatch == "owner") { var r = f.Renderers[0]; r.OwnerNode = (AtkComponentNode*)Fixture.Host; f.Renderers[0] = r; }
        if (mismatch == "pointer") f.Items[0] = f.Items[0] with { AtkComponentListItemRenderer = (AtkComponentListItemRenderer*)f.RendererAddresses[1] };
        if (mismatch == "duplicate") f.Items[1] = f.Items[0];
        Assert.False(f.Read().AllVisibleRowsDecoded); Assert.Equal(0, f.TextReadCount);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("scroll-refresh")]
    [InlineData("scrolled")]
    [InlineData("capacity")]
    public void Updating_scrolled_or_invalid_capacity_lists_are_not_complete(string mismatch)
    {
        var f = new Fixture(); f.AddRow(2, "碰", 0, true);
        if (mismatch == "update") f.ListData.IsUpdatePending = true;
        if (mismatch == "scroll-refresh") f.ListData.IsScrollRefreshPending = true;
        if (mismatch == "scrolled") f.ListData.FirstVisibleItemIndex = 1;
        if (mismatch == "capacity") f.ListData.AllocatedItemRendererListLength = 0;
        Assert.False(f.Read().AllVisibleRowsDecoded); Assert.Equal(0, f.TextReadCount);
    }

    [Fact]
    public void List_mutation_while_reading_discards_all_candidate_actions()
    {
        var f = new Fixture(); f.AddRow(2, "碰", 0, true); f.AddRow(21001, "放弃", 32, true);
        f.MutateBeforeRecheck = () => f.ListData.ListLength = 1;
        var menu = f.Read();
        Assert.Equal("ACTION_MENU_LIST_CHANGED_DURING_READ", menu.Code);
        Assert.False(menu.AllVisibleRowsDecoded); Assert.Empty(menu.Rows);
    }

    private sealed class Fixture
    {
        internal const nint Host = 0x1000, List = 0x2000, Table = 0x3000, Root = 0x4000,
            ListComponent = 0x5000, ItemArray = 0x7000;
        internal readonly Dictionary<nint, AtkResNode> Nodes = [];
        internal readonly Dictionary<string, nint> Addresses = [];
        internal readonly List<UiNode> Visible = [];
        internal readonly List<(nint Address, int Count)> Reads = [];
        internal int TextReadCount;
        internal AtkComponentList ListData;
        internal readonly List<AtkComponentList.ListItem> Items = [];
        internal readonly List<AtkComponentListItemRenderer> Renderers = [];
        internal readonly List<nint> RendererAddresses = [];
        internal Action? MutateBeforeRecheck;
        private readonly Dictionary<nint, byte[]> memory = [];
        private int count;

        internal Fixture()
        {
            Add("Emj/1", Root, 1, 1, 0, 1260, 700, 0, true);
            Add("Emj/46", Table, 46, 1, Root, 720, 700, 0, true);
            Add("Emj/104", Host, 104, 1052, Table, 400, 160, 0, true);
            Add("Emj/104/3", List, 3, 1030, Host, 350, 160, 0, true);
            ListData = new AtkComponentList { ItemRendererList = (AtkComponentList.ListItem*)ItemArray,
                IsItemInteractionEnabled = true, OwnerNode = (AtkComponentNode*)List };
            Write(List + (int)Marshal.OffsetOf<AtkComponentNode>(nameof(AtkComponentNode.Component)), ListComponent);
        }
        internal (nint Row, nint Leaf) AddRow(uint id, string label, float y, bool enabled)
        {
            nint address = 0x10000 + count++ * 0x2000, leaf = address + 0x400, buffer = address + 0x800;
            string path = "Emj/104/3/" + id;
            Add(path, address, id, 1029, List, 350, 32, y, enabled);
            Add(path + "/4", leaf, 4, 3, address, 310, 24, y, true);
            byte[] payload = Encoding.UTF8.GetBytes(label + "\0");
            var header = new byte[24]; BinaryPrimitives.WriteInt64LittleEndian(header, buffer);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), 64);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), payload.Length);
            memory[leaf + (int)Marshal.OffsetOf<AtkTextNode>(nameof(AtkTextNode.NodeText))] = header;
            memory[buffer] = payload;
            nint rendererAddress = address + 0x1000;
            RendererAddresses.Add(rendererAddress);
            Renderers.Add(new AtkComponentListItemRenderer { OwnerNode = (AtkComponentNode*)address, ListItemIndex = count - 1 });
            Items.Add(new AtkComponentList.ListItem { AtkComponentListItemRenderer = (AtkComponentListItemRenderer*)rendererAddress });
            Write(address + (int)Marshal.OffsetOf<AtkComponentNode>(nameof(AtkComponentNode.Component)), rendererAddress);
            ListData.ListLength = ListData.AllocatedItemRendererListLength = count;
            ListData.NumVisibleItems = (short)count;
            return (address, leaf);
        }
        internal PublicActionMenuCandidate Read()
        {
            SyncMetadata();
            return new PublicActionMenuReader(ReadBytes, (p, _) => Nodes[p]).Read(Visible, Addresses);
        }
        private void SyncMetadata()
        {
            Write(ListComponent, ListData);
            for (int i = 0; i < Items.Count; i++)
            { Write(ItemArray + i * sizeof(AtkComponentList.ListItem), Items[i]); Write(RendererAddresses[i], Renderers[i]); }
        }
        private void Write<T>(nint address, T value) where T : unmanaged
        { byte[] data = new byte[sizeof(T)]; MemoryMarshal.Write(data, in value); memory[address] = data; }
        private byte[] ReadBytes(nint address, int length, string field)
        {
            if (field == "actionMenu.listMetadataRecheck" && MutateBeforeRecheck is not null)
            { MutateBeforeRecheck(); SyncMetadata(); }
            if (field is "actionMenu.textHeader" or "actionMenu.finiteLabel") TextReadCount++;
            Reads.Add((address, length)); var data = memory[address]; Assert.Equal(length, data.Length); return data;
        }
        private void Add(string path, nint address, uint id, int type, nint parent, int width, int height, float y, bool enabled)
        {
            var n = new AtkResNode { NodeId = id, Type = (NodeType)type, ParentNode = (AtkResNode*)parent,
                Width = (ushort)width, Height = (ushort)height, ScaleX = 1, ScaleY = 1, ScreenY = y,
                NodeFlags = NodeFlags.Visible | (enabled ? NodeFlags.Enabled : 0) };
            n.Color.A = 255; Nodes[address] = n; Addresses[path] = address;
            Visible.Add(new(path, id, type, 0, y, 0, width, height, null));
        }
    }
}
