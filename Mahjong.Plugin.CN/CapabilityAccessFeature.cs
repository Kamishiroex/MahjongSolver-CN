namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private const string BetaExpired = "BETA_ACCESS_EXPIRED：测试版验证失效，相关操作已暂停；请在设置 → 测试版重新验证，原选择与任务进度已保留。";
    private int betaGeneration;
    private bool betaExpiryHandled;
    internal bool SelectedSourceAccessValid => !ExperimentalHandAiEnabled || TestAccessUnlocked;
    private int gameOperationsEnabled;
    private int operationGeneration;
    private int pendingOperationRequest = -1;
    private int pendingOperationGeneration;
    private Access.TaskOperationGrant? taskOperationGrant;
    internal bool GameOperationsEnabled => Volatile.Read(ref gameOperationsEnabled) != 0;
    internal bool GameOperationsAvailable => GameOperationsEnabled && TestAccessUnlocked;
    internal bool GameOperationsAuthorized => GameOperationsAvailable && taskRun?.Plan is not null &&
        Volatile.Read(ref taskOperationGrant)?.Allows(TestAccessUnlocked,GameOperationsEnabled,taskRun.RunId,
            CurrentCharacterContext(),Volatile.Read(ref operationGeneration)) == true;
    private bool RequireOperationCapability()
    {
        if (!RequireTestAccessCore()) { Status=BetaAccessStatus; return false; }
        if (GameOperationsEnabled) return true;
        Status="自动操作未启用：请在设置 → 测试版主动启用游戏操作，再授权本次任务。";
        return false;
    }
    internal void SetGameOperationsEnabled(bool enabled)
    {
        lock(gate)
        {
            if(disposed || enabled && !RequireTestAccessCore())return;
            if(GameOperationsEnabled==enabled)return;
            bool wasOperating=PlayRuntime?.Mode==Mahjong.Plugin.Dalamud.PlayMode.Automatic || TableAutomationArmed;
            RevokeGameOperations();
            Volatile.Write(ref gameOperationsEnabled,enabled?1:0);
            if(wasOperating)PausePlay();
            BetaAccessStatus=enabled?"游戏操作能力已启用，尚未授权任务；可主动开始自动打牌或连续任务。":
                "游戏操作能力已关闭；提示与只读功能仍可使用。";
            RecordJournalEvent("operation_capability_selected",new {Enabled=enabled,TaskAuthorized=false});
        }
    }
    private void RevokeGameOperations()
    {
        RevokeUiIntents();
        Interlocked.Increment(ref operationGeneration);
        Volatile.Write(ref taskOperationGrant,null);
        pendingOperationRequest=-1;
        ratingOperationGeneration=-1;
        ratingRefresh?.Cancel("游戏操作授权已撤销，资料窗口留给玩家处理。",false);
    }
    private bool GrantTaskOperations(int generation)
    {
        if(!GameOperationsAvailable || generation!=Volatile.Read(ref operationGeneration) || taskRun?.Plan is null)return false;
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
    private bool RequireSelectedSourceAccess() => !ExperimentalHandAiEnabled || RequireTestAccessCore();

    private void EnforceBetaAccess()
    {
        if (TestAccessUnlocked) { betaExpiryHandled = false; return; }
        aiProbe?.Stop("测试版验证失效，自检已取消。");
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
