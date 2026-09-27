using Dalamud.Bindings.ImGui;
namespace Mahjong.Plugin.CN;

internal sealed partial class MainWindow
{
    private bool betaSettingsOpen;
    private void DrawSolverSettings()
    {
        ImGui.TextWrapped("当前来源：" + plugin.DecisionSourceLabel);
        ImGui.TextWrapped("首次采用上游求解器，以后记住主动选择的来源和模型。重载不自动启动；切换来源会暂停，继续任务保留已有计数与停止规则。");
        if (plugin.ExperimentalHandAiEnabled && ImGui.Button("选择标准求解器（保持暂停）"))
            plugin.DispatchUi(() => plugin.SetExperimentalHandAiEnabled(false));
    }
    private void DrawBetaEntry()
    {
        ImGui.TextWrapped("测试版包含两个独立选项：实验求解器、游戏自动操作。标准模式不执行游戏操作，仅提供提示。");
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
        ImGui.TextWrapped("验证仅授予资格，不切换求解器、不预热、不启动任务。以下能力分别选择。");
        bool operations=plugin.GameOperationsEnabled;
        if(ImGui.Checkbox("启用游戏操作能力（本次加载）",ref operations))
            plugin.DispatchUi(()=>plugin.SetGameOperationsEnabled(operations));
        ImGui.TextWrapped("此能力可以代你出牌、鸣牌、和牌、处理结算，或报名、确认匹配、退桌、连续对局；使用上游求解器同样属于测试版操作。启用后还需明确授权本次任务；重载不恢复操作能力或授权。");
        if(plugin.GameOperationsAvailable)
        {
            if(ImGui.Button("授权本次自动打牌"))plugin.DispatchUi(plugin.StartAutomaticFromToolbar);
            if(ImGui.Button("配置连续任务（尚不启动）"))ShowQueue();
            if(ImGui.Button("授权本次评分窗口刷新"))plugin.DispatchUi(plugin.ReadOwnRatingWithNavigation);
            if(ImGui.IsItemHovered())ImGui.SetTooltip("仅本次打开金碟资料、切到方城战、读分后关闭自己打开的窗口；不授权打牌或排队。");
        }
        ImGui.Separator();
        ImGui.TextWrapped("实验求解器：仅改变建议来源；选择后仍需主动开始提示或授权自动任务。");
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
