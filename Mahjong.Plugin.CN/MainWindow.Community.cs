using Dalamud.Bindings.ImGui;
using Mahjong.Cn.Statistics;
namespace Mahjong.Plugin.CN;
internal sealed partial class MainWindow
{
    private string? communityEndpoint;
    private void DrawCommunity()
    {
        var options=plugin.NetworkOptions;var data=plugin.Community;
        bool show=options.ShowStatistics;
        if(ImGui.Checkbox("显示社区统计（可选联网）",ref show))
            plugin.SaveNetworkPreferences(options with {ShowStatistics=show});
        ImGui.TextWrapped("下载查询向 api.github.com 发送匿名公开资源请求，不含角色、评分或日志。首次启用才查询，成功缓存1小时，失败退避15分钟。关闭后停止新请求。");
        if(show)
        {
            ImGui.TextWrapped($"现存正式安装包累计下载：{data.Downloads.Count?.ToString()??"暂不可用"} · {data.Downloads.ReadAtUtc?.ToLocalTime().ToString("MM-dd HH:mm")??"尚未更新"}");
            if(data.Downloads.Error is {} error)ImGui.TextWrapped(error);
            ImGui.TextWrapped($"在线会话（自愿上报，近3分钟）：{data.ActiveSessions?.ToString()??"暂不可用"} · {data.PresenceReadAt?.ToLocalTime().ToString("HH:mm:ss")??"尚未更新"}");
        }
        communityEndpoint??=options.Endpoint??"";
        if(ImGui.CollapsingHeader("在线服务与参与权限"))
        {
            ImGui.TextWrapped("当前候选版没有部署公网服务。只有你配置服务并明确同意后才发送心跳。测试码不等于联网授权。");
            ImGui.SetNextItemWidth(-1);ImGui.InputText("##community-endpoint",ref communityEndpoint,256);
            if(ImGui.Button("保存服务地址（撤销参与）"))
                plugin.SaveNetworkPreferences(options with {Endpoint=string.IsNullOrWhiteSpace(communityEndpoint)?null:communityEndpoint.Trim(),Participate=false});
            if(options.Participate)
            {if(ImGui.Button("退出在线统计"))plugin.SaveNetworkPreferences(options with {Participate=false});}
            else
            {
                ImGui.BeginDisabled(options.Endpoint is null);
                if(ImGui.Button("参与在线统计…"))ImGui.OpenPopup("presence-consent");
                ImGui.EndDisabled();
            }
            if(ImGui.BeginPopup("presence-consent"))
            {
                try
                {
                    ImGui.TextWrapped("目标："+(options.Endpoint??"服务未配置"));
                    ImGui.TextWrapped("只发送本次加载生成的随机会话ID，每60～69秒一次。服务端保留约180秒；退出后等待过期。不发送角色名、账号、评分、手牌、测试码、日志或路径。服务/CDN仍能接触网络地址，部署者需另行审查访问日志。");
                    if(ImGui.Button("我同意，启用参与")){plugin.SaveNetworkPreferences(options with {Participate=true});ImGui.CloseCurrentPopup();}
                    if(ImGui.Button("取消"))ImGui.CloseCurrentPopup();
                }
                finally{ImGui.EndPopup();}
            }
        }
        ImGui.TextWrapped(data.Status);
        ImGui.TextWrapped("下载不等于人数；排除源码、模型、草稿与预发布，只统计白名单安装ZIP。在线统计不代表所有用户。");
    }
}
