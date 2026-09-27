namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private const string BetaExpired = "BETA_ACCESS_EXPIRED：测试版验证失效，相关操作已暂停；请在设置 → 测试版重新验证，原选择与任务进度已保留。";
    private int betaGeneration;
    private bool betaExpiryHandled;
    private Access.QualifiedTaskAccess? qualifiedTaskAccess;
    internal bool QualifiedTaskContinues => (TestAccessUnlocked || testAccess.HasExpired) && Volatile.Read(ref qualifiedTaskAccess)?.Matches(taskRun,
        TaskEngineIdentity, Volatile.Read(ref betaGeneration)) == true &&
        (taskRun.HasUnfinishedRun || TableAutomationArmed && tableAutomation.MatchCompleted || RatingRefreshBusy);
    internal bool BetaRuntimeAccessValid => TestAccessUnlocked || QualifiedTaskContinues;
    internal bool SelectedSourceAccessValid => !ExperimentalHandAiEnabled || BetaRuntimeAccessValid;
    private int operationGeneration;
    private int pendingOperationRequest = -1;
    private int pendingOperationGeneration;
    private Access.TaskOperationGrant? taskOperationGrant;
    // A valid lease exposes every beta capability, including after reload.
    // Availability is not a run intent: ordinary Start/Resume still creates a task grant.
    internal bool GameOperationsAvailable => TestAccessUnlocked;
    internal bool TaskOperationsAvailable => GameOperationsAvailable || QualifiedTaskContinues && taskRun.Plan is { } plan && (plan.Automatic || plan.Continuous);
    internal bool GameOperationsAuthorized => TaskOperationsAvailable && taskRun?.Plan is not null &&
        Volatile.Read(ref taskOperationGrant)?.Allows(TaskOperationsAvailable,taskRun.RunId,
            CurrentCharacterContext(),Volatile.Read(ref operationGeneration)) == true;
    private bool RequireOperationCapability()
    {
        if (TaskOperationsAvailable) return true;
        if (!RequireTestAccessCore()) { Status=BetaAccessStatus; return false; }
        return true;
    }
    private void PreserveQualifiedTaskAccess()
    {
        if (TestAccessUnlocked && taskRun?.Plan is { } plan && (plan.Automatic || plan.Continuous || ExperimentalHandAiEnabled))
            Volatile.Write(ref qualifiedTaskAccess, new(taskRun.RunId, taskRun.CharacterContext,
                plan, TaskEngineIdentity, Volatile.Read(ref betaGeneration)));
    }
    private void RevokeGameOperations()
    {
        CancelQuickRecovery();
        RevokeUiIntents();
        Interlocked.Increment(ref operationGeneration);
        Volatile.Write(ref taskOperationGrant,null);
        pendingOperationRequest=-1;
        ratingOperationGeneration=-1;
        ratingRefresh?.Cancel("游戏操作授权已撤销，资料窗口留给玩家处理。",false);
    }
    private bool GrantTaskOperations(int generation)
    {
        if(!TaskOperationsAvailable || generation!=Volatile.Read(ref operationGeneration) || taskRun?.Plan is null)return false;
        string context=CurrentCharacterContext();
        if(context.Length==0 || context!=taskRun.CharacterContext)return false;
        Volatile.Write(ref taskOperationGrant,new(taskRun.RunId,context,generation));
        RecordJournalEvent("task_operations_authorized",new {taskRun.RunId,Generation=generation,
            taskRun.Plan.Automatic,taskRun.Plan.Continuous,Source="explicit-user-task-start-or-resume"});
        return GameOperationsAuthorized;
    }
    internal string BetaAccessStatus { get; private set; } = "另提供测试版功能，验证后可用；不影响标准求解器。";

    // Capability checks do not stop unrelated standard play or observation.
    private bool RequireTestAccessCore()
    {
        if (TestAccessUnlocked) return true;
        BetaAccessStatus = "测试版需验证，请前往设置 → 测试版。";
        EnforceBetaAccess();
        return false;
    }
    private bool RequireSelectedSourceAccess() => !ExperimentalHandAiEnabled || BetaRuntimeAccessValid || RequireTestAccessCore();

    private void EnforceBetaAccess()
    {
        if (TestAccessUnlocked) { betaExpiryHandled = false; return; }
        aiProbe?.Stop("测试版验证失效，自检已取消。");
        if (QualifiedTaskContinues)
        {
            BetaAccessStatus = "测试资格已到期；本次任务继续有效（含无限循环），结束或重载后新任务需重新验证。";
            return;
        }
        bool taskOperations=PlayRuntime?.Mode==Mahjong.Plugin.Dalamud.PlayMode.Automatic || TableAutomationArmed || taskOperationGrant is not null;
        if (!ExperimentalHandAiEnabled && !taskOperations)
        {
            if(RatingRefreshBusy || pendingOperationRequest>=0) { RevokeGameOperations(); BetaAccessStatus=BetaExpired; }
            return; // A profile-only refresh expiry must not pause standard hints.
        }
        if (betaExpiryHandled) return;
        betaExpiryHandled = true;
        var previousMode = PlayRuntime?.Mode ?? Mahjong.Plugin.Dalamud.PlayMode.Off;
        gameplayAllowed = false;
        RevokeGameOperations();
        Interlocked.Increment(ref betaGeneration);
        Interlocked.Increment(ref modeRequestVersion);
        RevokeUiIntents();
        SuspendTableAutomation(BetaExpired); // Pauses the same task, preserving budgets and counts.
        PlayRuntime?.PauseAutomation(BetaExpired); // Invalidate policy and queued input; keep read-only observation.
        mortalSession?.Dispose(); mortalSession = null;
        BetaAccessStatus = BetaExpired;
        // A restored preference can expire while the plugin was unloaded. Notify even
        // before a task starts; keep the choice so renewal never changes the model.
        Status = BetaExpired;
        if (PendingStopAlert is null)
        {
            AlertUnexpectedStop(new(DateTimeOffset.UtcNow, BetaExpired, previousMode,
                null, null, null, null, null, null, null));
        }
        // No source switch or resume here.
    }
}
