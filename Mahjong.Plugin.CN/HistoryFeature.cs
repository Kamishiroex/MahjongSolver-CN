using System.Collections.Immutable;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN;
public sealed partial class Plugin
{
    internal sealed record HistoryState(ImmutableArray<MatchSummary> Items,string Status,bool Busy=false);
    private HistoryState history=new([],"点击刷新，从本机整场事件生成摘要。");
    internal HistoryState History=>Volatile.Read(ref history);
    internal sealed record ReviewState(string? Path,DecisionReviewResult Result,bool Busy=false);
    private ReviewState review=new(null,new([],"选择一场查看本地决策复盘。"));
    internal ReviewState Review=>Volatile.Read(ref review);
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
                int unreadable=0;
                foreach(string directory in JournalReviewStore.Recent(logsDirectory,200))
                {
                    historyCancellation.Token.ThrowIfCancellationRequested();
                    try { rows.AddRange(await JournalReviewStore.SummariesAsync(directory,historyCancellation.Token)); }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) {unreadable++;}
                }
                Volatile.Write(ref history,new(rows.DistinctBy(r=>r.Id).OrderByDescending(r=>r.StartedUtc).ToImmutableArray(),
                    $"范围：最近200份现存本机记录（含归档）；当前保留上限 {JournalKeepMatches} 场 / 1 GiB。无法读取 {unreadable} 份。100～200场仅供初步观察；对手环境、接管和桌型不同不能直接比较强弱。"));
            }
            catch(OperationCanceledException) { }
            catch(Exception ex){Volatile.Write(ref history,new(rows.ToImmutable(),"摘要读取失败："+ex.GetType().Name));}
        });
    }
    internal void RefreshDecisionReview(string path)
    {
        if(Review.Busy)return;
        Volatile.Write(ref review,new(path,new([],"正在校验原始事件…"),true));
        _=Task.Run(async()=>
        {
            try
            {
                var read=await JournalReviewStore.EventsAsync(path,historyCancellation.Token);
                Volatile.Write(ref review,new(path,DecisionReviewBuilder.Build(read)));
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) {Volatile.Write(ref review,new(path,new([],"复盘读取失败："+ex.GetType().Name)));}
        });
    }
}
