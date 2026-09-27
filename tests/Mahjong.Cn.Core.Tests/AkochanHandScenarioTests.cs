using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Cn.Engines;
using Xunit;

namespace Mahjong.Cn.Core.Tests;

public sealed class AkochanHandScenarioTests
{
    private static VisibleTile[] Hand() => [new(0), new(1), new(2), new(3), new(4, true), new(5), new(6),
        new(7), new(8), new(9), new(10), new(11), new(27), new(12)];
    private static AkochanDecision Decision(AkochanHandScenario scenario, params AkochanMove[] moves) =>
        new(moves.ToImmutableArray(), scenario.ScenarioReplay.InputSha256, scenario.ScenarioReplay.SourceLabel,
            true, AkochanInstallation.ExpectedCommit, "test-manifest", 1, null, null);
    private static AkochanMove Discard(VisibleTile tile) => new("dahai", 0, null, tile, [], false);

    [Fact]
    public void Scenario_is_explicitly_synthetic_preserves_redness_and_never_claims_live_history()
    {
        var source = Hand();
        var scenario = AkochanHandScenario.Create(source);
        Assert.Equal(source, scenario.ActualHand);
        source[0] = new(33);
        Assert.Equal(0, scenario.ActualHand[0].Id);
        Assert.True(scenario.IsSynthetic);
        Assert.False(scenario.IsLiveGameDecision);
        Assert.True(scenario.ScenarioReplay.IsSynthetic);
        Assert.Equal("experimental-hand-only", scenario.SourceLabel);
        Assert.Equal("offline:experimental-hand-only", scenario.ScenarioReplay.SourceLabel);
        Assert.Equal(3, scenario.ScenarioReplay.Events.Length);
        Assert.Contains(scenario.Assumptions, text => text.Contains("绝非真实完整牌谱", StringComparison.Ordinal));
        Assert.Contains(scenario.Assumptions, text => text.Contains("末张", StringComparison.Ordinal));
        using var round = JsonDocument.Parse(scenario.ScenarioReplay.Events[1]);
        var hands = round.RootElement.GetProperty("tehais");
        Assert.Equal("5mr", hands[0][4].GetString());
        for (int player = 1; player < 4; player++)
            Assert.All(hands[player].EnumerateArray(), x => Assert.Equal("?", x.GetString()));
    }

    [Fact]
    public void Assumed_indicator_and_its_dora_are_absent_from_input_and_deterministic()
    {
        var hand = Hand();
        var first = AkochanHandScenario.Create(hand);
        Assert.DoesNotContain(first.DoraIndicator.Id, hand.Select(t => t.Id));
        Assert.DoesNotContain(hand, t => MjaiTileCodec.DoraIndicatorForActualDora(t).Id == first.DoraIndicator.Id);
        Assert.Equal(first.ScenarioReplay.InputSha256, AkochanHandScenario.Create(hand).ScenarioReplay.InputSha256);
        Assert.False(first.UsesProvidedDoraIndicator);
        Assert.Contains(first.Assumptions, text => text.Contains("仍会影响未来摸牌估值", StringComparison.Ordinal));
    }

    [Fact]
    public void Provided_indicator_is_preserved_but_a_fifth_copy_is_rejected()
    {
        var hand = Hand();
        var supplied = new VisibleTile(33);
        var scenario = AkochanHandScenario.Create(hand, supplied);
        Assert.True(scenario.UsesProvidedDoraIndicator);
        Assert.Equal(supplied, scenario.DoraIndicator);
        hand[0] = hand[1] = hand[2] = hand[3] = new(0);
        Assert.Equal("AKOCHAN_HAND_SCENARIO_TILE_COUNT_CONFLICT",
            Assert.Throws<AkochanException>(() => AkochanHandScenario.Create(hand, new(0))).Code);
    }

    [Theory]
    [InlineData(13)]
    [InlineData(11)]
    [InlineData(15)]
    public void Non_fourteen_tile_inputs_require_caller_fallback(int count) =>
        Assert.Throws<AkochanException>(() => AkochanHandScenario.Create(Enumerable.Repeat(new VisibleTile(0), count).ToArray()));

    [Fact]
    public void Invalid_red_identity_duplicate_red_and_four_ordinary_fives_are_rejected()
    {
        var hand = Hand(); hand[0] = new(0, true);
        Assert.Throws<AkochanException>(() => AkochanHandScenario.Create(hand));
        hand = Hand(); hand[0] = new(4, true);
        Assert.Throws<AkochanException>(() => AkochanHandScenario.Create(hand));
        hand = Hand(); for (int i = 0; i < 4; i++) hand[i] = new(13);
        Assert.Throws<AkochanException>(() => AkochanHandScenario.Create(hand));
    }

    [Fact]
    public void Only_discard_is_extracted_from_reach_batch_and_red_identity_is_preserved()
    {
        var scenario = AkochanHandScenario.Create(Hand());
        var decision = Decision(scenario, new("reach", 0, null, null, [], null), Discard(new(4, true)));
        Assert.True(scenario.TryGetDiscard(decision, out var tile));
        Assert.Equal(new VisibleTile(4, true), tile);
        Assert.False(decision.IsLiveGameDecision);
    }

    [Theory]
    [InlineData("ankan")]
    [InlineData("hora")]
    [InlineData("reach")]
    [InlineData("kyushukyuhai")]
    public void Non_discard_engine_actions_never_escape_the_scenario_adapter(string action)
    {
        var scenario = AkochanHandScenario.Create(Hand());
        Assert.False(scenario.TryGetDiscard(Decision(scenario, new AkochanMove(action, 0, null, null, [], null)), out _));
    }

    [Fact]
    public void Different_request_identity_missing_physical_tile_and_non_synthetic_results_are_rejected()
    {
        var scenario = AkochanHandScenario.Create(Hand());
        var decision = Decision(scenario, Discard(new(0)));
        Assert.False(scenario.TryGetDiscard(decision with { InputSha256 = "different" }, out _));
        Assert.False(scenario.TryGetDiscard(decision with { IsSynthetic = false }, out _));
        Assert.False(scenario.TryGetDiscard(Decision(scenario, Discard(new(4, false))), out _));
        Assert.False(scenario.TryGetDiscard(Decision(scenario, Discard(new(0)) with { Actor = 1 }), out _));
    }
}
