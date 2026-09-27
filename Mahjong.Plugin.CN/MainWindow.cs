using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.Dalamud;
using Mahjong.Policy.Abstractions;
using Mahjong.Cn.PublicState;

namespace Mahjong.Plugin.CN;

internal sealed partial class MainWindow(Plugin plugin) : Window(Brand.MainWindowTitle)
{
    private static readonly Vector4 Jade = new(0.65f, 0.85f, 0.79f, 1);
    private static readonly Vector4 SecondaryText = new(0.77f, 0.83f, 0.81f, 1);
    private string testCodeInput = string.Empty;
    private bool selectPublicMonitor;
    private bool selectAi;
    private bool selectQueue;
    private string? engineDirectory;
    internal void ShowPublicMonitor() { IsOpen = true; selectPublicMonitor = true; }
    internal void ShowAi() { IsOpen = true; selectAi = true; }
    internal void ShowQueue() { IsOpen = true; selectQueue = true; }

    public override void OnClose() => testCodeInput = string.Empty;

    public override void OnOpen()
    {
        Size = new Vector2(980, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    private void DrawLegacy()
    {
        ImGui.TextColored(Jade, Brand.ProductName);
        SameLineIfFits(ImGui.CalcTextSize(Brand.ProductSubtitle).X);
        DrawSecondary(Brand.ProductSubtitle);
        ImGui.Separator();
        DrawSecondary($"版本 {LocalCaptureRecorder.PluginVersion} · 国服优化版");
        if (plugin.PendingStopAlert is { } stopped)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1, 0.3f, 0.25f, 1));
            ImGui.TextWrapped($"功能已暂停，请接管！ {stopped.Utc.ToLocalTime():HH:mm:ss}");
            ImGui.TextWrapped(Presentation.DisplayCopy.Summary(stopped.Reason));
            ImGui.PopStyleColor();
            ImGui.TextWrapped("请处理当前牌桌。确认提示不会恢复自动；排除原因后重新选择打牌模式。");
            if (ImGui.Button("我已看到停止原因")) plugin.AcknowledgeStopAlert();
            ImGui.SameLine();
            if (ImGui.Button("导出停止日志")) plugin.DispatchUi(plugin.ExportGameLogs);
            ImGui.Separator();
        }
        ImGui.TextWrapped("求解来源：" + plugin.DecisionSourceLabel);
        ImGui.TextUnformatted($"当前：{CurrentMode()}");
        float modeWidth = Math.Max(150, ImGui.CalcTextSize("暂停全部自动功能").X + ImGui.GetStyle().FramePadding.X * 2);
        var modeSize = new Vector2(modeWidth, Math.Max(36, ImGui.GetFrameHeight()));
        ImGui.BeginDisabled(plugin.Identity.Error is not null);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.18f, 0.35f, 0.32f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.24f, 0.43f, 0.39f, 1));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.29f, 0.49f, 0.44f, 1));
        try
        {
            if (ImGui.Button("手动提醒", modeSize)) plugin.DispatchUi(()=>plugin.ActivatePlay(false));
            SameLineIfFits(modeWidth);
            if (ImGui.Button("自动打牌", modeSize)) plugin.DispatchUi(plugin.StartAutomaticFromToolbar);
        }
        finally { ImGui.PopStyleColor(3); }
        ImGui.EndDisabled();
        SameLineIfFits(modeWidth);
        if (ImGui.Button("暂停全部自动功能", modeSize)) plugin.PausePlay();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("停止提醒、自动打牌和自动排队，继续只读记牌。/mjcn stop 停止全部读取。");
        bool keepBetweenHands = plugin.AutomationOptions.KeepAutomaticBetweenHands;
        if (ImGui.Checkbox("局间持续在线（等待其他玩家确认）", ref keepBetweenHands))
            QueueAutomationOptions(plugin.AutomationOptions with { KeepAutomaticBetweenHands = keepBetweenHands });
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("已观察到小局结算时持续等待，新手牌可读后自动继续。不会重复点击，也不会自动恢复读取错误、退出牌桌或手动暂停。设置立即生效并保存。");
        if (ImGui.Button("自动排队设置 / 进桌开打")) ShowQueue();
        if (plugin.TableAutomationArmed) ImGui.TextWrapped(plugin.TableAutomationStatus);
        ImGui.TextWrapped(Presentation.DisplayCopy.Summary(plugin.Monitoring ? plugin.PublicMonitor.Status :
            plugin.PlayRuntime is { } activeRuntime && (activeRuntime.Mode != PlayMode.Off || activeRuntime.IsObservingPaused)
                ? activeRuntime.Status : plugin.Status));
        if (plugin.StopRecordingError is { } writeError) ImGui.TextWrapped("停止记录保存失败：" + writeError);
        if (plugin.Identity.Error is { } identityError) ImGui.TextWrapped(identityError);
        if (ImGui.BeginTabBar("mjcn-tabs"))
        {
            if (ImGui.BeginTabItem("对局")) { DrawPlay(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("自动排队", selectQueue ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                selectQueue = false;
                DrawTableAutomation();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("设置", selectAi ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                if (selectAi) { betaSettingsOpen = true; selectAi = false; }
                DrawSolverSettings(); DrawSettings(); DrawBetaEntry();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("诊断", selectPublicMonitor ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
            {
                DrawJournal();
                if (selectPublicMonitor) ImGui.SetNextItemOpen(true);
                if (ImGui.CollapsingHeader("公开牌局与读取详情")) PublicMonitorView.Draw(plugin);
                if (ImGui.CollapsingHeader("高级采集")) DrawDiagnostics();
                selectPublicMonitor = false;
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("关于")) { DrawAbout(); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }
    }

    private static void DrawSecondary(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, SecondaryText);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static void DrawAbout()
    {
        ImGui.TextColored(Jade, Brand.ProductName);
        ImGui.TextWrapped(Brand.ProductSubtitle);
        ImGui.Spacing();
        ImGui.TextWrapped("面向 FFXIV 国服的本地牌局工具：手动提醒、自动打牌、任务管理、自动排队，以及本地事件日志和恢复核对。建议质量与实际操作效果需分别实测，不保证胜率或段位提升。");
        ImGui.Separator();
        ImGui.TextUnformatted("项目维护与来源");
        ImGui.TextWrapped("国服维护仓库：Kamishiroex/MahjongSolver-CN；贡献者见仓库记录。");
        DrawSecondary(Brand.ProjectUrl);
        if (ImGui.Button("复制项目地址")) ImGui.SetClipboardText(Brand.ProjectUrl);
        ImGui.TextWrapped("上游项目：XeldarAlz/FFXIV-AutoMahjongSolver；上游作者：XeldarAlz。本项目基于上游进行国服适配，并非将其代码声明为原创。");
        DrawSecondary(Brand.UpstreamUrl);
        ImGui.Separator();
        ImGui.TextUnformatted("许可证与第三方组件");
        ImGui.TextWrapped("插件遵循 AGPL-3.0-or-later。完整许可、署名和对应源码说明见随包 LICENSE.md、NOTICE、SOURCE.txt。");
        ImGui.TextWrapped("akochan 与凡夫 Mortal 是第三方引擎，源码、运行包和模型权重分别受其许可约束。公开插件包不包含个人模型或修改版 akochan；导入个人包不改变其分发条款。固定来源与说明见源码 docs/cn/AKOCHAN-LICENSE-BUILD.md 和 docs/cn/MORTAL-EXPERIMENT.md。");
    }

    private void DrawTableAutomation()
    {
        ImGui.TextUnformatted("自动排队与进桌开打");
        var options = plugin.AutomationOptions;
        bool queue = options.AutoQueue, start = options.AutoStart;
        if (ImGui.Checkbox("进桌后自动开启自动打牌", ref start))
            QueueAutomationOptions(options with { AutoStart = start });
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("入桌后按上方所选来源自动打牌；勾选后点击“启动自动功能”。不会覆盖你已选择的手动提醒。");
        options = plugin.AutomationOptions;
        if (ImGui.Checkbox("自动排队并确认进入", ref queue))
            QueueAutomationOptions(options with { AutoQueue = queue });
        options = plugin.AutomationOptions;
        int selected = Array.FindIndex(Automation.MahjongDuties.All, d => d.Id == options.DutyId);
        string[] names = Automation.MahjongDuties.All.Select(d => d.Name).ToArray();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.Combo("##排队桌型", ref selected, names, names.Length))
            QueueAutomationOptions(options with { DutyId = Automation.MahjongDuties.All[selected].Id });
        options = plugin.AutomationOptions;
        bool unlimited = options.MatchLimit == 0;
        if (ImGui.Checkbox("无限循环", ref unlimited))
            QueueAutomationOptions(options with { MatchLimit = unlimited ? 0 : 1 });
        options = plugin.AutomationOptions;
        if (options.MatchLimit > 0)
        {
            int limit = options.MatchLimit;
            ImGui.SetNextItemWidth(150);
            if (ImGui.InputInt("整场对局数", ref limit))
                QueueAutomationOptions(options with { MatchLimit = Math.Clamp(limit, 1, 9999) });
        }
        ImGui.TextWrapped(plugin.TableAutomationProgress);
        ImGui.TextWrapped("每场指完整东风战或半庄战，换局不计数。继续任务保留计数；明确结束后新建才从0开始。整场完成后按停止规则评估，再处理退桌与下一场。");
        ImGui.TextWrapped("段位战需单人；亲友桌需4人且你是队长。只有勾选自动排队才会自动退桌，不会退出未完成对局。");
        ImGui.BeginDisabled(plugin.TableAutomationArmed || plugin.Identity.Error is not null ||
            !(plugin.AutomationOptions.AutoQueue || plugin.AutomationOptions.AutoStart));
        if (ImGui.Button(plugin.TableAutomationArmed ? "自动功能已启动" : "预检并开始连续任务")) plugin.DispatchUi(plugin.ArmTableAutomation);
        ImGui.EndDisabled();
        if (ImGui.Button("停止排队 / 进桌开打")) plugin.StopTableAutomation();
        ImGui.TextWrapped(plugin.TableAutomationStatus);
        ImGui.Separator();
        ImGui.TextUnformatted("当前打牌状态：" + CurrentMode());
        if (plugin.PlayRuntime?.Mode is PlayMode.Automatic or PlayMode.Manual)
        {
            if (plugin.ExperimentalHandAiEnabled) ImGui.TextWrapped("当前求解来源：测试版。技术状态见设置 → 测试版。");
            if (plugin.PlayRuntime.AutoPlay is { } auto)
                ImGui.TextWrapped("最近操作：" + Presentation.DisplayCopy.Summary(auto.LastActionDescription == "(none)" ? "等待可执行动作" : auto.LastActionDescription));
            ImGui.TextWrapped("自动已开启时会等待稳定牌面、计算和行动机会，无需重复点击自动打牌。");
        }
        ImGui.TextWrapped("勾选后点击启动。修改设置、切换来源、重载或暂停后需重新启动。已提交的报名请在游戏任务搜索器取消。");
        ImGui.TextWrapped("自动开打使用当前决策来源；停止排队也暂停本次任务的新自动操作，不会强退当前牌桌。");
    }

    private static void SameLineIfFits(float width)
    {
        float right = ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X;
        if (ImGui.GetItemRectMax().X + ImGui.GetStyle().ItemSpacing.X + width <= right) ImGui.SameLine();
    }

    private void DrawSettings()
    {
        int[] counts = [20, 50, 100, 200, 500];
        int selectedCount = Array.IndexOf(counts, plugin.JournalKeepMatches);
        if (ImGui.Combo("保留最近对局记录", ref selectedCount, ["20 场", "50 场", "100 场", "200 场", "500 场"], counts.Length))
            plugin.DispatchUi(() => plugin.SetJournalKeepMatches(counts[selectedCount]));
        ImGui.TextWrapped("每整场一份；历史记录另有 1 GiB 预算，超过时先清理最早记录。当前场、最新恢复记录及手动导出的 ZIP 不清理。");
        ImGui.TextWrapped(plugin.JournalMaintenanceStatus);
        if (plugin.PlayRuntime is { } runtime)
        {
            int delay = runtime.Configuration.HumanizedDelayMs;
            if (ImGui.SliderInt("操作间隔（毫秒）", ref delay, 500, 3000))
                runtime.ConfigService.Update(c => c with { HumanizedDelayMs = delay });
        }
        else ImGui.TextWrapped("启动提醒或自动打牌后，可调整操作间隔。");
        bool advance = plugin.AutomationOptions.AutoAdvanceAfterHand;
        if (ImGui.Checkbox("自动点击结算后的下一局", ref advance))
            QueueAutomationOptions(plugin.AutomationOptions with { AutoAdvanceAfterHand = advance });
    }

    private void DrawJournal()
    {
        ImGui.TextWrapped(plugin.JournalStatus);
        ImGui.TextWrapped("详细导出包含真实后端、版本、公开牌局和操作证据；不含测试码。分享前仍请核对内容。普通摘要可在战绩页复制。");
        if (plugin.JournalLocation is { } path)
        {
            if (ImGui.Button("复制日志目录")) ImGui.SetClipboardText(path);
            ImGui.SameLine();
        }
        if (ImGui.Button("导出详细技术日志")) plugin.DispatchUi(plugin.ExportGameLogs);
        ImGui.SameLine();
        if (ImGui.Button("导出详细技术快照")) plugin.DispatchUi(plugin.ExportGameplay);
        ImGui.TextWrapped(plugin.ExportStatus);
        ImGui.Separator();
        ImGui.TextWrapped(plugin.RecoveryStatus);
        if (ImGui.Button("重新读取日志并核对桌面")) plugin.DispatchUi(plugin.StartLogRecovery);
        ImGui.SameLine();
        if (ImGui.Button("停止全部读取与操作")) plugin.Stop("用户停止全部读取、提醒、操作与记录。");
        if (plugin.LastStopPath is { } stoppedPath && ImGui.Button("复制最近停止记录路径"))
            ImGui.SetClipboardText(stoppedPath);
        ImGui.TextWrapped("日志保留可见事件和错误；恢复时核对当前桌面，无法补回的历史会标明缺口。");
    }

    private void DrawPlay()
    {
        var runtime = plugin.PlayRuntime;
        if (plugin.ExperimentalHandAiEnabled) ImGui.TextWrapped("当前求解来源：测试版。技术状态见设置 → 测试版。");
        if (runtime is null || runtime.Mode == PlayMode.Off)
        {
            ImGui.TextWrapped("进入多玛方城后，点击上方模式按钮即可使用。也可输入 /mjcn manual 或 /mjcn auto；/mjcn stop 随时停止。");
            return;
        }
        var aggregate = runtime.ActiveAggregator;
        var state = aggregate?.Latest;
        if (state is null)
        {
            ImGui.TextWrapped("等待可识别的麻将界面；未就绪时不会出牌。");
            return;
        }
        ImGui.TextWrapped("当前手牌：" + string.Join("  ", state.Hand.Select(Name)));
        ImGui.TextUnformatted($"手牌 {state.Hand.Count} 张 / 副露 {state.OurMelds.Count} 组 / 剩余牌 {state.WallRemaining}");
        var choice = aggregate?.LastChoice;
        if (choice is not null) ImGui.TextWrapped("本次建议来源：" + ChoiceSource(choice));
        if (choice is null)
            ImGui.TextWrapped("建议：等待当前动作或过渡结束。" + aggregate?.LastScorerError);
        else if (choice.Kind == ActionKind.Pass && choice.Reasoning.StartsWith("AKOCHAN_PENDING:", StringComparison.Ordinal))
            ImGui.TextWrapped("建议：等待测试版计算或当前牌局读取；尚未提交操作。");
        else if (choice.Reasoning.StartsWith("AKOCHAN_BLOCKED:", StringComparison.Ordinal))
            ImGui.TextWrapped("测试版已暂停；具体原因见诊断技术详情。");
        else
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.6f, 1), "建议：" + ActionName(choice.Kind) +
                (choice.DiscardTile is { } tile ? " " + Name(tile) : ""));
            var scored = aggregate?.LastScored;
            var bestMatch = scored?.Where(x => choice.DiscardTile == x.Discard).ToArray();
            if (bestMatch is { Length: > 0 })
            {
                var best = bestMatch[0];
                ImGui.TextWrapped($"上游牌效评估（独立于测试版结果）：弃后 {Shanten(best.ShantenAfter)}，有效进张 {best.UkeireKinds} 种 / 估计 {best.UkeireWeighted} 张；保留宝牌 {best.DoraRetained} 张。");
            }
            else ImGui.TextWrapped(ActionReason(choice.Kind));
            if (scored is not null && ImGui.CollapsingHeader("牌效备选（上游评分）"))
                foreach (var alternative in scored.Where(x => x.Discard != choice.DiscardTile).OrderByDescending(x => x.Score).Take(3))
                    ImGui.TextWrapped($"牌效备选：{Name(alternative.Discard)}，{Shanten(alternative.ShantenAfter)}，进张 {alternative.UkeireKinds} 种 / 估计 {alternative.UkeireWeighted} 张。");
            if (ImGui.CollapsingHeader("引擎原始理由"))
            {
                ImGui.TextWrapped(choice.Reasoning);
                foreach (var step in choice.ReasonSteps) ImGui.TextWrapped(step.ToString());
            }
        }
        if (runtime.AutoPlay is { } auto) ImGui.TextWrapped("自动操作记录：" + Presentation.DisplayCopy.Summary(auto.LastActionDescription));
    }

    private string CurrentMode() => plugin.PlayRuntime?.Mode switch
    {
        PlayMode.Manual => "手动提醒",
        PlayMode.Automatic => "自动打牌",
        _ => plugin.PlayRuntime?.IsObservingPaused == true ? "已暂停（继续只读记牌）" : "已停止",
    };

    private static string ChoiceSource(ActionChoice choice)
    {
        if (choice.Reasoning.StartsWith("AKOCHAN_FALLBACK", StringComparison.Ordinal)) return "标准求解器（历史回退记录；详见原始理由）";
        return choice.Reasoning.StartsWith("AKOCHAN_", StringComparison.Ordinal) ? "测试版" : "标准求解器";
    }

    private static string Name(Tile tile) => new Mahjong.Cn.VisibleTile(tile.Id).ChineseName;
    private static string Shanten(int value) => value < 0 ? "已成和牌形状" : value == 0 ? "听牌" : $"{value} 向听";
    private static string ActionName(ActionKind kind) => kind switch
    {
        ActionKind.Discard => "打出", ActionKind.Riichi => "立直并打出", ActionKind.Chi => "吃",
        ActionKind.Pon => "碰", ActionKind.AnKan => "暗杠", ActionKind.MinKan => "明杠",
        ActionKind.ShouMinKan => "加杠", ActionKind.Ron => "和牌（荣和）", ActionKind.Tsumo => "自摸",
        ActionKind.Kyushukyuhai => "九种幺九倒牌（流局）",
        _ => "放弃本次鸣牌 / 等待",
    };
    private static string ActionReason(ActionKind kind) => kind switch
    {
        ActionKind.Ron or ActionKind.Tsumo => "当前合法动作包含和牌，引擎选择结束本局。",
        ActionKind.Chi or ActionKind.Pon or ActionKind.AnKan or ActionKind.MinKan or ActionKind.ShouMinKan => "引擎结合当前牌形与合法候选选择此动作；具体评分见原始理由。",
        ActionKind.Riichi => "引擎在当前合法动作与听牌条件下建议立直。",
        _ => "引擎没有选择当前鸣牌；等待下一次自己的行动。",
    };

    private void DrawDiagnostics()
    {
        ImGui.TextWrapped("可选本地诊断：无需采集即可使用打牌助手。记录只保存在本机，不会自动上传。");
        ImGui.Separator();
        ImGui.TextUnformatted($"游戏：{plugin.Identity.GameVersion}   Dalamud：{plugin.Identity.DalamudVersion} / API {plugin.Identity.Api}");
        ImGui.TextWrapped(plugin.Status);
        if (ImGui.Button("立即停止（/mjcn stop）", new Vector2(-1, 36)))
            plugin.Stop("用户停止：建议、操作和采集均已停止。");
        ImGui.BeginDisabled(plugin.Capturing);
        bool lowerHand = plugin.CaptureLowerHand;
        if (ImGui.Checkbox("采集并核对本家下方牌面", ref lowerHand))
            plugin.SetLowerHandCapture(lowerHand);
        bool publicLayout = plugin.CapturePublicLayout;
        if (ImGui.Checkbox("公开牌桌布局诊断（牌河、副露和宝牌区域）", ref publicLayout))
            plugin.SetPublicLayoutCapture(publicLayout);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(plugin.Identity.Error is not null || plugin.Capturing || plugin.SavingArchive);
        if (ImGui.Button("开始整场本地采集")) plugin.DispatchUi(plugin.Start);
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("结束并打包整场记录")) plugin.DispatchUi(plugin.Export);
        ImGui.TextWrapped("点击开始后每秒采样2次，最多60分钟，后台自动分段保存；停止后自动生成ZIP。新采集保留先前已保存的会话。游戏意外退出时可分析已写入的完整记录。");
        ImGui.Combo("场景标记", ref plugin.MarkerIndex, Plugin.Markers, Plugin.Markers.Length);
        ImGui.TextUnformatted($"本场累计：{plugin.SavedFrameCount} 帧；实时缓存：{plugin.FrameCount} 帧；完整记录自动保存到本机");
        ImGui.TextWrapped(plugin.RecordingLocation);
        ImGui.TextWrapped(plugin.ExportStatus);
        ImGui.BeginDisabled(plugin.RecordingPath is null);
        if (ImGui.Button("复制本地记录路径")) ImGui.SetClipboardText(plugin.RecordingPath ?? "");
        ImGui.EndDisabled();
        ImGui.Separator();
        ImGui.TextUnformatted("当前识别");
        if (plugin.Latest is { } frame)
        {
            foreach (var addon in frame.Addons)
                ImGui.TextUnformatted($"{addon.Name}：存在 {addon.Present} / 可见 {addon.Visible} / 就绪 {addon.Ready} / 节点 {addon.VisibleNodes.Count}");
        }
        ImGui.TextWrapped("这里保留独立的可见牌面核对。此诊断结果与实验版上游读取链路分开；完整国服牌局仍待实机核实。");
        ImGui.TextWrapped("开始前可取消任一采集区域。公开布局只记录指定区域的变换和牌壳矩形，不采新区域牌面或对手暗牌。插件加载时不会自动开始采集。");
        var publicLayouts = plugin.Latest?.Addons.FirstOrDefault(x => x.Name == "Emj")?.PublicLayouts;
        if (publicLayouts is not null)
            ImGui.TextWrapped($"公开布局记录：{publicLayouts.Count}；归属/部件待核对：{publicLayouts.Count(x => x.Status != "PUBLIC_LAYOUT_METADATA_ONLY")}。尚不解释为牌河、副露或宝牌。");
        DrawLowerHandPreview();
        ImGui.Separator();
        if (plugin.Session.CurrentSuggestion is { } suggestion)
        {
            ImGui.TextUnformatted($"建议：{suggestion.Action} {suggestion.DiscardTile?.ChineseName}（槽位 {suggestion.DiscardSlot}）");
            ImGui.TextWrapped(suggestion.Reason);
            foreach (var candidate in suggestion.Alternatives.Take(3))
                ImGui.TextWrapped($"备选 {candidate.Tile.ChineseName}（槽位 {candidate.Slot}）：{candidate.Reason}");
            foreach (var limitation in suggestion.Limitations) ImGui.TextWrapped(limitation);
        }
        else ImGui.TextWrapped("推荐弃牌：暂停    备选项：暂无    理由：" + plugin.Session.Status.Message);
        ImGui.TextWrapped("此页的严格诊断状态不会自动启用操作；使用上方按钮主动选择提醒或自动打牌。停止会同时停止打牌和采集。");
    }

    private void DrawLowerHandPreview()
    {
        if (!plugin.CaptureLowerHand || plugin.Latest is not { } frame) return;
        var addon = frame.Addons.FirstOrDefault(x => x.Name == "Emj");
        var faces = addon?.LowerHandFaces ?? [];
        var check = LowerHandProfile.CheckPreview(faces, icon => TryGetIcon(icon, out _));
        ImGui.Separator();
        ImGui.TextUnformatted("本家牌面核对（实验性，只读）");
        if (!check.Eligible)
        {
            ImGui.TextWrapped("暂不可核对：" + check.Reason);
            var rejected = faces.FirstOrDefault(x => x.DiagnosticStatus != LowerHandProfile.VerifiedIconStatus);
            if (rejected is not null) ImGui.TextWrapped(rejected.DiagnosticStatus);
            return;
        }
        if (addon?.LowerHandReading is not { Stable: true } reading)
        {
            ImGui.TextWrapped(addon?.LowerHandReading?.Reason ?? "等待独立稳定采样及牌面目录核对。");
            return;
        }
        ImGui.TextWrapped($"当前下方 {reading.Tiles.Length} 张牌面已连续两次采样一致；这是可见图像，不代表完整手牌或轮到自己出牌。");
        ImGui.TextWrapped("请先暂停出牌：预览应与游戏中自己的手牌按同一顺序逐张对应。动画中不要确认，确认不会开启建议或操作。");
        int index = 0;
        foreach (var tile in reading.Tiles)
        {
            if (!TryGetIcon(tile.IconId, out var wrap)) return;
            if (index++ > 0) ImGui.SameLine();
            ImGui.BeginGroup();
            ImGui.Image(wrap!.Handle, new Vector2(32, 42));
            ImGui.TextUnformatted(tile.ChineseName);
            ImGui.EndGroup();
        }
        if (ImGui.Button("与我的手牌逐张一致")) plugin.RecordLowerHandReview(frame.Sequence, true);
        ImGui.SameLine();
        if (ImGui.Button("不一致")) plugin.RecordLowerHandReview(frame.Sequence, false);
        ImGui.TextWrapped(plugin.LowerHandReviewStatus);
    }

    private static bool TryGetIcon(uint icon, out IDalamudTextureWrap? wrap)
    {
        try { return Plugin.Textures.GetFromGameIcon(new GameIconLookup(icon, hiRes: false)).TryGetWrap(out wrap, out _); }
        catch { wrap = null; return false; }
    }
}
