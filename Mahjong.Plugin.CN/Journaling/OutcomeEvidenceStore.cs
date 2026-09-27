using System.Text.Json;
using Mahjong.Plugin.CN.Readers;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record PendingOutcome(int Schema,string Kind,Guid MatchId,DateTimeOffset ReadAtUtc,
    ResultUiSample? Final=null,RatingPageEvidence? Rating=null,Guid? ContextToken=null);
internal sealed record OutcomeWrite(string Directory,PendingOutcome Evidence,bool Staged=false);
internal sealed record OutcomeWriteResult(OutcomeWrite Job,bool Completed,string Code);
internal sealed record OutcomeDiscovery(OutcomeWrite[] Jobs,int Rejected);

/// <summary>Persist already observed evidence. Reload/retry performs no game read or input.</summary>
internal static class OutcomeEvidenceStore
{
    private const int Limit=32768;
    private static readonly SemaphoreSlim WriteGate=new(1,1);
    internal static bool Valid(PendingOutcome p)=>p.Schema==1 && p.MatchId!=Guid.Empty &&
        (p.Kind=="final" && p.Final is not null && FinalResultEvidence.Read(p.Final).Placement is not null ||
         p.Kind=="rating" && p.Rating is not null && p.ContextToken is { } token && token!=Guid.Empty &&
         p.Rating.ProfileVersion==MahjongRatingReader.Profile && p.Rating.CurrentRating is >=0 and <=99999 &&
         p.Rating.MatchesPlayed is >=0 and <=99999 && p.ReadAtUtc==p.Rating.ReadAtUtc);
    internal static async Task<OutcomeWriteResult> ProcessAsync(OutcomeWrite job)
    {
        await WriteGate.WaitAsync();
        try{return await ProcessCoreAsync(job);}
        finally{WriteGate.Release();}
    }
    private static async Task<OutcomeWriteResult> ProcessCoreAsync(OutcomeWrite job)
    {
        try
        {
            if(!Valid(job.Evidence))return new(job,false,"RESULT_EVIDENCE_INVALID");
            GameJournal.RejectLinks(job.Directory);
            if(!Directory.Exists(job.Directory))return new(job,false,"RESULT_DIRECTORY_MISSING");
            string pending=Path.Combine(job.Directory,"outcome-pending-"+job.Evidence.Kind+".json");
            if(!job.Staged){await WriteAsync(pending,job.Evidence);job=job with {Staged=true};}
            string closed=Path.Combine(job.Directory,"record-closed.json");GameJournal.RejectLinks(closed);
            if(!File.Exists(closed))return new(job,false,"RESULT_WAITING_FOR_JOURNAL");
            var read=await GameJournal.ReadAsync(Path.Combine(job.Directory,"events.jsonl"),summaryOnly:true);
            if(!read.IntegrityPassed || read.IncompleteTail || read.Lines.IsEmpty ||
                read.Lines[0].Entry.SessionId!=job.Evidence.MatchId || !read.Lines.Any(l=>l.Entry.Kind=="session_stopped"))
                return new(job,false,"RESULT_JOURNAL_MISMATCH");
            string tail=read.Lines[^1].Sha256;
            var rows=MatchSummaryBuilder.Build(read,job.Directory);
            if(job.Evidence.Kind=="final")
            {
                var value=new FinalResultSupplement(1,job.Evidence.MatchId,tail,job.Evidence.ReadAtUtc,job.Evidence.Final!);
                if(!FinalResultEvidence.Apply(read,rows,value).Any(r=>r.Id==value.MatchId && r.Placement.HasValue))
                    return new(job,false,"RESULT_FINAL_ASSOCIATION_REJECTED");
                await WriteAsync(Path.Combine(job.Directory,"final-result.json"),value);
            }
            else
            {
                var value=new MatchRatingSupplement(2,job.Evidence.MatchId,job.Evidence.ContextToken!.Value,tail,job.Evidence.Rating!);
                if(!MatchRatingEvidence.Apply(read,rows,value).Any(r=>r.Id==value.MatchId && r.RatingAfter.HasValue))
                    return new(job,false,"RESULT_RATING_ASSOCIATION_REJECTED");
                await WriteAsync(Path.Combine(job.Directory,"rating-after.json"),value);
            }
            await MatchSummaryBuilder.SaveAsync(job.Directory);
            GameJournal.RejectLinks(pending);File.Delete(pending);
            return new(job,true,"RESULT_SAVED");
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException)
        {return new(job,false,"RESULT_SAVE_"+ex.GetType().Name);}
    }
    private static async Task WriteAsync<T>(string file,T value)
    {
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(value);
        if(bytes.Length>Limit)throw new IOException("RESULT_FILE_LIMIT");
        string temp=file+".tmp";GameJournal.RejectLinks(file);GameJournal.RejectLinks(temp);
        await File.WriteAllBytesAsync(temp,bytes);GameJournal.ReplaceAtomically(temp,file);
    }
    internal static async Task<OutcomeDiscovery> DiscoverAsync(string root)
    {
        var jobs=new List<OutcomeWrite>();int rejected=0;
        foreach(string directory in GameJournal.RecentDirectories(root,200))
        foreach(string kind in new[]{"final","rating"})
        {
            try
            {
                string file=Path.Combine(directory,"outcome-pending-"+kind+".json");GameJournal.RejectLinks(file);
                if(!File.Exists(file))continue;
                if(new FileInfo(file).Length>Limit){rejected++;continue;}
                var value=JsonSerializer.Deserialize<PendingOutcome>(await File.ReadAllBytesAsync(file));
                if(value is not null && value.Kind==kind && Valid(value))jobs.Add(new(directory,value,true));
                else rejected++;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or JsonException) {rejected++;}
        }
        return new(jobs.ToArray(),rejected);
    }
}
