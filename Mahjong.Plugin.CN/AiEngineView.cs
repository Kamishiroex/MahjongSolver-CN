using Dalamud.Bindings.ImGui;
using System.Numerics;
using Mahjong.Cn.Engines;
using Mahjong.Cn.PublicState;

namespace Mahjong.Plugin.CN;

internal static class AiEngineView
{
    internal static void Draw(Plugin plugin, ref string directory)
    {
        DrawLiveStatus(plugin);
        DrawSelector(plugin);
        ImGui.TextWrapped("实战 AI 目录：" + plugin.UiEngineDirectory);
        ImGui.TextWrapped(plugin.EngineInstallationStatus);
        ImGui.BeginDisabled(plugin.EngineMaintenanceBusy);
        if (ImGui.Button("检查当前 AI 文件")) plugin.DispatchUi(()=>plugin.CheckEngineDirectory(plugin.UiEngineDirectory, false));
        ImGui.SameLine();
        ImGui.BeginDisabled(plugin.MortalSelected);
        if (ImGui.Button("导出当前 v5 迁移包")) plugin.DispatchUi(plugin.ExportEngine);
        ImGui.EndDisabled();
        ImGui.InputText("AI 包 ZIP 路径", ref plugin.EngineTransferPath, 2048);
        ImGui.InputText("模型名称（可选）", ref plugin.EngineImportName, 80);
        if (ImGui.Button("导入测试版资源（不启用）")) {string path=plugin.EngineTransferPath;plugin.DispatchUi(()=>plugin.ImportEngine(path));}
        ImGui.EndDisabled();
        ImGui.TextWrapped("兼容运行包只需导入一次，之后在此切换保存的资源。导入不改变有效求解来源；选择测试版并明确启动后才会预热和计算。支持当前凡夫/v5 协议；新架构需相应适配器。");
        if (plugin.MortalSelected) ImGui.TextWrapped("凡夫使用当前公开桌面，缺失历史不会补造；历史特征和国服东风赛制可能影响棋力。Q 值不是胜率。和牌使用当前菜单优先处理。");
        DrawGlobalInput(plugin);
        if (plugin.PublicMonitor.Current is { } snapshot)
        {
            var readiness = ReadinessEvaluator.Evaluate(snapshot, ReadinessProfiles.CompleteDecision);
            if (ImGui.CollapsingHeader("公开字段诊断详情"))
                foreach (var issue in readiness.Issues) ImGui.TextWrapped($"{issue.Path}：{issue.Reason}");
        }
        if (!ImGui.CollapsingHeader("引擎自检与安装")) return;
        ImGui.TextWrapped("修改目录后可校验并保存测试配置，不启用求解器。已经运行测试版时，修改资源会暂停；标准任务不受影响。");
        ImGui.InputText("引擎目录", ref directory, 2048);
        ImGui.BeginDisabled(plugin.EngineMaintenanceBusy);
        if (ImGui.Button("校验并保存测试配置")) {string path=directory;plugin.DispatchUi(()=>plugin.CheckEngineDirectory(path, true));}
        ImGui.SameLine();
        if (ImGui.Button("填入当前实战目录")) directory = plugin.UiEngineDirectory;
        ImGui.EndDisabled();
        ImGui.BeginDisabled(plugin.MortalSelected || plugin.AiProbe.Busy || plugin.Identity.Error is not null);
        if (ImGui.Button("暂停打牌并运行样例自检")) {string path=directory;plugin.DispatchUi(()=>plugin.StartAiProbe(path));}
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("停止本地 AI")) plugin.Stop("用户停止本地 AI 与其他活动。");
        ImGui.TextWrapped(plugin.AiProbe.Status);
        if (plugin.AiProbe.CleanupWarning is { } warning) ImGui.TextWrapped(warning);
        ImGui.TextWrapped("已有引擎可直接整包迁移。没有迁移包时，仍需按源码说明本机构建 v5；受独立分发条款限制，公开插件包不内置修改版 akochan。插件修复与原生引擎版本分别显示：0.6.3.1 的倒牌修复属于插件逻辑，仍使用 v5 引擎。");
        if (plugin.AiProbe.Result is { } result)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("公开离线样例建议（非当前牌局）");
            ImGui.TextWrapped(string.Join(" → ", result.Moves.Select(DescribeMove)));
            ImGui.TextWrapped($"引擎：{result.EngineName}；源码 {result.EngineCommit[..12]}；数据 {result.SourceLabel}");
            ImGui.TextWrapped($"本次启动至返回：{result.StartToResponseMilliseconds:F0} ms；每次从新进程重放完整输入。该时间不是游戏可用时限。");
            if (result.PeakWorkingSetBytes is { } peak) ImGui.TextUnformatted($"本次进程峰值工作集：{peak / (1024.0 * 1024):F1} MiB");
            if (result.CpuMilliseconds is { } cpu) ImGui.TextUnformatted($"本次进程 CPU 时间：{cpu:F0} ms");
            ImGui.TextWrapped(result.Explanation);
            if (ImGui.Button("导出 AI 自检结果")) plugin.DispatchUi(plugin.ExportAiProbe);
            ImGui.TextWrapped(plugin.ExportStatus);
        }
    }

    internal static void DrawLiveStatus(Plugin plugin)
    {
        ImGui.TextWrapped(plugin.ExperimentalHandAiStatus);
    }

    internal static void DrawSelector(Plugin plugin, string id = "engine-settings")
    {
        ImGui.PushID(id);
        ImGui.BeginDisabled(plugin.EngineMaintenanceBusy);
        if (ImGui.BeginCombo("实战模型", plugin.GlobalBackendLabel))
        {
            foreach (var profile in plugin.UiEngines)
                if (ImGui.Selectable(profile.Name + (profile.Installed ? "" : "（文件缺失）") + "##" + profile.Id,
                    profile.Id == plugin.SelectedEngineId)) plugin.DispatchUi(()=>plugin.SelectInstalledEngine(profile.Id));
            ImGui.EndCombo();
        }
        ImGui.EndDisabled();
        if (plugin.EngineMaintenanceBusy || !plugin.UiEngineInstalled)
            ImGui.TextWrapped(plugin.EngineInstallationStatus);
        if (plugin.MortalWarmupStatus is { } warmup) ImGui.TextWrapped(warmup);
        ImGui.PopID();
    }

    private static void DrawGlobalInput(Plugin plugin)
    {
        var trace = plugin.LastGlobalAiTrace;
        if (trace?.Input is { } input)
        {
            ImGui.TextWrapped($"最近一次 AI 输入（{input.Utc.ToLocalTime():HH:mm:ss}）：手牌 {input.Hand.Length}，" +
                $"牌河 {string.Join('/', input.Players.Select(p => p.River.Length))}，副露 {string.Join('/', input.Players.Select(p => p.Melds.Length))}。");
            ImGui.TextWrapped($"场风/局数 {input.RoundWind + 1}/{input.HandNumber}，本场 {input.Honba}，供托 {input.RiichiSticks}，余牌 {input.WallRemaining}；" +
                $"四家点数 {string.Join('/', input.Players.Select(p => p.Score))}。");
            ImGui.TextWrapped("公开宝牌指标：" + string.Join("、", input.DoraIndicators.Select(t => t.ChineseName)));
            var assumptions = trace.Decision?.Assumptions ?? input.Assumptions;
            if (ImGui.CollapsingHeader("分析依据与限制（诊断）"))
                foreach (var assumption in assumptions) ImGui.TextWrapped(assumption);
            if (trace.Decision is { } result && ImGui.CollapsingHeader("原生 AI 候选与评分（执行前核对当前合法动作）"))
                foreach (var candidate in result.Candidates.OrderByDescending(c => c.Score).Take(5))
                    ImGui.TextWrapped($"{string.Join(" → ", candidate.Moves.Select(DescribeMove))}：{candidate.Score:F2}");
        }
        if (ImGui.Button("导出最近一次测试版技术输入与结果")) plugin.DispatchUi(plugin.ExportGameplay);
        ImGui.TextWrapped(plugin.ExportStatus);
    }

    internal static string DescribeMove(AkochanMove move) => move.Type switch
    {
        "dahai" => "打出 " + (move.Tile?.ChineseName ?? "未知牌") + (move.Tsumogiri == true ? "（摸切）" : "（手切）"),
        "reach" => "立直", "chi" => "吃 " + move.Tile?.ChineseName,
        "pon" => "碰 " + move.Tile?.ChineseName, "daiminkan" => "明杠 " + move.Tile?.ChineseName,
        "ankan" => "暗杠", "kakan" => "加杠 " + move.Tile?.ChineseName,
        "hora" => move.Actor == move.Target ? "自摸" : "荣和", "none" => "跳过本次响应",
        "kyushukyuhai" => "九种幺九倒牌",
        _ => "未支持的动作",
    };
}
