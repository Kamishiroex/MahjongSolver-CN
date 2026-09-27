using Mahjong.Cn.Rating;
using Mahjong.Cn.Tasks;
using Xunit;

namespace Mahjong.Cn.Tests;

public sealed class TaskRuleTests
{
    [Fact]
    public void Explicit_manual_to_auto_resume_keeps_counts_time_rules_and_stop_after_match()
    {
        var run = new TaskRun();
        var rules = new StopRuleSet(MatchLimit: 5, ActiveSecondsLimit: 900);
        run.Start(new(false, true, 766, "fixture", rules, "profile"), "local-fixture", null, 0, Now);
        run.ObserveTable(true); run.CompleteMatch(run.MatchId!.Value); run.ObserveTable(false); run.ObserveTable(true);
        var runId = run.RunId; var matchId = run.MatchId;
        run.RequestStopAfterMatch(); run.Pause(20);
        run.ConfirmEngineForResume("fixture", true);
        Assert.True(run.Resume(50, Now, "local-fixture", null));
        Assert.Equal(runId, run.RunId); Assert.Equal(matchId, run.MatchId);
        Assert.Equal(1, run.CompletedMatches); Assert.Equal(20, run.ActiveSeconds);
        Assert.Equal(rules, run.Plan!.Rules); Assert.True(run.Plan.Automatic);
        Assert.True(run.AllowsGameplay); Assert.False(run.AllowsNextMatch);
        Assert.Equal(TaskRunPhase.StopAfterMatch, run.Phase);
        run.CompleteMatch(matchId!.Value); run.Tick(51, Now, "local-fixture", null);
        Assert.Equal(TaskRunPhase.Completed, run.Phase);
    }

    [Fact]
    public void Changing_mode_does_not_bypass_a_reached_limit_or_modify_a_running_plan()
    {
        var run = new TaskRun();
        run.Start(new(false, true, 766, "fixture", new(MatchLimit: 1), "profile"), "local-fixture", null, 0, Now);
        run.ConfirmEngineForResume("other", true);
        Assert.False(run.Plan!.Automatic); Assert.Equal("fixture", run.Plan.EngineIdentity);
        run.ObserveTable(true); run.Pause(1); run.CompleteMatch(run.MatchId!.Value);
        run.ConfirmEngineForResume("fixture", true);
        Assert.False(run.Resume(2, Now, "local-fixture", null));
        Assert.Equal(1, run.CompletedMatches); Assert.Equal(TaskRunPhase.Completed, run.Phase);
        Assert.False(run.AllowsGameplay); Assert.False(run.AllowsNextMatch);
    }

    [Fact]public void Completion_while_paused_keeps_count_without_reauthorizing_actions()
    {var r=new TaskRun();r.Start(new(true,true,766,"fixture",new(MatchLimit:1),"profile"),"local-fixture",null,0,Now);r.ObserveTable(true);r.Pause(1);Assert.True(r.CompleteMatch(r.MatchId!.Value));Assert.Equal(1,r.CompletedMatches);Assert.False(r.AllowsGameplay);Assert.False(r.AllowsNextMatch);Assert.False(r.Resume(2,Now,"local-fixture",null));Assert.Equal(TaskRunPhase.Completed,r.Phase);}
    [Fact]public void Required_data_timeout_cannot_rearm_and_new_engine_keeps_budget()
    {var r=new TaskRun();r.Start(new(true,true,766,"old",new(TargetRating:2000),"profile"),"local-fixture",null,0,Now);r.Tick(121,Now.AddSeconds(121),"local-fixture",null);Assert.Equal(TaskRunPhase.Problem,r.Phase);Assert.False(r.AllowsNextMatch);
     r.Start(new(true,true,766,"old",new(),"profile"),"local-fixture",null,0,Now);r.Pause(10);var id=r.RunId;r.ConfirmEngineForResume("new");Assert.True(r.Resume(20,Now,"local-fixture",null));Assert.Equal(id,r.RunId);Assert.Equal(10,r.ActiveSeconds);Assert.Equal("new",r.Plan!.EngineIdentity);}
    private static readonly DateTimeOffset Now=new(2026,9,27,0,0,0,TimeSpan.Zero);
    private static RatingObservation Rating(int n, Guid? match=null)=>new(n,2200,"synthetic","local-fixture",Now,"synthetic verified profile",RatingMapping.Verified,RatingFreshness.Fresh,"profile",match,1,null);
    private static StopRuleInput Input(RatingObservation? rating, Guid? match=null)=>new(0,0,false,false,"local-fixture","profile",Now,rating,RequiredMatchAssociation:match);
    [Theory] [InlineData(1999,StopDecision.Continue)] [InlineData(2000,StopDecision.StopAfterMatch)] [InlineData(2002,StopDecision.StopAfterMatch)]
    public void Current_rating_boundary_not_highest(int value,StopDecision expected)=>Assert.Equal(expected,StopRuleEvaluator.Evaluate(new(TargetRating:2000),Input(Rating(value))).Decision);
    [Fact] public void Local_table_points_cannot_be_used_as_rating()=>Assert.Equal(StopDecision.HoldForRequiredData,StopRuleEvaluator.Evaluate(new(TargetRating:2000),Input(null)).Decision);
    [Fact] public void Fresh_timestamp_without_result_association_is_not_refresh()
    { var match=Guid.NewGuid(); Assert.Equal(StopDecision.HoldForRequiredData,StopRuleEvaluator.Evaluate(new(TargetRating:2000),Input(Rating(1991),match)).Decision); Assert.Equal(StopDecision.StopAfterMatch,StopRuleEvaluator.Evaluate(new(TargetRating:2000),Input(Rating(2004,match),match)).Decision); }
    [Fact] public void Missing_rating_does_not_block_unrelated_modes_or_known_stop()
    { Assert.Equal(StopDecision.Continue,StopRuleEvaluator.Evaluate(new(),Input(null)).Decision); Assert.Equal("MATCH_LIMIT",StopRuleEvaluator.Evaluate(new(MatchLimit:2,TargetRating:2000),Input(null) with {CompletedMatches=2}).Code); }
    [Fact] public void Candidate_cached_foreign_and_wrong_version_are_untrusted()
    { foreach(var x in new[]{Rating(1800) with { Mapping=RatingMapping.Candidate },Rating(1800) with {Freshness=RatingFreshness.Cached},Rating(1800) with {LocalCharacterContext="other-fixture"},Rating(1800) with {ProfileVersion="other"}}) Assert.Equal(StopDecision.HoldForRequiredData,StopRuleEvaluator.Evaluate(new(TargetRating:2000),Input(x)).Decision); }
    [Fact] public void Normal_limit_latches_in_match_and_duplicate_results_do_not_count()
    { var r=new TaskRun();r.Start(new(true,true,766,"fixture",new(ActiveSecondsLimit:10),"profile"),"local-fixture",null,0,Now); r.ObserveTable(true);var id=r.MatchId!.Value;r.Tick(11,Now,"local-fixture",null);Assert.True(r.AllowsGameplay);Assert.False(r.AllowsNextMatch);Assert.Equal(TaskRunPhase.StopAfterMatch,r.Phase);Assert.True(r.CompleteMatch(id));Assert.False(r.CompleteMatch(id));r.Tick(12,Now,"local-fixture",null);Assert.Equal(TaskRunPhase.Completed,r.Phase);Assert.Equal(1,r.CompletedMatches); }
    [Fact] public void Pause_resume_preserves_identity_budget_and_counter()
    { var r=new TaskRun();r.Start(new(true,true,766,"fixture",new(ActiveSecondsLimit:30),"profile"),"local-fixture",null,0,Now);var run=r.RunId;r.Pause(10);r.Tick(100,Now,"local-fixture",null);Assert.Equal(10,r.ActiveSeconds);Assert.False(r.AllowsGameplay);Assert.True(r.Resume(100,Now,"local-fixture",null));r.Tick(111,Now,"local-fixture",null);Assert.Equal(21,r.ActiveSeconds);Assert.Equal(run,r.RunId); }
    [Fact] public void Between_match_deadline_closes_queue_permission()
    { var r=new TaskRun();r.Start(new(true,true,766,"fixture",new(DeadlineUtc:Now.AddSeconds(1)),"profile"),"local-fixture",null,0,Now);r.Tick(2,Now.AddSeconds(2),"local-fixture",null);Assert.False(r.AllowsNextMatch);Assert.False(r.AllowsGameplay); }
    [Fact] public void Completed_run_never_rearms_on_ticks_or_resume()
    { var r=new TaskRun();r.Start(new(true,true,766,"fixture",new(TargetRating:2000),"profile"),"local-fixture",Rating(2002),0,Now);r.Tick(2,Now,"local-fixture",Rating(1900));Assert.False(r.Resume(2,Now,"local-fixture",Rating(1900)));Assert.False(r.AllowsNextMatch); }
    [Fact] public void Character_switch_fails_and_clock_rollback_does_not_reduce_budget()
    { var r=new TaskRun();r.Start(new(true,true,766,"fixture",new(),"profile"),"local-fixture",null,10,Now);r.Tick(20,Now,"local-fixture",null);r.Tick(12,Now,"local-fixture",null);Assert.Equal(10,r.ActiveSeconds);r.Tick(21,Now,"other",null);Assert.Equal(TaskRunPhase.Problem,r.Phase);Assert.False(r.AllowsGameplay); }
}
