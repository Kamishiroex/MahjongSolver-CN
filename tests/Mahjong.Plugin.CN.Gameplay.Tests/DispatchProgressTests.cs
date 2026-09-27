using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class DispatchProgressTests
{
    // Explicit synthetic same-state/count examples: no captured transition is invented.
    private static StateSnapshot Before => StateSnapshot.Empty with
    {
        Hand = Tiles.Parse("123456789m12344p"), AddonStateCode = 6, WallRemaining = 40,
    };

    [Theory]
    [InlineData("discard")]
    [InlineData("riichi-tsumogiri")]
    [InlineData("ankan")]
    [InlineData("minkan")]
    [InlineData("shouminkan")]
    public void Later_draw_can_progress_even_when_animation_state_and_hand_count_repeat(string label)
    {
        var before = Before;
        var after = before with { Hand = Tiles.Parse("123456789m12345p"), WallRemaining = 36 };
        Assert.Equal(before.AddonStateCode, after.AddonStateCode);
        Assert.Equal(before.Hand.Count, after.Hand.Count);
        Assert.True(AutoPlayLoop.ObservedLaterDraw(label, before.Hand, before.WallRemaining, after));
    }

    [Theory]
    [InlineData("riichi")]
    [InlineData("riichi-confirm")]
    [InlineData("pass")]
    [InlineData("ron")]
    [InlineData("tsumo")]
    public void Other_operations_keep_their_own_completion_rules(string label) =>
        Assert.False(AutoPlayLoop.ObservedLaterDraw(label, Before.Hand, 40,
            Before with { Hand = Tiles.Parse("123456789m12345p"), WallRemaining = 36 }));

    [Fact]
    public void Sorting_single_field_changes_and_unreadable_or_next_hand_data_do_not_release_pending_input()
    {
        var before = Before;
        foreach (var after in new StateSnapshot?[]
        {
            null, before, before with { Hand = before.Hand.Reverse().ToArray(), WallRemaining = 36 },
            before with { Hand = Tiles.Parse("123456789m12345p") },
            before with { WallRemaining = 36 },
            before with { Hand = Tiles.Parse("123456789m12345p"), WallRemaining = 70 },
            before with { Hand = Tiles.Parse("123456789m12345p"), WallRemaining = -1 },
        }) Assert.False(AutoPlayLoop.ObservedLaterDraw("discard", before.Hand, before.WallRemaining, after));
    }
}
