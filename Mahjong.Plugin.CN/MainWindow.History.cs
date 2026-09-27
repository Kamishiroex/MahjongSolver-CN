using Dalamud.Bindings.ImGui;
using Mahjong.Plugin.CN.Ui;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN;
internal sealed partial class MainWindow
{
    private int historyPage;
    private void DrawHistory()
    {
        var history=plugin.History;
        ImGui.BeginDisabled(history.Busy);
        if(ImGui.Button("刷新本机整场摘要"))plugin.DispatchUi(plugin.RefreshHistory);
        ImGui.EndDisabled();
        ImGui.TextWrapped(history.Status);
        ImGui.TextWrapped("最终名次、逐局和牌/放铳结果及整场评分关联尚无已验证读取来源；未知项不进入统计分母。记录完成也不等于全程自动完成。");
        if(ImGui.CollapsingHeader("趋势与程序可靠性",ImGuiTreeNodeFlags.DefaultOpen))
        foreach(var trend in MatchTrends.Build(history.Items))
        {
            ImGui.PushID(trend.Key);
            try
            {
                ImGui.Separator();
                ImGui.TextWrapped($"{(trend.DutyId is {} duty?Automation.MahjongDuties.Find(duty)?.Name:null)??"桌型未知"} · {Presentation.DisplayCopy.Source(trend.Engine)} · {trend.Intervention}");
                ImGui.TextWrapped($"插件 {trend.Version} · 配置 {ShortReviewHash(trend.ConfigurationFingerprint)} · 记录 {trend.Records} / 完成 {trend.Completed} · 对手环境未知");
                ImGui.TextWrapped($"平均名次 {ReviewNumber(trend.MeanPlacement)} · 第四名率 {ReviewPercent(trend.FourthRate)}（有效 {trend.PlacementSamples} 场）");
                ImGui.TextWrapped($"和牌率 {ReviewPercent(trend.WinRate)} · 放铳率 {ReviewPercent(trend.DealInRate)}（有效 {trend.HandSamples} 小局）· 平均评分变化 {ReviewNumber(trend.MeanRatingDelta)}（有效 {trend.RatingSamples} 场）");
                ImGui.TextWrapped($"全程自动模式记录 {trend.AutomaticCompleted}/{trend.AutomaticSamples} · 记录接管 {trend.Takeovers} · 停止记录 {trend.Stops}");
                ImGui.TextWrapped($"提交 {trend.Submissions} · 后续状态变化 {trend.ObservedTransitions} · 观察超时 {trend.Timeouts} · 状态变化取消 {trend.CancelledWindows} · 确认错过必需操作：未知");
            }
            finally{ImGui.PopID();}
        }
        int pageCount=Math.Max(1,(history.Items.Length+9)/10);historyPage=Math.Clamp(historyPage,0,pageCount-1);
        if(ImGui.SmallButton("上一页"))historyPage=Math.Max(0,historyPage-1);
        SameLineIfFits(90*GlassTheme.Scale);ImGui.TextUnformatted($"{historyPage+1} / {pageCount}");
        SameLineIfFits(100*GlassTheme.Scale);if(ImGui.SmallButton("下一页"))historyPage=Math.Min(pageCount-1,historyPage+1);
        foreach(var row in history.Items.Skip(historyPage*10).Take(10))
        {
            ImGui.PushID(row.Id.ToString());
            try
            {
                ImGui.Separator();
                ImGui.TextWrapped($"记录始于 {row.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {row.Outcome}");
                ImGui.TextWrapped($"桌型：{(row.DutyId is {} id?Automation.MahjongDuties.Find(id)?.Name:null)??"未知"} · 来源：{Presentation.DisplayCopy.Source(row.Engine)} · 插件 {row.PluginVersion}");
                ImGui.TextWrapped($"最终名次：{row.Placement?.ToString()??"未知"}；资料页评分观察：赛前 {row.RatingBefore?.ToString()??"未知"} / 赛后 {row.RatingAfter?.ToString()??"未知"}；整场差值：{row.RatingDelta?.ToString()??"关联未验证"}。");
                ImGui.TextWrapped("接管 / 停止："+Presentation.DisplayCopy.Summary(row.StopReason));
                if(ImGui.SmallButton("复制普通摘要")) ImGui.SetClipboardText($"{Brand.ProductName} · {row.Outcome} · {Presentation.DisplayCopy.Source(row.Engine)} · 最终名次/评分变化未知");
                if(ImGui.CollapsingHeader("本地技术详情"))
                {
                    ImGui.TextWrapped($"后端 {row.Engine} · 桥 {row.Bridge??"未知"} · 模型 SHA256 {row.ModelSha256??"未知"}");
                    ImGui.TextWrapped($"配置 {row.ConfigurationFingerprint??(row.MixedConfiguration?"混合配置":"未知")} · 设置 SHA256 {row.SettingsSha256??"未知"}");
                    ImGui.TextWrapped(row.StopReason);
                    foreach(string stop in row.Stops)ImGui.TextWrapped(stop);
                    ImGui.BeginDisabled(plugin.Review.Busy);
                    if(ImGui.SmallButton("读取决策复盘"))plugin.DispatchUi(()=>plugin.RefreshDecisionReview(row.JournalPath));
                    ImGui.EndDisabled();
                    if(plugin.Review.Path==row.JournalPath)DrawDecisionReview();
                }
                if(ImGui.SmallButton("复制对应日志位置"))ImGui.SetClipboardText(row.JournalPath);
            }
            finally{ImGui.PopID();}
        }
    }
    private static string ShortReviewHash(string? value)=>value is null?"未知 / 不可比较":value[..Math.Min(10,value.Length)];
    private static string ReviewNumber(double? value)=>value?.ToString("0.00")??"未知";
    private static string ReviewPercent(double? value)=>value?.ToString("P1")??"未知";
    private void DrawDecisionReview()
    {
        var review=plugin.Review.Result;
        ImGui.TextWrapped(review.Status);
        foreach(var decision in review.Items.Reverse())
        {
            if(!ImGui.TreeNode($"{decision.Utc.ToLocalTime():HH:mm:ss} · 决策###review-{decision.Id}"))continue;
            try
            {
                ImGui.TextWrapped($"实际后端 {decision.Backend} · 输入 {decision.Input}");
                ImGui.TextWrapped("最终决策："+decision.Choice);
                ImGui.TextWrapped("原始候选："+decision.Candidates);
                ImGui.TextWrapped("过滤 / 选中 / 未继续评估："+decision.Filters);
                ImGui.TextWrapped("后端保护覆盖："+decision.Override);
                ImGui.TextWrapped("提交："+decision.Submissions);
                ImGui.TextWrapped("后续观察（不等于确认接受）："+decision.Observations);
            }
            finally{ImGui.TreePop();}
        }
        if(ImGui.TreeNode("未关联决策的提交（最近50条）"))
        {ImGui.TextWrapped(review.UnlinkedSubmissions);ImGui.TreePop();}
    }
}
