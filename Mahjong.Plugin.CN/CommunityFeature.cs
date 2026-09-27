using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Mahjong.Cn.Statistics;

namespace Mahjong.Plugin.CN;
public sealed partial class Plugin
{
    internal sealed record NetworkPreferences(int SchemaVersion=1,bool ShowStatistics=false,bool Participate=false,string? Endpoint=null);
    internal sealed record CommunityState(DownloadObservation Downloads,int? ActiveSessions,DateTimeOffset? PresenceReadAt,string Status);
    private NetworkPreferences networkPreferences=new();
    private CommunityState community=new(new(null,null,null),null,null,"可选联网统计已关闭。");
    private readonly HttpClient communityHttp=new(new HttpClientHandler {AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};
    private readonly CancellationTokenSource communityLifetime=new();
    private CancellationTokenSource? communityRequest;
    private PublicDownloadStats? downloadStats;
    private OptionalPresenceClient? presenceClient;
    private Task? communityWorker;
    private double nextCommunityPoll;
    private int communityGeneration,networkSaving;
    internal NetworkPreferences NetworkOptions=>Volatile.Read(ref networkPreferences);
    internal CommunityState Community=>Volatile.Read(ref community);
    private string NetworkPath=>Path.Combine(Interface.GetPluginConfigDirectory(),"network-preferences.json");
    private void LoadNetworkPreferences()
    {
        try
        {
            if(!File.Exists(NetworkPath))return;
            if(new FileInfo(NetworkPath).Length>4096)throw new InvalidDataException();
            var options=JsonSerializer.Deserialize<NetworkPreferences>(File.ReadAllText(NetworkPath));
            if(options is not {SchemaVersion:1} || !ValidNetworkPreferences(options))throw new InvalidDataException();
            networkPreferences=options;
        }
        catch(Exception ex){community=community with {Status="联网配置读取失败，已关闭："+ex.GetType().Name};}
    }
    private static bool ValidNetworkPreferences(NetworkPreferences options)=>options.Endpoint is null ||
        (options.Endpoint.Length<=256&&Uri.TryCreate(options.Endpoint,UriKind.Absolute,out var uri)&&OptionalPresenceClient.ValidEndpoint(uri));
    internal void SaveNetworkPreferences(NetworkPreferences options)
    {
        if(options.SchemaVersion!=1 || !ValidNetworkPreferences(options) || (options.Participate&&options.Endpoint is null))
        {community=community with {Status="服务地址无效或尚未配置；没有发送心跳。"};return;}
        if(Interlocked.CompareExchange(ref networkSaving,1,0)!=0)return;
        // Revocation is immediate; late results from previous settings cannot publish.
        Interlocked.Increment(ref communityGeneration);communityRequest?.Cancel();
        Volatile.Write(ref networkPreferences,options);nextCommunityPoll=0;
        community=community with {ActiveSessions=null,PresenceReadAt=null,Status="已应用联网选择，正在保存。"};
        string path=NetworkPath;
        _=Task.Run(async()=>
        {
            string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                if(File.Exists(path))
                {var old=JsonSerializer.Deserialize<NetworkPreferences>(await File.ReadAllTextAsync(path));if(old?.SchemaVersion!=1)throw new InvalidDataException();File.Copy(path,path+".bak",true);}
                await File.WriteAllTextAsync(tmp,JsonSerializer.Serialize(options));File.Move(tmp,path,true);
            }
            catch(Exception ex){community=community with {Status="联网选择仅本次生效，保存失败："+ex.GetType().Name};}
            finally{Interlocked.Exchange(ref networkSaving,0);try{if(File.Exists(tmp))File.Delete(tmp);}catch(IOException){}catch(UnauthorizedAccessException){}}
        });
    }
    private void UpdateCommunity()
    {
        var options=NetworkOptions;
        if(options is null)return; // Partial startup/disposal and isolated core fixtures: networking remains off.
        if(!options.ShowStatistics&&!options.Participate)return;
        double now=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
        if(now<nextCommunityPoll||communityWorker is {IsCompleted:false})return;
        nextCommunityPoll=now+60+Random.Shared.Next(0,10);
        int generation=Volatile.Read(ref communityGeneration);
        communityRequest?.Dispose();communityRequest=CancellationTokenSource.CreateLinkedTokenSource(communityLifetime.Token);
        communityRequest.CancelAfter(TimeSpan.FromSeconds(55));var token=communityRequest.Token;
        downloadStats??=new(communityHttp);presenceClient??=new(communityHttp);
        communityWorker=Task.Run(async()=>
        {
            var result=Community;
            void Publish(){if(!disposed&&generation==Volatile.Read(ref communityGeneration))Volatile.Write(ref community,result);}
            try
            {
                Uri? endpoint=options.Endpoint is null?null:new(options.Endpoint);
                result=result with {Status=endpoint is null?"在线服务未配置；不会发送心跳。":"在线会话仅统计自愿参与的近3分钟会话。"};
                try
                {
                    if(options.Participate)await presenceClient.HeartbeatAsync(endpoint,true,token);
                    if(options.ShowStatistics && endpoint is not null)
                        result=result with {ActiveSessions=await presenceClient.ReadCountAsync(endpoint,true,token),PresenceReadAt=DateTimeOffset.UtcNow};
                }
                catch(Exception ex) when(ex is HttpRequestException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
                {result=result with {Status="在线服务暂不可用："+ex.GetType().Name};nextCommunityPoll=now+300;}
                if(options.ShowStatistics)
                {
                    result=result with {Downloads=await downloadStats.ReadAsync(DateTimeOffset.UtcNow,token)};
                }
                Publish();
            }
            catch(Exception ex) when(ex is HttpRequestException or JsonException or InvalidDataException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
            {result=result with {Status="联网统计暂不可用："+ex.GetType().Name+"；本地任务不受影响。"};Publish();}
        });
    }
}
