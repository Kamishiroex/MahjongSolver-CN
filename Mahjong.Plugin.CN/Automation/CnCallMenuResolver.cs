using System.Collections.Immutable;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState.Variants;

namespace Mahjong.Plugin.CN.Automation;

/// <summary>Resolve intent against the current active renderer bindings, never a fixed action order.</summary>
internal static unsafe class CnCallMenuResolver
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static bool TryResolve(AtkUnitBase* unit, ActionKind intent, out int option, out ImmutableArray<string> labels)
    {
        option = -1; labels = [];
        return TryGetMenu(unit, out var list, out var shell) && TryResolve(list, shell, intent, out option, out labels);
    }

    internal static bool TryGetMenu(AtkUnitBase* unit, out AtkComponentList* list, out AtkResNode* shell)
    {
        list = null; shell = null;
        if (unit == null || !unit->IsVisible) return false;
        var host = Find(&unit->UldManager, 104, 1052);
        if (!CnPublicListClick.Visible(host) || ((AtkComponentNode*)host)->Component == null) return false;
        shell = Find(&((AtkComponentNode*)host)->Component->UldManager, 3, 1030);
        if (!CnPublicListClick.Visible(shell) || !Under(shell, host)) return false;
        list = (AtkComponentList*)((AtkComponentNode*)shell)->Component;
        return list != null;
    }

    internal static bool TryResolve(AtkComponentList* list, AtkResNode* shell, ActionKind intent,
        out int option, out ImmutableArray<string> labels)
    {
        option = -1; labels = [];
        string? expected = intent switch
        {
            ActionKind.Ron => "Ron", ActionKind.Tsumo => "Tsumo", ActionKind.Chi => "Chi",
            ActionKind.Pon => "Pon", ActionKind.Riichi => "Riichi", ActionKind.Pass => "Pass",
            ActionKind.Kyushukyuhai => "Kyushukyuhai",
            ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan => "Kan", _ => null,
        };
        if (expected is null || list == null || !CnPublicListClick.Visible(shell) || list->OwnerNode != shell ||
            list->ListLength is < 1 or > 8 || list->AllocatedItemRendererListLength < list->ListLength ||
            list->AllocatedItemRendererListLength > 64 || list->ItemRendererList == null ||
            !list->IsItemInteractionEnabled || list->IsUpdatePending || list->IsScrollRefreshPending ||
            list->FirstVisibleItemIndex != 0) return false;
        var found = ImmutableArray.CreateBuilder<string>();
        var owners = new HashSet<nint>();
        int match = -1;
        for (int i = 0; i < list->ListLength; i++)
        {
            var entry = list->ItemRendererList[i];
            var row = entry.AtkComponentListItemRenderer;
            if (row == null || row->ListItemIndex != i || row->OwnerNode == null ||
                !owners.Add((nint)row) || (int)row->OwnerNode->Type != 1029 ||
                ((AtkComponentNode*)row->OwnerNode)->Component != (AtkComponentBase*)row ||
                !CnPublicListClick.Visible((AtkResNode*)row->OwnerNode) || !Under((AtkResNode*)row->OwnerNode, shell)) return false;
            var node = Find(&row->UldManager, 4, (int)NodeType.Text);
            if (!CnPublicListClick.Visible(node) || !Under(node, (AtkResNode*)row->OwnerNode)) return false;
            string? label = Label((AtkTextNode*)node);
            if (label is null || found.Contains(label)) return false;
            found.Add(label);
            if (label != expected) continue;
            if (entry.IsDisabled || !row->IsEnabled) return false;
            match = i;
        }
        labels = found.ToImmutable();
        if (match < 0) return false;
        option = match; return true;
    }

    private static AtkResNode* Find(AtkUldManager* manager, uint id, int type)
    {
        if (manager == null || manager->NodeList == null || manager->NodeListCount is < 1 or > 512) return null;
        AtkResNode* found = null;
        for (int i = 0; i < manager->NodeListCount; i++)
        {
            var node = manager->NodeList[i];
            if (node == null || node->NodeId != id) continue;
            if (found != null || (int)node->Type != type) return null;
            found = node;
        }
        return found;
    }
    private static bool Under(AtkResNode* node, AtkResNode* ancestor)
    {
        for (int i = 0; node != null && i < 32; i++, node = node->ParentNode)
            if (node == ancestor) return true;
        return false;
    }
    private static string? Label(AtkTextNode* text)
    {
        ref var str = ref text->NodeText;
        if (str.StringPtr.Value == null || str.BufUsed is < 2 or > 65 || str.BufSize < str.BufUsed || str.BufSize > 4096) return null;
        var bytes = new ReadOnlySpan<byte>(str.StringPtr.Value, (int)str.BufUsed);
        if (bytes[^1] != 0) return null;
        foreach (byte b in bytes[..^1]) if (b < 0x20 || b == 0x7f) return null;
        try
        {
            string? label = ChineseActionLabels.Normalize(Utf8.GetString(bytes[..^1]));
            return label is "Ron" or "Tsumo" or "Chi" or "Pon" or "Kan" or "Riichi" or "Pass" or "Cancel" or "Kyushukyuhai" ? label : null;
        }
        catch (DecoderFallbackException) { return null; }
    }
}
