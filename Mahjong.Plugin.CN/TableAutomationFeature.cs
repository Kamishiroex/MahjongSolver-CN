using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.DutyState;
using Dalamud.Interface.ImGuiNotification;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Data.Files;
using Lumina.Excel.Sheets;
using Mahjong.Plugin.CN.Automation;
using Mahjong.Plugin.Dalamud;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private readonly TableAutomation tableAutomation = new();
    internal TableAutomationOptions AutomationOptions { get; private set; } = new();
    internal bool TableAutomationArmed => tableAutomation.Armed;
    internal string TableAutomationStatus => tableAutomation.Status;
    internal string TableAutomationProgress => taskRun?.Plan is not null ? TaskSummary : AutomationOptions.MatchLimit == 0
        ? $"已完成 {tableAutomation.CompletedMatches} 场 / 无限循环"
        : $"已完成 {tableAutomation.CompletedMatches} / {AutomationOptions.MatchLimit} 场";
    private const string MatchCompletedStop = "MATCH_COMPLETE：整场对局已完成，自动打牌停止，交由循环排队处理结算。";
    private double lastAutomationPoll;
    private bool startingFromTableAutomation;
    private int automationRequestVersion;
    private string? recordedAutomationStatus;
    private string AutomationSettingsPath => Path.Combine(Interface.GetPluginConfigDirectory(), "table-automation.json");
    private static double AutomationNow => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void LoadTableAutomationOptions()
    {
        try
        {
            if (!File.Exists(AutomationSettingsPath)) return;
            var options = JsonSerializer.Deserialize<TableAutomationOptions>(File.ReadAllText(AutomationSettingsPath));
            if (options is not null && options.ValidSelection &&
                options.MatchLimit is >= 0 and <= 9999) AutomationOptions = options;
        }
        catch (Exception ex) { Log.Warning("读取自动排队设置失败：{Type}", ex.GetType().Name); }
        // Preferences persist; authority to queue/input does not survive unload/login.
    }

    internal void SetTableAutomationOptions(TableAutomationOptions options)
    {
        lock (gate)
        {
            if (disposed || !options.ValidSelection || options.MatchLimit is < 0 or > 9999) return;
            if (options != AutomationOptions)
                PausePlay(); // Pause the runtime too; do not leave it armed behind a revoked input gate.
            try
            {
                string temp = AutomationSettingsPath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(options));
                File.Move(temp, AutomationSettingsPath, true);
                AutomationOptions = options;
                if (PlayRuntime is { } runtime)
                {
                    runtime.AutoAdvancePreference = options.AutoAdvanceAfterHand;
                    runtime.KeepAutomaticBetweenHandsPreference = options.KeepAutomaticBetweenHands;
                    runtime.ConfigService.Update(c => c with
                    { AutoAdvanceAfterHand = runtime.Mode == PlayMode.Automatic && options.AutoAdvanceAfterHand,
                        KeepAutomaticBetweenHands = options.KeepAutomaticBetweenHands });
                }
            }
            catch (Exception ex) { tableAutomation.Disarm("保存自动设置失败：" + ex.GetType().Name); }
        }
    }

    internal void ArmTableAutomation()
    {
        if(disposed || !RequireSelectedSourceAccess() || !RequireTestAccessCore())return;
        int operationEpoch=Volatile.Read(ref operationGeneration);
        int request = Interlocked.Increment(ref automationRequestVersion);
        _ = Framework.RunOnFrameworkThread(() =>
        {
            lock (gate)
            {
                if (disposed || request != Volatile.Read(ref automationRequestVersion) || operationEpoch!=Volatile.Read(ref operationGeneration) ||
                    !RequireSelectedSourceAccess() || !RequireTestAccessCore()) return;
                Identity = RuntimeIdentity.Read(Interface, Client);
                if (Identity.Error is not null || !Client.IsLoggedIn)
                { tableAutomation.Disarm(Identity.Error ?? "请先登录游戏。"); return; }
                if (!CheckLowerHandResource()) return;
                try
                {
                    if (AutomationOptions.AutoQueue)
                    {
                        if (!AutomationOptions.ValidSelection) throw new InvalidOperationException("麻将桌型选择无效。");
                        foreach (uint id in AutomationOptions.SelectedDuties)
                        {
                            var duty = MahjongDuties.Find(id)!;
                            var row = DataManager.GetExcelSheet<ContentFinderCondition>().GetRow(id);
                            if (!CnMatchmakingAdapter.MatchesSheet(duty, row)) throw new InvalidOperationException("麻将排队数据表与适配版本不一致。");
                        }
                        var resource = DataManager.GetFile<UldFile>("ui/uld/contentsfinderconfirm.uld");
                        if (resource is null || Convert.ToHexString(SHA256.HashData(resource.Data)) != CnMatchmakingAdapter.ConfirmUldHash)
                            throw new InvalidOperationException("匹配确认界面资源与适配版本不一致。");
                    }
                    if (journalActive && journal?.Fault is not null) throw new InvalidOperationException("请先处理当前日志存储错误。");
                    EnsureJournalCore("table_automation");
                    if (AutomationOptions.AutoQueue)
                    {
                        var duty = MahjongDuties.Find(AutomationOptions.DutyId)!;
                        bool leader = Objects.LocalPlayer is { } local && Party.PartyLeaderIndex < Party.Length && Party[(int)Party.PartyLeaderIndex]?.EntityId == local.EntityId;
                        if (MahjongDuties.PartyError(duty, Party.Length, Party.IsAlliance, leader) is { } partyError) throw new InvalidOperationException(partyError);
                        if (CnMatchmakingAdapter.ReadQueue(AutomationOptions.SelectedDuties).Phase != QueuePhase.None) throw new InvalidOperationException("已有报名，不能作为新任务接管；请先在游戏内取消。");
                    }
                    if (!BeginManagedTask(AutomationOptions.AutoStart, true)) { tableAutomation.Disarm(Status); return; }
                    if(!GrantTaskOperations(operationEpoch))return;
                    RequestSelectedMortalWarmup();
                    UpdateSelectedMortalWarmup();
                    tableAutomation.Arm(AutomationOptions with { MatchLimit = 0 }, AutomationNow);
                }
                catch (Exception ex) { tableAutomation.Disarm("自动功能未启动：" + ex.Message); }
                RecordTableAutomationState();
            }
        });
    }

    private void SuspendTableAutomation(string reason)
    {
        RevokeGameOperations();
        Interlocked.Increment(ref automationRequestVersion);
        lock (gate)
        {
            PauseTaskAuthority();
            tableAutomation.Disarm(reason);
            RecordTableAutomationState();
        }
    }

    internal void StopTableAutomation() => PausePlay();

    private void OnAutomationDutyCompleted(IDutyStateEventArgs args)
    {
        uint dutyId = args.ContentFinderCondition.RowId;
        uint territory = args.TerritoryType.RowId;
        int request = Volatile.Read(ref automationRequestVersion);
        Guid? observedRun, observedMatch;
        string observedRatingContext;
        Journaling.GameJournal? observedJournal;
        int observedOperationEpoch;
        lock(gate) { observedRun=taskRun?.RunId; observedMatch=taskRun?.MatchId; observedRatingContext=ratingContext; observedOperationEpoch=operationGeneration; observedJournal=journal; }
        // Events may originate in the network handler. Input/cleanup stays on the framework.
        _ = Framework.RunOnFrameworkThread(() =>
        {
            lock (gate)
            {
                if (!disposed && territory == 831 && MahjongDuties.Find(dutyId) is not null && journalActive && ReferenceEquals(journal,observedJournal))
                {
                    if(!reviewDutyCompleted)
                    {
                        RecordJournalEvent("match_result", new { DutyId = dutyId, MatchId=journal!.SessionId, Source = "IDutyState.DutyCompleted" });
                        reviewRatingPending=(journal!,journal!.SessionId,observedRatingContext,DateTimeOffset.UtcNow);
                        finalResultPending=reviewRatingPending;
                        reviewDutyCompleted=true;
                    }
                }
                if (disposed || territory != 831 || !Client.IsLoggedIn || Identity.Error is not null) return;
                if (GameOperationsAuthorized && observedOperationEpoch==operationGeneration && observedRun==taskRun?.RunId &&
                    ratingReadingEnabled && observedRatingContext.Length > 0 &&
                    observedRatingContext == CurrentCharacterContext() && MahjongDuties.Find(dutyId) is not null)
                    RequestRatingRefresh(true);
                if (journalActive && journal?.Fault is not null) return;
                bool ownedAutomation=request==Volatile.Read(ref automationRequestVersion);
                if (taskRun?.Plan is { } taskPlan && taskRun.MatchId is { } match &&
                    taskRun.RunId==observedRun && match==observedMatch && taskRun.CharacterContext==CurrentCharacterContext() &&
                    (taskPlan.DutyId == 0 || taskPlan.DutyId == dutyId ||
                        taskPlan.Continuous && taskAutomationSnapshot?.ContainsDuty(dutyId) == true) && taskRun.CompleteMatch(match))
                {
                    RecordJournalEvent("task_match_completed",new {taskRun.RunId,MatchId=match,ReviewMatchId=journal?.SessionId,taskRun.CompletedMatches,DutyId=dutyId,Engine=TaskEngineIdentity});
                    UpdateTaskCore();
                }
                if(!ownedAutomation)return; // Completion observations survive pause; old action authority does not.
                if (!tableAutomation.ObserveMatchCompleted(dutyId, AutomationNow)) return;
                RecordJournalEvent("table_match_completed", new
                { DutyId = dutyId, Source = "IDutyState.DutyCompleted", tableAutomation.CompletedMatches, AutomationOptions.MatchLimit });
                PlayRuntime?.StopAutomation(MatchCompletedStop);
                RecordTableAutomationState();
            }
        });
    }

    private void RecordTableAutomationState()
    {
        string key = tableAutomation.Armed + ":" + tableAutomation.Status + ":" + tableAutomation.CompletedMatches;
        if (!journalActive || recordedAutomationStatus == key) return;
        recordedAutomationStatus = key;
        RecordJournalEvent("table_automation_state", new
        {
            tableAutomation.Armed, tableAutomation.Status, AutomationOptions.AutoQueue,
            AutomationOptions.AutoStart, AutomationOptions.DutyId, AutomationOptions.SecondaryDutyId, tableAutomation.MatchedDutyId,
            AutomationOptions.MatchLimit, tableAutomation.CompletedMatches, tableAutomation.MatchCompleted,
        });
    }

    private unsafe void UpdateTableAutomationCore()
    {
        if (!tableAutomation.Armed || QuickRecoveryPending) return;
        double now = AutomationNow;
        if (now - lastAutomationPoll < 0.25) return;
        lastAutomationPoll = now;
        string? unavailable = !SelectedSourceAccessValid ? BetaExpired : !GameOperationsAuthorized ? "本次自动任务授权已失效。" : Identity.Error ??
            (!Client.IsLoggedIn ? "已登出游戏。" : journalActive && journal?.Fault is { } fault ? "日志错误：" + fault : null);
        if (unavailable is not null)
        {
            tableAutomation.Disarm("自动功能已停止：" + unavailable);
            ReportTableAutomationFailure(); RecordTableAutomationState(); return;
        }
        try
        {
            // Readiness enters the coordinator BEFORE it reserves any game action.
            bool engineReady = AwaitSelectedMortalForTable();
            if (!tableAutomation.Armed) return;
            if (taskRun?.Plan is not null && !taskRun.AllowsNextMatch && !tableAutomation.MatchCompleted && !taskRun.InMatch)
            { tableAutomation.Disarm(TaskSummary); RecordTableAutomationState(); return; }
            var table = (AtkUnitBase*)GameGui.GetAddonByName("Emj").Address;
            bool visible = table != null && table->IsVisible;
            bool inDuty = Conditions[ConditionFlag.BoundByDuty] || Conditions[ConditionFlag.BoundByDuty56] || Conditions[ConditionFlag.BoundByDuty95];
            bool busy = Conditions[ConditionFlag.BetweenAreas] || Conditions[ConditionFlag.BetweenAreas51] || Conditions[ConditionFlag.LoggingOut];
            bool tableBusy = busy || (visible && !table->IsReady);
            bool queueBusy = busy || Conditions[ConditionFlag.InCombat] || Conditions[ConditionFlag.Casting] ||
                Conditions[ConditionFlag.OccupiedInCutSceneEvent] || Conditions[ConditionFlag.OccupiedInQuestEvent] ||
                Conditions[ConditionFlag.Occupied] || Conditions[ConditionFlag.OccupiedInEvent] ||
                Conditions[ConditionFlag.TradeOpen] || Conditions[ConditionFlag.ExecutingCraftingAction] ||
                Conditions[ConditionFlag.ExecutingGatheringAction];
            var q = AutomationOptions.AutoQueue ? CnMatchmakingAdapter.ReadQueue(AutomationOptions.SelectedDuties) : (QueuePhase.None, false, false);
            nint confirm = GameGui.GetAddonByName("ContentsFinderConfirm").Address;
            uint currentDuty = DutyState.ContentFinderCondition.RowId;
            bool canLeave = tableAutomation.MatchCompleted && inDuty && !busy &&
                currentDuty == tableAutomation.MatchedDutyId && EventFramework.CanLeaveCurrentContent();
            var action = tableAutomation.Tick(now, new(Client.IsLoggedIn, visible, inDuty,
                visible || tableAutomation.MatchCompleted ? tableBusy : queueBusy,
                PlayRuntime?.Mode is PlayMode.Manual or PlayMode.Automatic, q.Item1, q.Item2, q.Item3,
                confirm != 0 && CnMatchmakingAdapter.CanAccept(confirm), currentDuty, canLeave, engineReady));
            if (!SelectedSourceAccessValid || !GameOperationsAuthorized) { EnforceBetaAccess(); return; }
            if (action == TableAutomationAction.Queue)
            {
                if (taskRun?.Plan is not null && !taskRun.AllowsNextMatch) return;
                var duty = MahjongDuties.Find(AutomationOptions.DutyId)!;
                bool leader = Objects.LocalPlayer is { } local && Party.PartyLeaderIndex < Party.Length &&
                    Party[(int)Party.PartyLeaderIndex]?.EntityId == local.EntityId;
                string? partyError = MahjongDuties.PartyError(duty, Party.Length, Party.IsAlliance, leader);
                if (partyError is not null) throw new InvalidOperationException(partyError);
                CnMatchmakingAdapter.Queue(AutomationOptions.SelectedDuties);
            }
            else if (action == TableAutomationAction.Accept)
            {
                if (taskRun?.Plan is not null && !taskRun.AllowsNextMatch) return;
                var check = CnMatchmakingAdapter.ReadQueue(AutomationOptions.SelectedDuties);
                if (check.Phase != QueuePhase.Ready || !check.Matches || !check.PopMatches)
                    throw new InvalidOperationException("匹配确认状态已改变。");
                CnMatchmakingAdapter.Accept(confirm);
            }
            else if (action == TableAutomationAction.StartPlay)
            {
                if (taskRun?.Plan is not null && !taskRun.AllowsGameplay) return;
                startingFromTableAutomation = true;
                try { ActivatePlayCore(true, Interlocked.Increment(ref modeRequestVersion)); }
                finally { startingFromTableAutomation = false; }
                if (PlayRuntime?.Mode != PlayMode.Automatic) tableAutomation.Disarm("自动开打未成功：" + Status);
            }
            else if (action == TableAutomationAction.LeaveCompletedMatch)
            {
                if (!tableAutomation.Armed || !tableAutomation.MatchCompleted ||
                    DutyState.ContentFinderCondition.RowId != tableAutomation.MatchedDutyId ||
                    !EventFramework.CanLeaveCurrentContent())
                    throw new InvalidOperationException("已完成对局的退桌条件发生变化。");
                EventFramework.LeaveCurrentContent(false);
            }
            if (action != TableAutomationAction.None)
                RecordJournalEvent("table_automation_action", new { Action = action.ToString(), AutomationOptions.DutyId,
                    AutomationOptions.SecondaryDutyId, tableAutomation.MatchedDutyId, DecisionSource = TechnicalDecisionSource });
        }
        catch (Exception ex)
        {
            tableAutomation.Disarm("自动排队 / 进桌开打已停止：" + ex.Message);
            Log.Warning("CN table automation stopped: {Type} {Reason}", ex.GetType().Name, ex.Message);
        }
        if (!tableAutomation.Armed && taskRun?.Phase != Mahjong.Cn.Tasks.TaskRunPhase.Completed && !(AutomationOptions.MatchLimit > 0 &&
            tableAutomation.CompletedMatches >= AutomationOptions.MatchLimit &&
            tableAutomation.Status.StartsWith("已完成设定的", StringComparison.Ordinal)))
            ReportTableAutomationFailure();
        RecordTableAutomationState();
    }

    private void ReportTableAutomationFailure()
    {
        Status = tableAutomation.Status;
        Open();
        try
        {
            Notifications.AddNotification(new Notification
            {
                Title = Brand.ProductName + "：自动排队已停止", Content = tableAutomation.Status,
                Type = NotificationType.Error, RespectUiHidden = false,
                InitialDuration = TimeSpan.FromSeconds(30),
            });
        }
        catch (Exception ex) { Log.Warning("自动排队通知失败：{Type}", ex.GetType().Name); }
        MessageBeep(0x30);
    }
}
