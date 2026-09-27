using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private readonly Dictionary<string,OutcomeWrite> resultWrites=[];
    private Task<OutcomeWriteResult[]>? resultWriteTask;
    private Task<OutcomeDiscovery>? resultDiscovery;
    private double nextResultWrite;
    private int resultWriteFailures;
    internal string ResultsStatus {get;private set;}="等待本人结算；已保存记录可重新校验和补全。";
    internal void RepairResultRecords()
    {
        if(disposed || logsDirectory is null || resultDiscovery is not null)return;
        ResultsStatus="正在查找已经采到但未完成保存的结果…";
        resultDiscovery=Task.Run(()=>OutcomeEvidenceStore.DiscoverAsync(logsDirectory));
        nextResultWrite=0;resultWriteFailures=0;
        RefreshHistory(); // Existing completed data can always be reconstructed, even without a model.
    }
    private static string ResultWriteKey(OutcomeWrite job)=>job.Directory+"|"+job.Evidence.Kind;
    private void QueueResultEvidence(GameJournal source,PendingOutcome value)
    {
        var job=new OutcomeWrite(source.DirectoryPath,value);
        resultWrites[ResultWriteKey(job)]=job;nextResultWrite=0;
        ResultsStatus="结算已读取，正在保存；未封口的日志会等待整场记录结束。";
        PollResultWrites(); // Start staging immediately, including when unload follows this frame.
    }
    private void PollResultWrites()
    {
        if(resultWrites is null)return;
        if(resultDiscovery is {IsCompleted:true} discovery)
        {
            resultDiscovery=null;
            if(discovery.IsCompletedSuccessfully)
            {
                foreach(var job in discovery.Result.Jobs)resultWrites.TryAdd(ResultWriteKey(job),job);
                ResultsStatus=$"找到 {discovery.Result.Jobs.Length} 份待保存结果；无效或无法读取 {discovery.Result.Rejected} 份（原文件保留）。";
            }
            else ResultsStatus="读取待保存结果失败："+discovery.Exception?.GetBaseException().GetType().Name;
        }
        if(resultWriteTask is {IsCompleted:true} task)
        {
            resultWriteTask=null;bool saved=false;string? failure=null;bool waiting=false,terminal=false;
            if(task.IsCompletedSuccessfully)
            foreach(var result in task.Result)
            {
                string key=ResultWriteKey(result.Job);
                // A later capture must not be removed by completion of an older job.
                if(!resultWrites.TryGetValue(key,out var current) || current.Evidence!=result.Job.Evidence)continue;
                if(result.Completed){resultWrites.Remove(key);saved=true;}
                else
                {
                    resultWrites[key]=result.Job;
                    if(result.Code=="RESULT_WAITING_FOR_JOURNAL")waiting=true;
                    else
                    {
                        failure=result.Code;
                        // No amount of retry can repair contradictory provenance.
                        // Keep the staged file for diagnosis, but don't spin forever.
                        if(result.Code is "RESULT_EVIDENCE_INVALID" or "RESULT_JOURNAL_MISMATCH" or
                            "RESULT_FINAL_ASSOCIATION_REJECTED" or "RESULT_RATING_ASSOCIATION_REJECTED"){resultWrites.Remove(key);terminal=true;}
                    }
                }
            }
            else failure="RESULT_WRITE_WORKER_FAILED";
            if(failure is not null){resultWriteFailures++;ResultsStatus="结果保存未完成："+failure+
                (terminal?"；关联未通过，保留原文件供诊断。":"；已保留待处理数据，将重试。");}
            else {resultWriteFailures=0;ResultsStatus=waiting?"结果已暂存，等待整场日志封口。":"结算结果保存完成。";}
            if(saved && !disposed)RefreshHistory();
            nextResultWrite=AutomationNow+(failure is null?1:Math.Min(30,Math.Pow(2,Math.Min(resultWriteFailures,5))));
        }
        if(resultWriteTask is null && resultWrites.Count>0 && AutomationNow>=nextResultWrite)
        {
            var batch=resultWrites.Values.Take(16).ToArray();
            resultWriteTask=Task.Run(async()=>
            {
                var results=new List<OutcomeWriteResult>();
                foreach(var job in batch)results.Add(await OutcomeEvidenceStore.ProcessAsync(job));
                return results.ToArray();
            });
        }
    }
    private void StageRemainingResultsOnUnload()
    {
        if(resultWrites is null || resultWrites.Count==0)return;
        var jobs=resultWrites.Values.ToArray();var previous=resultWriteTask;
        // Managed evidence only; never wait synchronously on the framework or read the game after unload.
        _=Task.Run(async()=>
        {
            if(previous is not null)try{await previous;}catch { }
            foreach(var job in jobs)await OutcomeEvidenceStore.ProcessAsync(job);
        });
    }
}
