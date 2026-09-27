using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Automation;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed unsafe class CnPublicListClickTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void Already_selected_tsumo_row_still_prepares_exact_registered_click(int selected)
    {
        using var f = new Fixture(); f.List->SelectedItemIndex = selected;
        Assert.True(f.Prepare(0, out var click, out var data));
        Assert.Equal(AtkEventType.ListItemClick, click.State.EventType);
        Assert.Equal(7u, click.Param); // Copied parameter, never hard-coded from this fixture or live value zero.
        Assert.Equal((nint)f.Unit, (nint)click.Listener);
        Assert.Equal((nint)f.Row, (nint)data.ListItemData.ListItemRenderer);
        Assert.Equal(0, data.ListItemData.SelectedIndex); Assert.Equal(selected, f.List->SelectedItemIndex);
        Assert.Equal((nint)0, (nint)click.NextEvent);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("renderer-disabled")]
    [InlineData("hidden")]
    [InlineData("refresh")]
    [InlineData("update")]
    [InlineData("interaction")]
    [InlineData("missing-row")]
    [InlineData("missing-node")]
    [InlineData("wrong-listener")]
    [InlineData("wrong-target")]
    [InlineData("highlight-only")]
    [InlineData("cycle")]
    [InlineData("global")]
    public void Ambiguous_or_inactive_menu_never_prepares_an_action(string reason)
    {
        using var f = new Fixture();
        switch (reason)
        {
            case "disabled": f.Items->IsDisabled = true; break;
            case "renderer-disabled": f.RowNode->NodeFlags &= ~NodeFlags.Enabled; break;
            case "hidden": f.RowNode->NodeFlags &= ~NodeFlags.Visible; break;
            case "refresh": f.List->IsScrollRefreshPending = true; break;
            case "update": f.List->IsUpdatePending = true; break;
            case "interaction": f.List->IsItemInteractionEnabled = false; break;
            case "missing-row": f.Row->ListItemIndex = 1; break;
            case "missing-node": f.Row->OwnerNode = null; break;
            case "wrong-listener": f.Event->Listener = (AtkEventListener*)f.List; break;
            case "wrong-target": f.Event->Target = (AtkEventTarget*)f.RowNode; break;
            case "highlight-only": f.Event->State.EventType = AtkEventType.ListItemHighlight; break;
            case "cycle": f.Event->NextEvent = f.Event; break;
            case "global": f.Event->State.StateFlags = AtkEventStateFlags.IsGlobalEvent; break;
        }
        Assert.False(f.Prepare(0, out _, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void Out_of_range_row_is_rejected(int option)
    { using var f = new Fixture(); Assert.False(f.Prepare(option, out _, out _)); }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> allocations = [];
        internal readonly AtkUnitBase* Unit;
        internal readonly AtkComponentList* List;
        internal readonly AtkComponentListItemRenderer* Row;
        internal readonly AtkComponentNode* Shell;
        internal readonly AtkComponentNode* RowNode;
        internal readonly AtkEvent* Event;
        internal readonly AtkComponentList.ListItem* Items;
        internal Fixture()
        {
            Unit = Allocate<AtkUnitBase>(); List = Allocate<AtkComponentList>(); Row = Allocate<AtkComponentListItemRenderer>();
            Shell = Allocate<AtkComponentNode>(); RowNode = Allocate<AtkComponentNode>(); Event = Allocate<AtkEvent>();
            Items = Allocate<AtkComponentList.ListItem>();
            Shell->NodeFlags = RowNode->NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;
            Shell->Color.A = RowNode->Color.A = 255; Shell->Type = (NodeType)1030;
            Shell->Component = (AtkComponentBase*)List; List->OwnerNode = Shell;
            Row->OwnerNode = RowNode; RowNode->ParentNode = (AtkResNode*)Shell;
            Row->ListItemIndex = 0; List->ListLength = 2; List->AllocatedItemRendererListLength = 1;
            List->IsItemInteractionEnabled = true; List->ItemRendererList = Items; Items->AtkComponentListItemRenderer = Row;
            Event->State.EventType = AtkEventType.ListItemClick; Event->Listener = (AtkEventListener*)Unit;
            Event->Target = (AtkEventTarget*)Shell; Event->Param = 7; Shell->AtkEventManager.Event = Event;
        }
        internal bool Prepare(int option, out AtkEvent click, out AtkEventData data) =>
            CnPublicListClick.TryPrepare(Unit, List, (AtkResNode*)Shell, option, out click, out data);
        private T* Allocate<T>() where T : unmanaged
        { var p = (T*)NativeMemory.AllocZeroed((nuint)sizeof(T)); allocations.Add((nint)p); return p; }
        public void Dispose() { foreach (nint p in allocations) NativeMemory.Free((void*)p); }
    }
}
