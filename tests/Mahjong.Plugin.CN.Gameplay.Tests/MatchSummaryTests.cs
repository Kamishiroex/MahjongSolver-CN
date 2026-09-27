using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Plugin.CN.Journaling;
using Xunit;
namespace Mahjong.Plugin.CN.Gameplay.Tests;
public sealed class MatchSummaryTests
{
    private static readonly Guid Session=Guid.NewGuid();
    private static JournalLine Line(int n,string kind,object data)=>new(new(2,Session,n,DateTimeOffset.Parse("2026-09-27T00:00:00Z"),kind,JsonSerializer.SerializeToElement(data),""),"");
    [Fact]public void Duplicate_result_notifications_have_one_summary_without_invented_placement()
    {
        var id=Guid.NewGuid();var lines=ImmutableArray.Create(Line(1,"session_started",new{PluginVersion="fixture"}),Line(2,"task_match_completed",new{MatchId=id,DutyId=766,Engine="fixture-engine"}),Line(3,"task_match_completed",new{MatchId=id,DutyId=766}));
        var result=MatchSummaryBuilder.Build(new(lines,false,null),"fixture");Assert.Single(result);Assert.Equal("完成",result[0].Outcome);Assert.Equal("fixture-engine",result[0].Engine);
        Assert.Equal("未知（记录不完整）",MatchSummaryBuilder.Build(new(lines,true,null),"fixture")[0].Outcome);
    }
    [Fact]public void Hidden_table_or_arbitrary_completion_text_cannot_become_verified_whole_match()
    {var lines=ImmutableArray.Create(Line(1,"session_started",new{}),Line(2,"match_result",new{Source="untrusted"}),Line(3,"session_stopped",new{Reason="scene exit"}));Assert.Equal("中断 / 未确认完成",MatchSummaryBuilder.Build(new(lines,false,null),"fixture")[0].Outcome);}
}
