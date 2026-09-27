using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Mahjong.Cn;
using Mahjong.Cn.Engines;
using Mahjong.Core;
using Mahjong.Plugin.CN.Experimental;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class AkochanGlobalPolicyTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Captured_8s_ron_rejects_old_pass_and_accepts_matching_native_hora(bool fixedEngine)
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures","ron-false-furiten-20260925.json")));
        using var h=new Harness();
        h.Input=fixture.RootElement.GetProperty("ActualPublicInput").Deserialize<AkochanGlobalSnapshot>()! with { Utc=h.Now };
        h.State=h.State with {Hand=h.Input.Hand.Select(t=>Tile.FromId(t.Id)).ToArray(),
            Legal=new(h.Input.LegalActions,[],[],[],[])};
        h.Choose(); var request=await h.Next();
        var decision=fixture.RootElement.GetProperty("OldDecision").Deserialize<AkochanGlobalDecision>()!;
        if(fixedEngine) decision=decision with {Candidates=[new([new("hora",0,3,new VisibleTile(25),[],null)],-17.65),..decision.Candidates]};
        await h.Complete(request,decision with {InputSha256=AkochanGlobalEngine.ComputeInputSha256(h.Input)});
        var result=h.Choose();
        if(fixedEngine) Assert.Equal(ActionKind.Ron,result.Kind);
        else Assert.Contains("AKOCHAN_BLOCKED: GLOBAL_AI_WIN_OPTION_MISSING",result.Reasoning);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Offered_win_never_becomes_pass_when_native_candidate_is_missing(bool tsumo)
    {
        using var h = new Harness();
        h.Input = h.Input with { LegalActions = (tsumo ? ActionFlags.Tsumo : ActionFlags.Ron) | ActionFlags.Pass };
        h.State = h.State with { Legal = h.State.Legal with { Flags = h.Input.LegalActions } };
        h.Choose(); var request = await h.Next();
        await h.Complete(request,request.Result() with { Candidates=[new([new("none",0,null,null,[],null)],100)] });
        Assert.Contains("AKOCHAN_BLOCKED: GLOBAL_AI_WIN_OPTION_MISSING",h.Choose().Reasoning);
        Assert.Equal("GLOBAL_AI_WIN_OPTION_MISSING",h.Traces.Last().Error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Native_legal_hora_is_selected_even_when_pass_scores_higher(bool tsumo)
    {
        using var h = new Harness();
        var trigger = tsumo ? h.Input.Trigger : new AkochanGlobalTrigger("dahai",3,new VisibleTile(30));
        h.Input = h.Input with { Trigger=trigger, LegalActions=(tsumo ? ActionFlags.Tsumo : ActionFlags.Ron) | ActionFlags.Pass };
        if (!tsumo) h.Input = h.Input with { Hand=h.Input.Hand.RemoveAt(13) };
        h.State = h.State with { Hand=h.Input.Hand.Select(t=>Tile.FromId(t.Id)).ToArray(), Legal=h.State.Legal with { Flags=h.Input.LegalActions } };
        h.Choose(); var request = await h.Next();
        await h.Complete(request,request.Result() with { Candidates=[new([new("none",0,null,null,[],null)],100),
            new([new("hora",0,tsumo ? 0 : 3,trigger.Tile,[],null)],50)] });
        Assert.Equal(tsumo ? ActionKind.Tsumo : ActionKind.Ron,h.Choose().Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Captured_post_riichi_draw_waits_without_dispatch_and_later_tsumo_still_reaches_AI(int index)
    {
        using var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "result-transition-20260925.json")));
        var captured = fixture.RootElement.GetProperty("RiichiDraws")[index].GetProperty("Input")
            .Deserialize<AkochanGlobalSnapshot>()!;
        using var h = new Harness();
        h.Input = captured with { Utc = h.Now };
        h.State = h.State with { Hand = captured.Hand.Select(t => Tile.FromId(t.Id)).ToArray(),
            Legal = new(captured.LegalActions, [], [], [], []) };
        Assert.Contains("AKOCHAN_PENDING: GLOBAL_WAIT_RIICHI_DRAW", h.Choose().Reasoning);
        Assert.Null(h.Policy.PendingWork);
        Assert.Empty(h.Traces);
        h.Input = h.Input with { LegalActions = ActionFlags.Tsumo | ActionFlags.Pass };
        h.State = h.State with { Legal = h.State.Legal with { Flags = h.Input.LegalActions } };
        h.Choose();
        var request = await h.Next();
        await h.Complete(request, request.Result() with { Candidates =
            [new([new("hora", 0, 0, h.Input.Trigger.Tile, [], null)], 100)] });
        Assert.Equal(ActionKind.Tsumo, h.Choose().Kind);
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private sealed record Request(AkochanGlobalSnapshot Input, CancellationToken Token,
        TaskCompletionSource<AkochanGlobalDecision> Completion)
    {
        internal AkochanGlobalDecision Result(int tile = 28) => new(
            [new([new("dahai", 0, null, new VisibleTile(tile), [], false)], 42)],
            AkochanGlobalEngine.ComputeInputSha256(Input), Input.Assumptions,
            AkochanInstallation.ExpectedCommit, 75, "{}");
    }
    private sealed class Harness : IDisposable
    {
        internal DateTimeOffset Now = new(2026, 9, 24, 9, 20, 0, TimeSpan.Zero);
        internal AkochanGlobalSnapshot Input;
        internal StateSnapshot State;
        internal string? Error;
        internal LegalActions? ResponseLegal;
        internal readonly List<GlobalAiTrace> Traces = [];
        internal readonly AkochanGlobalPolicy Policy;
        private readonly Channel<Request> requests = Channel.CreateUnbounded<Request>();
        internal Harness(bool mortal = false, Task? preparation = null, Func<bool>? accessValid = null)
        {
            ImmutableArray<VisibleTile> hand = [new(1),new(5),new(6),new(7),new(8),new(8),new(9),
                new(10),new(10),new(19),new(25),new(26),new(28),new(30)];
            Input = new(0,0,1,0,0,0,60,hand,[new(31)],
                Enumerable.Range(0,4).Select(i => new AkochanGlobalPlayer(i,i,25000,false,false,null,[],[])).ToImmutableArray(),
                new("tsumo",0,new(30)),["explicit missing history"])
                { ContextKey = "table1:round1", Utc = Now, LegalActions = ActionFlags.Discard };
            State = StateSnapshot.Empty with { Hand = hand.Select(t => Tile.FromId(t.Id)).ToArray(),
                AddonStateCode = 30, Legal = new(ActionFlags.Discard,hand.Select(t => Tile.FromId(t.Id)).ToArray(),[],[],[]) };
            Policy = new(_ => Error is null ? new(Input,null) { ResponseLegal = ResponseLegal } : new(null,Error), _ => {}, Traces.Add,
                (input,token) => {
                    var request = new Request(input,token,new(TaskCreationOptions.RunContinuationsAsynchronously));
                    requests.Writer.TryWrite(request);
                    return request.Completion.Task; // Model a native worker completing after cancellation.
                }, () => Now, expectedCommit: mortal ? MortalInstallation.Commit : null,
                engineLabel: mortal ? "Mortal V4" : "akochan v5", preparation: preparation, accessValid: accessValid);
        }
        internal ActionChoice Choose() => Policy.Choose(State);
        internal async Task<Request> Next() => await requests.Reader.ReadAsync().AsTask().WaitAsync(Limit);
        internal async Task Complete(Request request, AkochanGlobalDecision? decision = null)
        {
            var task = Policy.PendingWork!;
            request.Completion.SetResult(decision ?? request.Result());
            await task.WaitAsync(Limit);
        }
        public void Dispose() => Policy.Dispose();
    }

    [Fact]
    public async Task Expired_test_permission_discards_late_result_and_renewal_cannot_revive_old_policy()
    {
        bool allowed = true;
        using var h = new Harness(accessValid: () => allowed);
        h.Choose(); var request = await h.Next(); var work = h.Policy.PendingWork!;
        allowed = false;
        Assert.Contains("BETA_ACCESS_REVOKED", h.Choose().Reasoning);
        Assert.True(request.Token.IsCancellationRequested);
        request.Completion.SetResult(request.Result()); await work.WaitAsync(Limit);
        allowed = true;
        Assert.Contains("BETA_ACCESS_REVOKED", h.Choose().Reasoning);
        Assert.DoesNotContain(h.Traces, x => x.Phase == "decision");
        Assert.Null(h.Policy.PendingWork);
    }

    [Fact]
    public void Unauthorized_test_policy_never_projects_or_starts_inference()
    {
        using var policy = new AkochanGlobalPolicy(_ => throw new Exception("Projection must not run"), _ => {}, _ => {},
            (_, _) => throw new Exception("Inference must not run"), accessValid: () => false);
        Assert.Contains("BETA_ACCESS_REVOKED", policy.Choose(StateSnapshot.Empty).Reasoning);
        Assert.Null(policy.PendingWork);
    }

    [Fact]
    public async Task Bound_response_uses_public_candidates_and_survives_legacy_candidate_changes_after_two_seconds()
    {
        using var h = new Harness();
        ImmutableArray<VisibleTile> hand = [new(0),new(8),new(10),new(12),new(13,true),new(15),new(15),
            new(16),new(16),new(17),new(23),new(23),new(33)];
        h.Input = h.Input with { Hand = hand, Trigger = new("dahai",2,new(23)),
            LegalActions = ActionFlags.Pon | ActionFlags.Pass,
            Players = h.Input.Players.SetItem(2,h.Input.Players[2] with { River = [new(new(23),null,false)] }) };
        h.State = h.State with { Hand = hand.Select(t => Tile.FromId(t.Id)).ToArray(),
            Legal = new(ActionFlags.Pon | ActionFlags.Pass,[],[new(MeldKind.Pon,new(15),[new(15),new(15)],1)],[],[]) };
        h.ResponseLegal = new(ActionFlags.Pon | ActionFlags.Pass,[],[new(MeldKind.Pon,new(23),[new(23),new(23)],2)],[],[]);
        Assert.StartsWith("AKOCHAN_PENDING",h.Choose().Reasoning);
        var request = await h.Next();
        h.Now = h.Now.AddSeconds(4); h.Input = h.Input with { Utc = h.Now };
        h.State = h.State with { Legal = h.State.Legal with { PonCandidates = [] } };
        h.Choose(); Assert.False(request.Token.IsCancellationRequested);
        await h.Complete(request,request.Result() with { Candidates =
            [new([new("pon",0,2,new(23),[new(23),new(23)],null)],99)] });
        var choice = h.Choose();
        Assert.Equal(ActionKind.Pon,choice.Kind);
        Assert.Equal(23,choice.Call!.Value.ClaimedTile.Id);
        Assert.Equal(2,choice.Call.Value.FromSeat);
        Assert.Single(h.Traces,t => t.Phase == "request");
    }

    [Fact]
    public async Task Public_table_enters_worker_and_completed_AI_result_supplies_action()
    {
        using var h = new Harness();
        h.Input = h.Input with { Players = h.Input.Players.SetItem(1,h.Input.Players[1] with {
            River = [new(new(0),true,false)], Score = 23000 }) };
        Assert.StartsWith("AKOCHAN_PENDING",h.Choose().Reasoning);
        var request = await h.Next();
        Assert.Equal(23000, request.Input.Players[1].Score);
        Assert.Single(request.Input.Players[1].River);
        await h.Complete(request);
        var choice = h.Choose();
        Assert.Equal(ActionKind.Discard,choice.Kind);
        Assert.Equal(28,choice.DiscardTile!.Value.Id);
        Assert.StartsWith("AKOCHAN_GLOBAL",choice.Reasoning);
        Assert.DoesNotContain("explicit missing history",choice.Reasoning);
        Assert.Same(choice,h.Choose());
        Assert.Equal(new[] { "request","decision" },h.Traces.Select(t => t.Phase));
        Assert.Contains("explicit missing history", h.Traces.Single(t => t.Phase == "decision").Decision!.Assumptions);
    }

    [Theory]
    [InlineData("river")]
    [InlineData("score")]
    [InlineData("context")]
    [InlineData("legal")]
    public async Task Changed_public_state_cancels_old_worker_and_rejects_late_result(string change)
    {
        using var h = new Harness();
        h.Choose(); var old = await h.Next(); var oldTask = h.Policy.PendingWork!;
        if (change == "river") h.Input = h.Input with { Players=h.Input.Players.SetItem(1,h.Input.Players[1] with { River=[new(new(0),null,false)] }) };
        if (change == "score") h.Input = h.Input with { Players=h.Input.Players.SetItem(1,h.Input.Players[1] with { Score=26000 }) };
        if (change == "context") h.Input = h.Input with { ContextKey="table2:round1" };
        if (change == "legal") h.State = h.State with { Legal=h.State.Legal with { DiscardableTiles=[Tile.FromId(28)] } };
        Assert.StartsWith("AKOCHAN_PENDING",h.Choose().Reasoning);
        var current = await h.Next(); Assert.True(old.Token.IsCancellationRequested);
        old.Completion.SetResult(old.Result(1)); await oldTask.WaitAsync(Limit);
        Assert.StartsWith("AKOCHAN_PENDING",h.Choose().Reasoning);
        await h.Complete(current);
        Assert.Equal(28,h.Choose().DiscardTile!.Value.Id);
    }

    [Fact]
    public async Task Fresh_timestamp_does_not_restart_same_semantic_request()
    {
        using var h = new Harness(); h.Choose(); var request = await h.Next();
        h.Now=h.Now.AddMilliseconds(150); h.Input=h.Input with { Utc=h.Now };
        Assert.True(h.Policy.RequiresRefresh); h.Choose();
        Assert.False(request.Token.IsCancellationRequested);
        await h.Complete(request); Assert.Equal(ActionKind.Discard,h.Choose().Kind);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("hand")]
    [InlineData("missing")]
    public async Task Inconsistent_input_cancels_pending_result(string change)
    {
        using var h=new Harness(); h.Choose(); var request=await h.Next(); var work=h.Policy.PendingWork!;
        if(change=="stale") h.Now=h.Now.AddSeconds(2);
        if(change=="future") h.Input=h.Input with { Utc=h.Now.AddSeconds(1) };
        if(change=="hand") h.State=h.State with { Hand=h.State.Hand.Skip(1).ToArray() };
        if(change=="missing") h.Error="PUBLIC_OWN_MELDS_UNKNOWN";
        Assert.StartsWith("AKOCHAN_PENDING",h.Choose().Reasoning);
        Assert.True(request.Token.IsCancellationRequested);
        request.Completion.SetResult(request.Result()); await work.WaitAsync(Limit);
        Assert.StartsWith("AKOCHAN_PENDING",h.Choose().Reasoning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_blocks_even_a_late_completed_answer(bool completed)
    {
        using var h=new Harness(); h.Choose(); var request=await h.Next(); var task=h.Policy.PendingWork!;
        h.Now=h.Now.AddSeconds(11); h.Input=h.Input with { Utc=h.Now };
        if(completed) await h.Complete(request);
        Assert.Contains("AKOCHAN_BLOCKED: GLOBAL_AI_TIMEOUT",h.Choose().Reasoning);
        if(!completed) { request.Completion.SetResult(request.Result()); await task.WaitAsync(Limit); }
        Assert.Equal(ActionKind.Pass,h.Choose().Kind);
    }

    [Fact]
    public async Task Failure_is_explicitly_blocked_and_never_falls_back_to_upstream()
    {
        using var h=new Harness(); h.Choose(); var request=await h.Next(); var work=h.Policy.PendingWork!;
        request.Completion.SetException(new AkochanException("TEST_NATIVE_FAILURE"));
        await Assert.ThrowsAsync<AkochanException>(async()=>await work.WaitAsync(Limit));
        var choice=h.Choose();
        Assert.Equal(ActionKind.Pass,choice.Kind); Assert.Contains("AKOCHAN_BLOCKED: TEST_NATIVE_FAILURE",choice.Reasoning);
        Assert.Same(choice,h.Choose()); Assert.Single(h.Traces,t=>t.Phase=="error");
    }

    [Fact]
    public async Task Mismatching_response_identity_is_rejected()
    {
        using var h=new Harness(); h.Choose(); var request=await h.Next();
        await h.Complete(request,request.Result() with { InputSha256="wrong" });
        Assert.Contains("RESPONSE_IDENTITY_MISMATCH",h.Choose().Reasoning);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Mortal_source_accepts_only_the_selected_engine_identity(bool matching)
    {
        using var h = new Harness(mortal: true);
        h.Choose(); var request = await h.Next();
        await h.Complete(request, request.Result() with { EngineCommit = matching ? MortalInstallation.Commit : AkochanInstallation.ExpectedCommit });
        var result = h.Choose();
        if (matching) { Assert.Equal(ActionKind.Discard, result.Kind); Assert.Contains("Mortal V4", result.Reasoning); }
        else Assert.Contains("RESPONSE_IDENTITY_MISMATCH", result.Reasoning);
    }

    [Fact]
    public async Task Ranked_native_actions_are_filtered_by_current_game_allowlist()
    {
        using var h=new Harness(); h.State=h.State with { Legal=h.State.Legal with { DiscardableTiles=[Tile.FromId(28)] } };
        h.Choose(); var request=await h.Next();
        var result=request.Result();
        await h.Complete(request,result with { Candidates=result.Candidates.Insert(0,new([new("dahai",0,null,new(1),[],false)],999)) });
        Assert.Equal(28,h.Choose().DiscardTile!.Value.Id);
    }

    [Fact]
    public async Task Dispose_prevents_late_AI_result_from_becoming_action()
    {
        using var h=new Harness(); h.Choose(); var request=await h.Next(); var work=h.Policy.PendingWork!;
        h.Policy.Dispose(); Assert.True(request.Token.IsCancellationRequested);
        request.Completion.SetResult(request.Result()); await work.WaitAsync(Limit);
        Assert.StartsWith("AKOCHAN_BLOCKED",h.Choose().Reasoning);
    }

    [Fact]
    public async Task Warmup_has_no_turn_deadline_and_uses_only_fresh_input_after_ready()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var h = new Harness(preparation: ready.Task);
        Assert.StartsWith("AKOCHAN_PENDING", h.Choose().Reasoning);
        h.Now = h.Now.AddSeconds(25);
        Assert.StartsWith("AKOCHAN_PENDING", h.Choose().Reasoning);
        Assert.Null(h.Policy.PendingWork);
        Assert.Empty(h.Traces);
        ready.SetResult();
        Assert.StartsWith("AKOCHAN_PENDING", h.Choose().Reasoning); // Old observation still rejected.
        Assert.Null(h.Policy.PendingWork);
        h.Input = h.Input with { Utc = h.Now, ContextKey = "new-current-hand" };
        h.Choose(); var request = await h.Next();
        await h.Complete(request);
        Assert.Equal(ActionKind.Discard, h.Choose().Kind);
    }

    [Fact]
    public void Failed_warmup_blocks_with_visible_cause_and_dispose_never_resumes()
    {
        using var h = new Harness(preparation: Task.FromException(new AkochanException("MORTAL_FILE_HASH:test")));
        Assert.Contains("AKOCHAN_BLOCKED: MORTAL_FILE_HASH:test", h.Choose().Reasoning);
        Assert.Null(h.Policy.PendingWork);
        Assert.Single(h.Traces);
        h.Policy.Dispose();
        Assert.Contains("AKOCHAN_BLOCKED", h.Choose().Reasoning);
    }
}
