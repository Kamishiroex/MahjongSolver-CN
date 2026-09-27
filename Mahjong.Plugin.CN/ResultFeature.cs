using System.Security.Cryptography;
using System.Text.Json;
using Lumina.Data.Files;
using Mahjong.Plugin.CN.Journaling;
using Mahjong.Plugin.CN.Readers;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private ResultUiReader? resultReader;
    private bool resultRankResourceChecked;
    private readonly HandResultTracker handResults=new();
    private string? resultRound,resultCandidateHash,finalCandidateHash;
    private DateTimeOffset finalCandidateSince;
    private double lastResultPoll;
    private Guid resultMatchId;
    private (GameJournal Journal,Guid Match,string Context,DateTimeOffset End)? finalResultPending;
    private int resultSamplesInRound;
    private string? resultReadFault;
    private string? pendingHandHash;

    private void ResetResults()
    {
        handResults.Reset();resultRound=resultCandidateHash=finalCandidateHash=null;
        resultMatchId=Guid.Empty;resultSamplesInRound=0;
        pendingHandHash=null;resultReader?.ClearSeatNames();
    }

    private void UpdateResultsCore()
    {
        if(disposed || Identity.Error is not null || !Client.IsLoggedIn ||
            !journalActive && finalResultPending is null || AutomationNow-lastResultPoll<0.2)return;
        lastResultPoll=AutomationNow;
        try
        {
            resultReader??=new(name=>GameGui.GetAddonByName(name).Address,
                (a,n)=>global::Dalamud.SafeMemory.ReadBytes(a,n,out var bytes)?bytes:null);
            DateTimeOffset now=DateTimeOffset.UtcNow;
            if(journalActive && reviewTableStarted && LowerHandUldHash==Diagnostics.LowerHandProfile.EmjUldSha256)
            {
                if(resultMatchId==Guid.Empty)resultMatchId=journal!.SessionId;
                bool resultSurface=PlayRuntime?.RecoverySnapshot?.AddonStateCode==29;
                var round=journalPublicMonitor?.Current?.RoundId;
                string? id=round is {IsConfirmed:true}?round.Value:resultSurface || handResults.Pending?resultRound:null;
                if(id is not null && id!=resultRound)
                {
                    resultRound=id;resultSamplesInRound=0;resultCandidateHash=null;
                    RecordJournalEvent("review_hand_started",new {MatchId=resultMatchId,RoundId=id});
                }
                var sample=resultReader.Observe("Emj",true,PlayerState.CharacterName);
                // Record only result transitions, not another stream of table scores.
                if(resultSurface || sample.Values.Any(v=>ResultResourceCatalog.Banner(v) is not null))
                {
                    string hash=JsonSerializer.Serialize(sample);
                    if(hash!=resultCandidateHash && resultSamplesInRound<64)
                    {
                        resultSamplesInRound++;
                        resultCandidateHash=hash;
                        RecordJournalEvent("review_result_observation",new {MatchId=resultMatchId,RoundId=id,ResultSurface=resultSurface,Sample=sample});
                    }
                }
                if(handResults.Observe(id,resultSurface,sample,now) is { } hand)
                    RecordJournalEvent("review_hand_result",new {MatchId=resultMatchId,Result=hand});
                if(handResults.Evidence is {} evidence)
                {
                    string hash=JsonSerializer.Serialize(evidence);
                    if(hash!=pendingHandHash)
                    {pendingHandHash=hash;RecordJournalEvent("review_hand_pending",new {MatchId=resultMatchId,Evidence=evidence});}
                }
            }
            if(finalResultPending is not { } pending)return;
            if(now>pending.End.AddMinutes(5) || pending.Context!=CurrentCharacterContext())
            {finalResultPending=null;finalCandidateHash=null;return;}
            if(!resultRankResourceChecked)
            {
                var file=DataManager.GetFile<UldFile>("ui/uld/EmjRankResult.uld");
                if(file is null || Convert.ToHexString(SHA256.HashData(file.Data))!=ResultUiReader.RankUldSha256)return;
                resultRankResourceChecked=true;
            }
            var final=resultReader.Observe("EmjTotalResult",true,PlayerState.CharacterName);
            string candidate=JsonSerializer.Serialize(final);
            if(finalCandidateHash!=candidate) { finalCandidateHash=candidate;finalCandidateSince=now;return; }
            if(now-finalCandidateSince<TimeSpan.FromMilliseconds(600) || FinalResultEvidence.Read(final).Placement is null)return;
            finalResultPending=null;
            QueueResultEvidence(pending.Journal,new(1,"final",pending.Match,now,Final:final));
        }
        catch(Exception ex)
        {
            string code=ex.GetType().Name;
            if(resultReadFault!=code)Log.Warning("Result observation failed: {Type}",code);
            resultReadFault=code;
        }
    }

}
