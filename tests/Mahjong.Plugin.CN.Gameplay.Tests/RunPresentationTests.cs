using Mahjong.Cn.Tasks;
using Mahjong.Plugin.CN.Automation;
using Mahjong.Plugin.CN.Presentation;
using Mahjong.Plugin.Dalamud;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class RunPresentationTests
{
    [Fact]
    public void Waiting_for_other_players_and_recovery_do_not_claim_user_pause()
    {
        var waiting = RunPresentation.Describe(TaskRunPhase.Running, PlayMode.Automatic, false, false, false, "", null, 29, true, "", "");
        Assert.Equal("等待下一局", waiting.Title); Assert.False(waiting.NeedsAttention);
        var variant = RunPresentation.Describe(TaskRunPhase.Running, PlayMode.Automatic, false, false, false, "", null, 25, true, "", "");
        Assert.Equal("鸣牌选牌中", variant.Title);
        var retry = RunPresentation.Describe(TaskRunPhase.Running, PlayMode.Off, true, false, true, "核对旧操作", null, 29, true, "", "");
        Assert.Equal("正在恢复", retry.Title); Assert.Contains("核对旧操作", retry.Detail);
        var stopped = RunPresentation.Describe(TaskRunPhase.Paused, PlayMode.Off, true, false, false, "", "GLOBAL_AI_TIMEOUT", null, false, "", "");
        Assert.True(stopped.NeedsAttention); Assert.Contains("超时", stopped.Detail);
    }
    [Fact]
    public void Engine_must_be_ready_before_queue_accept_and_first_dealer_turn_start()
    {
        var queue = new TableAutomation(); queue.Arm(new(true, true), 0);
        var idle = new TableAutomationObservation(true, false, false, false, false, QueuePhase.None, false, false, false, EngineReady: false);
        Assert.Equal(TableAutomationAction.None, queue.Tick(4, idle));
        Assert.Equal(TableAutomationAction.None, queue.Tick(40, idle)); Assert.True(queue.Armed);
        Assert.Equal(TableAutomationAction.Queue, queue.Tick(41, idle with { EngineReady = true }));
        var pop = idle with { Queue = QueuePhase.Ready, QueueMatches = true, PopMatches = true, AcceptAvailable = true };
        Assert.Equal(TableAutomationAction.None, queue.Tick(42, pop));
        Assert.Equal(TableAutomationAction.Accept, queue.Tick(43, pop with { EngineReady = true }));
        var table = idle with { TableVisible = true, InDuty = true, Queue = QueuePhase.InContent };
        Assert.Equal(TableAutomationAction.None, queue.Tick(44, table));
        Assert.Equal(TableAutomationAction.None, queue.Tick(46, table));
        Assert.Equal(TableAutomationAction.None, queue.Tick(47, table with { EngineReady = true }));
        Assert.Equal(TableAutomationAction.StartPlay, queue.Tick(49, table with { EngineReady = true }));
        queue.Disarm("user pause");
        Assert.Equal(TableAutomationAction.None, queue.Tick(50, table with { EngineReady = true }));
    }
    [Fact]
    public void Completed_match_exit_does_not_require_model_but_next_queue_does()
    {
        var queue = new TableAutomation(); queue.Arm(new(true, true), 0);
        var table = new TableAutomationObservation(true, true, true, false, true, QueuePhase.InContent, true, true, false, CanLeave: true);
        queue.Tick(1, table); Assert.True(queue.ObserveMatchCompleted(766, 2));
        Assert.Equal(TableAutomationAction.LeaveCompletedMatch, queue.Tick(11, table with { EngineReady = false }));
        var outside = table with { TableVisible = false, InDuty = false, Queue = QueuePhase.None, Playing = false, EngineReady = false };
        queue.Tick(12, outside);
        Assert.Equal(TableAutomationAction.None, queue.Tick(18, outside));
        Assert.Equal(TableAutomationAction.None, queue.Tick(30, outside));
        Assert.Equal(TableAutomationAction.Queue, queue.Tick(31, outside with { EngineReady = true }));
    }
}
