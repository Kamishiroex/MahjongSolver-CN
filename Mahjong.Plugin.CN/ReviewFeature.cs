using System.Security.Cryptography;
using System.Text.Json;
using Mahjong.Core;
using Mahjong.Engine;
using Mahjong.Policy.Abstractions;
using Mahjong.Plugin.CN.Journaling;
using Mahjong.Plugin.CN.Readers;
using Mahjong.Cn.Rating;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private Guid reviewConfiguration;
    private sealed record ReviewSettings(int HumanizedDelayMs,bool AutoAdvanceAfterHand,bool KeepAutomaticBetweenHands);
    private sealed record ReviewIdentity(Guid ConfigurationId,string Fingerprint,string Backend,string PluginVersion,
        string PluginBuild,string? Bridge,string? ModelSha256,string? ManifestSha256,string SettingsSha256,
        ReviewSettings Settings,string? Error)
    { public string IdentitySource=>"installed manifest; actual engine identity checked separately before decisions"; }
    private ReviewIdentity? reviewConfigurationMetadata;
    private ReviewSettings CurrentReviewSettings()=>new(PlayRuntime?.Configuration.HumanizedDelayMs??1200,
        AutomationOptions.AutoAdvanceAfterHand,AutomationOptions.KeepAutomaticBetweenHands);
    private static string ReviewHash(object value)=>Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static ReviewIdentity WithReviewSettings(ReviewIdentity identity,ReviewSettings settings)
    {
        string hash=ReviewHash(settings);
        return identity with {Settings=settings,SettingsSha256=hash,Fingerprint=ReviewHash(new {identity.Backend,
            identity.PluginBuild,identity.Bridge,identity.ModelSha256,identity.ManifestSha256,SettingsSha256=hash})};
    }
    private Guid? reviewDecisionId;
    private string? reviewDecisionHash;
    private bool reviewTableStarted;
    private uint? reviewDuty;
    private (GameJournal Journal, Guid Match, string Context, DateTimeOffset End)? reviewRatingPending;

    private IPolicy CreateReviewedPolicy(IPolicy policy)
    {
        string backend=ExperimentalHandAiEnabled ? MortalSelected?"Mortal":"Akochan":"upstream-efficiency";
        var configuration=Guid.NewGuid(); reviewConfiguration=configuration;
        var target=journal;
        string? directory=ExperimentalHandAiEnabled?DefaultGlobalEngineDirectory:null;
        var settings=CurrentReviewSettings();
        string settingsHash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings)));
        // Same display version can be rebuilt. Include policy/rules/input assemblies so
        // changing a dependency alone cannot silently merge two evaluation groups.
        string build=ReviewHash(new[] {typeof(Plugin),typeof(Mahjong.Policy.Efficiency.EfficiencyPolicy),
            typeof(ScoredDiscard),typeof(StateSnapshot),typeof(Mahjong.Rules.Rulesets.DomanRuleSet),
            typeof(Mahjong.Cn.Engines.MortalInstallation)}.Select(t=>new {Assembly=t.Assembly.GetName().Name,
                Mvid=t.Assembly.ManifestModule.ModuleVersionId}).ToArray());
        _=Task.Run(() =>
        {
            string? bridge=null,model=null,manifestHash=null,error=null;
            try
            {
                if(directory is not null)
                {
                    string file=Path.Combine(directory,backend=="Mortal"?"mortal-installation.json":"akochan-installation.json");
                    if(new FileInfo(file).Length>8*1024*1024)throw new InvalidDataException();
                    byte[] bytes=File.ReadAllBytes(file); manifestHash=Convert.ToHexString(SHA256.HashData(bytes));
                    using var doc=JsonDocument.Parse(bytes);var m=doc.RootElement;
                    if(m.TryGetProperty("bridge",out var b)&&b.ValueKind==JsonValueKind.String)bridge=b.GetString();
                    if(m.TryGetProperty("model_sha256",out var h)&&h.ValueKind==JsonValueKind.String)model=h.GetString();
                    if(m.TryGetProperty("globalSnapshot",out var g)&&g.TryGetProperty("id",out var id))bridge=id.GetString();
                }
            }
            catch(Exception ex){error=ex.GetType().Name;}
            var metadata=WithReviewSettings(new ReviewIdentity(configuration,"",backend,Diagnostics.LocalCaptureRecorder.PluginVersion,
                build,bridge,model,manifestHash,settingsHash,settings,error),settings);
            lock(gate)
            {
                target?.Event("review_configuration",metadata);
                if(reviewConfiguration==configuration)
                {
                    reviewConfigurationMetadata=metadata;
                    if(journalActive && !ReferenceEquals(journal,target)) RecordJournalEvent("review_configuration",metadata);
                }
            }
        });
        return DecisionReviewPolicy.Wrap(policy,(hash,state,choice,candidates)=>
        {
            if(!journalActive)return;
            var currentSettings=CurrentReviewSettings();
            if(reviewConfigurationMetadata is { } metadata && metadata.ConfigurationId==configuration && metadata.Settings!=currentSettings)
            {
                reviewConfigurationMetadata=WithReviewSettings(metadata,currentSettings);
                RecordJournalEvent("review_configuration",reviewConfigurationMetadata);
            }
            var trace=LastGlobalAiTrace;
            bool correlated=ExperimentalHandAiEnabled && trace is not null && ReferenceEquals(trace.Choice,choice);
            var id=Guid.NewGuid(); reviewDecisionId=id; reviewDecisionHash=hash;
            RecordJournalEvent("review_decision",new {DecisionId=id,ConfigurationId=configuration,Backend=backend,
                InputSha256=hash,EngineInputSha256=correlated?trace!.InputSha256:null,
                Choice=choice,RawDiscardCandidates=candidates,
                CandidateSource=correlated?"global_ai_decision/error":candidates is not null?"EfficiencyPolicy.discard.Score":"no-ranked-candidates-exposed",
                Mode=PlayRuntime?.Mode.ToString(),ObservationSessionId=PlayRuntime?.ObservationSessionId,
                ObservationSequence=PlayRuntime?.ObservationSequence,
                BackendOverride=correlated?BackendOverride(trace!.Decision?.AppliedSnapshotJson):null,
                Snapshot=new {Hand=state.Hand.Select(t=>t.Id).ToArray(),state.WallRemaining,state.AddonStateCode,
                    LegalActions=(int)state.Legal.Flags}, ActionConfirmed=false});
        });
    }
    private static string? BackendOverride(string? json)
    {
        try { using var doc=JsonDocument.Parse(json??"{}");return doc.RootElement.TryGetProperty("selection_origin",out var p)?p.GetString():null; }
        catch(JsonException){return null;}
    }
    private Guid? ReviewDecisionFor(StateSnapshot state) => reviewDecisionHash==DecisionReviewPolicy.InputHash(state)?reviewDecisionId:null;
    private void RecordReviewEvent(string kind,object value) => RecordJournalEvent(kind,value);

    private void BeginReviewTable(bool sawSetup)
    {
        if(!journalActive||reviewTableStarted)return;
        reviewTableStarted=true;
        RecordJournalEvent("review_match_started",new {SawTableSetup=sawSetup,Mode=PlayRuntime?.Mode.ToString(),
            RatingBefore=RatingEvidence(CurrentRating),OpponentEnvironment="unknown"});
    }
    private object? RatingEvidence(RatingObservation? value) => value is not null &&
        value.Trusted(CurrentCharacterContext(),MahjongRatingReader.Profile,DateTimeOffset.UtcNow)
        ? new {value.CurrentRating,value.ReadAtUtc,value.ProfileVersion,value.EvidenceRevision,Mapping="Verified"}:null;

    private void RecordReviewRating(RatingObservation value)
    {
        if(reviewRatingPending is not { } pending || value.ReadAtUtc<=pending.End || value.ReadAtUtc>pending.End.AddMinutes(5) ||
            value.LocalCharacterContext!=pending.Context || !value.Trusted(pending.Context,MahjongRatingReader.Profile,DateTimeOffset.UtcNow))return;
        reviewRatingPending=null;
        // Rating refresh normally finishes after the match journal has closed. This is
        // a bounded supplement in that same record, never a new unrelated game session.
        _=Task.Run(async()=>
        {
            await pending.Journal.Completion.ConfigureAwait(false);
            try
            {
                string path=Path.Combine(pending.Journal.DirectoryPath,"rating-after.json");
                GameJournal.RejectLinks(path);
                byte[] data=JsonSerializer.SerializeToUtf8Bytes(new {Schema=1,MatchId=pending.Match,
                    RatingAfter=value.CurrentRating,value.ReadAtUtc,value.ProfileVersion,value.EvidenceRevision,
                    AssociationVerified=false,Reason="资料页字段已验证；整场结算刷新关联尚未实机验证。"});
                string temp=path+".tmp";GameJournal.RejectLinks(temp);
                await File.WriteAllBytesAsync(temp,data);GameJournal.ReplaceAtomically(temp,path);
                await MatchSummaryBuilder.SaveAsync(pending.Journal.DirectoryPath);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { /* Optional summary never stops play. */ }
        });
    }
}
