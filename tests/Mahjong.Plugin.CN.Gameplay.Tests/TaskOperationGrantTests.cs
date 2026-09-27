using Mahjong.Plugin.CN.Access;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class TaskOperationGrantTests
{
    [Theory]
    [InlineData(true,true,true,true,true)]
    [InlineData(false,true,true,true,false)]
    [InlineData(true,false,true,true,false)]
    [InlineData(true,true,false,true,false)]
    [InlineData(true,true,true,false,false)]
    public void Authorization_requires_live_qualification_same_task_character_and_generation(
        bool qualified,bool sameRun,bool sameCharacter,bool sameGeneration,bool expected)
    {
        var run=Guid.NewGuid();
        var grant=new TaskOperationGrant(run,"synthetic",3);
        Assert.Equal(expected,grant.Allows(qualified,sameRun?run:Guid.NewGuid(),
            sameCharacter?"synthetic":"other-synthetic",sameGeneration?3:4));
    }
    [Fact]
    public void Missing_task_or_character_can_never_authorize()
    {
        Assert.False(new TaskOperationGrant(Guid.Empty,"synthetic",0).Allows(true,Guid.Empty,"synthetic",0));
        var run=Guid.NewGuid();
        Assert.False(new TaskOperationGrant(run,"",0).Allows(true,run,"",0));
    }

    [Fact]
    public void Qualified_task_receipt_survives_hands_matches_and_pause_but_not_a_new_task()
    {
        var task = new Mahjong.Cn.Tasks.TaskRun();
        var plan = new Mahjong.Cn.Tasks.TaskPlan(true,true,766,"beta:fixture",new(MatchLimit:0),"fixture");
        task.Start(plan,"local-fixture",null,0,DateTimeOffset.UnixEpoch);
        var access = new QualifiedTaskAccess(task.RunId,task.CharacterContext,plan,"beta:fixture",4);
        for (int i=0;i<12;i++)
        {
            task.ObserveTable(true);Assert.True(task.CompleteMatch(task.MatchId!.Value));task.ObserveTable(false);
            task.Tick(i,DateTimeOffset.UnixEpoch.AddDays(30),"local-fixture",null);
            Assert.True(access.Matches(task,"beta:fixture",4));
        }
        task.Pause(12);Assert.True(access.Matches(task,"beta:fixture",4));
        Assert.True(task.Resume(13,DateTimeOffset.UnixEpoch.AddDays(31),"local-fixture",null));
        Assert.True(access.Matches(task,"beta:fixture",4));
        Assert.False(access.Matches(task,"beta:other",4));
        Assert.False(access.Matches(task,"beta:fixture",5));
        task.End(14);Assert.False(access.Matches(task,"beta:fixture",4));
        task.Start(plan,"local-fixture",null,15,DateTimeOffset.UnixEpoch.AddDays(31));
        Assert.False(access.Matches(task,"beta:fixture",4));
    }

    [Fact]
    public void Retained_task_does_not_cover_changed_character_or_expanded_operation_scope()
    {
        var task = new Mahjong.Cn.Tasks.TaskRun();
        var plan = new Mahjong.Cn.Tasks.TaskPlan(false,false,0,"beta:fixture",new(MatchLimit:1),"fixture");
        task.Start(plan,"local-fixture",null,0,DateTimeOffset.UnixEpoch);
        Assert.False(new QualifiedTaskAccess(task.RunId,"other",plan,"beta:fixture",0).Matches(task,"beta:fixture",0));
        var access = new QualifiedTaskAccess(task.RunId,task.CharacterContext,plan,"beta:fixture",0);
        task.Pause(1);task.ConfirmEngineForResume("beta:fixture",true);
        Assert.False(access.Matches(task,"beta:fixture",0));
    }
}
