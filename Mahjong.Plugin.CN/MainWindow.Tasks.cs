using System.Globalization;
using Dalamud.Bindings.ImGui;
using Mahjong.Cn.Tasks;
using Mahjong.Cn.Rating;
using Mahjong.Plugin.CN.Ui;

namespace Mahjong.Plugin.CN;

internal sealed partial class MainWindow
{
    private void QueueAutomationOptions(Automation.TableAutomationOptions options)=>plugin.DispatchUi(()=>plugin.SetTableAutomationOptions(options));
    private bool taskEditorLoaded, durationEnabled, deadlineEnabled;
    private int durationMinutes=60;
    private string deadlineText="", taskEditorError="";
    private void DrawTaskRunControls()
    {
        ImGui.TextWrapped(plugin.TaskSummary);
        ImGui.TextWrapped(Presentation.DisplayCopy.Summary(plugin.Status));
        ImGui.BeginDisabled(plugin.TaskRun.Phase!=TaskRunPhase.Paused);
        if(ImGui.Button("预检并继续同一任务")) plugin.DispatchUi(plugin.ResumeTask);
        ImGui.EndDisabled();
        SameLineIfFits(140*GlassTheme.Scale);
        if(ImGui.Button("结束本次任务")) plugin.EndTask();
        ImGui.TextWrapped("继续保留场数、时长与原规则，并明确采用当前求解来源；修改桌型需结束后新建。暂停不强退，关闭窗口不暂停。");
    }
    private void DrawTaskRules()
    {
        if(plugin.GameOperationsAvailable)
        {
            bool keep=plugin.AutomationOptions.KeepAutomaticBetweenHands;
            if(ImGui.Checkbox("局间持续等待其他玩家确认",ref keep))QueueAutomationOptions(plugin.AutomationOptions with {KeepAutomaticBetweenHands=keep});
        }
        ImGui.TextWrapped("标准任务只提示并跟踪目标；到达已支持的时长或截止目标后停止提示。修改规则会暂停任务，需主动继续或新建。");
        if(!taskEditorLoaded)
        {
            var rules=plugin.TaskRules; taskEditorLoaded=true;
            durationEnabled=rules.ActiveSecondsLimit is not null;
            durationMinutes=(int)((rules.ActiveSecondsLimit??3600)/60);
            deadlineEnabled=rules.DeadlineUtc is not null;
            deadlineText=(rules.DeadlineUtc??DateTimeOffset.Now.AddHours(2)).ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz",CultureInfo.InvariantCulture);
        }
        ImGui.Checkbox("有效运行时长上限",ref durationEnabled);
        if(durationEnabled) { ImGui.SetNextItemWidth(180*GlassTheme.Scale); ImGui.InputInt("分钟（暂停不计时）",ref durationMinutes); }
        ImGui.Checkbox("截止日期时间",ref deadlineEnabled);
        if(deadlineEnabled)
        { ImGui.SetNextItemWidth(-1); ImGui.InputText("##task-deadline",ref deadlineText,64); ImGui.TextWrapped("格式 yyyy-MM-dd HH:mm +08:00；明确日期和 UTC 时差，保存后不会随系统时区改变。"); }
        ImGui.TextWrapped("提示任务跟踪当前整场；测试版连续任务另有场数设置。暂停不计入有效运行时长。");
        if(ImGui.Button("保存任务规则（不启动）"))
        {
            taskEditorError=""; DateTimeOffset? deadline=null;
            if(deadlineEnabled)
            {
                if(DateTimeOffset.TryParseExact(deadlineText,"yyyy-MM-dd HH:mm zzz",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed)) deadline=parsed.ToUniversalTime();
                else taskEditorError="截止日期格式无效，请填写完整日期和时差。";
            }
            var rules=new StopRuleSet(ActiveSecondsLimit:durationEnabled?durationMinutes*60d:null,DeadlineUtc:deadline);
            if(rules.Validate() is not null)taskEditorError="时长须为 1～10080 分钟，且停止规则不能冲突。";
            if(taskEditorError.Length==0) plugin.DispatchUi(()=>plugin.SaveTaskRules(rules));
        }
        ImGui.TextWrapped(taskEditorError.Length>0?taskEditorError:plugin.TaskSettingsStatus);
        ImGui.Separator();
        ImGui.TextWrapped("评分目标 / 下限 / 累计掉分：暂不可启用。本人资料页字段已核对，整场结果刷新关联尚未实机验证。");
        ImGui.TextWrapped("连续第四名：暂不可启用，缺少已核验的最终名次来源。未知结果不会算作第四名。");
        if(plugin.TaskRun.Plan is { } plan)
            ImGui.TextWrapped($"当前授权快照：{(plan.Rules.MatchLimit==0?"不限场":plan.Rules.MatchLimit+" 场")}；时长 {plan.Rules.ActiveSecondsLimit/60:0.#} 分钟；截止 {plan.Rules.DeadlineUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz")??"未设置"}。");
    }
    private void DrawRating()
    {
        if(view.Rating is {CurrentRating:{ } current} rating)
        {
            bool fresh=rating.Freshness==RatingFreshness.Fresh && DateTimeOffset.UtcNow-rating.ReadAtUtc<TimeSpan.FromSeconds(3);
            ImGui.TextColored(GlassTheme.Accent,$"{(fresh?"当前":"上次读取")}麻将评分：{current}");
            ImGui.TextWrapped($"最高评分：{rating.HighestRating?.ToString()??"未知"} · 段位：{rating.Rank??"未知"}");
            ImGui.TextWrapped($"{rating.ReadAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {rating.Source} · {(fresh?"页面读取":"保留读数")}");
            if(rating.FailureReason is { } error)ImGui.TextWrapped(error);
        }
        else ImGui.TextWrapped(view.Rating?.FailureReason??(plugin.GameOperationsAvailable
            ? "尚未读取。点击刷新本人评分即可。" : "尚未读取。请手动打开金碟／方城战资料页，再点击读取。"));
        ImGui.BeginDisabled(view.RatingRefreshing);
        if(ImGui.Button(view.RatingRefreshing?"正在刷新…###refresh-rating":plugin.GameOperationsAvailable
            ? "刷新本人评分###refresh-rating" : "读取已打开页面###refresh-rating")) plugin.DispatchUi(plugin.RefreshOwnRating);
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered())ImGui.SetTooltip(plugin.GameOperationsAvailable
            ? "自动打开金碟资料、切到方城战并读分；只关闭插件自己打开的窗口。"
            : "标准模式只读已显示的方城战资料页，不开窗、切页或关窗。");
        if(!string.IsNullOrEmpty(view.RatingStatus))ImGui.TextWrapped(view.RatingStatus);
        ImGui.TextWrapped("评分不同于本场点数；资料页读数不证明上一整场结算已经刷新，目标停止尚未开放。");
    }
}
