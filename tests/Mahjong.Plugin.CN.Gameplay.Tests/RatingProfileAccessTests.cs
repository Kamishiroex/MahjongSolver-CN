using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Readers;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed unsafe class RatingProfileAccessTests
{
    // CN 2026.09.15 observed navigation: GoldSaucerInfo, radio node 8, text 方城战,
    // ButtonClick, root listener and node target. Personal profile values are excluded.
    [Fact] public void Uses_live_root_name_and_label_instead_of_guessed_GSInfo_and_long_label()
    {
        using var f = new Fixture();
        var lookedUp = new List<string>();
        var access = new CnRatingProfileAccess(name => { lookedUp.Add(name); return name == "GoldSaucerInfo" ? (nint)f.Root : 0; });
        Assert.True(access.IsOpen); Assert.Equal(["GoldSaucerInfo"], lookedUp);
        Assert.Equal((nint)f.Radio, (nint)CnRatingProfileAccess.FindMahjongTab(f.Root));
        Assert.True(CnRatingProfileAccess.TryPrepareTabClick(f.Root, f.Radio, out var click));
        Assert.Equal(AtkEventType.ButtonClick, click.State.EventType); Assert.Equal(6u, click.Param);
        Assert.Equal((nint)f.Root, (nint)click.Listener); Assert.Equal((nint)f.Node, (nint)click.Target);
    }

    [Theory] [InlineData(6u)] [InlineData(41u)]
    public void Copies_registered_parameter_without_inventing_a_tab_callback(uint parameter)
    {
        using var f = new Fixture(); f.Event->Param = parameter;
        Assert.True(CnRatingProfileAccess.TryPrepareTabClick(f.Root, f.Radio, out var click));
        Assert.Equal(parameter, click.Param); Assert.Equal(0, (nint)click.NextEvent);
    }

    [Theory]
    [InlineData("old-label")] [InlineData("wrong-type")] [InlineData("hidden")]
    [InlineData("hidden-parent")] [InlineData("disabled")] [InlineData("wrong-owner")]
    [InlineData("wrong-listener")] [InlineData("wrong-target")] [InlineData("cycle")]
    [InlineData("duplicate-event")] [InlineData("global")] [InlineData("highlight-only")]
    [InlineData("unready")] [InlineData("invalid-length")] [InlineData("duplicate-tab")]
    public void Rejects_ambiguous_or_unavailable_navigation(string reason)
    {
        using var f = new Fixture();
        switch (reason)
        {
            case "old-label": f.Text("多玛方城战"); break;
            case "wrong-type": f.Info->ComponentType = ComponentType.Button; break;
            case "hidden": f.Node->NodeFlags &= ~NodeFlags.Visible; break;
            case "hidden-parent": f.Parent->NodeFlags &= ~NodeFlags.Visible; break;
            case "disabled": f.Node->NodeFlags &= ~NodeFlags.Enabled; break;
            case "wrong-owner": f.Radio->OwnerNode = null; break;
            case "wrong-listener": f.Event->Listener = (AtkEventListener*)f.Radio; break;
            case "wrong-target": f.Event->Target = (AtkEventTarget*)f.Parent; break;
            case "cycle": f.Event->NextEvent = f.Event; break;
            case "duplicate-event": f.ExtraEvent[0] = f.Event[0]; f.Event->NextEvent = f.ExtraEvent; break;
            case "global": f.Event->State.StateFlags = AtkEventStateFlags.IsGlobalEvent; break;
            case "highlight-only": f.Event->State.EventType = AtkEventType.ButtonPress; break;
            case "unready": f.Root->Flags1A1 = 0; break;
            case "invalid-length": f.TextNode->NodeText.BufUsed = 1024; break;
            case "duplicate-tab": f.Root->UldManager.NodeList[1] = (AtkResNode*)f.Node; f.Root->UldManager.NodeListCount = 2; break;
        }
        Assert.False(CnRatingProfileAccess.TryPrepareTabClick(f.Root, f.Radio, out _));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<nint> memory = [];
        internal readonly AtkUnitBase* Root;
        internal readonly AtkComponentNode* Node;
        internal readonly AtkResNode* Parent;
        internal readonly AtkComponentRadioButton* Radio;
        internal readonly AtkUldComponentInfo* Info;
        internal readonly AtkTextNode* TextNode;
        internal readonly AtkEvent* Event;
        internal readonly AtkEvent* ExtraEvent;
        internal Fixture()
        {
            Root = Alloc<AtkUnitBase>(); Node = Alloc<AtkComponentNode>(); Parent = Alloc<AtkResNode>();
            Radio = Alloc<AtkComponentRadioButton>(); Info = Alloc<AtkUldComponentInfo>(); TextNode = Alloc<AtkTextNode>();
            Event = Alloc<AtkEvent>(); ExtraEvent = Alloc<AtkEvent>();
            Root->IsVisible = true; Root->Flags1A1 = 1; Root->Alpha = 255;
            Root->UldManager.NodeList = (AtkResNode**)Alloc<nint>(2); Root->UldManager.NodeList[0] = (AtkResNode*)Node;
            Root->UldManager.NodeListCount = 1;
            Node->NodeId = 8; Node->Type = (NodeType)1006; Node->ParentNode = Parent;
            Node->Color.A = Parent->Color.A = 255; Node->NodeFlags = Parent->NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;
            Node->Component = (AtkComponentBase*)Radio; Radio->OwnerNode = Node;
            Radio->UldManager.BaseType = AtkUldManagerBaseType.Component;
            Radio->UldManager.Objects = (AtkUldObjectInfo*)Info; Info->ComponentType = ComponentType.RadioButton;
            Radio->ButtonTextNode = TextNode; TextNode->Type = NodeType.Text; Text("方城战");
            Node->AtkEventManager.Event = Event; Event->State.EventType = AtkEventType.ButtonClick;
            Event->Param = 6; Event->Listener = (AtkEventListener*)Root; Event->Target = (AtkEventTarget*)Node;
        }
        internal void Text(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value + "\0"); var p = Alloc<byte>(bytes.Length);
            bytes.CopyTo(new Span<byte>(p, bytes.Length)); TextNode->NodeText.StringPtr = p;
            TextNode->NodeText.BufUsed = bytes.Length;
        }
        private T* Alloc<T>(int count = 1) where T : unmanaged
        { var p = (T*)NativeMemory.AllocZeroed((nuint)(sizeof(T) * count)); memory.Add((nint)p); return p; }
        public void Dispose() { foreach (nint p in memory) NativeMemory.Free((void*)p); }
    }
}
