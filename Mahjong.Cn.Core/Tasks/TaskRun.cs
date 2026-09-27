using Mahjong.Cn.Rating;

namespace Mahjong.Cn.Tasks;

public enum TaskRunPhase { NotStarted, Running, Paused, StopAfterMatch, WaitingForData, Completed, Problem }
public sealed record TaskPlan(bool Automatic, bool Continuous, uint DutyId, string EngineIdentity,
    StopRuleSet Rules, string ProfileVersion);

/// <summary>Session authority and budgets; no game input. Never persisted as armed state.</summary>
public sealed class TaskRun
{
    public Guid RunId { get; private set; }
    public Guid? MatchId { get; private set; }
    public TaskPlan? Plan { get; private set; }
    public TaskRunPhase Phase { get; private set; }
    public string Code { get; private set; } = "NOT_STARTED";
    public string CharacterContext { get; private set; } = "";
    public int CompletedMatches { get; private set; }
    public double ActiveSeconds { get; private set; }
    public bool InMatch { get; private set; }
    public bool StopLatched { get; private set; }
    public int Generation { get; private set; }
    public RatingObservation? StartingRating { get; private set; }
    private double lastTime;
    private bool matchCompleted, departed = true;
    private Guid? requiredRatingMatch;
    private int? fourthStreak = 0;
    private double? waitingSince;
    private string? latchedCode;
    public bool AllowsGameplay => Phase is TaskRunPhase.Running or TaskRunPhase.StopAfterMatch && !matchCompleted;
    public bool AllowsNextMatch => Phase == TaskRunPhase.Running && !StopLatched;
    public bool HasUnfinishedRun => Plan is not null && Phase != TaskRunPhase.Completed;

    public void Start(TaskPlan plan, string context, RatingObservation? rating, double now, DateTimeOffset utc)
    {
        Generation++; RunId=Guid.NewGuid(); MatchId=null; Plan=plan; CharacterContext=context;
        CompletedMatches=0; ActiveSeconds=0; lastTime=now; InMatch=matchCompleted=StopLatched=false;
        departed=true; requiredRatingMatch=null; fourthStreak=0; waitingSince=null; latchedCode=null;
        StartingRating=rating?.Trusted(context,plan.ProfileVersion,utc)==true ? rating : null;
        Phase=TaskRunPhase.Running; Code="STARTED"; Tick(now,utc,context,rating);
    }
    public void ObserveTable(bool inTable)
    {
        if (!inTable) { if(matchCompleted) departed=true; return; }
        if (Phase is not (TaskRunPhase.Running or TaskRunPhase.StopAfterMatch)) return;
        if (MatchId is null || (matchCompleted && departed && AllowsNextMatch))
        { MatchId=Guid.NewGuid(); InMatch=true; matchCompleted=false; departed=false; }
    }
    public bool CompleteMatch(Guid id, int? verifiedPlacement = null)
    {
        if (MatchId!=id || matchCompleted || !InMatch) return false;
        matchCompleted=true; InMatch=false; CompletedMatches++; requiredRatingMatch=id;
        fourthStreak=verifiedPlacement is null ? null : verifiedPlacement==4 ? fourthStreak+1 : 0;
        return true;
    }
    public void Tick(double now, DateTimeOffset utc, string context, RatingObservation? rating)
    {
        Advance(now);
        if (Plan is null || Phase is TaskRunPhase.Completed or TaskRunPhase.Problem) return;
        if (context != CharacterContext || string.IsNullOrEmpty(context)) { Fail("CHARACTER_CONTEXT_CHANGED",now); return; }
        if (Phase==TaskRunPhase.Paused) return;
        if (StopLatched)
        { Code=latchedCode??"USER_AFTER_MATCH"; Phase=InMatch ? TaskRunPhase.StopAfterMatch : TaskRunPhase.Completed; return; }
        var result=StopRuleEvaluator.Evaluate(Plan.Rules,new(CompletedMatches,ActiveSeconds,InMatch,false,
            context,Plan.ProfileVersion,utc,rating,StartingRating,requiredRatingMatch,fourthStreak));
        Code=result.Code;
        if(result.Decision==StopDecision.StopImmediately) { Fail(result.Code,now); return; }
        if(result.Decision==StopDecision.StopAfterMatch)
        { StopLatched=true;latchedCode=result.Code; Phase=InMatch ? TaskRunPhase.StopAfterMatch : TaskRunPhase.Completed; Generation++; return; }
        Phase=result.Decision==StopDecision.HoldForRequiredData ? TaskRunPhase.WaitingForData : TaskRunPhase.Running;
        if(Phase==TaskRunPhase.WaitingForData)
        { waitingSince??=ActiveSeconds; if(ActiveSeconds-waitingSince>=120)Fail("REQUIRED_DATA_TIMEOUT",now); }
        else waitingSince=null;
    }
    public void RequestStopAfterMatch() { StopLatched=true; latchedCode??="USER_AFTER_MATCH"; Code=latchedCode; Generation++; }
    public void ConfirmEngineForResume(string engine)
    { if(Phase==TaskRunPhase.Paused && Plan is not null) Plan=Plan with {EngineIdentity=engine}; }
    public void Pause(double now)
    { Advance(now); if(HasUnfinishedRun) { Phase=TaskRunPhase.Paused; Code="PAUSED"; Generation++; } }
    public bool Resume(double now, DateTimeOffset utc, string context, RatingObservation? rating)
    {
        if(Phase!=TaskRunPhase.Paused) return false;
        lastTime=now; Phase=TaskRunPhase.Running; Generation++; Tick(now,utc,context,rating);
        return AllowsGameplay || AllowsNextMatch;
    }
    public void End(double now) { Advance(now); Phase=TaskRunPhase.Completed; Code="USER_ENDED"; Generation++; }
    public void Fail(string reason,double now) { Advance(now); Phase=TaskRunPhase.Problem; Code=reason; Generation++; }
    private void Advance(double now)
    {
        if (!double.IsFinite(now)) return;
        if(Phase is TaskRunPhase.Running or TaskRunPhase.StopAfterMatch or TaskRunPhase.WaitingForData)
            ActiveSeconds+=Math.Max(0,now-lastTime);
        lastTime=Math.Max(lastTime,now);
    }
}
