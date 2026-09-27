using Dalamud.Bindings.ImGui;
using Mahjong.Cn.PublicState;
namespace Mahjong.Plugin.CN;
internal sealed partial class MainWindow
{
    private void DrawOverviewAside()
    {Card("rating","麻将评分",DrawRating);Card("summary","任务摘要",()=>ImGui.TextWrapped(view.TaskStatus));}
    private void DrawOverviewMain()
    {
        DrawDecision();DrawHand();
        Card("table-summary","公开桌面摘要",()=>
        {
            if(view.PublicTable is not {Stability:StabilityState.Stable} table)
            {ImGui.TextWrapped("等待当前稳定的公开桌面。未知字段不会沿用默认值。");return;}
            string Value(Field<int> f)=>f.IsConfirmed?f.Value.ToString():"未知";
            string[] winds=["东","南","西","北"];
            string wind=table.RoundWind.IsConfirmed&&table.RoundWind.Value is >=0 and <4?winds[table.RoundWind.Value]:"未知";
            ImGui.TextWrapped($"场风 {wind} · 局数 {Value(table.HandNumber)} · {Value(table.Honba)} 本场 · 供托 {Value(table.RiichiSticks)} · 余牌 {Value(table.WallRemaining)}");
            var own=table.Players.FirstOrDefault(p=>p.Position==ScreenPosition.Lower);
            ImGui.TextWrapped("本场点数："+(own is null?"未知":Value(own.Score)));
            ImGui.TextWrapped("本场点数不参与麻将评分目标规则。副露与逐家公开牌面见诊断。");
        });
        if(!view.RecentEvents.IsDefaultOrEmpty)Card("recent-actions","最近提交记录",()=>
        {foreach(string entry in view.RecentEvents)ImGui.TextWrapped(entry);});
    }
}
