using System.Collections.Immutable;
using System.Text.Json;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record DecisionReviewRow(Guid Id, DateTimeOffset Utc, string Backend, string Input,
    string Choice, string Candidates, string Filters, string Override, string Submissions, string Observations)
{
    public double? ChooseMilliseconds {get;init;}
    public double? EngineMilliseconds {get;init;}
}
internal sealed record DecisionReviewResult(ImmutableArray<DecisionReviewRow> Items,string Status)
{
    public string UnlinkedSubmissions { get; init; } = "无记录";
}

internal static class DecisionReviewBuilder
{
    internal static DecisionReviewResult Build(JournalReadResult read)
    {
        if(!read.IntegrityPassed || read.IncompleteTail)return new([],"记录校验未通过或尾部不完整，不能作为可靠复盘。");
        var entries=read.Lines.Select(l=>l.Entry).ToArray();
        var traces=Traces(read);
        var rows=ImmutableArray.CreateBuilder<DecisionReviewRow>();
        foreach(var e in entries.Where(e=>e.Kind=="review_decision").TakeLast(50))
        {
            if(!Guid.TryParse(Text(e.Data,"DecisionId"),out var id))continue;
            string? input=Text(e.Data,"EngineInputSha256");
            var trace=input is null?null:traces.Where(t=>t.Sequence<e.Sequence && Text(t.Data,"InputSha256")==input).MaxBy(t=>t.Sequence);
            var submissions=entries.Where(s=>s.Kind=="action_submission" && Text(s.Data,"DecisionId")==id.ToString()).ToArray();
            var ids=submissions.Select(s=>Text(s.Data,"SubmissionId")).Where(s=>s is not null).ToHashSet();
            var observed=entries.Where(s=>s.Kind=="review_action_observation" && ids.Contains(Text(s.Data,"SubmissionId"))).ToArray();
            string candidates=Json(e.Data,"RawDiscardCandidates"), filters="未记录 / 不适用";
            if(trace is not null)
            {
                if(trace.Data.TryGetProperty("Decision",out var decision) && decision.ValueKind==JsonValueKind.Object)
                    candidates=Json(decision,"Candidates");
                filters=Json(trace.Data,"CandidateReviews");
            }
            rows.Add(new(id,e.Utc,Text(e.Data,"Backend")??"未知",input??Text(e.Data,"InputSha256")??"未知",
                Json(e.Data,"Choice"),candidates,filters,Text(e.Data,"BackendOverride")??"后端内部覆盖未知（当前桥未报告）",
                submissions.Length==0?"未记录提交；可能仅提醒或状态已改变。":string.Join('\n',submissions.Select(s=>s.Data.GetRawText())),
                observed.Length==0?"未知；没有关联的后续观察。":string.Join('\n',observed.Select(s=>s.Data.GetRawText())))
                {ChooseMilliseconds=Number(e.Data,"ChooseMilliseconds"),EngineMilliseconds=Number(e.Data,"EngineMilliseconds")});
        }
        return new(rows.ToImmutable(),"最近50条去重决策；原始候选、过滤原因与输入标识取自本地技术日志。状态变化不等于操作已被接受；保护流程可独立于模型提交，详见未关联提交。")
        {
            UnlinkedSubmissions=string.Join('\n',entries.Where(e=>e.Kind=="action_submission" && Text(e.Data,"DecisionId") is null)
                .TakeLast(50).Select(e=>$"{e.Utc:HH:mm:ss} {e.Data.GetRawText()}"))
        };
    }
    internal static List<JournalBody> Traces(JournalReadResult read)
    {
        var entries=read.Lines.Select(l=>l.Entry).ToArray();
        var traces=entries.Where(e=>e.Kind is "global_ai_decision" or "global_ai_error").ToList();
        foreach(var chunks in entries.Where(e=>e.Kind=="global_ai_trace_chunk").GroupBy(e=>Text(e.Data,"TraceId")))
        {
            try
            {
                var pieces=chunks.OrderBy(e=>MatchSummaryBuilder.Number(e.Data,"Index")).ToArray();
                int? count=MatchSummaryBuilder.Number(pieces[0].Data,"Count"), size=MatchSummaryBuilder.Number(pieces[0].Data,"TotalBytes");
                if(count is null or <=0 or >256 || size is null or <=0 or >2*1024*1024 || pieces.Length!=count)continue;
                using var bytes=new MemoryStream();
                for(int i=0;i<pieces.Length;i++)
                {
                    var data=pieces[i].Data;
                    if(MatchSummaryBuilder.Number(data,"Index")!=i || MatchSummaryBuilder.Number(data,"Count")!=count ||
                        MatchSummaryBuilder.Number(data,"TotalBytes")!=size)throw new InvalidDataException();
                    byte[] piece=Convert.FromBase64String(data.GetProperty("Data").GetString()!);
                    if(bytes.Length+piece.Length>size)throw new InvalidDataException(); bytes.Write(piece);
                }
                if(bytes.Length!=size)continue;
                using var doc=JsonDocument.Parse(bytes.ToArray());
                if(Text(doc.RootElement,"Phase") is "decision" or "error") traces.Add(pieces[^1] with {Data=doc.RootElement.Clone()});
            }
            catch(Exception ex) when(ex is JsonException or FormatException or InvalidOperationException or InvalidDataException or KeyNotFoundException) { }
        }
        return traces;
    }
    private static double? Number(JsonElement data,string key)=>data.TryGetProperty(key,out var p) && p.ValueKind==JsonValueKind.Number && p.TryGetDouble(out double n) && double.IsFinite(n) && n>=0?n:null;
    private static string? Text(JsonElement data,string key)=>MatchSummaryBuilder.Text(data,key);
    private static string Json(JsonElement data,string key) => data.ValueKind==JsonValueKind.Object && data.TryGetProperty(key,out var p) && p.ValueKind!=JsonValueKind.Null
        ?p.GetRawText():"未知 / 未提供";
}
