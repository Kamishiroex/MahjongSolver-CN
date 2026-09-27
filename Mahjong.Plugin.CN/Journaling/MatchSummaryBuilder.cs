using System.Collections.Immutable;
using System.Text.Json;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record MatchSummary(Guid Id, DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc,
    uint? DutyId, string Engine, string PluginVersion, string Outcome, string StopReason, string JournalPath)
{
    public bool IntegrityVerified { get; init; }
    public string? ConfigurationFingerprint { get; init; }
    public bool MixedConfiguration { get; init; }
    public string MatchType { get; init; } = "unknown";
    public string OpponentEnvironment { get; init; } = "unknown";
    public string? Bridge { get; init; }
    public string? ModelSha256 { get; init; }
    public string? SettingsSha256 { get; init; }
    public int? Placement { get; init; }
    public int? RatingBefore { get; init; }
    public int? RatingAfter { get; init; }
    public int? RatingDelta { get; init; }
    public int? HandsWithVerifiedOutcome { get; init; }
    public int? Wins { get; init; }
    public int? DealIns { get; init; }
    public bool? AutomaticWholeMatch { get; init; }
    public int? RecordedTakeovers { get; init; }
    public ImmutableArray<string> Stops { get; init; } = [];
    public int Submissions { get; init; }
    public int ObservedTransitions { get; init; }
    public int ObservationTimeouts { get; init; }
    public int CancelledWindows { get; init; }
    public int? ConfirmedMissedActions { get; init; }
}

/// <summary>Derived exclusively from verified journal envelopes. Unavailable outcomes stay null.</summary>
internal static class MatchSummaryBuilder
{
    private static readonly SemaphoreSlim SaveGate=new(1,1);
    internal static bool Retains(string kind) => kind is "session_started" or "session_stopped" or "mode_selected" or
        "task_started" or "task_resumed" or "task_match_completed" or "match_result" or "play_paused" or "play_stopped" or
        "table_automation_action" or "review_configuration" or "review_match_started" or "review_table_context" or
        "action_submission" or "review_action_observation" or "review_window_cancelled" or "review_decision";

    internal static ImmutableArray<MatchSummary> Build(JournalReadResult read, string path)
    {
        if (read.Lines.IsEmpty) return [];
        var entries = read.Lines.Select(x => x.Entry).ToArray();
        var completions = entries.Where(e => e.Kind == "task_match_completed" && Guid.TryParse(Text(e.Data,"MatchId"), out _))
            .DistinctBy(e => Text(e.Data,"MatchId")).ToArray();
        if (completions.Length == 0)
        {
            var completed = entries.FirstOrDefault(e => e.Kind == "match_result" && Text(e.Data,"Source") == "IDutyState.DutyCompleted");
            return [One(entries, read, path, entries[0].SessionId, completed?.Utc)];
        }
        // Older journals can contain multiple matches. Do not attribute all their pauses/actions to every match.
        var rows = ImmutableArray.CreateBuilder<MatchSummary>();
        long start = 0;
        foreach (var completed in completions)
        {
            var window = entries.Where(e => e.Sequence > start && e.Sequence <= completed.Sequence).ToArray();
            rows.Add(One(window, read, path, Guid.Parse(Text(completed.Data,"MatchId")!), completed.Utc));
            start = completed.Sequence;
        }
        return rows.ToImmutable();
    }

    private static MatchSummary One(JournalBody[] entries, JournalReadResult read, string path, Guid id, DateTimeOffset? completed)
    {
        bool trusted = read.IntegrityPassed && !read.IncompleteTail;
        string version="未知", engine="未知", reason="无记录", matchType="unknown";
        uint? duty=null; string? mode=null; bool automaticOnly=true, sawSetup=false;
        int takeovers=0; int? before=null; bool afterCompletion=false;
        var stops=ImmutableArray.CreateBuilder<string>();
        foreach (var e in entries)
        {
            if (Text(e.Data,"PluginVersion") is { } v) version=v;
            if ((Text(e.Data,"Backend") ?? Text(e.Data,"Engine") ?? Text(e.Data,"DecisionSource")) is { } model) engine=model;
            if (Number(e.Data,"DutyId") is { } d && d>=0) duty=(uint)d;
            if (e.Kind=="review_table_context") matchType=Text(e.Data,"MatchType")??"unknown";
            if(e.Kind=="match_result" && Text(e.Data,"Source")=="IDutyState.DutyCompleted" || e.Kind=="task_match_completed")
                afterCompletion=true;
            if(afterCompletion && e.Kind is "mode_selected" or "play_paused" or "play_stopped")continue;
            if (e.Kind=="review_match_started")
            {
                sawSetup=Flag(e.Data,"SawTableSetup"); mode=Text(e.Data,"Mode");
                automaticOnly &= IsAutomatic(mode);
                if (e.Data.TryGetProperty("RatingBefore",out var r) && r.ValueKind==JsonValueKind.Object && Text(r,"Mapping")=="Verified")
                    before=Number(r,"CurrentRating");
            }
            if (e.Kind=="mode_selected")
            {
                string? next=Text(e.Data,"Mode");
                if (IsAutomatic(mode) && string.Equals(next,"manual",StringComparison.OrdinalIgnoreCase)) takeovers++;
                mode=next; automaticOnly &= IsAutomatic(mode);
            }
            if (e.Kind=="play_paused")
            {
                if (IsAutomatic(mode)) takeovers++;
                mode="paused"; automaticOnly=false; reason="用户暂停 / 人工接管";
            }
            if (e.Kind=="play_stopped")
            {
                automaticOnly=false; reason=Text(e.Data,"Reason")??"停止，详见日志"; stops.Add(reason);
            }
            if (e.Kind=="session_stopped" && stops.Count==0) reason=Text(e.Data,"Reason")??reason;
        }
        var used=entries.Where(e=>e.Kind=="review_decision").Select(e=>Text(e.Data,"ConfigurationId")).Where(x=>x is not null).ToHashSet();
        var configs=entries.Where(e=>e.Kind=="review_configuration" && (used.Count==0 || used.Contains(Text(e.Data,"ConfigurationId")))).ToArray();
        var fingerprints=configs.Select(e=>Text(e.Data,"Fingerprint")).Where(s=>s is not null).Distinct().ToArray();
        var config=configs.LastOrDefault();
        if(config is not null)engine=Text(config.Data,"Backend")??engine;
        bool configKnown=fingerprints.Length==1 && configs.All(e=>Text(e.Data,"Error") is null);
        var submissions=entries.Where(e=>e.Kind=="action_submission" && Text(e.Data,"Result")=="Submitted").ToArray();
        var submissionIds=submissions.Select(e=>Text(e.Data,"SubmissionId")).Where(x=>x is not null).ToHashSet();
        var observations=entries.Where(e=>e.Kind=="review_action_observation" && submissionIds.Contains(Text(e.Data,"SubmissionId")));
        return new(id,entries[0].Utc,trusted?completed:null,duty,engine,version,
            !trusted?"未知（记录不完整）":completed is not null?"完成":entries.Any(e=>e.Kind=="session_stopped")?"中断 / 未确认完成":"未知 / 记录中",reason,path)
        {
            IntegrityVerified=trusted, ConfigurationFingerprint=configKnown?fingerprints[0]:null,
            MixedConfiguration=fingerprints.Length>1, MatchType=matchType,
            Bridge=config is null?null:Text(config.Data,"Bridge"), ModelSha256=config is null?null:Text(config.Data,"ModelSha256"),
            SettingsSha256=config is null?null:Text(config.Data,"SettingsSha256"), RatingBefore=trusted?before:null,
            AutomaticWholeMatch=trusted && completed is not null && sawSetup ? automaticOnly : null,
            RecordedTakeovers=trusted && entries.Any(e=>e.Kind=="review_match_started")?takeovers:null,
            Stops=trusted?stops.ToImmutable():[], Submissions=trusted?submissions.Length:0,
            ObservedTransitions=trusted?observations.Where(e=>Flag(e.Data,"StateTransitionObserved")).DistinctBy(e=>Text(e.Data,"SubmissionId")).Count():0,
            ObservationTimeouts=trusted?observations.Where(e=>Flag(e.Data,"TimedOut")).DistinctBy(e=>Text(e.Data,"SubmissionId")).Count():0,
            CancelledWindows=trusted?entries.Count(e=>e.Kind=="review_window_cancelled"):0,
            // No verified final placement, per-hand outcome or match-rating association reader exists yet.
        };
    }

    internal static async Task<ImmutableArray<MatchSummary>> LoadAsync(string directory, CancellationToken cancellation=default)
    {
        var read=await GameJournal.ReadAsync(Path.Combine(directory,"events.jsonl"),cancellation,summaryOnly:true);
        var rows=Build(read,directory);
        // Unchained supplement is display-only; never turn it into a trusted rating delta.
        string after=Path.Combine(directory,"rating-after.json"); GameJournal.RejectLinks(after);
        if (!File.Exists(after) || new FileInfo(after).Length>16384) return rows;
        try
        {
            using var doc=JsonDocument.Parse(await File.ReadAllBytesAsync(after,cancellation)); var r=doc.RootElement;
            if (Number(r,"Schema")!=1 || !Guid.TryParse(Text(r,"MatchId"),out var match)) return rows;
            return rows.Select(row=>row.IntegrityVerified && row.CompletedUtc is not null && row.Id==match
                ? row with {RatingAfter=Number(r,"RatingAfter")} : row).ToImmutableArray();
        }
        catch(JsonException) { return rows; }
    }
    internal static async Task SaveAsync(string directory)
    {
        await SaveGate.WaitAsync();
        try
        {
            var rows=await LoadAsync(directory);
            string file=Path.Combine(directory,"match-summary.json"), temp=file+".tmp";
            GameJournal.RejectLinks(file); GameJournal.RejectLinks(temp);
            // Convenience file, not an authority: the dashboard verifies the journal again.
            await File.WriteAllBytesAsync(temp,JsonSerializer.SerializeToUtf8Bytes(new {Schema=1,
                Matches=rows.Select(r=>r with {JournalPath="."}), Source="verified journal; unknown results stay null"}));
            GameJournal.ReplaceAtomically(temp,file);
        }
        finally{SaveGate.Release();}
    }
    internal static string? Text(JsonElement data,string key)=>data.ValueKind==JsonValueKind.Object && data.TryGetProperty(key,out var p) && p.ValueKind==JsonValueKind.String?
        p.GetString() is { } s?s[..Math.Min(s.Length,500)]:null:null;
    internal static int? Number(JsonElement data,string key)=>data.ValueKind==JsonValueKind.Object && data.TryGetProperty(key,out var p) && p.ValueKind==JsonValueKind.Number && p.TryGetInt32(out int n)?n:null;
    internal static bool Flag(JsonElement data,string key)=>data.ValueKind==JsonValueKind.Object && data.TryGetProperty(key,out var p) && p.ValueKind==JsonValueKind.True;
    private static bool IsAutomatic(string? mode)=>string.Equals(mode,"automatic",StringComparison.OrdinalIgnoreCase);
}
