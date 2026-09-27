using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Automation;

/// <summary>CN 2026.09.15: selection/highlight is not activation. Uses the registered public menu event.</summary>
internal static unsafe class CnPublicListClick
{
    internal static bool Dispatch(AtkUnitBase* unit, int option, Func<bool> allowed, Action beforeDispatch)
    {
        if (!CnCallMenuResolver.TryGetMenu(unit, out var list, out var shell)) return false;
        if (!TryPrepare(unit, list, shell, option, out var click, out var data) || !allowed()) return false;
        beforeDispatch();
        click.Listener->ReceiveEvent(AtkEventType.ListItemClick, (int)click.Param, &click, &data);
        return true; // Submission only; the existing outcome timeout remains mandatory.
    }

    internal static bool TryPrepare(AtkUnitBase* unit, AtkComponentList* list, AtkResNode* shell, int option,
        out AtkEvent click, out AtkEventData data)
    {
        click = default; data = default;
        if (unit == null || list == null || !Visible(shell) || list->OwnerNode != shell ||
            !list->IsItemInteractionEnabled || list->IsUpdatePending || list->IsScrollRefreshPending ||
            list->ListLength is < 1 or > 8 || option < 0 || option >= list->ListLength ||
            list->AllocatedItemRendererListLength is < 1 or > 8 || list->ItemRendererList == null ||
            list->HoveredItemIndex3 is < short.MinValue or > short.MaxValue) return false;
        AtkComponentListItemRenderer* renderer = null;
        for (int i = 0; i < list->AllocatedItemRendererListLength; i++)
        {
            ref var entry = ref list->ItemRendererList[i];
            var row = entry.AtkComponentListItemRenderer;
            if (row == null || row->ListItemIndex != option) continue;
            if (renderer != null || entry.IsDisabled || !Visible((AtkResNode*)row->OwnerNode) || !row->IsEnabled) return false;
            renderer = row;
        }
        if (renderer == null) return false;
        AtkEvent* match = null;
        var seen = new HashSet<nint>();
        for (var e = shell->AtkEventManager.Event; e != null; e = e->NextEvent)
        {
            if (seen.Count >= 32 || !seen.Add((nint)e)) return false;
            if (e->State.EventType != AtkEventType.ListItemClick) continue;
            if (match != null || e->Listener != (AtkEventListener*)unit || e->Target != (AtkEventTarget*)shell ||
                e->Param > int.MaxValue || e->State.StateFlags.HasFlag(AtkEventStateFlags.IsGlobalEvent)) return false;
            match = e;
        }
        if (match == null) return false;
        click = *match; click.NextEvent = null;
        data.ListItemData.ListItemRenderer = renderer;
        data.ListItemData.SelectedIndex = option;
        data.ListItemData.HoveredItemIndex3 = (short)list->HoveredItemIndex3;
        // Zeroed mouse/modifier data: ordinary primary click. Never mutate SelectedItemIndex.
        return true;
    }

    internal static bool Visible(AtkResNode* node)
    {
        if (node == null) return false;
        var seen = new HashSet<nint>();
        for (int i = 0; node != null && i < 32; i++, node = node->ParentNode)
            if (!seen.Add((nint)node) || !node->NodeFlags.HasFlag(NodeFlags.Visible) || node->Color.A == 0 || node->IsDrawDisabled)
                return false;
        return node == null;
    }
}
