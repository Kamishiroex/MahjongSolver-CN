using System.Collections.Immutable;
using Mahjong.Core;
using Xunit;

namespace Mahjong.Cn.Tests;

/// <summary>Independent synthetic Mahjong shapes, not CN captures or evidence of live action legality.</summary>
public sealed class HandShapeAdvisorTests
{
    private const HandShapePurpose Purpose = HandShapePurpose.ClosedHandEfficiencyOnly;

    [Theory]
    [InlineData("123456m789p123s55z")]
    [InlineData("1122m3344p5566s77z")]
    [InlineData("119m19p19s1234567z")]
    public void Four_sets_and_pair_seven_pairs_and_thirteen_orphans_stop_discard_advice(string notation)
    {
        var result = HandShapeAdvisor.Analyze(FromNotation(notation), Purpose);
        Assert.Equal(HandShapeStatus.CompleteShape, result.Status);
        Assert.Equal(-1, result.CurrentShanten);
        Assert.Empty(result.Options);
        Assert.Null(result.Recommended);
        Assert.Contains("是否有役", result.Message);
        Assert.Contains("另行判断", result.Message);
    }

    [Fact]
    public void Discarding_isolated_nine_leaves_known_two_sided_wait_with_own_copy_removed()
    {
        // After 9m: 123m + 123p + 123s + 45s + white pair. Draw 3s or 6s completes four sets/pair.
        var result = HandShapeAdvisor.Analyze(FromNotation("123m123p12345s55z9m"), Purpose);
        Assert.Equal(HandShapeStatus.Suggestions, result.Status);
        var best = Assert.IsType<HandShapeDiscardOption>(result.Recommended);
        Assert.Equal(8, best.Tile.Id);
        Assert.Equal(14, best.DisplayPosition);
        Assert.Equal(0, best.ShantenAfter);
        Assert.Contains(best.ImprovingTiles, t => t.Tile.Id == 20 && t.MaximumCopiesOutsideHand == 3);
        Assert.Contains(best.ImprovingTiles, t => t.Tile.Id == 23 && t.MaximumCopiesOutsideHand == 4);
        Assert.Equal(7, best.TheoreticalImprovingCopies);
        Assert.Contains("非牌山余量", best.Reason);
        Assert.Contains(result.Limitations, t => t.Contains("未评估役种"));
    }

    [Fact]
    public void Known_discarded_copy_is_not_returned_to_theoretical_available_copies()
    {
        // Dropping one 1m from the triplet worsens this shape; drawing 1m back improves it,
        // but three original known 1m copies (including the discard) leave at most ONE outside.
        var result = HandShapeAdvisor.Analyze(FromNotation("111m123p45689s77z2z"), Purpose);
        var dropOneMan = Assert.Single(result.Options, t => t.Tile.Id == 0);
        Assert.Contains(dropOneMan.ImprovingTiles, t => t.Tile.Id == 0 && t.MaximumCopiesOutsideHand == 1);
        Assert.All(result.Options, option => Assert.All(option.ImprovingTiles,
            t => Assert.InRange(t.MaximumCopiesOutsideHand, 1, 4)));
    }

    [Fact]
    public void Ordinary_five_is_selected_before_red_five_without_changing_display_positions()
    {
        var input = FromNotation("55m123p456s123456z");
        input = input.SetItem(0, input[0] with { Tile = new VisibleTile(4, true) });
        var shuffled = input.Reverse().ToImmutableArray();
        var result = HandShapeAdvisor.Analyze(shuffled, Purpose);
        var option = Assert.Single(result.Options, t => t.Tile.Id == 4);
        Assert.False(option.Tile.Red);
        Assert.Equal(2, option.DisplayPosition);
        Assert.Contains("保留赤五", option.Reason);
        Assert.True(input[0].Tile.Red);
        Assert.Equal(14, shuffled[0].DisplayPosition);
    }

    [Fact]
    public void A_red_five_without_ordinary_copy_is_still_an_honest_physical_candidate()
    {
        var input = FromNotation("5m123p456s1234567z");
        input = input.SetItem(0, input[0] with { Tile = new VisibleTile(4, true) });
        var result = HandShapeAdvisor.Analyze(input, Purpose);
        var option = Assert.Single(result.Options, t => t.Tile.Id == 4);
        Assert.True(option.Tile.Red);
        Assert.Equal(1, option.DisplayPosition);
    }

    [Fact]
    public void Four_copy_limit_combines_red_and_ordinary_fives()
    {
        var input = FromNotation("55555m123p456s123z");
        input = input.SetItem(0, input[0] with { Tile = new VisibleTile(4, true) });
        AssertInvalid(HandShapeAdvisor.Analyze(input, Purpose), "SHAPE_TILE_COPIES");
        var four = FromNotation("5555m123p456s1234z");
        four = four.SetItem(0, four[0] with { Tile = new VisibleTile(4, true) });
        Assert.Equal(HandShapeStatus.Suggestions, HandShapeAdvisor.Analyze(four, Purpose).Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    [InlineData(15)]
    public void Partial_or_extra_tiles_are_not_silently_filled_or_removed(int count)
    {
        var input = Enumerable.Range(0, count).Select(i => new HandShapeTile(i + 1, new VisibleTile(i % 34))).ToImmutableArray();
        AssertInvalid(HandShapeAdvisor.Analyze(input, Purpose), "SHAPE_TILE_COUNT");
    }

    [Fact]
    public void Default_array_unknown_ids_invalid_red_and_duplicate_positions_are_rejected()
    {
        var valid = FromNotation("123m123p12345s55z9m");
        AssertInvalid(HandShapeAdvisor.Analyze(default, Purpose), "SHAPE_TILE_COUNT");
        AssertInvalid(HandShapeAdvisor.Analyze(valid.SetItem(0, new(1, new(-1))), Purpose), "SHAPE_UNKNOWN_TILE");
        AssertInvalid(HandShapeAdvisor.Analyze(valid.SetItem(0, new(1, new(34))), Purpose), "SHAPE_UNKNOWN_TILE");
        AssertInvalid(HandShapeAdvisor.Analyze(valid.SetItem(0, new(1, new(27, true))), Purpose), "SHAPE_RED_TILE");
        AssertInvalid(HandShapeAdvisor.Analyze(valid.SetItem(0, new(2, new(0))), Purpose), "SHAPE_DISPLAY_POSITION");
        AssertInvalid(HandShapeAdvisor.Analyze(valid.SetItem(0, new(0, new(0))), Purpose), "SHAPE_DISPLAY_POSITION");
        AssertInvalid(HandShapeAdvisor.Analyze(valid.SetItem(0, new(15, new(0))), Purpose), "SHAPE_DISPLAY_POSITION");
    }

    [Theory]
    [InlineData(HandShapePurpose.Unspecified)]
    [InlineData((HandShapePurpose)99)]
    public void Caller_must_explicitly_choose_the_closed_hand_only_purpose(HandShapePurpose purpose)
        => AssertInvalid(HandShapeAdvisor.Analyze(FromNotation("123m123p12345s55z9m"), purpose), "SHAPE_PURPOSE_REQUIRED");

    [Fact]
    public void Results_are_immutable_and_failed_next_input_does_not_reuse_old_advice()
    {
        var input = FromNotation("123m123p12345s55z9m");
        var first = HandShapeAdvisor.Analyze(input, Purpose);
        var again = HandShapeAdvisor.Analyze(input.Reverse().ToImmutableArray(), Purpose);
        Assert.Equal(first.Recommended, again.Recommended is { } b ? b with { ImprovingTiles = first.Recommended!.ImprovingTiles } : null);
        Assert.Equal(first.Options.Select(t => (t.DisplayPosition, t.Tile, t.ShantenAfter, t.TheoreticalImprovingCopies)),
            again.Options.Select(t => (t.DisplayPosition, t.Tile, t.ShantenAfter, t.TheoreticalImprovingCopies)));
        AssertInvalid(HandShapeAdvisor.Analyze(input.RemoveAt(0), Purpose), "SHAPE_TILE_COUNT");
        Assert.NotNull(first.Recommended);
        Assert.Equal(14, input.Length);
        Assert.Contains(first.Limitations, t => t.Contains("不授权"));
    }

    private static ImmutableArray<HandShapeTile> FromNotation(string notation) =>
        Tiles.Parse(notation).Select((t, index) => new HandShapeTile(index + 1, new VisibleTile(t.Id))).ToImmutableArray();

    private static void AssertInvalid(HandShapeAdvice result, string code)
    {
        Assert.Equal(HandShapeStatus.InvalidInput, result.Status);
        Assert.Equal(code, result.Code);
        Assert.Empty(result.Options);
        Assert.Null(result.CurrentShanten);
        Assert.Null(result.Recommended);
    }
}
