using Mahjong.Plugin.CN.Access;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class TaskOperationGrantTests
{
    [Theory]
    [InlineData(true,true,true,true,true,true)]
    [InlineData(false,true,true,true,true,false)]
    [InlineData(true,false,true,true,true,false)]
    [InlineData(true,true,false,true,true,false)]
    [InlineData(true,true,true,false,true,false)]
    [InlineData(true,true,true,true,false,false)]
    public void Authorization_requires_live_qualification_capability_same_task_character_and_generation(
        bool qualified,bool enabled,bool sameRun,bool sameCharacter,bool sameGeneration,bool expected)
    {
        var run=Guid.NewGuid();
        var grant=new TaskOperationGrant(run,"synthetic",3);
        Assert.Equal(expected,grant.Allows(qualified,enabled,sameRun?run:Guid.NewGuid(),
            sameCharacter?"synthetic":"other-synthetic",sameGeneration?3:4));
    }
    [Fact]
    public void Missing_task_or_character_can_never_authorize()
    {
        Assert.False(new TaskOperationGrant(Guid.Empty,"synthetic",0).Allows(true,true,Guid.Empty,"synthetic",0));
        var run=Guid.NewGuid();
        Assert.False(new TaskOperationGrant(run,"",0).Allows(true,true,run,"",0));
    }
}
