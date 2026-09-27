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
        bool keep=plugin.AutomationOptions.KeepAutomaticBetweenHands;
        if(ImGui.Checkbox("局间持续等待其他玩家确认",ref keep))QueueAutomationOptions(plugin.AutomationOptions with {KeepAutomaticBetweenHands=keep});
        ImGui.TextWrapped("修改对局选项会暂停任务，需预检后继续或结束后新建；不会覆盖读取错误与安全停止。");
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
        ImGui.TextWrapped("场数在上方统一设置；0 表示不限场。时长与截止时间只约束连续任务，单场按钮完成当前一整场后结束。");
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
            ImGui.TextWrapped($"{rating.ReadAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {rating.Source} · {(fresh?"页面读取":"旧缓存 / 需刷新")}");
            if(rating.FailureReason is { } error)ImGui.TextWrapped(error);
        }
        else ImGui.TextWrapped(view.Rating?.FailureReason??"尚未读取。请打开本人金碟／麻将资料页后点击读取。");
        if(ImGui.Button("读取本人评分（资料页）")) plugin.DispatchUi(plugin.ReadOwnRating);
        ImGui.TextWrapped("评分不同于本场点数；资料页读数不证明上一整场结算已经刷新，目标停止尚未开放。");
    }
}
