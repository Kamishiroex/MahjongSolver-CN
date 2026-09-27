using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mahjong.Core;
using Mahjong.Cn.Engines;
using Mahjong.Engine;
using Mahjong.Plugin.CN.Experimental;
using Mahjong.Policy.Abstractions;
using Mahjong.Policy.Efficiency;
using Mahjong.Rules.Rulesets;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record CorpusCase(string Key,StateSnapshot Snapshot,AkochanGlobalSnapshot? PublicInput,
    ActionChoice RecordedChoice,string Backend,string? Configuration,JsonElement? Candidates,JsonElement? Filters,
    string? Override,double? Milliseconds);
internal sealed record DecisionCorpusFile(int Schema,string Source,string JournalTailSha256,CorpusCase[] Cases,int Skipped);
internal sealed record CorpusDecision(string Key,ActionChoice? Choice,JsonElement? Candidates,JsonElement? Filters,
    string? Override,double Milliseconds,string? Error);
internal sealed record CorpusRun(int Schema,string CorpusSha256,string Backend,string BuildSha256,string? InstallationSha256,
    CorpusDecision[] Cases)
{
    public bool GameOperationsExecuted=>false;
    public string Scope=>"Offline replay. Decision/candidate changes are not proof of playing strength or live acceptance.";
}

/// <summary>Fixed public inputs only. Export and replay never access the game or confer task authority.</summary>
internal static class DecisionCorpus
{
    internal const int MaxBytes=8*1024*1024;
    internal static DecisionCorpusFile Export(JournalReadResult read)
    {
        if(!read.IntegrityPassed || read.IncompleteTail || read.Lines.IsEmpty)throw new InvalidDataException("CORPUS_JOURNAL_INTEGRITY");
        var cases=new List<CorpusCase>();int skipped=0;
        var traces=DecisionReviewBuilder.Traces(read);
        foreach(var line in read.Lines.Where(l=>l.Entry.Kind=="review_decision").TakeLast(200))
        {
            var data=line.Entry.Data;
            try
            {
                if(!data.TryGetProperty("ReplaySnapshot",out var raw)){skipped++;continue;}
                var state=raw.Deserialize<StateSnapshot>()!;
                ValidateState(state);
                string hash=DecisionReviewPolicy.InputHash(state);
                if(hash!=MatchSummaryBuilder.Text(data,"InputSha256"))throw new InvalidDataException("CORPUS_INPUT_HASH");
                string backend=MatchSummaryBuilder.Text(data,"Backend")??"unknown";
                string? nativeHash=MatchSummaryBuilder.Text(data,"EngineInputSha256");
                var trace=nativeHash is null?null:traces.Where(t=>t.Sequence<line.Entry.Sequence &&
                    MatchSummaryBuilder.Text(t.Data,"InputSha256")==nativeHash).MaxBy(t=>t.Sequence);
                AkochanGlobalSnapshot? input=null;
                if(trace is not null && trace.Data.TryGetProperty("Input",out var p))input=p.Deserialize<AkochanGlobalSnapshot>();
                if(input is not null && AkochanGlobalEngine.ComputeInputSha256(input)!=nativeHash)throw new InvalidDataException("CORPUS_NATIVE_HASH");
                if(backend!="upstream-efficiency" && input is null){skipped++;continue;}
                string? configuration=MatchSummaryBuilder.Text(data,"ConfigurationId");
                var identity=read.Lines.LastOrDefault(l=>l.Entry.Kind=="review_configuration" &&
                    MatchSummaryBuilder.Text(l.Entry.Data,"ConfigurationId")==configuration);
                var decision=trace is null?null:Property(trace.Data,"Decision");
                var candidates=decision is {ValueKind:JsonValueKind.Object}?Property(decision.Value,"Candidates"):Property(data,"RawDiscardCandidates");
                var item=new CorpusCase(Key(state,input),state,input,data.GetProperty("Choice").Deserialize<ActionChoice>()!,backend,
                    identity is null?null:MatchSummaryBuilder.Text(identity.Entry.Data,"Fingerprint"),candidates,
                    trace is null?null:Property(trace.Data,"CandidateReviews"),MatchSummaryBuilder.Text(data,"BackendOverride"),
                    Number(data,input is null?"ChooseMilliseconds":"EngineMilliseconds"));
                if(cases.All(c=>c.Key!=item.Key))cases.Add(item);
            }
            catch(Exception ex) when(ex is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            {skipped++;}
        }
        return new(1,"Verified local journal; visible inputs; no names or account identifiers",read.Lines[^1].Sha256,cases.ToArray(),skipped);
    }
    internal static string Key(StateSnapshot state,AkochanGlobalSnapshot? input)=>DecisionReviewPolicy.InputHash(state)+
        (input is null?"":":"+AkochanGlobalEngine.ComputeInputSha256(input));
    internal static void ValidateState(StateSnapshot state)
    {
        if(state is null || state.SchemaVersion!=StateSnapshot.CurrentSchemaVersion || state.OurSeat is <0 or >3 ||
            state.Hand is null || state.Hand.Count is <1 or >14 || state.Hand.Any(t=>t.Id>=34) ||
            state.Hand.GroupBy(t=>t.Id).Any(g=>g.Count()>4) || state.Seats is not {Count:4} || state.Scores is not {Count:4} ||
            state.OurMelds is null || state.OurMelds.Count>4 || state.Legal is null || state.UraDoraIndicators.Count!=0)
            throw new InvalidDataException("CORPUS_STATE_INVALID");
    }
    internal static async Task WriteNewAsync<T>(string path,T value)
    {
        var safe=Diagnostics.DiagnosticPrivacy.Sanitize(JsonSerializer.SerializeToElement(value));
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(safe,new JsonSerializerOptions{WriteIndented=true});
        if(bytes.Length>MaxBytes)throw new InvalidDataException("CORPUS_SIZE_LIMIT");
        GameJournal.RejectLinks(path);
        await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        await file.WriteAsync(bytes);
    }
    internal static async Task<T> ReadAsync<T>(string path)
    {
        GameJournal.RejectLinks(path);
        if(new FileInfo(path).Length>MaxBytes)throw new InvalidDataException("CORPUS_SIZE_LIMIT");
        return JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path))??throw new InvalidDataException("CORPUS_EMPTY");
    }
    internal static async Task<CorpusRun> ReplayAsync(DecisionCorpusFile corpus,string backend,string? directory=null)
    {
        if(corpus.Schema!=1 || corpus.Cases is null || corpus.Cases.Length is <1 or >200 ||
            corpus.Cases.Select(c=>c.Key).Distinct().Count()!=corpus.Cases.Length)throw new InvalidDataException("CORPUS_SCHEMA");
        if(backend is not ("upstream" or "mortal" or "akochan"))throw new ArgumentException("CORPUS_BACKEND");
        // Experimental executables are only loaded by an explicit offline backend argument.
        MortalInstallation? mortal=null;AkochanInstallation? ako=null;string? installHash=null;
        if(backend!="upstream")
        {
            if(directory is null)throw new ArgumentException("CORPUS_INSTALLATION_REQUIRED");
            if(backend=="mortal")mortal=await MortalInstallation.LoadAsync(directory);
            else ako=await AkochanInstallation.LoadAsync(directory);
            string manifest=Path.Combine(directory,backend=="mortal"?"mortal-installation.json":"akochan-installation.json");
            installHash=Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifest)));
        }
        await using var mortalEngine=new MortalGlobalEngine();
        var results=new List<CorpusDecision>();
        foreach(var item in corpus.Cases)
        {
            var watch=Stopwatch.StartNew();
            try
            {
                ValidateState(item.Snapshot);
                if(item.Key!=Key(item.Snapshot,item.PublicInput))throw new InvalidDataException("CORPUS_INPUT_CHANGED");
                ActionChoice choice;JsonElement? candidates=null,filters=null;string? selection=null;
                if(backend=="upstream")
                {
                    var policy=new EfficiencyPolicy(new DomanRuleSet());
                    policy.CandidatesScored+=v=>candidates=JsonSerializer.SerializeToElement(v);
                    choice=policy.Choose(item.Snapshot);
                }
                else
                {
                    var input=item.PublicInput??throw new InvalidDataException("CORPUS_PUBLIC_INPUT_MISSING");
                    DateTimeOffset time=DateTimeOffset.UtcNow;GlobalAiTrace? final=null;
                    // Only freshness time is replaced. Canonical input excludes Utc; its hash must stay identical.
                    var replay=input with {Utc=time};
                    if(AkochanGlobalEngine.ComputeInputSha256(replay)!=AkochanGlobalEngine.ComputeInputSha256(input))
                        throw new InvalidDataException("CORPUS_REFRESH_CHANGED_INPUT");
                    using var policy=new AkochanGlobalPolicy(_=>new(replay,null),_=>{},t=>final=t,
                        (s,c)=>mortal is not null?mortalEngine.AnalyzeAsync(mortal,s,c):
                            new AkochanGlobalEngine().AnalyzeAsync(ako!,s,TimeSpan.FromSeconds(10),c),
                        clock:()=>time,expectedCommit:mortal is not null?MortalInstallation.Commit:AkochanInstallation.ExpectedCommit,
                        engineLabel:backend,accessValid:()=>true);
                    choice=policy.Choose(item.Snapshot);
                    if(policy.PendingWork is {} pending){await pending.WaitAsync(TimeSpan.FromSeconds(30));choice=policy.Choose(item.Snapshot);}
                    candidates=final?.Decision is {} d?JsonSerializer.SerializeToElement(d.Candidates):null;
                    filters=final is null?null:JsonSerializer.SerializeToElement(final.CandidateReviews);
                    if(final?.Decision is {} decision)
                    {using var doc=JsonDocument.Parse(decision.AppliedSnapshotJson);selection=MatchSummaryBuilder.Text(doc.RootElement,"selection_origin");}
                }
                string? error=choice.Reasoning?.StartsWith("AKOCHAN_BLOCKED:",StringComparison.Ordinal)==true?choice.Reasoning:
                    choice.Reasoning?.StartsWith("AKOCHAN_PENDING:",StringComparison.Ordinal)==true?"CORPUS_NO_COMPLETED_DECISION":null;
                results.Add(new(item.Key,choice,candidates,filters,selection,watch.Elapsed.TotalMilliseconds,error));
            }
            catch(Exception ex){results.Add(new(item.Key,null,null,null,null,watch.Elapsed.TotalMilliseconds,
                ex is AkochanException akoError?akoError.Code:ex is EngineProcessException processError?"PROCESS_"+processError.Code:
                ex is InvalidDataException invalid?invalid.Message:ex.GetType().Name));}
        }
        var builds=new[]{typeof(Plugin),typeof(EfficiencyPolicy),typeof(DomanRuleSet),typeof(StateSnapshot)}
            .Select(t=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(t.Assembly.Location)))).ToArray();
        return new(1,Hash(corpus),backend,Hash(builds),installHash,results.ToArray());
    }
    internal static object Compare(CorpusRun before,CorpusRun after)
    {
        if(before.Schema!=1 || after.Schema!=1 || before.CorpusSha256!=after.CorpusSha256)
            throw new InvalidDataException("CORPUS_COMPARISON_REQUIRES_IDENTICAL_INPUTS");
        if(before.Cases.Select(c=>c.Key).Distinct().Count()!=before.Cases.Length ||
            after.Cases.Select(c=>c.Key).Distinct().Count()!=after.Cases.Length)throw new InvalidDataException("CORPUS_DUPLICATE_CASE");
        var comparisons=before.Cases.Join(after.Cases,a=>a.Key,b=>b.Key,(a,b)=>new {a.Key,
            ActionChanged=ActionKey(a.Choice)!=ActionKey(b.Choice),CandidatesChanged=!Same(a.Candidates,b.Candidates),
            FiltersChanged=!Same(a.Filters,b.Filters),OverrideChanged=a.Override!=b.Override,
            BeforeError=a.Error,AfterError=b.Error,BeforeMilliseconds=a.Milliseconds,AfterMilliseconds=b.Milliseconds}).ToArray();
        return new {Schema=1,SameCorpus=true,Compared=comparisons.Length,MissingBefore=after.Cases.Length-comparisons.Length,
            MissingAfter=before.Cases.Length-comparisons.Length,BeforeBackend=before.Backend,AfterBackend=after.Backend,
            BeforeBuild=before.BuildSha256,AfterBuild=after.BuildSha256,BeforeInstallation=before.InstallationSha256,AfterInstallation=after.InstallationSha256,
            Cases=comparisons,StrengthImproved=(bool?)null,GameOperationsExecuted=false};
    }
    private static string? ActionKey(ActionChoice? c)=>c is null?null:JsonSerializer.Serialize(c with {Reasoning=""});
    private static bool Same(JsonElement? a,JsonElement? b)=>a is null?b is null:b is not null && JsonElement.DeepEquals(a.Value,b.Value);
    private static string Hash(object value)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static JsonElement? Property(JsonElement d,string k)=>d.TryGetProperty(k,out var p)&&p.ValueKind!=JsonValueKind.Null?p.Clone():null;
    private static double? Number(JsonElement d,string k)=>d.TryGetProperty(k,out var p)&&p.ValueKind==JsonValueKind.Number&&p.TryGetDouble(out var n)?n:null;
}
