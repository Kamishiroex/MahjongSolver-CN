using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Tests.Replay;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game.Variants;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class SharedResponseStateTests
{
    [Theory]
    [InlineData(30, "吃", ActionFlags.Chi)]
    [InlineData(30, "碰", ActionFlags.Pon)]
    [InlineData(30, "杠", ActionFlags.MinKan)]
    [InlineData(30, "和牌", ActionFlags.Ron)]
    [InlineData(6, "吃", ActionFlags.Chi)]
    [InlineData(6, "碰", ActionFlags.Pon)]
    [InlineData(6, "杠", ActionFlags.MinKan)]
    [InlineData(6, "和牌", ActionFlags.Ron)]
    public void Current_response_survives_shared_state_with_open_hand(int code, string label, ActionFlags expected)
    {
        // Actual stopped hand: 246677m3s, previously pon north and green dragon.
        var state = Read(code, "246677m3s", true, [label, "放弃"]);
        Assert.Equal(expected | ActionFlags.Pass, state.Legal.Flags);
        Assert.Equal(1, AutoPlayLoop.ComputePassIndex(state.Legal));
        Assert.False(state.Legal.Can(ActionFlags.Discard));
    }

    [Theory]
    [InlineData(30, false, "246677m3s")]
    [InlineData(6, false, "246677m3s")]
    [InlineData(29, true, "246677m3s")]
    [InlineData(32, true, "246677m3s")]
    [InlineData(0, true, "246677m3s")]
    [InlineData(30, true, "246677m33s")]
    public void Hidden_result_unknown_or_discard_shape_does_not_promote_stale_response(int code, bool visible, string hand)
        => Assert.False(Read(code, hand, visible, ["吃", "放弃"]).Legal.Can(ActionFlags.Chi));

    [Fact]
    public void Missing_partial_or_duplicate_list_never_uses_parent_label_on_shared_state()
    {
        foreach (string[] labels in new[] { Array.Empty<string>(), ["吃"], ["吃", "未知"], ["吃", "吃", "放弃"] })
            Assert.Equal(ActionFlags.None, Read(30, "246677m3s", true, labels).Legal.Flags);
    }

    private static StateSnapshot Read(int code, string hand, bool visible, string[] labels)
    {
        var profile = JsonLayoutProfileLoader.Load(Path.Combine(TestPaths.LayoutsDir, "emj.json"));
        var variant = new BaseEmjVariant(profile, new StubPluginLog(), Path.GetTempPath());
        var memory = new AddonMemoryBuilder(profile).WithScores(25000, 31200, 24000, 19800).WithHand(hand).Build();
        return variant.BuildSnapshotFromMemory(memory,
            [AtkValueRecord.OfInt(code), AtkValueRecord.OfString("碰")], new(new MeldTracker(), null), visible, labels)!;
    }
}
