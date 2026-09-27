using Mahjong.Plugin.CN.Automation;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class TransientRecoveryTests
{
    [Theory]
    [InlineData("AUTO_SNAPSHOT_UNAVAILABLE: transition",true)]
    [InlineData("READ_OR_POLICY_ERROR：READ_FAILED",true)]
    [InlineData("READ_OR_POLICY_ERROR：SCHEMA_MISMATCH READ_FAILED",false)]
    [InlineData("AUTO_OUTCOME_TIMEOUT: submitted",false)]
    [InlineData("AUTO_DISPATCH_EXCEPTION: callback",false)]
    [InlineData("BETA_ACCESS_EXPIRED：expired",false)]
    [InlineData("USER_PAUSE",false)]
    [InlineData("SCENE_EXIT：exit",false)]
    [InlineData("AKOCHAN_BLOCKED: unknown input",false)]
    [InlineData("AKOCHAN_BLOCKED",false)]
    [InlineData("AKOCHAN_BLOCKED: GLOBAL_AI_TIMEOUT",true)]
    [InlineData("AKOCHAN_BLOCKED: GLOBAL_AI_PROCESS_Timeout",true)]
    [InlineData("AKOCHAN_BLOCKED: GLOBAL_AI_PROCESS_EndOfStream",true)]
    [InlineData("AKOCHAN_BLOCKED: GLOBAL_AI_PROCESS_TransportFailure",true)]
    [InlineData("AKOCHAN_BLOCKED: GLOBAL_AI_PROCESS_ResponseMismatch",false)]
    [InlineData("AKOCHAN_BLOCKED: GLOBAL_AI_TIMEOUT_OR_CANCELLED",false)]
    [InlineData("AKOCHAN_BLOCKED: GLOBAL_AI_WIN_OPTION_MISSING",false)]
    public void Only_read_transients_are_eligible(string reason,bool expected)=>Assert.Equal(expected,TransientRecoveryGate.Eligible(reason));
    [Fact] public void Requires_new_stable_read_and_same_original_authority()
    {
        var gate=new TransientRecoveryGate();Assert.True(gate.Begin("AUTO_SNAPSHOT_UNAVAILABLE","task-model-character-generation",0));
        Assert.Equal(RecoveryCheck.Waiting,gate.Observe("task-model-character-generation","one",.1,true,false));
        Assert.Equal(RecoveryCheck.Waiting,gate.Observe("task-model-character-generation","two",.3,true,false));
        Assert.Equal(RecoveryCheck.Resume,gate.Observe("task-model-character-generation","two",.7,true,false));
        Assert.False(gate.Pending);
        gate.Begin("AUTO_STATE_INCONSISTENT","old",1);
        Assert.Equal(RecoveryCheck.Abandon,gate.Observe("new","two",2,true,false));
    }
    [Fact] public void Unconfirmed_submission_never_retries_same_input_and_times_out()
    {
        var g=new TransientRecoveryGate();g.Begin("AUTO_SNAPSHOT_UNAVAILABLE","task",0);
        Assert.Equal(RecoveryCheck.Waiting,g.Observe("task","same",1,true,true));
        Assert.Equal(RecoveryCheck.Abandon,g.Observe("task","same",9,true,true));
        Assert.Equal("RECOVERY_PREVIOUS_INPUT_UNCONFIRMED",g.Code);
    }
    [Fact] public void Cancel_and_retry_budget_prevent_late_resume_and_error_loops()
    {
        var g=new TransientRecoveryGate();
        for(int i=0;i<3;i++){Assert.True(g.Begin("TABLE_OBSERVATION_ERROR","task",i));g.Cancel();}
        Assert.False(g.Begin("TABLE_OBSERVATION_ERROR","task",4));
        Assert.Equal(RecoveryCheck.Abandon,g.Observe("task","same",5,true,false));
        Assert.True(g.Begin("TABLE_OBSERVATION_ERROR","task",63));
    }
}
