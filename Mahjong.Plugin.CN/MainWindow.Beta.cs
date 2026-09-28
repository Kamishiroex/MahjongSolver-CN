using Dalamud.Bindings.ImGui;
namespace Mahjong.Plugin.CN;

internal sealed partial class MainWindow
{
    private void DrawSolverSettings()
    {
        if (ImGui.BeginCombo("求解来源", plugin.DecisionSourceLabel))
        {
            if (ImGui.Selectable("标准求解器", !plugin.ExperimentalHandAiEnabled))
                plugin.DispatchUi(() => plugin.SetExperimentalHandAiEnabled(false));
            if (plugin.TestAccessUnlocked && ImGui.Selectable("测试版", plugin.ExperimentalHandAiEnabled))
                plugin.DispatchUi(() => plugin.SetExperimentalHandAiEnabled(true));
            ImGui.EndCombo();
        }
        ImGui.TextWrapped("首次采用上游求解器，以后记住主动选择的来源和模型。重载不自动启动；切换来源会暂停，继续任务保留已有计数与停止规则。");
    }
    private void DrawBetaEntry()
    {
        ImGui.TextWrapped("资格有效时全部测试功能直接可用，无需逐项启用或重复授权。标准模式不执行游戏操作，仅提供提示。");
        ImGui.TextWrapped(plugin.TestAccessStatus);
        if (!plugin.TestAccessUnlocked && plugin.QualifiedTaskContinues)
            ImGui.TextWrapped("本次任务继续有效，包含无限循环；暂停后可继续同一任务，结束或重载后需重新验证。");
        if (!plugin.TestAccessUnlocked)
        {
            bool submit = ImGui.InputText("测试验证", ref testCodeInput, 128,
                ImGuiInputTextFlags.Password | ImGuiInputTextFlags.EnterReturnsTrue);
            submit |= ImGui.Button("验证测试版");
            if (submit)
            {
                string entered = testCodeInput; testCodeInput = "";
                plugin.DispatchUi(() => plugin.VerifyTestCode(entered));
            }
            return; // Only beta resources are gated, never the rest of the settings page.
        }
        if (plugin.TestAccessExpiresAt is { } expiry) ImGui.TextWrapped($"有效期至 {expiry.ToLocalTime():yyyy-MM-dd HH:mm}");
        ImGui.TextWrapped("顶部直接开始自动打牌，任务页可开始连续对局，总览可刷新本人评分。自动功能可以代你出牌、鸣牌、和牌、结算、报名、确认匹配及退桌；使用上游求解器也一样。");
        ImGui.TextWrapped("验证或续期不切换来源、不预热或开打。主动选择测试来源后提前预热；重载时，资格有效且上次已选用的模型也会提前准备。开始任务才操作游戏，暂停始终有效。");
        AiEngineView.DrawSelector(plugin, "beta-engine");
        if (ImGui.CollapsingHeader("本地技术详情、资源导入与自检"))
        {
            AiEngineView.DrawLiveStatus(plugin);
            engineDirectory ??= plugin.UiEngineDirectory;
            AiEngineView.Draw(plugin, ref engineDirectory);
        }
    }
}
