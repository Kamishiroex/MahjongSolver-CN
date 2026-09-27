// SPDX-License-Identifier: AGPL-3.0-or-later
// Integrates the reviewed jade PR without replacing the retained classic view.
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Presentation;
using Mahjong.Plugin.CN.Ui;

namespace Mahjong.Plugin.CN;

internal sealed partial class MainWindow
{
    private static readonly string[] PageNames = ["总览", "任务", "战绩与记录", "设置", "诊断"];
    private GlassTheme.StyleScope? windowColors;
    private int page;
    private bool compact, resizeForced;
    private Vector2? requestedSize;
    private Vector2 fullSize = new(980, 680);
    private PluginUiSnapshot view = PluginUiSnapshot.Empty;

    public override void PreDraw()
    {
        if (requestedSize is { } size)
        { Size = size; SizeCondition = ImGuiCond.Always; requestedSize = null; resizeForced = true; }
        else if (resizeForced) { SizeCondition = ImGuiCond.FirstUseEver; resizeForced = false; }
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(340, 260), MaximumSize = new Vector2(float.MaxValue) };
        // Only colors cross the WindowHost lifecycle. Its Alpha stack stays untouched.
        windowColors = GlassTheme.PushWindowColors();
    }
    public override void PostDraw() { windowColors?.Dispose(); windowColors = null; }

    public override void Draw()
    {
        using var style = GlassTheme.PushContent();
        view = plugin.UiSnapshot;
        if (!GlassTheme.ModernLayout)
        {
            if (ImGui.SmallButton("外观 / 切换布局")) ImGui.OpenPopup("appearance-popup");
            if (ImGui.BeginPopup("appearance-popup"))
            { try { GlassTheme.DrawSettings(); } finally { ImGui.EndPopup(); } }
            DrawLegacy();
            DrawTaskRunControls();
            return;
        }
        if (selectQueue || selectAi || selectPublicMonitor)
        {
            if (compact) SetCompact(false);
            page = selectPublicMonitor ? 4 : selectAi ? 3 : 1;
            if (selectAi) betaSettingsOpen = true;
            selectQueue = selectAi = selectPublicMonitor = false;
        }
        DrawPersistentControls();
        if (plugin.PendingStopAlert is { } stop)
        {
            ImGui.TextColored(GlassTheme.Warning, "功能已暂停，请接管；确认不恢复。");
            Scroll("stop-detail", new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 2), () => ImGui.TextWrapped(Presentation.DisplayCopy.Summary(stop.Reason)));
            if (ImGui.SmallButton("我已看到停止原因")) plugin.AcknowledgeStopAlert();
            SameLineIfFits(140 * GlassTheme.Scale);
            if (ImGui.SmallButton("导出停止日志")) plugin.DispatchUi(plugin.ExportGameLogs);
        }
        if (plugin.Identity.Error is { } error) ImGui.TextWrapped(error);
        ImGui.Separator();
        if (compact)
        {
            Scroll("compact-content", Vector2.Zero, () => { DrawDecision(); ImGui.TextWrapped(view.TaskStatus); });
            return;
        }
        float logicalWidth = ImGui.GetContentRegionAvail().X / GlassTheme.Scale;
        if (logicalWidth >= 760)
        {
            Scroll("navigation", new Vector2(134 * GlassTheme.Scale, 0), () =>
            {
                for (int i = 0; i < PageNames.Length; i++)
                    if (ImGui.Selectable(PageNames[i], page == i, ImGuiSelectableFlags.None, new Vector2(0, 32 * GlassTheme.Scale))) page = i;
                ImGui.Separator();
                if (ImGui.Button("关于与许可")) ImGui.OpenPopup("about");
                DrawAboutPopup();
            });
            ImGui.SameLine();
        }
        else
        {
            ImGui.SetNextItemWidth(Math.Max(100 * GlassTheme.Scale, ImGui.GetContentRegionAvail().X - 120 * GlassTheme.Scale));
            ImGui.Combo("##page", ref page, PageNames, PageNames.Length);
            ImGui.SameLine();
            if (ImGui.Button("关于与许可")) ImGui.OpenPopup("about");
            DrawAboutPopup();
        }
        Scroll("page-content", Vector2.Zero, DrawPage);
    }
    private void DrawPersistentControls()
    {
        var buttonSize = new Vector2(Math.Max(92 * GlassTheme.Scale,
            ImGui.CalcTextSize("自动打牌").X + ImGui.GetStyle().FramePadding.X * 2), ImGui.GetFrameHeight());
        float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - buttonSize.X;
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(GlassTheme.Accent, Brand.ProductName);
        ImGui.SameLine(right);
        if (ImGui.Button(compact ? "展开###layout-toggle" : "紧凑###layout-toggle", buttonSize)) SetCompact(!compact);
        ImGui.TextWrapped(Brand.ProductSubtitle);
        ImGui.TextDisabled($"{view.ModeLabel} · {view.Engine} · {LocalCaptureRecorder.PluginVersion}");
        bool automatic = view.Mode == Mahjong.Plugin.Dalamud.PlayMode.Automatic && !view.Paused;
        if(ImGui.Button("开始提示",buttonSize))plugin.DispatchUi(()=>plugin.ActivatePlay(false));
        if(plugin.GameOperationsAvailable)
        {
        SameLineIfFits(buttonSize.X);
        ImGui.BeginDisabled(automatic);
        if (ImGui.Button(automatic ? "自动运行###toolbar-auto" : "自动打牌###toolbar-auto", buttonSize))
            plugin.DispatchUi(plugin.StartAutomaticFromToolbar);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("测试版游戏操作：授权本次自动出牌、鸣牌、和牌及结算。\n继续已有任务，保留场数与停止条件。");
        }
        SameLineIfFits(buttonSize.X);
        using (var danger = new GlassTheme.StyleScope())
        {
            danger.Color(ImGuiCol.Button, new Vector4(.50f,.14f,.14f,1));
            danger.Color(ImGuiCol.ButtonHovered, new Vector4(.65f,.20f,.18f,1));
            danger.Color(ImGuiCol.Text, Vector4.One);
            // Immediate revocation, never queued behind other UI intentions.
            if (ImGui.Button("暂停", buttonSize)) plugin.PausePlay();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("暂停提醒、自动打牌与排队，继续只读记牌。\n保留任务进度；需主动点击自动打牌或继续任务恢复。");
        }
        SameLineIfFits(buttonSize.X);
        if (ImGui.Button("本场后停", buttonSize)) plugin.DispatchUi(plugin.StopAfterCurrentMatch);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("打完当前整场东风战或半庄后停止，不再排下一场。\n不是在当前小局结束后停。");
        SameLineIfFits(buttonSize.X);
        if (ImGui.Button("停止全部", buttonSize)) plugin.Stop("用户停止全部读取、提醒与自动操作。");
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("停止全部读取、提醒、自动打牌和排队。\n不会强退牌局；已提交的报名需在游戏内取消。\n关闭窗口不等于暂停。");
        ImGui.TextWrapped(view.TaskStatus);
        if(plugin.TableAutomationArmed)ImGui.TextWrapped("测试版游戏操作 · 连续任务已授权："+plugin.TableAutomationStatus);
        else if(plugin.RatingRefreshBusy)ImGui.TextWrapped("测试版游戏操作 · 本次评分窗口刷新已授权。");
        else if(!plugin.ExperimentalHandAiEnabled && !automatic)ImGui.TextWrapped("标准模式不执行游戏操作，仅提供提示。");
        if(!plugin.SelectedSourceAccessValid)
        {
            using (var warning = new GlassTheme.StyleScope())
            { warning.Color(ImGuiCol.Text, GlassTheme.Warning); ImGui.TextWrapped("测试版验证已失效，原模型选择已保留。"); }
            if(ImGui.SmallButton("前往测试版续期"))
            { if(compact)SetCompact(false);page=3;betaSettingsOpen=true; }
        }
    }
    private static void DrawAboutPopup()
    {
        ImGui.SetNextWindowSize(new Vector2(580 * GlassTheme.Scale, 0), ImGuiCond.Appearing);
        if (!ImGui.BeginPopup("about")) return;
        try { DrawAbout(); } finally { ImGui.EndPopup(); }
    }
    private void DrawPage()
    {
        switch (page)
        {
            case 0:
                Card("start", "启动", () =>
                {
                    ImGui.TextWrapped("当前来源：" + view.Engine);
                    if (ImGui.Button("手动提醒")) plugin.DispatchUi(() => plugin.ActivatePlay(false));
                    SameLineIfFits(140 * GlassTheme.Scale);
                    if (ImGui.Button("目标与任务")) page = 1;
                });
                bool wide=ImGui.GetContentRegionAvail().X/GlassTheme.Scale>=950;
                if(wide && ImGui.BeginTable("overview-columns",2,ImGuiTableFlags.SizingStretchProp))
                {
                    try
                    {
                        ImGui.TableSetupColumn("main",ImGuiTableColumnFlags.WidthStretch,2);
                        ImGui.TableSetupColumn("side",ImGuiTableColumnFlags.WidthStretch,1);
                        ImGui.TableNextColumn(); DrawOverviewMain();
                        ImGui.TableNextColumn(); DrawOverviewAside();
                    }
                    finally{ImGui.EndTable();}
                }
                else { DrawOverviewMain(); DrawOverviewAside(); }
                if (ImGui.CollapsingHeader("牌效备选与原始理由")) DrawPlay();
                break;
            case 1:
                Card("task-state", "本次任务", DrawTaskRunControls);
                if(plugin.GameOperationsAvailable)Card("task", "测试版自动任务 · 桌型与场数", DrawTableAutomation);
                else Card("hint-task","提示任务",()=>
                { ImGui.TextWrapped("跟踪当前整场与已支持的时间目标，不代替玩家操作。");
                  if(ImGui.Button("开始本场提示任务"))plugin.DispatchUi(()=>plugin.ActivatePlay(false)); });
                Card("rules", "停止规则 · 任意一项达到即本场后停", DrawTaskRules);
                break;
            case 2: Card("history-summary", "整场摘要", DrawHistory); Card("history", "整场日志与恢复", DrawJournal); break;
            case 3:
                Card("appearance", "外观", () => { GlassTheme.DrawSettings(); if (ImGui.Button("使用推荐尺寸")) requestedSize = new Vector2(980,680); });
                Card("solver", "求解器", DrawSolverSettings);
                Card("settings", "记录与操作", DrawSettings);
                Card("beta", "测试版", DrawBetaEntry);
                Card("community", "可选社区统计与隐私", DrawCommunity); break;
            case 4:
                Card("diagnostics", "日志与阻塞原因", DrawJournal);
                if (ImGui.CollapsingHeader("公开牌局与读取详情")) PublicMonitorView.Draw(plugin);
                if (ImGui.CollapsingHeader("高级采集")) DrawDiagnostics(); break;
        }
    }
    private void DrawDecision() => Card("decision", "最近建议 · 执行前仍需校验", () =>
    {
        if (view.LastChoice is not { } choice) { ImGui.TextWrapped(Presentation.DisplayCopy.Summary(view.Status)); return; }
        if (choice.Reasoning.StartsWith("AKOCHAN_PENDING", StringComparison.Ordinal)) { ImGui.TextWrapped("等待计算或动作窗口；没有新的可执行建议。"); return; }
        if (choice.Reasoning.StartsWith("AKOCHAN_BLOCKED", StringComparison.Ordinal)) { ImGui.TextWrapped("测试版暂不可用，请查看诊断技术详情。"); return; }
        ImGui.TextColored(GlassTheme.Accent, ActionName(choice.Kind) + (choice.DiscardTile is { } tile ? " " + Name(tile) : ""));
        ImGui.TextWrapped("来源：" + ChoiceSource(choice));
        ImGui.TextWrapped("仅为运行时最近建议，不代表已经提交或游戏接受。");
    });
    private void DrawHand() => Card("hand", "已核对手牌 · 原槽位顺序", () =>
    {
        if (view.Hand.IsDefaultOrEmpty) { ImGui.TextWrapped("等待新鲜、稳定的公开牌面；不补演示牌或猜测赤牌槽位。"); return; }
        int index = 0;
        foreach (var tile in view.Hand)
        {
            var size = new Vector2(Math.Max(40 * GlassTheme.Scale, ImGui.CalcTextSize(tile.ChineseName).X + 12 * GlassTheme.Scale), 50 * GlassTheme.Scale);
            if (index++ > 0) SameLineIfFits(size.X);
            var pos = ImGui.GetCursorScreenPos(); var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(pos, pos+size, ImGui.GetColorU32(new Vector4(.88f,.93f,.91f,1)), 5 * GlassTheme.Scale);
            draw.AddText(pos+(size-ImGui.CalcTextSize(tile.ChineseName))*.5f, ImGui.GetColorU32(tile.RedFive ? new Vector4(.7f,.1f,.1f,1) : new Vector4(.06f,.17f,.16f,1)), tile.ChineseName);
            ImGui.Dummy(size);
        }
        if (view.Table is { } table) ImGui.TextWrapped($"手牌 {view.Hand.Length} 张；副露 {table.OurMelds.Count} 组。副露与牌局点数见公开牌局诊断。");
    });
    private void SetCompact(bool value)
    {
        if (compact == value) return;
        if (value) fullSize = Vector2.Max(ImGui.GetWindowSize()/GlassTheme.Scale, new Vector2(520,420));
        compact=value; requestedSize=value ? new Vector2(420,360) : fullSize;
    }
    private static void Scroll(string id, Vector2 size, Action draw)
    {
        bool visible=ImGui.BeginChild(id,size,false,ImGuiWindowFlags.None);
        try { if(visible) draw(); } finally { ImGui.EndChild(); }
    }
    private static void Card(string id, string title, Action draw)
    {
        // One natural-height table cell, not an auto-resizing zero-height child.
        // Invoke the content exactly once. Background goes behind content in channel 0.
        var list=ImGui.GetWindowDrawList(); var start=ImGui.GetCursorScreenPos();
        float width=ImGui.GetContentRegionAvail().X;
        list.ChannelsSplit(2); list.ChannelsSetCurrent(1); ImGui.PushID(id);
        ImGui.BeginGroup();
        try
        {
            using var padding=new GlassTheme.StyleScope();
            padding.Var(ImGuiStyleVar.CellPadding,new Vector2(12,10)*GlassTheme.Scale);
            if(ImGui.BeginTable("card",1,ImGuiTableFlags.NoSavedSettings | ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX,new Vector2(width,0)))
            {
                try { ImGui.TableNextRow(); ImGui.TableNextColumn(); ImGui.TextColored(GlassTheme.Accent,title); ImGui.Separator(); draw(); }
                finally { ImGui.EndTable(); }
            }
        }
        finally
        {
            ImGui.EndGroup(); var end=ImGui.GetItemRectMax();
            list.ChannelsSetCurrent(0);
            list.AddRectFilled(start,new Vector2(start.X+width,end.Y),ImGui.GetColorU32(new Vector4(.55f,.8f,.75f,.10f)),10*GlassTheme.Scale);
            list.AddRect(start,new Vector2(start.X+width,end.Y),ImGui.GetColorU32(new Vector4(.5f,.75f,.7f,.24f)),10*GlassTheme.Scale);
            list.ChannelsMerge(); ImGui.PopID();
        }
        ImGui.Spacing();
    }
}
