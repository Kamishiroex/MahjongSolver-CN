using System.Text.Json;
using Mahjong.Plugin.CN.Journaling;
using Mahjong.Plugin.CN.Readers;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class ResultPersistenceTests
{
    [Fact]public async Task Staged_result_survives_reload_and_is_bound_only_after_the_original_journal_closes()
    {
        using var folder=new Mahjong.Plugin.Dalamud.Tests.Stubs.TempDir();
        var journal=new GameJournal(folder.Path);var now=DateTimeOffset.UtcNow;
        var anchor=new MatchRatingAnchor(journal.SessionId,Guid.NewGuid(),now.AddMinutes(-30),
            new(1800,42,now.AddMinutes(-30).AddSeconds(-1),MahjongRatingReader.Profile,1));
        journal.AppendRecorded("review_rating_anchor",JsonSerializer.SerializeToElement(anchor),now.AddMinutes(-30));
        journal.AppendRecorded("match_result",JsonSerializer.SerializeToElement(new {MatchId=journal.SessionId,Source="IDutyState.DutyCompleted"}),now);
        await journal.Completion;
        var evidence=new PendingOutcome(1,"rating",journal.SessionId,now.AddSeconds(1),
            Rating:new(1810,43,now.AddSeconds(1),MahjongRatingReader.Profile,2),ContextToken:anchor.ContextToken);
        var staged=await OutcomeEvidenceStore.ProcessAsync(new(journal.DirectoryPath,evidence));
        Assert.Equal("RESULT_WAITING_FOR_JOURNAL",staged.Code);Assert.True(staged.Job.Staged);
        var discovered=await OutcomeEvidenceStore.DiscoverAsync(folder.Path);Assert.Equal(0,discovered.Rejected);
        var recovered=Assert.Single(discovered.Jobs);
        journal.AppendRecorded("session_stopped",JsonSerializer.SerializeToElement(new {Reason="table_exit"}),now.AddSeconds(2));
        await journal.CompleteAsync();
        var wrong=await OutcomeEvidenceStore.ProcessAsync(recovered with {Evidence=evidence with {MatchId=Guid.NewGuid()},Staged=true});
        Assert.Equal("RESULT_JOURNAL_MISMATCH",wrong.Code);
        var saved=await OutcomeEvidenceStore.ProcessAsync(recovered);Assert.True(saved.Completed,saved.Code);
        Assert.Equal(10,Assert.Single(await MatchSummaryBuilder.LoadAsync(journal.DirectoryPath)).RatingDelta);
        Assert.Empty((await OutcomeEvidenceStore.DiscoverAsync(folder.Path)).Jobs);
    }
    [Fact]public async Task Locked_destination_can_be_retried_without_losing_the_staged_observation()
    {
        using var folder=new Mahjong.Plugin.Dalamud.Tests.Stubs.TempDir();
        var j=new GameJournal(folder.Path);var now=DateTimeOffset.UtcNow;
        j.AppendRecorded("match_result",JsonSerializer.SerializeToElement(new {MatchId=j.SessionId,Source="IDutyState.DutyCompleted"}),now);
        j.Event("session_stopped",new {});await j.CompleteAsync();
        var sample=new ResultUiSample("EmjTotalResult",ResultUiReader.Profile,true,Enumerable.Range(20,4).SelectMany(r=>new[]{
            new ResultUiValue($"EmjTotalResult/{r}/10",r-19,null,false),new ResultUiValue($"EmjTotalResult/{r}/15",null,null,r==20)}).ToArray(),null);
        string file=Path.Combine(j.DirectoryPath,"final-result.json");
        OutcomeWriteResult blocked;
        using(var locked=new FileStream(file,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            blocked=await OutcomeEvidenceStore.ProcessAsync(new(j.DirectoryPath,new(1,"final",j.SessionId,now,Final:sample)));
        Assert.False(blocked.Completed);Assert.True(blocked.Job.Staged);
        Assert.Contains("RESULT_SAVE_",blocked.Code);
        var result=await OutcomeEvidenceStore.ProcessAsync(blocked.Job);Assert.True(result.Completed,result.Code);
        Assert.Equal(1,Assert.Single(await MatchSummaryBuilder.LoadAsync(j.DirectoryPath)).Placement);
        Assert.False(File.Exists(Path.Combine(j.DirectoryPath,"outcome-pending-final.json")));
    }
    [Fact]public async Task Invalid_pending_file_is_counted_and_left_for_diagnosis()
    {
        using var folder=new Mahjong.Plugin.Dalamud.Tests.Stubs.TempDir();
        var j=new GameJournal(folder.Path);j.Event("session_stopped",new {});await j.CompleteAsync();
        string file=Path.Combine(j.DirectoryPath,"outcome-pending-final.json");await File.WriteAllTextAsync(file,"{}");
        var found=await OutcomeEvidenceStore.DiscoverAsync(folder.Path);Assert.Empty(found.Jobs);Assert.Equal(1,found.Rejected);Assert.True(File.Exists(file));
    }
}
