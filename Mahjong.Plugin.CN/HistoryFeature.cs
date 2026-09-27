using System.Collections.Immutable;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN;
public sealed partial class Plugin
{
    internal sealed record HistoryState(ImmutableArray<MatchSummary> Items,string Status,bool Busy=false);
    private HistoryState history=new([],"点击刷新，从本机整场事件生成摘要。");
    internal HistoryState History=>Volatile.Read(ref history);
    private readonly CancellationTokenSource historyCancellation=new();
    internal void RefreshHistory()
    {
        if(History.Busy || logsDirectory is null)return;
        Volatile.Write(ref history,new(History.Items,"正在后台校验最近记录…",true));
        _=Task.Run(async()=>
        {
            var rows=ImmutableArray.CreateBuilder<MatchSummary>();
            try
            {
                foreach(string directory in GameJournal.RecentDirectories(logsDirectory,50))
                {
                    historyCancellation.Token.ThrowIfCancellationRequested();
                    var read=await GameJournal.ReadAsync(Path.Combine(directory,"events.jsonl"),historyCancellation.Token,summaryOnly:true);
                    rows.AddRange(MatchSummaryBuilder.Build(read,directory));
                }
                Volatile.Write(ref history,new(rows.DistinctBy(r=>r.Id).OrderByDescending(r=>r.StartedUtc).ToImmutableArray(),
                    "范围：最近50份未归档记录；归档ZIP仍保留在日志目录旁，不纳入本页。名次、评分变化来源未验证，不计算胜率或平均名次。"));
            }
            catch(OperationCanceledException) { }
            catch(Exception ex){Volatile.Write(ref history,new(rows.ToImmutable(),"摘要读取失败："+ex.GetType().Name));}
        });
    }
}
