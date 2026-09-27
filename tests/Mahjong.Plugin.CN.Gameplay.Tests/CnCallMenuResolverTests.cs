using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Cn.PublicState;
using Mahjong.Policy.Abstractions;
using Mahjong.Plugin.CN.Automation;
using Mahjong.Plugin.Dalamud.Actions;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed unsafe class CnCallMenuResolverTests
{
    [ThreadStatic] private static int clickCount;
    [ThreadStatic] private static int clickedIndex;
    [ThreadStatic] private static int clickedParam;
    [ThreadStatic] private static AtkEventType clickedType;

    [UnmanagedCallersOnly]
    private static void Receive(AtkEventListener* self, AtkEventType type, int param, AtkEvent* e, AtkEventData* data)
    { clickCount++; clickedIndex = data->ListItemData.SelectedIndex; clickedParam = param; clickedType = type; }

    [Theory]
    [InlineData("Pon,Pass", ActionKind.Pass, 1)]
    [InlineData("Pon,Pass", ActionKind.Pon, 0)]
    [InlineData("Chi,Pass", ActionKind.Chi, 0)]
    [InlineData("Ron,Chi,Pass", ActionKind.Ron, 0)]
    [InlineData("Tsumo,Pass", ActionKind.Tsumo, 0)]
    [InlineData("Riichi,Pass", ActionKind.Riichi, 0)]
    [InlineData("Kan,Pass", ActionKind.AnKan, 0)]
    [InlineData("Kan,Pass", ActionKind.MinKan, 0)]
    [InlineData("Kan,Pass", ActionKind.ShouMinKan, 0)]
    [InlineData("九种幺九倒牌,放弃", ActionKind.Kyushukyuhai, 0)]
    public void Dispatcher_uses_registered_click_not_retained_parent_callback_for_all_intents(string labels, ActionKind intent, int expected)
    {
        using var f = new Fixture(labels.Split(','));
        f.PrepareNativeRecorder();
        clickCount = 0;
        var dispatcher = f.Dispatcher(() => true);
        Assert.Equal(InputDispatcher.DispatchResult.Submitted, dispatcher.DispatchCallOption(7, intent));
        Assert.Equal(1, clickCount);
        Assert.Equal(expected, clickedIndex);
        Assert.Equal(317, clickedParam);
        Assert.Equal(AtkEventType.ListItemClick, clickedType);
        Assert.Equal("registered-list-click", dispatcher.LastCallDispatch!.Route);
        Assert.Equal(new[] { "Pass", "Pon", "Pass" }, dispatcher.LastCallDispatch.ParentLabels);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Missing_registered_event_never_falls_back_to_old_callback(bool allowed)
    {
        using var f = new Fixture(["Pon", "Pass"]);
        f.PrepareNativeRecorder();
        f.Shell->AtkEventManager.Event = null;
        clickCount = 0;
        var result = f.Dispatcher(() => allowed).DispatchCallOption(1, ActionKind.Pass);
        Assert.Equal(allowed ? InputDispatcher.DispatchResult.HookFailed : InputDispatcher.DispatchResult.OperationBlocked, result);
        Assert.Equal(0, clickCount);
    }

    [Fact]
    public void Full_addon_path_resolves_active_row_and_rechecks_changed_text()
    {
        using var f = new Fixture(["Ron", "Chi", "Pass"]);
        Assert.True(CnCallMenuResolver.TryResolve(f.Unit, ActionKind.Ron, out int option, out _));
        Assert.Equal(0, option);
        ((AtkTextNode*)f.Row(0)->UldManager.NodeList[0])->NodeText.StringPtr.Value[0] = (byte)'X';
        Assert.False(CnCallMenuResolver.TryResolve(f.Unit, ActionKind.Ron, out option, out _));
        Assert.Equal(-1, option);
    }
    [Fact]
    public void Captured_ron_chi_pass_menu_resolves_ron_zero_instead_of_upstream_chi_index_one()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "ron-dispatched-as-chi-20260925.json")));
        var menu = doc.RootElement.GetProperty("Menu").Deserialize<PublicActionMenuObservation>()!;
        using var f = new Fixture(menu.Rows.Select(r => r.Action.ToString()).ToArray());
        var legal = new Mahjong.Core.LegalActions(Mahjong.Core.ActionFlags.Ron | Mahjong.Core.ActionFlags.Chi | Mahjong.Core.ActionFlags.Pass, [], [], [], []);
        Assert.Equal(1, AutoPlayLoop.ComputeAcceptIndex(ActionKind.Ron, legal, null)); // retained international order
        Assert.True(f.Resolve(ActionKind.Ron, out int option)); Assert.Equal(0, option);
        Assert.True(f.Resolve(ActionKind.Chi, out option)); Assert.Equal(1, option);
        Assert.True(f.Resolve(ActionKind.Pass, out option)); Assert.Equal(2, option);
    }

    [Theory]
    [InlineData("九种幺九倒牌,放弃", ActionKind.Kyushukyuhai, 0)]
    [InlineData("放弃,九种幺九倒牌", ActionKind.Kyushukyuhai, 1)]
    [InlineData("九种幺九倒牌,放弃", ActionKind.Pass, 1)]
    [InlineData("Ron,Chi,Pass", ActionKind.Ron, 0)]
    [InlineData("Chi,Ron,Pass", ActionKind.Ron, 1)]
    [InlineData("Ron,Pon,Kan,Pass", ActionKind.Pon, 1)]
    [InlineData("Ron,Pon,Kan,Pass", ActionKind.MinKan, 2)]
    [InlineData("Ron,Pon,Chi,Kan,Pass", ActionKind.Ron, 0)]
    [InlineData("Ron,Pon,Chi,Kan,Pass", ActionKind.Chi, 2)]
    [InlineData("Tsumo,Kan,Riichi,Pass", ActionKind.AnKan, 1)]
    [InlineData("Tsumo,Kan,Riichi,Pass", ActionKind.ShouMinKan, 1)]
    [InlineData("Tsumo,Kan,Riichi,Pass", ActionKind.Riichi, 2)]
    [InlineData("Tsumo,Kan,Pass", ActionKind.Tsumo, 0)]
    [InlineData("自摸,杠,放弃", ActionKind.Tsumo, 0)]
    [InlineData("和牌,吃,放弃", ActionKind.Ron, 0)]
    [InlineData("碰,放弃", ActionKind.Pass, 1)]
    [InlineData("放弃,吃,和牌", ActionKind.Ron, 2)]
    public void Uses_current_labels_for_every_intent_and_any_order(string labels, ActionKind intent, int expected)
    {
        using var f = new Fixture(labels.Split(','));
        Assert.True(f.Resolve(intent, out int option)); Assert.Equal(expected, option);
    }

    [Theory]
    [InlineData("missing")][InlineData("disabled")][InlineData("hidden")][InlineData("renderer-disabled")]
    [InlineData("update")][InlineData("scroll")][InlineData("interaction")][InlineData("duplicate")]
    [InlineData("binding")][InlineData("owner")][InlineData("unknown")][InlineData("missing-text")]
    [InlineData("invalid-utf8")][InlineData("reparented")][InlineData("offscreen")][InlineData("cancel")]
    public void Changed_or_ambiguous_menu_never_falls_back_to_an_index(string reason)
    {
        using var f = new Fixture(reason switch { "missing" => ["Chi", "Pass"], "duplicate" => ["Ron", "Ron", "Pass"],
            "unknown" => ["Ron", "Other", "Pass"], "cancel" => ["Ron", "Cancel"], _ => ["Ron", "Chi", "Pass"] });
        switch (reason)
        {
            case "disabled": f.List->ItemRendererList[0].IsDisabled = true; break;
            case "hidden": f.Row(0)->OwnerNode->NodeFlags &= ~NodeFlags.Visible; break;
            case "renderer-disabled": f.Row(0)->OwnerNode->NodeFlags &= ~NodeFlags.Enabled; break;
            case "update": f.List->IsUpdatePending = true; break;
            case "scroll": f.List->IsScrollRefreshPending = true; break;
            case "interaction": f.List->IsItemInteractionEnabled = false; break;
            case "binding": f.Row(0)->ListItemIndex = 1; break;
            case "owner": f.List->OwnerNode = null; break;
            case "missing-text": f.Row(0)->UldManager.NodeListCount = 0; break;
            case "invalid-utf8": ((AtkTextNode*)f.Row(0)->UldManager.NodeList[0])->NodeText.StringPtr.Value[0] = 255; break;
            case "reparented": f.Row(0)->OwnerNode->ParentNode = null; break;
            case "offscreen": f.List->FirstVisibleItemIndex = 1; break;
        }
        Assert.False(f.Resolve(reason == "cancel" ? ActionKind.Pass : ActionKind.Ron, out int option));
        Assert.Equal(-1, option);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> allocations = [];
        internal readonly AtkComponentList* List;
        internal readonly AtkComponentNode* Shell;
        internal readonly AtkUnitBase* Unit;
        internal Fixture(string[] labels)
        {
            List = Allocate<AtkComponentList>(); Shell = Allocate<AtkComponentNode>();
            Unit = Allocate<AtkUnitBase>(); Unit->IsVisible = true;
            var host = Allocate<AtkComponentNode>(); Visible((AtkResNode*)host); host->Type = (NodeType)1052; host->NodeId = 104;
            host->Component = Allocate<AtkComponentBase>(); host->Component->OwnerNode = host;
            Unit->UldManager.NodeList = (AtkResNode**)Allocate<nint>(); Unit->UldManager.NodeList[0] = (AtkResNode*)host; Unit->UldManager.NodeListCount = 1;
            host->Component->UldManager.NodeList = (AtkResNode**)Allocate<nint>();
            host->Component->UldManager.NodeList[0] = (AtkResNode*)Shell; host->Component->UldManager.NodeListCount = 1;
            Shell->NodeId = 3; Shell->ParentNode = (AtkResNode*)host;
            Visible((AtkResNode*)Shell); Shell->Type = (NodeType)1030; Shell->Component = (AtkComponentBase*)List;
            List->OwnerNode = Shell; List->ListLength = List->AllocatedItemRendererListLength = labels.Length;
            List->IsItemInteractionEnabled = true; List->ItemRendererList = Allocate<AtkComponentList.ListItem>(labels.Length);
            for (int i = 0; i < labels.Length; i++)
            {
                var row = Allocate<AtkComponentListItemRenderer>(); var owner = Allocate<AtkComponentNode>();
                row->OwnerNode = owner; owner->Component = (AtkComponentBase*)row; owner->Type = (NodeType)1029;
                Visible((AtkResNode*)owner); owner->ParentNode = (AtkResNode*)Shell;
                row->ListItemIndex = i; List->ItemRendererList[i].AtkComponentListItemRenderer = row;
                var text = Allocate<AtkTextNode>(); Visible((AtkResNode*)text); text->NodeId = 4; text->Type = NodeType.Text;
                text->ParentNode = (AtkResNode*)owner;
                byte[] label = Encoding.UTF8.GetBytes(labels[i] + "\0");
                text->NodeText.StringPtr = Allocate<byte>(label.Length); label.CopyTo(new Span<byte>(text->NodeText.StringPtr, label.Length));
                text->NodeText.BufSize = text->NodeText.BufUsed = label.Length;
                row->UldManager.NodeList = (AtkResNode**)Allocate<nint>(); row->UldManager.NodeList[0] = (AtkResNode*)text;
                row->UldManager.NodeListCount = 1;
            }
        }
        internal AtkComponentListItemRenderer* Row(int i) => List->ItemRendererList[i].AtkComponentListItemRenderer;
        internal void PrepareNativeRecorder()
        {
            var vtable = Allocate<nint>(3);
            vtable[2] = (nint)(delegate* unmanaged<AtkEventListener*, AtkEventType, int, AtkEvent*, AtkEventData*, void>)&Receive;
            *(nint**)Unit = vtable;
            var e = Allocate<AtkEvent>(); e->State.EventType = AtkEventType.ListItemClick;
            e->Param = 317; e->Listener = (AtkEventListener*)Unit; e->Target = (AtkEventTarget*)Shell;
            Shell->AtkEventManager.Event = e;
            Unit->AtkValues = Allocate<AtkValue>(4); Unit->AtkValuesCount = 4;
            Unit->AtkValues[0].Type = AtkValueType.Int; Unit->AtkValues[0].Int = 15;
            string[] labels = ["Pass", "Pon", "Pass"];
            for (int i = 0; i < labels.Length; i++)
            {
                var text = Encoding.UTF8.GetBytes(labels[i] + "\0");
                Unit->AtkValues[i + 1].Type = AtkValueType.String;
                Unit->AtkValues[i + 1].String = Allocate<byte>(text.Length);
                text.CopyTo(new Span<byte>(Unit->AtkValues[i + 1].String, text.Length));
            }
        }
        internal InputDispatcher Dispatcher(Func<bool> allowed)
        {
            var gui = RuntimeModeLifecycleTests.ServiceProxy.Create<global::Dalamud.Plugin.Services.IGameGui>((method, _) =>
                method.Name == "GetAddonByName" ? Activator.CreateInstance(method.ReturnType, (nint)Unit) : throw new NotSupportedException());
            return new(new Mahjong.Plugin.Dalamud.GameState.MahjongAddon(gui, new Mahjong.Plugin.Dalamud.Tests.Stubs.StubPluginLog()), canOperate: allowed);
        }
        internal bool Resolve(ActionKind kind, out int option) => CnCallMenuResolver.TryResolve(List, (AtkResNode*)Shell, kind, out option, out _);
        private static void Visible(AtkResNode* node) { node->NodeFlags = NodeFlags.Visible | NodeFlags.Enabled; node->Color.A = 255; }
        private T* Allocate<T>(int count = 1) where T : unmanaged
        { var p = (T*)NativeMemory.AllocZeroed((nuint)(sizeof(T) * count)); allocations.Add((nint)p); return p; }
        public void Dispose() { foreach (nint p in allocations) NativeMemory.Free((void*)p); }
    }
}
