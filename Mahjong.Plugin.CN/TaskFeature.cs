using System.Text.Json;
using Mahjong.Cn.Tasks;
using Mahjong.Plugin.CN.Automation;
using Mahjong.Plugin.CN.Readers;
using Mahjong.Plugin.Dalamud;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private readonly TaskRun taskRun = new();
    private TableAutomationOptions? taskAutomationSnapshot;
    private bool resumingTask;
    private string? lastTaskBlock;
    private string? normalTaskStopReason;
    private int taskSettingsSaving;
    internal StopRuleSet TaskRules { get; private set; } = new();
    internal string TaskSettingsStatus { get; private set; } = "配置不代表启动授权。";
    internal TaskRun TaskRun => taskRun;
    private string TaskEngineIdentity => DecisionSourceLabel + ":" + (ExperimentalHandAiEnabled ? SelectedEngineId : "upstream");
    internal string TaskSummary => taskRun.Plan is null ? "任务未启动。" :
        $"任务：{TaskPhaseLabel(taskRun.Phase)} · 已完成 {taskRun.CompletedMatches} 场 · 有效运行 {taskRun.ActiveSeconds/60:F1} 分钟 · {TaskReason(taskRun.Code)}";
    private static string TaskPhaseLabel(TaskRunPhase phase)=>phase switch
    { TaskRunPhase.Running=>"运行中",TaskRunPhase.Paused=>"已暂停",TaskRunPhase.StopAfterMatch=>"本场后停",TaskRunPhase.WaitingForData=>"等待必要数据",TaskRunPhase.Completed=>"已完成",TaskRunPhase.Problem=>"需处理问题",_=>"未启动" };
    private static string TaskReason(string code)=>code switch
    { "CONTINUE"=>"继续当前授权", "MATCH_LIMIT"=>"达到场数上限", "DURATION_LIMIT"=>"达到有效时长", "DEADLINE"=>"达到截止时间", "USER_AFTER_MATCH"=>"用户要求本场后停", "RATING_REFRESH_REQUIRED"=>"等待与本场关联的可信评分", "USER_ENDED"=>"用户结束", "PAUSED"=>"继续时重新预检", "CHARACTER_CONTEXT_CHANGED"=>"角色上下文已改变", _=>code };
    private string TaskSettingsPath=>Path.Combine(Interface.GetPluginConfigDirectory(),"task-presets.json");
    private sealed record SavedTaskRules(int SchemaVersion,StopRuleSet Rules);
    private void LoadTaskRules()
    {
        try
        {
            if(!File.Exists(TaskSettingsPath))return;
            if(new FileInfo(TaskSettingsPath).Length>16384)throw new InvalidDataException();
            var saved=JsonSerializer.Deserialize<SavedTaskRules>(File.ReadAllText(TaskSettingsPath));
            if(saved is not {SchemaVersion:1,Rules:not null} || saved.Rules.Validate() is not null)throw new InvalidDataException();
            TaskRules=saved.Rules with {MatchLimit=0}; // MatchLimit has exactly one persisted owner: table-automation.json.
        }
        catch(Exception ex) { TaskSettingsStatus="任务预设读取失败，原文件保留："+ex.GetType().Name; }
    }
    internal void SaveTaskRules(StopRuleSet rules)
    {
        if(rules.Validate() is { } error){TaskSettingsStatus=error;return;}
        if(Interlocked.CompareExchange(ref taskSettingsSaving,1,0)!=0){TaskSettingsStatus="正在保存，请稍候。";return;}
        PausePlay();
        string path=TaskSettingsPath;
        _=Task.Run(async () =>
        {
            string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                if(File.Exists(path))
                {
                    var old=JsonSerializer.Deserialize<SavedTaskRules>(await File.ReadAllTextAsync(path));
                    if(old?.SchemaVersion!=1)throw new InvalidDataException("FUTURE_OR_UNKNOWN_SCHEMA");
                    File.Copy(path,path+".bak",true);
                }
                await File.WriteAllTextAsync(tmp,JsonSerializer.Serialize(new SavedTaskRules(1,rules with {MatchLimit=0})));
                File.Move(tmp,path,true);
                await Framework.RunOnFrameworkThread(()=>{TaskRules=rules with {MatchLimit=0};TaskSettingsStatus="已保存。运行授权不会恢复；继续旧任务保留其原规则，新任务才使用新规则。";});
            }
            catch(Exception ex){TaskSettingsStatus="任务预设未保存："+ex.GetType().Name;}
            finally{try{if(File.Exists(tmp))File.Delete(tmp);}catch(IOException){}catch(UnauthorizedAccessException){} Interlocked.Exchange(ref taskSettingsSaving,0);}
        });
    }
    private bool BeginManagedTask(bool automatic,bool continuous)
    {
        if (!resumingTask && (automatic || continuous || ExperimentalHandAiEnabled) && !RequireTestAccessCore()) return false;
        if((automatic || continuous) && !RequireOperationCapability())return false;
        if(taskRun is null || resumingTask)return true; // Existing isolated runtime fixtures have no plugin coordinator.
        if(taskRun.HasUnfinishedRun){Status="现有任务尚未结束，请继续同一任务，或明确结束后新建。";return false;}
        string context=CurrentCharacterContext();
        if(context.Length==0){Status="预检未通过：本人角色上下文未就绪。";return false;}
        var rules=continuous ? TaskRules with {MatchLimit=AutomationOptions.MatchLimit} : !automatic ? TaskRules with {MatchLimit=1} : new StopRuleSet(MatchLimit:1);
        if(rules.NeedsRating && !MahjongRatingReader.MatchRefreshVerified)
        {Status="评分目标暂不可用：资料页字段已核对，但整场后刷新关联尚未实机验证。";return false;}
        if(rules.ConsecutiveFourthLimit is not null){Status="连续第四名规则暂不可用：最终名次读取尚未验证。";return false;}
        if(automatic && ExperimentalHandAiEnabled && EngineActivationError is { } engineError){Status="预检未通过："+engineError;return false;}
        taskAutomationSnapshot=AutomationOptions;
        ratingReadingEnabled=true;
        taskRun.Start(new(automatic,continuous,continuous?AutomationOptions.DutyId:0,TaskEngineIdentity,rules,MahjongRatingReader.Profile),context,CurrentRating,AutomationNow,DateTimeOffset.UtcNow);
        PreserveQualifiedTaskAccess();
        lastTaskBlock=null;
        normalTaskStopReason=null;
        RecordJournalEvent("task_started",new { taskRun.RunId,Mode=automatic?"automatic":"manual",Continuous=continuous,Rules=rules,Engine=TechnicalDecisionSource,AutomaticRestored=false });
        Status=TaskSummary;
        return taskRun.AllowsGameplay || taskRun.AllowsNextMatch;
    }
    internal void StopAfterCurrentMatch()
    { lock(gate){ if(taskRun.HasUnfinishedRun){taskRun.RequestStopAfterMatch();UpdateTaskCore();} } }
    internal void EndTask()
    { PausePlay();lock(gate){taskRun.End(AutomationNow);Status=TaskSummary;} }
    internal void StartAutomaticFromToolbar()
    {
        lock(gate)
        {
            if(disposed || !RequireSelectedSourceAccess() || !RequireOperationCapability())return;
            if(taskRun?.HasUnfinishedRun!=true) { ActivatePlay(true); return; }
            if(taskRun.Phase is TaskRunPhase.Problem or TaskRunPhase.WaitingForData)
            { Status="请先处理当前任务的数据问题："+TaskSummary; return; }
            if(gameplayAllowed && PlayRuntime?.Mode==PlayMode.Automatic && taskRun.Plan?.Automatic==true)return;
            // Explicit mode change: preserve the same run, rules, elapsed time and match counts.
            PausePlay();
            // Resume supersedes the queued pause, so invalidate the old policy now too.
            PlayRuntime?.PauseAutomation("用户切换为自动打牌，正在重新预检。");
            ResumeTaskCore(true);
        }
    }
    internal void ResumeTask() => ResumeTaskCore(null);
    private void ResumeTaskCore(bool? automatic)
    {
        if(disposed || !RequireSelectedSourceAccess())return;
        bool needsOperations=automatic==true || taskRun?.Plan is { } requestedPlan && (requestedPlan.Automatic || requestedPlan.Continuous);
        if (!TestAccessUnlocked && automatic == true && taskRun?.Plan?.Automatic != true)
        { Status="测试资格已到期，只可继续原任务模式；改为自动打牌需重新验证。"; return; }
        if(needsOperations && !RequireOperationCapability())return;
        int operationEpoch=Volatile.Read(ref operationGeneration);
        RevokeUiIntents();
        int request=Interlocked.Increment(ref modeRequestVersion);
        _=Framework.RunOnFrameworkThread(()=>
        {
            lock(gate)
            {
                if(disposed || request!=Volatile.Read(ref modeRequestVersion) || !RequireSelectedSourceAccess())return;
                if(needsOperations && (operationEpoch!=Volatile.Read(ref operationGeneration) || !RequireOperationCapability()))return;
                Identity=RuntimeIdentity.Read(Interface,Client);
                if(Identity.Error is not null || !Client.IsLoggedIn || (ExperimentalHandAiEnabled && EngineMaintenanceBusy) ||
                    (ExperimentalHandAiEnabled && EngineActivationError is not null) || taskRun?.Plan is not { } plan)
                {Status="继续预检未通过：请检查版本、登录和模型状态。";return;}
                if(plan.Continuous && taskAutomationSnapshot!=AutomationOptions)
                {Status="配置与本次任务快照不同；请还原配置后继续，或结束旧任务后创建新任务，计数未重置。";return;}
                taskRun.ConfirmEngineForResume(TaskEngineIdentity,automatic);
                if(!CheckLowerHandResource() || !taskRun.Resume(AutomationNow,DateTimeOffset.UtcNow,CurrentCharacterContext(),CurrentRating))
                {Status=TaskSummary;return;}
                PreserveQualifiedTaskAccess();
                if(needsOperations && !GrantTaskOperations(operationEpoch))return;
                resumingTask=true;
                ratingReadingEnabled=true;
                RecordJournalEvent("task_resumed",new {taskRun.RunId,Engine=TaskEngineIdentity,taskRun.CompletedMatches,taskRun.ActiveSeconds});
                try
                {
                    if(plan.Continuous) tableAutomation.Resume(AutomationNow,automatic);
                    if(taskRun.InMatch || !plan.Continuous) ActivatePlayCore(automatic??plan.Automatic,request);
                }
                finally{resumingTask=false;}
                lastTaskBlock=null;Status=TaskSummary;
            }
        });
    }
    private void PauseTaskAuthority()
    { if(!resumingTask && taskRun?.HasUnfinishedRun==true){taskRun.Pause(AutomationNow);gameplayAllowed=false;Interlocked.Increment(ref modeRequestVersion);} }
    private void UpdateTaskCore()
    {
        if(taskRun?.Plan is null)return;
        if(taskRun.Phase is TaskRunPhase.Paused or TaskRunPhase.Completed or TaskRunPhase.Problem)return;
        bool inDuty=Conditions[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty] ||
            Conditions[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty56] || Conditions[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty95];
        bool ownTable=inDuty && Client.TerritoryType==831;
        taskRun.ObserveTable(ownTable);
        taskRun.Tick(AutomationNow,DateTimeOffset.UtcNow,CurrentCharacterContext(),CurrentRating);
        if(!taskRun.AllowsGameplay && taskRun.Phase!=TaskRunPhase.Paused && lastTaskBlock!=taskRun.Code)
        {
            lastTaskBlock=taskRun.Code;gameplayAllowed=false;
            Interlocked.Increment(ref modeRequestVersion);Interlocked.Increment(ref automationRequestVersion);
            normalTaskStopReason="TASK_BOUNDARY: "+TaskSummary;
            PlayRuntime?.PauseAutomation(normalTaskStopReason);
            RecordJournalEvent("task_boundary",new {taskRun.RunId,taskRun.CompletedMatches,Phase=taskRun.Phase.ToString(),taskRun.Code});
            Status=TaskSummary+"；已提交的匹配请在游戏内取消，不会自动确认下一场。";
        }
    }
}
