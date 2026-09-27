using Dalamud.Bindings.ImGui;
namespace Mahjong.Plugin.CN;

internal sealed partial class MainWindow
{
    private bool betaSettingsOpen;
    private void DrawSolverSettings()
    {
        ImGui.TextWrapped("当前来源：" + plugin.DecisionSourceLabel);
        ImGui.TextWrapped("默认采用上游求解器。切换来源会暂停，继续任务保留已有计数与停止规则。");
        if (plugin.ExperimentalHandAiEnabled && ImGui.Button("选择标准求解器（保持暂停）"))
            plugin.DispatchUi(() => plugin.SetExperimentalHandAiEnabled(false));
    }
    private void DrawBetaEntry()
    {
        ImGui.TextWrapped("另提供测试版功能，验证后可用；不影响标准求解器。");
        if (!betaSettingsOpen)
        { if (ImGui.SmallButton("打开测试版设置")) betaSettingsOpen = true; return; }
        if (ImGui.SmallButton("收起测试版设置")) { betaSettingsOpen = false; testCodeInput = ""; return; }
        ImGui.TextWrapped(plugin.BetaAccessStatus);
        ImGui.TextWrapped(plugin.TestAccessStatus);
        bool submit = ImGui.InputText("测试验证", ref testCodeInput, 128,
            ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);
        submit |= ImGui.Button("验证测试版");
        if (submit)
        {
            string entered = testCodeInput; testCodeInput = "";
            plugin.DispatchUi(() => plugin.VerifyTestCode(entered));
        }
        if (!plugin.TestAccessUnlocked) return; // Only this secondary capability panel is gated.
        if (plugin.TestAccessExpiresAt is { } expiry) ImGui.TextWrapped($"有效期至 {expiry.ToLocalTime():yyyy-MM-dd HH:mm}");
        ImGui.TextWrapped("验证和导入不会切换来源或启动。选择测试版后仍需主动启动或继续任务。");
        if (!plugin.ExperimentalHandAiEnabled && ImGui.Button("主动选择测试版（尚不启动）"))
            plugin.DispatchUi(() => plugin.SetExperimentalHandAiEnabled(true));
        if (plugin.ExperimentalHandAiEnabled && ImGui.Button("选择标准求解器（保持暂停）##beta"))
            plugin.DispatchUi(() => plugin.SetExperimentalHandAiEnabled(false));
        AiEngineView.DrawSelector(plugin, "beta-engine");
        if (ImGui.CollapsingHeader("本地技术详情、资源导入与自检"))
        {
            AiEngineView.DrawLiveStatus(plugin);
            engineDirectory ??= plugin.UiEngineDirectory;
            AiEngineView.Draw(plugin, ref engineDirectory);
        }
    }
}
