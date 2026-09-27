using Mahjong.Plugin.Dalamud.GameState.Variants;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed class ChineseActionLabelsTests
{
    [Theory]
    [InlineData("碰", "Pon")]
    [InlineData("吃", "Chi")]
    [InlineData("杠", "Kan")]
    [InlineData("立直", "Riichi")]
    [InlineData("自摸", "Tsumo")]
    [InlineData("和牌", "Ron")]
    [InlineData("放弃", "Pass")]
    [InlineData("取消", "Cancel")]
    [InlineData("确定", "Confirm")]
    [InlineData("确认", "Confirm")]
    [InlineData("下一局", "Next")]
    [InlineData("继续", "Next")]
    [InlineData("九种幺九倒牌", "Kyushukyuhai")]
    [InlineData("  和牌  ", "Ron")]
    [InlineData("Ron", "Ron")]
    [InlineData("Pon", "Pon")]
    public void Exact_static_resource_button_text_maps_to_upstream_action(string input, string expected)
        => Assert.Equal(expected, ChineseActionLabels.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("碰！")]
    [InlineData("和牌！")]
    [InlineData("自动和牌有效")]
    [InlineData("门前清自摸")]
    [InlineData("荣和")]
    [InlineData("下一步")]
    [InlineData("确认舍牌")]
    [InlineData("碰abc")]
    [InlineData("ron")]
    [InlineData("流局")]
    [InlineData("四家立直")]
    [InlineData("三家和")]
    [InlineData("四杠散了")]
    [InlineData("四风子连打")]
    [InlineData("荒牌平局")]
    [InlineData("要流局吗？")]
    public void Declarations_help_settings_and_unverified_aliases_do_not_become_buttons(string? input)
        => Assert.Null(ChineseActionLabels.Normalize(input));
}
