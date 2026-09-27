using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using Mahjong.Core;
using Mahjong.Engine;
using Mahjong.Plugin.CN.Journaling;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class MatchReviewTests
{
    private static readonly Guid Session=Guid.NewGuid();
    private static JournalReadResult Read(params (string Kind,object Data)[] data)=>new(data.Select((d,i)=>new JournalLine(
        new(2,Session,i+1,DateTimeOffset.UnixEpoch.AddSeconds(i),d.Kind,JsonSerializer.SerializeToElement(d.Data),""),"fixture")).ToImmutableArray(),false,null);
    private static MatchSummary Summary(params (string Kind,object Data)[] data)=>Assert.Single(MatchSummaryBuilder.Build(Read(data),"fixture"));
    [Fact] public void Submission_exit_and_fake_result_do_not_supply_placement_rating_or_completion()
    {
        var s=Summary(("review_match_started",new {SawTableSetup=true,Mode="Automatic"}),
            ("action_submission",new {Result="Submitted",SubmissionId=Guid.NewGuid(),Label="ron",ActionConfirmed=true}),
            ("match_result",new {Source="unverified",Placement=1,RatingDelta=99}), ("session_stopped",new {Reason="table_exit"}));
        Assert.Null(s.CompletedUtc);Assert.Null(s.Placement);Assert.Null(s.Wins);Assert.Null(s.RatingDelta);
        Assert.Equal(1,s.Submissions);Assert.Equal(0,s.ObservedTransitions);Assert.Null(s.AutomaticWholeMatch);
    }
    [Fact] public void Correlated_transition_timeout_and_cancel_are_separate_from_acceptance_or_missed_action()
    {
        Guid a=Guid.NewGuid(),b=Guid.NewGuid();
        var s=Summary(("action_submission",new {Result="Submitted",SubmissionId=a}), ("action_submission",new {Result="Submitted",SubmissionId=b}),
            ("review_action_observation",new {SubmissionId=a,StateTransitionObserved=true}),
            ("review_action_observation",new {SubmissionId=a,StateTransitionObserved=true}),
            ("review_action_observation",new {SubmissionId=b,TimedOut=true}),
            ("review_action_observation",new {SubmissionId=Guid.NewGuid(),StateTransitionObserved=true}),
            ("review_window_cancelled",new {}));
        Assert.Equal(2,s.Submissions);Assert.Equal(1,s.ObservedTransitions);Assert.Equal(1,s.ObservationTimeouts);
        Assert.Equal(1,s.CancelledWindows);Assert.Null(s.ConfirmedMissedActions);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void Whole_auto_mode_requires_seen_setup_and_completion(bool setup)
    {
        var s=Summary(("review_match_started",new {SawTableSetup=setup,Mode="Automatic"}),
            ("match_result",new {Source="IDutyState.DutyCompleted"}));
        Assert.Equal(setup?(bool?)true:null,s.AutomaticWholeMatch);Assert.Null(s.Placement);
    }
    [Fact] public void Manual_switch_pause_and_error_disqualify_whole_auto_without_guessing_unseen_takeovers()
    {
        var s=Summary(("review_match_started",new {SawTableSetup=true,Mode="Automatic"}),
            ("mode_selected",new {Mode="manual"}),("play_paused",new {}),
            ("play_stopped",new {Reason="READ_FAILED"}),("match_result",new {Source="IDutyState.DutyCompleted"}));
        Assert.False(s.AutomaticWholeMatch);Assert.Equal(1,s.RecordedTakeovers);Assert.Equal("READ_FAILED",Assert.Single(s.Stops));
    }
    [Fact] public void Expected_post_completion_stop_does_not_disqualify_observed_automatic_match()
    {
        var s=Summary(("review_match_started",new {SawTableSetup=true,Mode="Automatic"}),
            ("match_result",new {Source="IDutyState.DutyCompleted"}), ("play_stopped",new {Reason="MATCH_COMPLETE"}));
        Assert.True(s.AutomaticWholeMatch);Assert.Empty(s.Stops);
    }
    [Fact] public void Mixed_settings_or_model_cannot_become_one_comparable_configuration()
    {
        var s=Summary(("review_configuration",new {Fingerprint="a",Backend="Mortal"}),
            ("review_configuration",new {Fingerprint="b",Backend="Mortal"}));
        Assert.True(s.MixedConfiguration);Assert.Null(s.ConfigurationFingerprint);
    }
    [Fact] public void Incomplete_chain_keeps_reliability_and_outcomes_unknown()
    {
        var read=Read(("review_match_started",new {SawTableSetup=true,Mode="Automatic"}),
            ("match_result",new {Source="IDutyState.DutyCompleted"}),("play_stopped",new {Reason="error"}));
        var row=Assert.Single(MatchSummaryBuilder.Build(read with {Error="hash-mismatch"},"fixture"));
        Assert.False(row.IntegrityVerified);Assert.Null(row.CompletedUtc);Assert.Empty(row.Stops);Assert.Null(row.RecordedTakeovers);
    }
    [Fact] public void Two_legacy_matches_do_not_share_interventions_or_last_duty()
    {
        var rows=MatchSummaryBuilder.Build(Read(("play_paused",new {}),
            ("task_match_completed",new {MatchId=Guid.NewGuid(),DutyId=766}),
            ("task_match_completed",new {MatchId=Guid.NewGuid(),DutyId=643})),"fixture");
        Assert.Equal(2,rows.Length);Assert.Equal((uint)766,rows[0].DutyId);Assert.Equal((uint)643,rows[1].DutyId);
        Assert.NotEqual(rows[0].StopReason,rows[1].StopReason);
    }
    private static MatchSummary Verified(int placement)=>new(Guid.NewGuid(),DateTimeOffset.UnixEpoch,DateTimeOffset.UnixEpoch.AddMinutes(30),
        766,"test-backend","fixture","完成","","fixture") {IntegrityVerified=true,ConfigurationFingerprint="config-a",
        MatchType="tonpu",RecordedTakeovers=0,Placement=placement};
    [Fact] public void Trends_keep_denominators_and_separate_duty_model_settings_and_takeover()
    {
        var a=Verified(1);var b=Verified(4);
        var rows=new[]{a,b,Verified(2) with {Placement=null},Verified(2) with {DutyId=643},
            Verified(2) with {ConfigurationFingerprint="config-b"},Verified(2) with {RecordedTakeovers=1},
            Verified(2) with {ConfigurationFingerprint=null}};
        var trends=MatchTrends.Build(rows);Assert.Equal(5,trends.Length);
        var trend=Assert.Single(trends.Where(t=>t.Records==3));Assert.Equal(2,trend.PlacementSamples);
        Assert.Equal(2.5,trend.MeanPlacement);Assert.Equal(.5,trend.FourthRate);Assert.Null(trend.WinRate);Assert.Null(trend.MeanRatingDelta);
        Assert.Equal(0,Assert.Single(trends.Where(t=>t.ConfigurationFingerprint is null)).PlacementSamples);
    }
    [Fact] public void Verified_hand_rates_are_weighted_by_hand_count_not_mean_of_match_percentages()
    {
        var trend=Assert.Single(MatchTrends.Build([Verified(1) with {HandsWithVerifiedOutcome=4,Wins=2,DealIns=1,RatingDelta=10},
            Verified(3) with {HandsWithVerifiedOutcome=8,Wins=1,DealIns=2},Verified(4)]));
        Assert.Equal(12,trend.HandSamples);Assert.Equal(.25,trend.WinRate);Assert.Equal(.25,trend.DealInRate);
        Assert.Equal(1,trend.RatingSamples);Assert.Equal(10,trend.MeanRatingDelta);
    }
    [Fact] public void Decision_review_links_original_candidates_filters_and_observation_without_claiming_acceptance()
    {
        Guid decision=Guid.NewGuid(),submission=Guid.NewGuid();
        var read=Read(("global_ai_decision",new {InputSha256="input",Decision=new {Candidates=new[]{new {Score=7}}},
                CandidateReviews=new[]{new {Index=0,Disposition="filtered",Reason="AVAILABLE_WIN_GUARD"}}}),
            ("review_decision",new {DecisionId=decision,EngineInputSha256="input",Backend="test",Choice="ron"}),
            ("action_submission",new {DecisionId=decision,SubmissionId=submission,Result="Submitted"}),
            ("review_action_observation",new {SubmissionId=submission,StateTransitionObserved=true,ActionConfirmed=false}));
        var row=Assert.Single(DecisionReviewBuilder.Build(read).Items);
        Assert.Contains("7",row.Candidates);Assert.Contains("AVAILABLE_WIN_GUARD",row.Filters);
        Assert.Contains("Submitted",row.Submissions);Assert.Contains("\"ActionConfirmed\":false",row.Observations);
        Assert.Contains("未知",row.Override);Assert.Empty(DecisionReviewBuilder.Build(read with {IncompleteTail=true}).Items);
    }
    [Fact] public void Chunked_trace_reassembles_but_missing_piece_never_supplies_candidates()
    {
        string json=JsonSerializer.Serialize(new {Phase="decision",InputSha256="input",Decision=new {Candidates=new[]{new {Score=42}}}});
        byte[] bytes=System.Text.Encoding.UTF8.GetBytes(json);
        var read=Read(("global_ai_trace_chunk",new {TraceId="fixture",Index=0,Count=1,TotalBytes=bytes.Length,Data=Convert.ToBase64String(bytes)}),
            ("review_decision",new {DecisionId=Guid.NewGuid(),EngineInputSha256="input",Choice="discard"}));
        Assert.Contains("42",Assert.Single(DecisionReviewBuilder.Build(read).Items).Candidates);
        var missing=Read(("global_ai_trace_chunk",new {TraceId="fixture",Index=0,Count=2,TotalBytes=bytes.Length,Data=Convert.ToBase64String(bytes)}),
            ("review_decision",new {DecisionId=Guid.NewGuid(),EngineInputSha256="input",Choice="discard"}));
        Assert.Contains("未知",Assert.Single(DecisionReviewBuilder.Build(missing).Items).Candidates);
    }
    [Fact] public void Observer_failure_dedup_and_invalidation_never_change_policy_result_or_marker()
    {
        var inner=new FakePolicy();int observed=0;
        var policy=DecisionReviewPolicy.Wrap(inner,(_,_,_,_)=>{observed++;throw new IOException("fixture");});
        Assert.IsAssignableFrom<IIndependentPublicStatePolicy>(policy);
        Assert.Same(inner.Choice,policy.Choose(StateSnapshot.Empty));Assert.Same(inner.Choice,policy.Choose(StateSnapshot.Empty));
        Assert.Equal(2,inner.Calls);Assert.Equal(1,observed);
        ((IRefreshablePolicy)policy).Invalidate();policy.Choose(StateSnapshot.Empty);Assert.Equal(2,observed);
        ((IDisposable)policy).Dispose();Assert.True(inner.Disposed);
    }
    private sealed class FakePolicy:IIndependentPublicStatePolicy,IDisposable
    {
        internal ActionChoice Choice=new(ActionKind.Pass,null,null,"fixture");internal int Calls;internal bool Disposed;
        public ActionChoice Choose(StateSnapshot state){Calls++;return Choice;}
        public bool RequiresRefresh=>false;
        public void Invalidate() { }
        public void Dispose()=>Disposed=true;
    }
    [Fact] public async Task Derived_summary_archive_and_unverified_rating_cannot_override_source_outcome()
    {
        string root=Path.Combine(Path.GetTempPath(),"mjcn-review-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var journal=new GameJournal(root);journal.Event("match_result",new {Source="IDutyState.DutyCompleted"});await journal.CompleteAsync();
            await File.WriteAllTextAsync(Path.Combine(journal.DirectoryPath,"rating-after.json"),JsonSerializer.Serialize(new
                {Schema=1,MatchId=journal.SessionId,RatingAfter=1810,AssociationVerified=true}));
            await MatchSummaryBuilder.SaveAsync(journal.DirectoryPath);
            await File.WriteAllTextAsync(Path.Combine(journal.DirectoryPath,"match-summary.json"),"fake-placement-and-rating-delta");
            var row=Assert.Single(await JournalReviewStore.SummariesAsync(journal.DirectoryPath));
            Assert.Equal(1810,row.RatingAfter);Assert.Null(row.RatingDelta);Assert.Null(row.Placement);
            string zip=Path.Combine(root,"record.zip");ZipFile.CreateFromDirectory(journal.DirectoryPath,zip);
            var archived=Assert.Single(await JournalReviewStore.SummariesAsync(zip));Assert.Equal(row.Outcome,archived.Outcome);Assert.Equal(zip,archived.JournalPath);
        }
        finally {Directory.Delete(root,true);}
    }
}
