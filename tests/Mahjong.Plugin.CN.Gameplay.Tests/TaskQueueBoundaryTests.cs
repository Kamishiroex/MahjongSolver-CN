using Mahjong.Cn.Tasks;
using Mahjong.Plugin.CN.Automation;
using Xunit;
namespace Mahjong.Plugin.CN.Gameplay.Tests;
public sealed class TaskQueueBoundaryTests
{
    [Fact]public void Completing_whole_match_blocks_next_queue_but_allows_verified_exit()
    {
        var utc=DateTimeOffset.Parse("2026-09-27T00:00:00Z");var task=new TaskRun();var queue=new TableAutomation();
        task.Start(new(true,true,766,"fixture",new(MatchLimit:1),"fixture"),"fixture",null,0,utc);
        queue.Arm(new(true,true,766,MatchLimit:0),0);
        var table=new TableAutomationObservation(true,true,true,false,true,QueuePhase.InContent,true,true,false,766,true);
        task.ObserveTable(true);queue.Tick(1,table);
        task.ObserveTable(false);Assert.Equal(0,task.CompletedMatches); // Hidden addon is not completion.
        Assert.True(task.CompleteMatch(task.MatchId!.Value));Assert.True(queue.ObserveMatchCompleted(766,2));
        task.Tick(2,utc,"fixture",null);Assert.False(task.AllowsNextMatch);Assert.False(task.AllowsGameplay);
        Assert.Equal(TableAutomationAction.LeaveCompletedMatch,queue.Tick(11,table));
        Assert.False(task.CompleteMatch(task.MatchId.Value));Assert.Equal(1,task.CompletedMatches);
    }
    [Fact]public void Explicit_resume_retains_owned_queue_but_pause_blocks_late_accept()
    {
        var queue=new TableAutomation();queue.Arm(new(true,true),0);
        var o=new TableAutomationObservation(true,false,false,false,false,QueuePhase.None,false,false,false);
        Assert.Equal(TableAutomationAction.Queue,queue.Tick(4,o));queue.Disarm("pause");
        var ready=o with {Queue=QueuePhase.Ready,QueueMatches=true,PopMatches=true,AcceptAvailable=true};
        Assert.Equal(TableAutomationAction.None,queue.Tick(5,ready));queue.Resume(6);
        Assert.Equal(TableAutomationAction.Accept,queue.Tick(7,ready));Assert.Equal(TableAutomationAction.None,queue.Tick(8,ready));
    }
}
