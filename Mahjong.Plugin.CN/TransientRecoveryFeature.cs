using Mahjong.Plugin.CN.Automation;
using Mahjong.Plugin.CN.Journaling;
using Mahjong.Plugin.Dalamud;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private readonly TransientRecoveryGate transientRecovery=new();
    private GameplayStopInfo? recoveringStop;
    private string? lastSubmittedProgress;
    private Guid lastSubmittedRun;
    private nint recoveryTable;
    internal bool RetryTransientErrors {get;private set;}=true;
    internal string QuickRecoveryStatus {get;private set;}="临时读取异常会立即重新核对；原任务授权有效且状态稳定后才继续。";
    internal bool QuickRecoveryPending=>transientRecovery?.Pending==true;

    internal void SetTransientRecovery(bool enabled)
    {
        RetryTransientErrors=enabled;
        if(!enabled && QuickRecoveryPending)AbandonQuickRecovery("用户关闭异常恢复。");
        SetJournalKeepMatches(JournalKeepMatches);
    }
    private string RecoveryBinding()=>ReviewHash(new {Run=taskRun?.RunId,Match=taskRun?.MatchId,
        ModeRequest=Volatile.Read(ref modeRequestVersion),Operation=Volatile.Read(ref operationGeneration),
        Source=TaskEngineIdentity,Character=CurrentCharacterContext(),Table=recoveryTable.ToInt64(),AutomationOptions});
    private static string RecoveryProgress(StateSnapshot state)=>ReviewHash(new {
        Hand=state.Hand.Select(t=>t.Id).Order().ToArray(),state.AkaDora,
        Melds=state.OurMelds.Select(m=>new {m.Kind,Ids=m.Tiles.Select(t=>t.Id).Order().ToArray()}),
        // Opponents moving or the wall counter changing does not prove our last click took effect.
        OwnRiver=state.Us.DiscardCount,state.RoundWind,state.Honba});
    private void TrackRecoverySubmission(GameplayActionSubmission submission)
    {
        if(submission.Result!="Submitted")return;
        lastSubmittedRun=taskRun?.RunId??Guid.Empty;
        try{lastSubmittedProgress=PlayRuntime?.RecoverySnapshot is { } state?RecoveryProgress(state):"unknown";}
        catch{lastSubmittedProgress="unknown";}
    }
    private bool TryBeginQuickRecovery(GameplayStopInfo stop)
    {
        if(disposed || !RetryTransientErrors || taskRun?.AllowsGameplay!=true ||
            !journalActive || journal?.Fault is not null || !readObservationAllowed ||
            !SelectedSourceAccessValid || Identity.Error is not null || !Client.IsLoggedIn ||
            stop.PreviousMode==PlayMode.Off || stop.PreviousMode==PlayMode.Automatic && !GameOperationsAuthorized ||
            !TransientRecoveryGate.Eligible(stop.Reason))return false;
        recoveryTable=GameGui.GetAddonByName("Emj").Address;
        if(recoveryTable==0 || !transientRecovery.Begin(stop.Reason,RecoveryBinding(),AutomationNow))return false;
        gameplayAllowed=false;recoveringStop=stop;
        QuickRecoveryStatus="读取或计算暂时中断，正在重新核对当前牌桌；旧请求和动作已取消。";
        Status=QuickRecoveryStatus;
        NoteUiEvent(QuickRecoveryStatus);
        RecordJournalEvent("play_recovery_started",new {Reason=stop.Reason,Attempt=transientRecovery.Attempts,
            PreviousMode=stop.PreviousMode.ToString(),NewAuthorityGranted=false});
        return true;
    }
    private void UpdateQuickRecovery()
    {
        if(!QuickRecoveryPending || recoveringStop is not { } stop)return;
        var runtime=PlayRuntime;
        bool authority=!disposed && RetryTransientErrors && Client.IsLoggedIn && Identity.Error is null &&
            journalActive && journal?.Fault is null && readObservationAllowed &&
            (!ExperimentalHandAiEnabled || !EngineMaintenanceBusy && EngineActivationError is null) &&
            SelectedSourceAccessValid && taskRun?.AllowsGameplay==true &&
            (stop.PreviousMode!=PlayMode.Automatic || GameOperationsAuthorized) &&
            GameGui.GetAddonByName("Emj").Address==recoveryTable;
        if(!authority){AbandonQuickRecovery("版本、牌桌、任务或原授权已改变。");return;}
        StateSnapshot? state=null;
        try {if(runtime is {IsObservingPaused:true} && runtime.StopCompletion.IsCompleted)state=runtime.AddonReader.TryBuildSnapshot();}
        catch { /* Read-only retry remains bounded by the gate's deadline. */ }
        if(state is not null && (state.SchemaVersion!=StateSnapshot.CurrentSchemaVersion || state.OurSeat is <0 or >3 ||
            state.Seats is not {Count:4} || state.Scores is not {Count:4} || state.AddonStateCode<0))state=null;
        bool ready=state is {AddonStateCode: not (25 or 29)} &&
            state.Hand.Count+3*state.OurMelds.Count is 13 or 14 &&
            publicRecoverySamples>=2;
        string? progress=state is null?null:RecoveryProgress(state);
        bool uncertain=stop.PreviousMode==PlayMode.Automatic && lastSubmittedRun==taskRun!.RunId &&
            lastSubmittedProgress is not null && (progress is null || lastSubmittedProgress=="unknown" || progress==lastSubmittedProgress);
        string? input=state is null?null:DecisionReviewPolicy.InputHash(state);
        var check=transientRecovery.Observe(RecoveryBinding(),input,AutomationNow,ready,uncertain);
        if(check==RecoveryCheck.Waiting)
        {
            QuickRecoveryStatus=uncertain?"正在核对上次提交的结果，尚未确认前不会再次操作。":"正在等待新读数稳定，暂停可取消恢复。";
            return;
        }
        if(check==RecoveryCheck.Abandon){AbandonQuickRecovery(transientRecovery.Code);return;}
        recoveringStop=null;
        gameplayAllowed=stop.PreviousMode==PlayMode.Automatic;
        try {runtime!.SetMode(stop.PreviousMode);} // Creates a fresh policy; no old choice/queue is replayed.
        catch {recoveringStop=stop;AbandonQuickRecovery("重新创建运行链路失败。");return;}
        if(runtime.Mode==stop.PreviousMode)
        {
            QuickRecoveryStatus="已核对并恢复原模式；沿用本次任务授权，未重发旧动作。";
            NoteUiEvent(QuickRecoveryStatus);
            RecordJournalEvent("play_recovery_resumed",new {Mode=runtime.Mode.ToString(),Attempt=transientRecovery.Attempts});
        }
        else if(!QuickRecoveryPending){recoveringStop=stop;AbandonQuickRecovery("重新创建运行链路失败。");}
    }
    private void AbandonQuickRecovery(string reason)
    {
        var stop=recoveringStop;recoveringStop=null;transientRecovery?.Cancel();
        QuickRecoveryStatus="自动恢复未完成："+reason+" 请接管或重新选择模式。";
        gameplayAllowed=false;
        SuspendTableAutomation(QuickRecoveryStatus);
        RecordJournalEvent("play_recovery_failed",new {Reason=reason});
        if(stop is not null)AlertUnexpectedStop(stop with {Reason=QuickRecoveryStatus});
    }
    private void CancelQuickRecovery()
    {transientRecovery?.Cancel();recoveringStop=null;}
}
