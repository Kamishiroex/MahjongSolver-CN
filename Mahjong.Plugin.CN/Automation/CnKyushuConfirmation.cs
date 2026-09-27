using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Automation;

/// <summary>Optional confirmation after our own submitted declaration; no general yes-clicker.</summary>
internal static unsafe class CnKyushuConfirmation
{
    internal static AtkEvent* Find(AtkUnitBase* owner, AddonSelectYesno* dialog)
    {
        if (owner == null || owner->Id == 0 || !owner->IsVisible || dialog == null || !dialog->IsVisible ||
            (dialog->ParentId != owner->Id && dialog->HostId != owner->Id) ||
            !CnPublicListClick.Visible((AtkResNode*)dialog->PromptText) ||
            !ExactPrompt(dialog->PromptText) || dialog->YesButton == null || !dialog->YesButton->IsEnabled ||
            !CnPublicListClick.Visible((AtkResNode*)dialog->YesButton->OwnerNode)) return null;
        AtkEvent* found = null;
        var seen = new HashSet<nint>();
        for (var e = dialog->YesButton->OwnerNode->AtkEventManager.Event; e != null; e = e->NextEvent)
        {
            if (seen.Count >= 32 || !seen.Add((nint)e)) return null;
            if (e->State.EventType != AtkEventType.ButtonClick) continue;
            if (found != null || e->Listener != (AtkEventListener*)dialog || e->Param > int.MaxValue ||
                e->State.StateFlags.HasFlag(AtkEventStateFlags.IsGlobalEvent)) return null;
            found = e;
        }
        return found;
    }

    private static bool ExactPrompt(AtkTextNode* text)
    {
        // CN 2026.09.15 EmjAddon row32, read from the local sheet. No result text
        // such as 四杠散了/三家和/荒牌平局 can authorize an input.
        ref var s = ref text->NodeText;
        ReadOnlySpan<byte> expected = "要流局吗？"u8;
        return s.StringPtr.Value != null && s.BufUsed == expected.Length + 1 && s.BufSize >= s.BufUsed &&
            s.BufSize <= 4096 && s.StringPtr.Value[expected.Length] == 0 &&
            new ReadOnlySpan<byte>(s.StringPtr.Value, expected.Length).SequenceEqual(expected);
    }
}
