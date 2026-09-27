using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.Dalamud.GameState.Variants;

namespace Mahjong.Plugin.CN.Diagnostics;

internal sealed record PublicActionMenuRow(string Path, float ScreenY, string? Action, bool? Enabled, string Code,
    int? ListItemIndex = null, bool? ListItemDisabled = null, bool? RendererEnabled = null,
    bool? ListInteractionEnabled = null);
internal sealed record PublicActionMenuListState(int ListLength, int AllocatedRendererCount,
    int FirstVisibleItemIndex, int NumVisibleItems, bool IsUpdatePending, bool IsScrollRefreshPending,
    bool IsItemInteractionEnabled);
internal sealed record PublicActionMenuCandidate(string Code, bool Visible, bool AllVisibleRowsDecoded,
    IReadOnlyList<PublicActionMenuRow> Rows, PublicActionMenuListState? ListState = null)
{
    public string MappingStatus => "Candidate";
    public bool CompleteLegalActions => false;
    public bool ActionOccurred => false;
}

/// <summary>
/// Fixed Emj 104(1052)/3(1030)/row(1029)/4(Text) public action menu. Reads only finite
/// labels from renderers bound to active ListItems, never all preallocated node clones.
/// Enabled combines ListItem.IsDisabled, renderer button Enabled, and list interaction.
/// Render order and ListItemIndex are not callback
/// index; menu absence does not establish Pass/Discard/None or a completed action.
/// </summary>
internal sealed unsafe class PublicActionMenuReader(Func<nint, int, string, byte[]> bytes,
    Func<nint, string, AtkResNode> node)
{
    internal const int MaximumVisibleRows = 8;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly int TextOffset = (int)Marshal.OffsetOf<AtkTextNode>(nameof(AtkTextNode.NodeText));
    private static readonly int UsedOffset = (int)Marshal.OffsetOf<Utf8String>(nameof(Utf8String.BufUsed));
    private static readonly int ComponentOffset = (int)Marshal.OffsetOf<AtkComponentNode>(nameof(AtkComponentNode.Component));

    internal PublicActionMenuCandidate Read(IReadOnlyList<UiNode> visible,
        IReadOnlyDictionary<string, nint> addresses, IReadOnlyDictionary<string, nint>? containers = null)
    {
        if (!addresses.TryGetValue("Emj/104", out var host) || !addresses.TryGetValue("Emj/104/3", out var list) ||
            !visible.Any(n => n.Path == "Emj/104" && n.Type == 1052) ||
            !visible.Any(n => n.Path == "Emj/104/3" && n.Type == 1030))
            return new("ACTION_MENU_NOT_VISIBLE", false, false, []);
        if (!Collect(list, out var container) || !CheckContainer(container, list, host, addresses))
            return new("ACTION_MENU_OWNER_UNVERIFIED", true, false, []);
        nint component = ReadComponent(list);
        if (component == 0) return new("ACTION_MENU_LIST_COMPONENT_UNAVAILABLE", true, false, []);
        var listData = ReadStruct<AtkComponentList>(component, "actionMenu.listMetadata");
        var state = State(listData);
        if ((nint)listData.OwnerNode != list)
            return new("ACTION_MENU_LIST_OWNER_MISMATCH", true, false, [], state);
        if (listData.ListLength is < 1 or > MaximumVisibleRows || listData.AllocatedItemRendererListLength < listData.ListLength ||
            listData.AllocatedItemRendererListLength > 64 || listData.ItemRendererList == null)
            return new(listData.ListLength > MaximumVisibleRows ? "ACTION_MENU_ROW_LIMIT" : "ACTION_MENU_LIST_RANGE_INVALID", true, false, [], state);
        if (listData.IsUpdatePending || listData.IsScrollRefreshPending || listData.FirstVisibleItemIndex != 0)
            return new("ACTION_MENU_LIST_UPDATING_OR_SCROLLED", true, false, [], state);
        var roots = visible.Where(n => IsRowPath(n.Path) && n.Type >= 1000).ToArray();
        if (roots.Length > 64 || roots.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != roots.Length)
            return new("ACTION_MENU_RENDERER_POOL_INVALID", true, false, [], state);
        var activeRows = new List<(UiNode Node, nint Owner, nint Renderer, AtkComponentList.ListItem Item)>();
        var rendererSet = new HashSet<nint>();
        for (int i = 0; i < listData.ListLength; i++)
        {
            var entry = ReadStruct<AtkComponentList.ListItem>((nint)listData.ItemRendererList + i * sizeof(AtkComponentList.ListItem), "actionMenu.activeListItem");
            nint rendererAddress = (nint)entry.AtkComponentListItemRenderer;
            if (rendererAddress == 0 || !rendererSet.Add(rendererAddress))
                return new("ACTION_MENU_ACTIVE_RENDERER_INVALID", true, false, [], state);
            var renderer = ReadStruct<AtkComponentListItemRenderer>(rendererAddress, "actionMenu.rendererMetadata");
            nint ownerAddress = (nint)renderer.OwnerNode;
            var ownerMatches = roots.Where(x => addresses.TryGetValue(x.Path, out nint p) && p == ownerAddress).ToArray();
            if (renderer.ListItemIndex != i || ownerMatches.Length != 1 || ReadComponent(ownerAddress) != rendererAddress)
                return new("ACTION_MENU_ACTIVE_RENDERER_BINDING_MISMATCH", true, false, [], state);
            activeRows.Add((ownerMatches[0], ownerAddress, rendererAddress, entry));
        }
        var rows = new List<PublicActionMenuRow>();
        foreach (var active in activeRows)
        {
            var item = active.Node;
            string textPath = item.Path + "/4";
            var row = new PublicActionMenuRow(item.Path, item.Y, null, null, "ACTION_MENU_ROW_UNVERIFIED",
                rows.Count, active.Item.IsDisabled, null, state.IsItemInteractionEnabled);
            if (item.Type != 1029 || !addresses.TryGetValue(item.Path, out var rowAddress) ||
                !addresses.TryGetValue(textPath, out var textAddress) ||
                !visible.Any(x => x.Path == textPath && x.Type == 3) || !Collect(textAddress, out var chain) ||
                !CheckRow(chain, textAddress, rowAddress, list, host, addresses))
            { rows.Add(row); continue; }
            var owner = node(rowAddress, "actionMenu.rowOwner");
            bool rendererEnabled = (owner.NodeFlags & NodeFlags.Enabled) != 0;
            row = row with { ScreenY = owner.ScreenY, RendererEnabled = rendererEnabled,
                Enabled = !active.Item.IsDisabled && rendererEnabled && state.IsItemInteractionEnabled };
            string? label = ReadLabel(textAddress, out string error);
            rows.Add(row with { Action = label, Code = error });
        }
        // UI mutation can occur between individual reads, especially in the external probe.
        var after = ReadStruct<AtkComponentList>(component, "actionMenu.listMetadataRecheck");
        if (ReadComponent(list) != component || after.OwnerNode != listData.OwnerNode ||
            after.ItemRendererList != listData.ItemRendererList || State(after) != state)
            return new("ACTION_MENU_LIST_CHANGED_DURING_READ", true, false, [], state);
        for (int i = 0; i < activeRows.Count; i++)
        {
            var entry = ReadStruct<AtkComponentList.ListItem>((nint)after.ItemRendererList + i * sizeof(AtkComponentList.ListItem), "actionMenu.activeListItemRecheck");
            var renderer = ReadStruct<AtkComponentListItemRenderer>(activeRows[i].Renderer, "actionMenu.rendererMetadataRecheck");
            if (entry.AtkComponentListItemRenderer != activeRows[i].Item.AtkComponentListItemRenderer ||
                entry.IsDisabled != activeRows[i].Item.IsDisabled || renderer.ListItemIndex != i ||
                (nint)renderer.OwnerNode != activeRows[i].Owner)
                return new("ACTION_MENU_LIST_CHANGED_DURING_READ", true, false, [], state);
        }
        var sorted = rows.OrderBy(x => x.ScreenY).ToArray();
        bool complete = sorted.All(x => x.Code == "ACTION_MENU_LABEL_CANDIDATE") &&
            sorted.Select(x => x.ScreenY).Distinct().Count() == sorted.Length;
        return new(complete ? "ACTION_MENU_VISIBLE_CANDIDATE" : "ACTION_MENU_PARTIAL_CANDIDATE", true, complete, sorted, state);
    }

    private nint ReadComponent(nint address)
        => (nint)BinaryPrimitives.ReadInt64LittleEndian(bytes(address + ComponentOffset, 8, "actionMenu.componentPointer"));
    private T ReadStruct<T>(nint address, string field) where T : unmanaged
        => MemoryMarshal.Read<T>(bytes(address, sizeof(T), field));
    private static PublicActionMenuListState State(AtkComponentList value) => new(value.ListLength,
        value.AllocatedItemRendererListLength, value.FirstVisibleItemIndex, value.NumVisibleItems,
        value.IsUpdatePending, value.IsScrollRefreshPending, value.IsItemInteractionEnabled);

    private static bool IsRowPath(string path)
    {
        if (!path.StartsWith("Emj/104/3/", StringComparison.Ordinal) || path.Length > 28) return false;
        string id = path[10..];
        // IDs identify current UI renderers only. No numeric clone-to-action or callback mapping.
        return uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out uint value) && value is > 0 and <= 1000000 &&
            value.ToString(CultureInfo.InvariantCulture) == id;
    }

    private bool Collect(nint address, out List<(nint Address, AtkResNode Node)> chain)
    {
        chain = [];
        var seen = new HashSet<nint>();
        while (address != 0 && chain.Count < 16)
        {
            if (!seen.Add(address)) return false;
            var n = node(address, "actionMenu.parent");
            if ((n.NodeFlags & NodeFlags.Visible) == 0 || n.Color.A == 0 || n.IsDrawDisabled ||
                !float.IsFinite(n.ScreenX) || !float.IsFinite(n.ScreenY) ||
                !float.IsFinite(n.Rotation) || Math.Abs(n.Rotation) > 0.0001f ||
                !float.IsFinite(n.ScaleX) || n.ScaleX <= 0 || n.ScaleX != n.ScaleY) return false;
            chain.Add((address, n)); address = (nint)n.ParentNode;
        }
        return address == 0;
    }

    private static bool CheckContainer(List<(nint Address, AtkResNode Node)> c, nint list, nint host,
        IReadOnlyDictionary<string, nint> addresses)
    {
        int i = 0;
        if (!Take(c, ref i, list, 3, 1030)) return false;
        OptionalRoot(c, ref i);
        if (!Take(c, ref i, host, 104, 1052)) return false;
        return End(c, i, addresses);
    }

    private static bool CheckRow(List<(nint Address, AtkResNode Node)> c, nint text, nint row, nint list, nint host,
        IReadOnlyDictionary<string, nint> addresses)
    {
        int i = 0;
        if (!Take(c, ref i, text, 4, 3) || c[0].Node.Width != 310 || c[0].Node.Height != 24) return false;
        OptionalRoot(c, ref i);
        if (i >= c.Count || c[i].Address != row || (int)c[i].Node.Type != 1029 || c[i].Node.Width != 350 || c[i].Node.Height != 32)
            return false;
        i++; OptionalRoot(c, ref i);
        if (!Take(c, ref i, list, 3, 1030)) return false;
        OptionalRoot(c, ref i);
        if (!Take(c, ref i, host, 104, 1052)) return false;
        return End(c, i, addresses);
    }

    private static bool End(List<(nint Address, AtkResNode Node)> c, int i, IReadOnlyDictionary<string, nint> addresses)
        => addresses.TryGetValue("Emj/46", out var table) && addresses.TryGetValue("Emj/1", out var root) &&
           Take(c, ref i, table, 46, 1) && Take(c, ref i, root, 1, 1) && i == c.Count && c[^1].Node.ParentNode == null;
    private static bool Take(List<(nint Address, AtkResNode Node)> c, ref int i, nint address, uint id, int type)
    {
        if (i >= c.Count || c[i].Address != address || c[i].Node.NodeId != id || (int)c[i].Node.Type != type) return false;
        i++; return true;
    }
    private static void OptionalRoot(List<(nint Address, AtkResNode Node)> c, ref int i)
    { if (i < c.Count && c[i].Node.NodeId == 1 && c[i].Node.Type == NodeType.Res) i++; }

    private string? ReadLabel(nint textAddress, out string code)
    {
        code = "ACTION_MENU_TEXT_INVALID";
        var header = bytes(textAddress + TextOffset, UsedOffset + 8, "actionMenu.textHeader");
        if (header.Length != UsedOffset + 8) return null;
        nint ptr = (nint)BinaryPrimitives.ReadInt64LittleEndian(header);
        long size = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        long used = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(UsedOffset));
        if (ptr == 0 || used is < 2 or > 65 || size < used || size > 4096) return null;
        var payload = bytes(ptr, (int)used, "actionMenu.finiteLabel");
        if (payload.Length != used || payload[^1] != 0) return null;
        foreach (byte value in payload.AsSpan(0, payload.Length - 1)) if (value < 0x20 || value == 0x7f) return null;
        string? label;
        try { label = ChineseActionLabels.Normalize(Utf8.GetString(payload.AsSpan(0, payload.Length - 1))); }
        catch (DecoderFallbackException) { return null; }
        if (label is not ("Pon" or "Chi" or "Kan" or "Ron" or "Riichi" or "Tsumo" or "Pass" or "Cancel" or "Kyushukyuhai"))
        { code = "ACTION_MENU_UNKNOWN_LABEL"; return null; }
        code = "ACTION_MENU_LABEL_CANDIDATE"; return label;
    }
}
