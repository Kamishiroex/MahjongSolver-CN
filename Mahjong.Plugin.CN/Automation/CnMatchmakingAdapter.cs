using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace Mahjong.Plugin.CN.Automation;

/// <summary>Framework-thread-only. Uses the pinned CN structs, without private signatures or callback IDs.</summary>
internal static unsafe class CnMatchmakingAdapter
{
    internal const string ConfirmUldHash = "E04679EE2A0829C80491DA648EEAA27442014D991DE84490EF5524AE01A6B4BE";
    internal static bool MatchesSheet(MahjongDuty duty, ContentFinderCondition row) =>
        row.RowId == duty.Id && row.ContentType.RowId == 19 && row.Content.RowId == duty.ContentId &&
        row.TerritoryType.RowId == 831 && row.ContentLinkType == 1 && row.IsInDutyFinder &&
        row.Name.ToString() == "多玛方城战：" + duty.Name;

    internal static (QueuePhase Phase, bool Matches, bool PopMatches) ReadQueue(uint selected)
    {
        var finder = ContentsFinder.Instance();
        if (finder == null) throw new InvalidOperationException("QUEUE_STATE_UNAVAILABLE");
        ref var info = ref finder->QueueInfo;
        if ((int)info.QueueState > 5) throw new InvalidOperationException("QUEUE_STATE_UNKNOWN");
        int count = 0;
        bool matches = true;
        foreach (var entry in info.QueuedEntries)
        {
            if (entry.ContentType == ContentsType.None) continue;
            count++;
            matches &= entry.ContentType == ContentsType.Regular && entry.Id == selected;
        }
        return ((QueuePhase)info.QueueState, matches && count == 1,
            info.PoppedQueueEntry.ContentType == ContentsType.Regular && info.PoppedQueueEntry.Id == selected);
    }

    internal static void Queue(uint selected)
    {
        var finder = ContentsFinder.Instance();
        if (finder == null || finder->QueueInfo.QueueState != FFXIVClientStructs.FFXIV.Client.Enums.ContentsFinderQueueState.None)
            throw new InvalidOperationException("QUEUE_ALREADY_ACTIVE");
        if (finder->IsUnrestrictedParty || finder->IsExplorerMode)
            throw new InvalidOperationException("请先关闭解除人数限制及探索模式，再报名麻将。");
        var penalties = FFXIVClientStructs.FFXIV.Client.Game.UI.InstanceContent.Instance();
        if (penalties == null || penalties->GetPenaltyRemainingInMinutes(0) != 0)
            throw new InvalidOperationException("排队惩罚尚未结束或惩罚状态不可读。");
        // QueueDuties consumes ContentFinderCondition row IDs (not Content/territory IDs).
        // The client/server retain their normal availability/rank/party validation.
        finder->QueueInfo.QueueDuties(&selected, 1);
    }

    internal static bool CanAccept(nint address) => FindCommenceEvent((AddonContentsFinderConfirm*)address) != null;

    internal static void Accept(nint address)
    {
        var registered = FindCommenceEvent((AddonContentsFinderConfirm*)address);
        if (registered == null) throw new InvalidOperationException("匹配确认按钮没有唯一、可用的点击事件。");
        // Dispatch the actual event registered to CommenceButton. No guessed callback parameter.
        var copy = *registered;
        copy.NextEvent = null;
        AtkEventData data = default;
        copy.Listener->ReceiveEvent(AtkEventType.ButtonClick, (int)copy.Param, &copy, &data);
    }

    internal static AtkEvent* FindCommenceEvent(AddonContentsFinderConfirm* addon)
    {
        if (addon == null || !addon->IsVisible || addon->CommenceButton == null) return null;
        var node = addon->CommenceButton->OwnerNode;
        if (node == null || !addon->CommenceButton->IsEnabled ||
            !node->NodeFlags.HasFlag(NodeFlags.Visible)) return null;
        AtkEvent* result = null;
        var ev = node->AtkEventManager.Event;
        int count = 0;
        for (; ev != null && count++ < 32; ev = ev->NextEvent)
        {
            if (ev->State.EventType != AtkEventType.ButtonClick) continue;
            if (ev->Listener != (AtkEventListener*)addon || ev->Param > int.MaxValue ||
                ev->State.StateFlags.HasFlag(AtkEventStateFlags.IsGlobalEvent) || result != null) return null;
            result = ev;
        }
        return ev == null ? result : null; // Reject cycles/unbounded lists.
    }
}
