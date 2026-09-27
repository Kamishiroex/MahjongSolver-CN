namespace Mahjong.Plugin.CN.Automation;

internal sealed record TableAutomationOptions(bool AutoQueue = false, bool AutoStart = false, uint DutyId = 766,
    bool AutoAdvanceAfterHand = true, int MatchLimit = 0, bool KeepAutomaticBetweenHands = false);
internal sealed record MahjongDuty(uint Id, string Name, bool Friends, uint ContentId);

internal static class MahjongDuties
{
    // Read from the CN 2026.09.15 ContentFinderCondition sheet; see docs/cn/AUTO-QUEUE.md.
    internal static readonly MahjongDuty[] All =
    [
        new(766, "东风战一般桌（段位战）", false, 61005),
        new(767, "东风战有段桌（段位战）", false, 61006),
        new(768, "东风战4人亲友桌（带食断）", true, 61007),
        new(769, "东风战4人亲友桌（不带食断）", true, 61008),
        new(643, "半庄战一般桌（段位战）", false, 61001),
        new(644, "半庄战有段桌（段位战）", false, 61002),
        new(645, "半庄战4人亲友桌（带食断）", true, 61003),
        new(650, "半庄战4人亲友桌（不带食断）", true, 61004),
    ];
    internal static MahjongDuty? Find(uint id) => All.SingleOrDefault(x => x.Id == id);
    internal static string? PartyError(MahjongDuty duty, int members, bool alliance, bool leader) =>
        alliance ? "不能以团队报名麻将。" : duty.Friends
            ? members != 4 ? "亲友桌需要4人小队。" : !leader ? "亲友桌需要由小队队长报名。" : null
            : members > 1 ? "段位战需要单人报名，请先离开小队。" : null;
}

internal enum QueuePhase { None, Pending, Queued, Ready, Accepted, InContent }
internal enum TableAutomationAction { None, Queue, Accept, StartPlay, LeaveCompletedMatch }
internal readonly record struct TableAutomationObservation(
    bool LoggedIn, bool TableVisible, bool InDuty, bool Busy, bool Playing,
    QueuePhase Queue, bool QueueMatches, bool PopMatches, bool AcceptAvailable,
    uint DutyId = 766, bool CanLeave = false);

/// <summary>Pure, monotonic-time coordinator. Never retries an ambiguous native submission.</summary>
internal sealed class TableAutomation
{
    internal bool Armed { get; private set; }
    internal string Status { get; private set; } = "自动排队 / 进桌开打未启动。";
    internal TableAutomationOptions Options { get; private set; } = new();
    internal int CompletedMatches { get; private set; }
    internal bool MatchCompleted { get; private set; }
    internal string Progress => Options.MatchLimit == 0 ? $"已完成 {CompletedMatches} 场 / 无限循环" :
        $"已完成 {CompletedMatches} / {Options.MatchLimit} 场";
    private double readySince = double.NaN, absentSince = double.NaN, submittedAt, acceptAt;
    private bool tableSeen, startHandled, submitted, queueSeen, accepted;
    private double earliestQueue;
    private double completedAt, leaveAt;
    private bool leaveSubmitted;

    internal void Arm(TableAutomationOptions options, double now)
    {
        Options = options;
        Armed = (options.AutoQueue || options.AutoStart) && MahjongDuties.Find(options.DutyId) is not null &&
            options.MatchLimit is >= 0 and <= 9999;
        CompletedMatches = 0;
        MatchCompleted = leaveSubmitted = false;
        readySince = absentSince = double.NaN;
        tableSeen = startHandled = submitted = queueSeen = accepted = false;
        earliestQueue = now + 3;
        Status = Armed ? "已启动，等待游戏就绪。" : "请先选择自动排队或进桌自动开打。";
    }

    // IDutyState.DutyCompleted is the completion evidence, never state 29 (one hand),
    // a disappearing addon, a score estimate or a successful leave submission.
    internal bool ObserveMatchCompleted(uint dutyId, double now)
    {
        if (!Armed || !tableSeen || MatchCompleted || dutyId != Options.DutyId) return false;
        MatchCompleted = true;
        CompletedMatches++;
        completedAt = now;
        Status = "整场对局已完成，等待结算和退桌。";
        return true;
    }

    internal void Disarm(string reason)
    {
        Armed = false;
        Status = reason;
    }

    // Explicit same-task resume preserves counters, owned queue evidence and match identity.
    internal void Resume(double now)
    {
        Armed = Options.AutoQueue || Options.AutoStart;
        readySince = double.NaN;
        earliestQueue = Math.Max(earliestQueue, now + 3);
        Status = "继续同一任务；保留场数与已提交的排队状态。";
    }

    internal TableAutomationAction Tick(double now, TableAutomationObservation o)
    {
        if (!Armed) return TableAutomationAction.None;
        if (!o.LoggedIn) return Fail("已登出：自动排队和进桌开打已停止。");
        if (MatchCompleted && (o.TableVisible || o.InDuty || o.Busy || o.Queue == QueuePhase.InContent))
        {
            // Retain completion through loading/hidden UI; never start playing a completed table.
            if (leaveSubmitted && now - leaveAt > 30)
                return Fail("整场已完成，但退桌30秒未生效；已停止循环，请手动退桌后重新启动。");
            if (Options.AutoQueue && !leaveSubmitted && !o.Busy && o.InDuty &&
                o.DutyId == Options.DutyId && o.CanLeave && now - completedAt >= 8)
            {
                leaveSubmitted = true;
                leaveAt = now;
                Status = "整场已完成，正在退桌；不会退出未完成的对局。";
                return TableAutomationAction.LeaveCompletedMatch;
            }
            if (Options.AutoQueue && !leaveSubmitted && now - completedAt > 60)
                return Fail("整场已完成，但游戏60秒内未允许退桌；请手动退桌后重新启动。");
            Status = "整场已完成，等待结算退桌。";
            return TableAutomationAction.None;
        }
        if (o.TableVisible)
        {
            if (o.DutyId != Options.DutyId)
                return Fail("当前牌桌不是所选桌型；自动功能已停止，请选择对应桌型后启动。");
            tableSeen = true;
            submitted = queueSeen = accepted = false;
            absentSince = double.NaN;
            if (o.Busy) readySince = double.NaN;
            else if (double.IsNaN(readySince)) readySince = now;
            if (o.Playing) startHandled = true; // Never replace a manually chosen mode.
            if (Options.AutoStart && !startHandled && !o.Busy && now - readySince >= 2)
            {
                startHandled = true; // Reserve BEFORE invoking runtime; a failure must not loop.
                Status = "正在按所选决策来源启动自动打牌。";
                return TableAutomationAction.StartPlay;
            }
            Status = o.Busy ? "牌桌正在加载，等待界面就绪。" :
                o.Playing ? "已入桌；保留当前打牌模式。" :
                Options.AutoStart && !startHandled ? "等待牌桌连续就绪2秒，再自动开打。" :
                Options.AutoStart ? "本桌已处理自动启动；不会重复开启。" : "已入桌；请自行选择手动提醒或自动打牌。";
            return TableAutomationAction.None;
        }
        readySince = double.NaN;
        if (o.InDuty || o.Busy || o.Queue == QueuePhase.InContent)
        {
            absentSince = double.NaN;
            Status = "等待对局、结算或场景切换完成。";
            return TableAutomationAction.None;
        }
        if (tableSeen)
        {
            if (double.IsNaN(absentSince)) absentSince = now;
            if (now - absentSince < 5) return TableAutomationAction.None;
            if (!MatchCompleted)
                return Fail("牌桌已退出，但未收到本场完成事件；不计入完成场数，循环已停止。请查看事件日志。");
            tableSeen = startHandled = false;
            MatchCompleted = leaveSubmitted = false;
            if (Options.MatchLimit > 0 && CompletedMatches >= Options.MatchLimit)
                return Fail($"已完成设定的 {Options.MatchLimit} 场，自动排队与进桌开打已结束。");
            earliestQueue = now + 5;
        }
        if (!Options.AutoQueue) { Status = "等待进入下一张牌桌。"; return TableAutomationAction.None; }
        if (o.Queue != QueuePhase.None)
        {
            if (!submitted || !o.QueueMatches) return Fail("检测到非本功能发起的排队或桌型改变；自动排队已停止，不接管其他报名。");
            queueSeen = true;
            if (o.Queue == QueuePhase.Ready)
            {
                if (!o.PopMatches) return Fail("匹配确认桌型与选择不一致，已停止自动确认。");
                if (!accepted && o.AcceptAvailable)
                {
                    accepted = true;
                    acceptAt = now;
                    Status = "已提交进入确认，等待入桌。";
                    return TableAutomationAction.Accept;
                }
                if (accepted && now - acceptAt > 15) return Fail("进入确认未生效；已停止重复点击，请检查游戏提示。");
                Status = accepted ? "等待游戏接受进入确认。" : "等待匹配确认按钮可用。";
            }
            else Status = o.Queue == QueuePhase.Accepted ? "匹配已确认，等待进入。" : "正在匹配所选桌型。";
            return TableAutomationAction.None;
        }
        if (submitted)
        {
            if (queueSeen) return Fail("报名已被取消或匹配已结束；自动排队已停止，请重新启动。");
            if (now - submittedAt > 10) return Fail("游戏未接受报名；请检查解锁、段位、队伍和惩罚提示，再重新启动。");
            Status = "已提交报名，等待游戏确认。";
            return TableAutomationAction.None;
        }
        if (now < earliestQueue) { Status = "等待排队冷却。"; return TableAutomationAction.None; }
        submitted = true;
        submittedAt = now;
        Status = "正在报名所选麻将桌。";
        return TableAutomationAction.Queue;
    }

    private TableAutomationAction Fail(string reason) { Disarm(reason); return TableAutomationAction.None; }
}
