using Dalamud.Bindings.ImGui;
using Mahjong.Plugin.CN.Ui;

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
                ImGui.TextWrapped("最终名次：未知；评分前后 / 差值：未验证。");
                ImGui.TextWrapped("接管 / 停止："+Presentation.DisplayCopy.Summary(row.StopReason));
                if(ImGui.SmallButton("复制普通摘要")) ImGui.SetClipboardText($"{Brand.ProductName} · {row.Outcome} · {Presentation.DisplayCopy.Source(row.Engine)} · 最终名次/评分变化未知");
                if(ImGui.CollapsingHeader("本地技术详情")) { ImGui.TextWrapped(row.Engine); ImGui.TextWrapped(row.StopReason); }
                if(ImGui.SmallButton("复制对应日志位置"))ImGui.SetClipboardText(row.JournalPath);
            }
            finally{ImGui.PopID();}
        }
    }
}
