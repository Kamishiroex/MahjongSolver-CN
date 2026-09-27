using System.Reflection;
using System.Runtime.CompilerServices;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Tests.Replay;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game.Variants;
using Mahjong.Policy.Abstractions;
using Mahjong.Policy.Efficiency;
using Mahjong.Rules.Rulesets;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Synthetic upstream-profile menus; these do not establish CN native callback behavior.</summary>
public sealed class ClosedHandWinMenuTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Special_draw_permission_requires_current_visible_menu(bool visible)
    {
        var state = Read(6, "碰", ["九种幺九倒牌", "放弃"], visible);
        Assert.Equal(visible, state.Legal.Can(ActionFlags.Kyushukyuhai));
        Assert.False(state.Legal.Can(ActionFlags.Pon));
        Assert.True(state.Legal.Can(ActionFlags.Discard));
    }

    [Theory]
    [InlineData(6, "立直")]
    [InlineData(6, "碰")]
    [InlineData(6, "杠")]
    [InlineData(15, "立直")]
    [InlineData(28, "碰")]
    public void Current_visible_tsumo_replaces_stale_parent_call_labels(int stateCode, string staleLabel)
    {
        var state = Read(stateCode, staleLabel, ["自摸", "放弃"]);
        var expected = ActionFlags.Tsumo | ActionFlags.Pass;
        if (stateCode == 6) expected |= ActionFlags.Discard;
        Assert.Equal(expected, state.Legal.Flags);
        Assert.Empty(state.Legal.PonCandidates);
        Assert.Empty(state.Legal.KanCandidates);

        var choice = new EfficiencyPolicy(new DomanRuleSet()).Choose(state);
        Assert.Equal(ActionKind.Tsumo, choice.Kind);
        Assert.Equal(0, AutoPlayLoop.ComputeAcceptIndex(choice.Kind, state.Legal, choice.Call));
        Assert.Equal(1, AutoPlayLoop.ComputePassIndex(state.Legal));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(15)]
    [InlineData(28)]
    public void Pure_tsumo_classic_parent_labels_remain_supported_without_readable_list(int stateCode)
    {
        var state = Read(stateCode, "自摸", []);
        Assert.True(state.Legal.Can(ActionFlags.Tsumo));
        var choice = new EfficiencyPolicy(new DomanRuleSet()).Choose(state);
        Assert.Equal(ActionKind.Tsumo, choice.Kind);
        Assert.Equal(0, AutoPlayLoop.ComputeAcceptIndex(choice.Kind, state.Legal, choice.Call));
    }

    [Fact]
    public void Hidden_menu_does_not_grant_tsumo_from_either_source()
    {
        var state = Read(6, "自摸", ["自摸", "放弃"], visible: false);
        Assert.False(state.Legal.Can(ActionFlags.Tsumo));
        Assert.Equal(ActionFlags.Discard, state.Legal.Flags);
    }

    [Fact]
    public void Recognized_visible_menu_does_not_merge_a_stale_tsumo_parent_label()
    {
        var state = Read(6, "自摸", ["立直", "放弃"]);
        Assert.False(state.Legal.Can(ActionFlags.Tsumo));
        Assert.True(state.Legal.Can(ActionFlags.Riichi));
    }

    [Fact]
    public void Complete_all_simples_hand_keeps_tsumo_choice_without_scorer_error_in_cache()
    {
        var state = Read(6, "自摸", ["自摸", "放弃"]);
        var aggregator = (StateAggregator)RuntimeHelpers.GetUninitializedObject(typeof(StateAggregator));
        typeof(StateAggregator).GetField("policy", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(aggregator, new EfficiencyPolicy(new DomanRuleSet()));

        aggregator.ApplySnapshot(state);
        Assert.Same(state, aggregator.Latest);
        Assert.Null(aggregator.LastScorerError);
        Assert.NotEmpty(aggregator.LastScored!);
        Assert.Equal(ActionKind.Tsumo, aggregator.LastChoice!.Kind);
    }

    private static StateSnapshot Read(int stateCode, string parentLabel, string[] currentList, bool visible = true)
    {
        var profile = JsonLayoutProfileLoader.Load(Path.Combine(TestPaths.LayoutsDir, "emj.json"));
        var variant = new BaseEmjVariant(profile, new StubPluginLog(), Path.GetTempPath());
        var memory = new AddonMemoryBuilder(profile).WithScores(25000, 25000, 25000, 25000)
            .WithHand("234m456m678p33567s").Build();
        return variant.BuildSnapshotFromMemory(memory,
            [AtkValueRecord.OfInt(stateCode), AtkValueRecord.OfInt(0), AtkValueRecord.OfString(parentLabel)],
            new(new MeldTracker(), null), visible, currentList)!;
    }
}
