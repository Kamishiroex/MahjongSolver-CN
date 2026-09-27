namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private const string BetaExpired = "BETA_ACCESS_EXPIRED：测试版验证失效，已暂停，请接管。";
    private int betaGeneration;
    private bool betaExpiryHandled;
    internal bool SelectedSourceAccessValid => !ExperimentalHandAiEnabled || TestAccessUnlocked;
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
        if (!ExperimentalHandAiEnabled || betaExpiryHandled) return;
        betaExpiryHandled = true;
        bool hadAuthority = gameplayAllowed || tableAutomation.Armed || taskRun?.AllowsGameplay == true;
        var previousMode = PlayRuntime?.Mode ?? Mahjong.Plugin.Dalamud.PlayMode.Off;
        gameplayAllowed = false;
        Interlocked.Increment(ref betaGeneration);
        Interlocked.Increment(ref modeRequestVersion);
        RevokeUiIntents();
        SuspendTableAutomation(BetaExpired); // Pauses the same task, preserving budgets and counts.
        PlayRuntime?.PauseAutomation(BetaExpired); // Invalidate policy and queued input; keep read-only observation.
        mortalSession?.Dispose(); mortalSession = null;
        BetaAccessStatus = BetaExpired;
        if (hadAuthority)
        {
            Status = BetaExpired;
            // Queue-only tasks have no gameplay runtime to publish a takeover alert.
            if (PendingStopAlert is null)
                AlertUnexpectedStop(new(DateTimeOffset.UtcNow, BetaExpired, previousMode,
                    null, null, null, null, null, null, null));
        }
        // No source switch or resume here.
    }
}
