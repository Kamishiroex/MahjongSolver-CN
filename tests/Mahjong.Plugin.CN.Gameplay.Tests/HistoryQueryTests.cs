using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN.Gameplay.Tests;
public sealed class HistoryQueryTests
{
    private static readonly DateTimeOffset Now=DateTimeOffset.UnixEpoch.AddDays(100);
    private static MatchSummary Row(int days=0)=>new(Guid.NewGuid(),Now.AddDays(-days),Now,766,"test","fixture","complete","","fixture")
    {IntegrityVerified=true,Placement=1,RatingDelta=0,ObservedHands=4,HandsWithVerifiedOutcome=4};
    [Fact]public void Filters_apply_to_the_same_rows_as_coverage_and_keep_unknowns_out_of_valid_denominators()
    {
        var rows=new[]{Row(8),Row(7),Row() with {DutyId=643},Row() with {Engine="other"},Row() with {RatingDelta=null},
            Row() with {IntegrityVerified=false},Row() with {HandsWithVerifiedOutcome=2},Row() with {CompletedUtc=null}};
        var filtered=HistoryQuery.Filter(rows,Now,7,766,"test");Assert.Equal(5,filtered.Length);
        var gaps=HistoryQuery.Filter(rows,Now,7,766,"test",true);Assert.Equal(4,gaps.Length);
        var c=HistoryQuery.Coverage(filtered);Assert.Equal(5,c.Records);Assert.Equal(1,c.Untrusted);
        Assert.Equal(3,c.Completed);Assert.Equal(3,c.Rated);Assert.Equal(14,c.VerifiedHands);Assert.Equal(20,c.Hands);
        Assert.False(HistoryQuery.HasGap(Row()));Assert.True(HistoryQuery.HasGap(Row() with {ObservedHands=0}));
    }
}
