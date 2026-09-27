using System.Collections.Immutable;
using System.Text.Json;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record MatchSummary(Guid Id, DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc,
    uint? DutyId, string Engine, string PluginVersion, string Outcome, string StopReason, string JournalPath);

/// <summary>Only verified event envelopes are accepted. No inference of score/rank from table points.</summary>
internal static class MatchSummaryBuilder
{
    internal static bool Retains(string kind)=>kind is "session_started" or "session_stopped" or "mode_selected" or
        "task_started" or "task_resumed" or "task_match_completed" or "match_result" or "play_paused" or "play_stopped" or "table_automation_action";
    internal static ImmutableArray<MatchSummary> Build(JournalReadResult read,string path)
    {
        if(read.Lines.IsEmpty)return [];
        var entries=read.Lines.Select(x=>x.Entry).ToArray();
        string version="未知",engine="未知",reason="无记录";uint? duty=null;
        foreach(var e in entries)
        {
            if(Text(e.Data,"PluginVersion") is { } v)version=v;
            if((Text(e.Data,"Engine")??Text(e.Data,"DecisionSource")) is { } model)engine=model;
            if(Number(e.Data,"DutyId") is { } d)duty=d;
            if(e.Kind is "play_stopped" or "session_stopped")reason=Text(e.Data,"Reason")??"停止，详见日志";
            if(e.Kind=="play_paused")reason="用户暂停 / 人工接管";
        }
        bool trusted=read.IntegrityPassed&&!read.IncompleteTail;
        var completions=entries.Where(e=>e.Kind=="task_match_completed" && Guid.TryParse(Text(e.Data,"MatchId"),out _))
            .GroupBy(e=>Text(e.Data,"MatchId")).Select(g=>g.First()).ToArray();
        if(completions.Length>0)
            return completions.Select(e=>new MatchSummary(Guid.Parse(Text(e.Data,"MatchId")!),entries[0].Utc,trusted?e.Utc:null,
                Number(e.Data,"DutyId")??duty,Text(e.Data,"Engine")??engine,version,trusted?"完成":"未知（记录不完整）",reason,path)).ToImmutableArray();
        var complete=entries.FirstOrDefault(e=>e.Kind=="match_result" && Text(e.Data,"Source")=="IDutyState.DutyCompleted");
        string outcome=!trusted?"未知（记录不完整）":complete is not null?"完成":entries.Any(e=>e.Kind=="session_stopped")?"中断 / 未确认完成":"未知 / 记录中";
        return [new(entries[0].SessionId,entries[0].Utc,trusted?complete?.Utc:null,duty,engine,version,outcome,reason,path)];
    }
    private static string? Text(JsonElement data,string key)=>data.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?
        p.GetString() is { } s ? s[..Math.Min(s.Length,300)] : null : null;
    private static uint? Number(JsonElement data,string key)=>data.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.Number&&p.TryGetUInt32(out uint value)?value:null;
}
