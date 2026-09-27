using Dalamud.Bindings.ImGui;
using Mahjong.Plugin.CN.Ui;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN;
internal sealed partial class MainWindow
{
    private int historyPage;
    private int historyDays;
    private uint? historyDuty;
    private string? historyBackend;
    private bool historyGapsOnly;
    private void DrawHistory()
    {
        var history=plugin.History;
        ImGui.BeginDisabled(history.Busy);
        if(ImGui.Button("刷新本机整场摘要"))plugin.DispatchUi(plugin.RefreshHistory);
        ImGui.EndDisabled();
        SameLineIfFits(150*GlassTheme.Scale);
        if(ImGui.Button("重建摘要并重试保存"))plugin.DispatchUi(plugin.RepairResultRecords);
        if(ImGui.IsItemHovered())ImGui.SetTooltip("重新校验原始日志及已暂存的结算数据。\n不执行游戏操作，不把中途退出记为整场完成。");
        ImGui.TextWrapped(plugin.ResultsStatus);
        ImGui.TextWrapped(history.Status);
        if(ImGui.Combo("时间范围",ref historyDays,["全部记录","最近 7 天","最近 30 天","最近 90 天"],4))historyPage=0;
        if(ImGui.BeginCombo("桌型",historyDuty is {} selected?Automation.MahjongDuties.Find(selected)?.Name??selected.ToString():"全部桌型"))
        {
            if(ImGui.Selectable("全部桌型",historyDuty is null)){historyDuty=null;historyPage=0;}
            foreach(uint duty in history.Items.Where(r=>r.DutyId.HasValue).Select(r=>r.DutyId!.Value).Distinct().Order())
                if(ImGui.Selectable(Automation.MahjongDuties.Find(duty)?.Name??duty.ToString(),historyDuty==duty)){historyDuty=duty;historyPage=0;}
            ImGui.EndCombo();
        }
        if(ImGui.BeginCombo("求解来源",historyBackend is null?"全部来源":Presentation.DisplayCopy.Source(historyBackend)))
        {
            if(ImGui.Selectable("全部来源",historyBackend is null)){historyBackend=null;historyPage=0;}
            foreach(string backend in history.Items.Select(r=>r.Engine).Distinct().Order())
                if(ImGui.Selectable(Presentation.DisplayCopy.Source(backend)+"###filter-"+backend,historyBackend==backend)){historyBackend=backend;historyPage=0;}
            ImGui.EndCombo();
        }
        if(ImGui.Checkbox("只看信息缺失的记录",ref historyGapsOnly))historyPage=0;
        var rows=HistoryQuery.Filter(history.Items,DateTimeOffset.UtcNow,new[]{0,7,30,90}[historyDays],historyDuty,historyBackend,historyGapsOnly);
        var coverage=HistoryQuery.Coverage(rows);
        ImGui.TextWrapped($"当前筛选 {coverage.Records} 份 · 确认完成 {coverage.Completed} · 名次 {coverage.Ranked} / 评分变化 {coverage.Rated} 份 · 逐局已核对 {coverage.VerifiedHands}/{coverage.Hands}");
        ImGui.TextWrapped($"校验未通过 {coverage.Untrusted} · 混合配置 {coverage.Mixed} · 有异常中断 {coverage.Interrupted}。缺失项不会记成 0；全量覆盖也不代表棋力更强。");
        ImGui.TextWrapped("名次取自本人整场结算；逐局结果需公开结算提示与四家付款相符；评分差值需赛前、赛后总场数恰好增加 1。未知项不进入分母，记录完成也不等于全程自动完成。");
        if(ImGui.CollapsingHeader("趋势与程序可靠性",ImGuiTreeNodeFlags.DefaultOpen))
        foreach(var trend in MatchTrends.Build(rows))
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
        int pageCount=Math.Max(1,(rows.Length+9)/10);historyPage=Math.Clamp(historyPage,0,pageCount-1);
        if(ImGui.SmallButton("上一页"))historyPage=Math.Max(0,historyPage-1);
        SameLineIfFits(90*GlassTheme.Scale);ImGui.TextUnformatted($"{historyPage+1} / {pageCount}");
        SameLineIfFits(100*GlassTheme.Scale);if(ImGui.SmallButton("下一页"))historyPage=Math.Min(pageCount-1,historyPage+1);
        foreach(var row in rows.Skip(historyPage*10).Take(10))
        {
            ImGui.PushID(row.Id.ToString());
            try
            {
                ImGui.Separator();
                ImGui.TextWrapped($"记录始于 {row.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm} · {row.Outcome}");
                ImGui.TextWrapped($"桌型：{(row.DutyId is {} id?Automation.MahjongDuties.Find(id)?.Name:null)??"未知"} · 来源：{Presentation.DisplayCopy.Source(row.Engine)} · 插件 {row.PluginVersion}");
                ImGui.TextWrapped($"最终名次：{row.Placement?.ToString()??"未知"}；资料页评分观察：赛前 {row.RatingBefore?.ToString()??"未知"} / 赛后 {row.RatingAfter?.ToString()??"未知"}；整场差值：{row.RatingDelta?.ToString()??"关联未验证"}。");
                ImGui.TextWrapped($"小局：已核对 {row.HandsWithVerifiedOutcome??0} / 已观察 {row.ObservedHands} · 和牌 {row.Wins?.ToString()??"未知"} · 放铳 {row.DealIns?.ToString()??"未知"}");
                if(row.Placement is null)ImGui.TextWrapped("名次缺口："+row.PlacementEvidence);
                if(row.RatingDelta is null)ImGui.TextWrapped("评分缺口："+row.RatingAssociation);
                if(row.CompletedUtc is null)ImGui.TextWrapped("未观察到整场完成事件；退桌、断线或日志结束均不代替结算。");
                if(row.Hands.Length>0 && ImGui.TreeNode("逐局结果"))
                {
                    int handIndex=0;
                    foreach(var hand in row.Hands)
                        ImGui.TextWrapped($"小局记录 {++handIndex} · {HandResultKind(hand.Kind)} · 和牌 {(hand.SelfWon.HasValue?hand.SelfWon.Value?"是":"否":"未知")} / 放铳 {(hand.SelfDealtIn.HasValue?hand.SelfDealtIn.Value?"是":"否":"未知")} · {HandResultReason(hand.Code)}");
                    ImGui.TreePop();
                }
                ImGui.TextWrapped("接管 / 停止："+Presentation.DisplayCopy.Summary(row.StopReason));
                if(ImGui.SmallButton("复制普通摘要")) ImGui.SetClipboardText($"{Brand.ProductName} · {row.Outcome} · {Presentation.DisplayCopy.Source(row.Engine)} · 名次 {row.Placement?.ToString()??"未知"} · 评分变化 {row.RatingDelta?.ToString("+0;-0;0")??"未知"} · 和牌 {row.Wins?.ToString()??"未知"} / 放铳 {row.DealIns?.ToString()??"未知"}");
                if(ImGui.CollapsingHeader("本地技术详情"))
                {
                    ImGui.TextWrapped($"名次依据：{row.PlacementEvidence}；评分关联：{row.RatingAssociation}");
                    ImGui.TextWrapped($"后端 {row.Engine} · 桥 {row.Bridge??"未知"} · 模型 SHA256 {row.ModelSha256??"未知"}");
                    ImGui.TextWrapped($"配置 {row.ConfigurationFingerprint??(row.MixedConfiguration?"混合配置":"未知")} · 设置 SHA256 {row.SettingsSha256??"未知"}");
                    ImGui.TextWrapped(row.StopReason);
                    foreach(string stop in row.Stops)ImGui.TextWrapped(stop);
                    ImGui.BeginDisabled(plugin.Review.Busy);
                    if(ImGui.SmallButton("读取决策复盘"))plugin.DispatchUi(()=>plugin.RefreshDecisionReview(row.JournalPath));
                    ImGui.EndDisabled();
                    if(plugin.Review.Path==row.JournalPath)DrawDecisionReview();
                    if(ImGui.SmallButton("导出固定决策样本"))plugin.DispatchUi(()=>plugin.ExportDecisionCorpus(row.JournalPath));
                    ImGui.TextWrapped(plugin.CorpusExportStatus);
                }
                if(ImGui.SmallButton("复制对应日志位置"))ImGui.SetClipboardText(row.JournalPath);
            }
            finally{ImGui.PopID();}
        }
    }
    private static string HandResultKind(string kind)=>kind switch {"Ron"=>"荣和","Tsumo"=>"自摸","Draw"=>"流局",_=>"待判定"};
    private static string HandResultReason(string code)=>code switch
    {
        "RESULT_CONFIRMED"=>"结算已核对", "RESULT_BANNER_MISSING"=>"缺少结算提示",
        "RESULT_PAYMENTS_INCOMPLETE"=>"缺少完整结算前后点数", "RESULT_SCORE_ANIMATION"=>"点数仍在动画变化",
        "SPECIAL_DRAW_UNVERIFIED"=>"特殊流局付款待核验", "RESULT_WIN_PAYER_AMBIGUOUS"=>"和牌/付款归属不唯一",
        "RESULT_CONTRADICTORY" or "RESULT_PAYMENTS_CONTRADICTORY"=>"结算读数矛盾",_=>"结算依据不完整",
    };
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
                ImGui.TextWrapped($"本次策略调用 {decision.ChooseMilliseconds?.ToString("F1")??"未知"} ms · 后端请求至返回 {decision.EngineMilliseconds?.ToString("F1")??"未知"} ms（不等于提交耗时）");
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
