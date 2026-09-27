using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Core;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN.Gameplay.Tests;
public sealed class DecisionCorpusTests
{
    internal static DecisionCorpusFile Fixture()
    {
        // Constructed deterministic rule fixtures, not claimed as a captured game.
        var hands=new[]{"123456m234p678s55z","234567m345p456s11z","1123456m234p789s1z"};
        return new(1,"constructed offline regression positions",new string('0',64),hands.Select(h=>
        {
            var state=StateSnapshot.Empty with {Hand=Tiles.Parse(h),Legal=LegalActions.None with {Flags=ActionFlags.Discard,DiscardableTiles=Tiles.Parse(h)},SeatInfoKnown=true,AddonStateCode=30};
            return new CorpusCase(DecisionCorpus.Key(state,null),state,null,new(Mahjong.Policy.Abstractions.ActionKind.Pass),
                "upstream-efficiency","fixture",null,null,null,null);
        }).ToArray(),0);
    }
    [Fact]public async Task Fixed_public_inputs_roundtrip_and_run_without_any_game_host_model_or_operation_services()
    {
        using var folder=new Mahjong.Plugin.Dalamud.Tests.Stubs.TempDir();
        var corpus=Fixture();string path=Path.Combine(folder.Path,"corpus.json");
        await DecisionCorpus.WriteNewAsync(path,corpus);
        var read=await DecisionCorpus.ReadAsync<DecisionCorpusFile>(path);
        Assert.Equal(corpus.Cases.Select(c=>c.Key),read.Cases.Select(c=>DecisionCorpus.Key(c.Snapshot,c.PublicInput)));
        var run=await DecisionCorpus.ReplayAsync(read,"upstream");var repeat=await DecisionCorpus.ReplayAsync(read,"upstream");
        Assert.All(run.Cases,r=>Assert.Null(r.Error));Assert.False(run.GameOperationsExecuted);
        var result=JsonSerializer.SerializeToElement(DecisionCorpus.Compare(run,repeat));
        Assert.Equal(3,result.GetProperty("Compared").GetInt32());
        Assert.All(result.GetProperty("Cases").EnumerateArray(),c=>Assert.False(c.GetProperty("ActionChanged").GetBoolean()));
        Assert.Throws<InvalidDataException>(()=>DecisionCorpus.Compare(run,repeat with {CorpusSha256="changed"}));
        var changed=read with {Cases=[read.Cases[0] with {Snapshot=read.Cases[0].Snapshot with {WallRemaining=19}}]};
        Assert.NotNull((await DecisionCorpus.ReplayAsync(changed,"upstream")).Cases[0].Error);
    }
    [Fact]public void Export_requires_intact_chain_and_exact_input_and_never_invents_a_legacy_snapshot()
    {
        var state=Fixture().Cases[0].Snapshot;var session=Guid.NewGuid();
        JournalLine Line(object value)=>new(new(2,session,1,DateTimeOffset.UnixEpoch,"review_decision",JsonSerializer.SerializeToElement(value),""),"tail");
        var read=new JournalReadResult([Line(new {DecisionId=Guid.NewGuid(),ReplaySnapshot=state,InputSha256=DecisionCorpus.Key(state,null),
            Backend="upstream-efficiency",Choice=new Mahjong.Policy.Abstractions.ActionChoice(Mahjong.Policy.Abstractions.ActionKind.Discard,Tile.FromId(27)),ChooseMilliseconds=5})],false,null);
        Assert.Single(DecisionCorpus.Export(read).Cases);
        Assert.Throws<InvalidDataException>(()=>DecisionCorpus.Export(read with {IncompleteTail=true}));
        var legacy=read with {Lines=[Line(new {InputSha256="legacy",Choice="discard"})]};
        Assert.Empty(DecisionCorpus.Export(legacy).Cases);Assert.Equal(1,DecisionCorpus.Export(legacy).Skipped);
    }
}
