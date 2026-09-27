using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Automation;
using System.Text;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed unsafe class KyushuConfirmationTests
{
    [Theory]
    [InlineData("valid")][InlineData("wrong-owner")][InlineData("hidden")]
    [InlineData("different-prompt")][InlineData("disabled")][InlineData("duplicate")]
    [InlineData("cycle")][InlineData("wrong-listener")][InlineData("global")]
    public void Confirmation_requires_exact_question_owner_enabled_button_and_unique_registered_event(string scenario)
    {
        AtkUnitBase owner = default; owner.Id = 81; owner.IsVisible = true;
        AddonSelectYesno dialog = default; dialog.IsVisible = true; dialog.ParentId = 81;
        AtkTextNode prompt = default; prompt.NodeFlags = NodeFlags.Visible; prompt.Color.A = 255;
        var bytes = Encoding.UTF8.GetBytes((scenario == "different-prompt" ? "确认退出游戏？" : "要流局吗？") + "\0");
        fixed (byte* b = bytes)
        {
            prompt.NodeText.StringPtr = b; prompt.NodeText.BufUsed = prompt.NodeText.BufSize = bytes.Length;
            dialog.PromptText = &prompt;
            AtkComponentNode buttonNode = default; buttonNode.NodeFlags = NodeFlags.Visible | NodeFlags.Enabled; buttonNode.Color.A = 255;
            AtkComponentButton button = default; button.OwnerNode = &buttonNode; dialog.YesButton = &button;
            AtkEvent click = default; click.Listener = (AtkEventListener*)&dialog;
            click.Param = 317; click.State.EventType = AtkEventType.ButtonClick;
            buttonNode.AtkEventManager.Event = &click;
            AtkEvent duplicate = click;
            switch (scenario)
            {
                case "wrong-owner": dialog.ParentId = 19; break;
                case "hidden": prompt.NodeFlags = 0; break;
                case "disabled": buttonNode.NodeFlags = NodeFlags.Visible; break;
                case "duplicate": click.NextEvent = &duplicate; break;
                case "cycle": click.NextEvent = &click; break;
                case "wrong-listener": click.Listener = (AtkEventListener*)&owner; break;
                case "global": click.State.StateFlags = AtkEventStateFlags.IsGlobalEvent; break;
            }
            Assert.Equal(scenario == "valid", CnKyushuConfirmation.Find(&owner,&dialog) == &click);
        }
    }
}
