using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Automation;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class MatchmakingEventBindingTests
{
    [Fact]
    public unsafe void Commence_requires_unique_enabled_local_event_and_bounded_chain()
    {
        AddonContentsFinderConfirm addon = default;
        AtkComponentButton button = default;
        AtkComponentNode owner = default;
        addon.AtkUnitBase.IsVisible = true;
        addon.CommenceButton = &button; button.OwnerNode = &owner;
        owner.NodeFlags = NodeFlags.Visible | NodeFlags.Enabled;
        AtkEvent first = default, second = default;
        first.Listener = (AtkEventListener*)&addon;
        first.Param = 817; // Arbitrary: binding must read the event, never a magic callback number.
        first.State.EventType = AtkEventType.ButtonClick;
        owner.AtkEventManager.Event = &first;
        Assert.True(CnMatchmakingAdapter.FindCommenceEvent(&addon) == &first);
        first.NextEvent = &second; second = first; second.NextEvent = null;
        Assert.True(CnMatchmakingAdapter.FindCommenceEvent(&addon) == null);
        first.NextEvent = null; first.State.StateFlags = AtkEventStateFlags.IsGlobalEvent;
        Assert.True(CnMatchmakingAdapter.FindCommenceEvent(&addon) == null);
        first.State.StateFlags = 0; first.NextEvent = &first;
        Assert.True(CnMatchmakingAdapter.FindCommenceEvent(&addon) == null);
        first.NextEvent = null; owner.NodeFlags = NodeFlags.Visible;
        Assert.True(CnMatchmakingAdapter.FindCommenceEvent(&addon) == null);
        owner.NodeFlags |= NodeFlags.Enabled; first.Listener = (AtkEventListener*)&button;
        Assert.True(CnMatchmakingAdapter.FindCommenceEvent(&addon) == null);
    }
}
