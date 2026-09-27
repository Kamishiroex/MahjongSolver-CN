using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Tests.Replay;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game.Variants;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class OpenHandWinMenuTests
{
    [Theory]
    [InlineData("123m456p789s11z", "自摸", ActionFlags.Tsumo)]
    [InlineData("123m456p11z", "自摸", ActionFlags.Tsumo)]
    [InlineData("123m11z", "自摸", ActionFlags.Tsumo)]
    [InlineData("11z", "自摸", ActionFlags.Tsumo)]
    [InlineData("123m456p789s11z", "和牌", ActionFlags.Ron)]
    public void Current_visible_win_menu_is_not_lost_after_melds(string hand, string button, ActionFlags expected)
    {
        var state = Read(hand, [button, "放弃"], modalVisible: true);
        Assert.Equal(expected | ActionFlags.Pass, state.Legal.Flags);
        Assert.False(state.Legal.Can(ActionFlags.Discard));
    }

    [Theory]
    [InlineData("碰")]
    [InlineData("吃")]
    [InlineData("杠")]
    [InlineData("自摸！")]
    public void Old_call_or_declaration_text_does_not_replace_post_call_discard(string label)
    {
        var state = Read("123m456p789s11z", [label], modalVisible: true);
        Assert.Equal(ActionFlags.Discard, state.Legal.Flags);
    }

    [Fact]
    public void Hidden_menu_or_stale_atk_tsumo_label_does_not_grant_a_win()
    {
        Assert.Equal(ActionFlags.Discard, Read("123m456p789s11z", ["自摸"], false).Legal.Flags);
        Assert.Equal(ActionFlags.Discard, Read("123m456p789s11z", [], true).Legal.Flags);
    }

    private static StateSnapshot Read(string hand, string[] labels, bool modalVisible)
    {
        var profile = JsonLayoutProfileLoader.Load(Path.Combine(TestPaths.LayoutsDir, "emj.json"));
        var variant = new BaseEmjVariant(profile, new StubPluginLog(), Path.GetTempPath());
        var memory = new AddonMemoryBuilder(profile).WithScores(25000, 25000, 25000, 25000).WithHand(hand).Build();
        return variant.BuildSnapshotFromMemory(memory,
            [AtkValueRecord.OfInt(6), AtkValueRecord.OfInt(0), AtkValueRecord.OfString("自摸")],
            new(new MeldTracker(), null), modalVisible, labels)!;
    }
}
