namespace Mahjong.Plugin.Dalamud.GameState.Variants;

/// <summary>
/// Exact BUTTON text from CN 2026.09.15.0000.0000, fixed local Lumina build:
/// EmjAddon column0 rows16..23 = Riichi/Pon/Chi/Kan/Tsumo/Ron/Pass/Cancel; row51 = next round.
/// exd/emjaddon.exh SHA256 1234B1B9171CC049091ABF8B783B8604D2D9F053630E256018AF353705211DF0;
/// exd/emjaddon_0_chs.exd SHA256 562DD709133FA479A9DA0D6A260D53FC028CFEFF02F2A1472860431263AFF9A1.
/// Generic Addon rows1/2/3762/2549 provide 确定/取消/确认/继续 (not Mahjong-specific callback evidence).
/// This helper does not establish visibility, enabled state, modal ownership or callback parameters.
/// Declaration strings such as 碰！ and help/setting text intentionally do not match button labels.
/// </summary>
internal static class ChineseActionLabels
{
    internal static string? Normalize(string? label) => label?.Trim() switch
    {
        "Pon" or "碰" => "Pon",
        "Chi" or "吃" => "Chi",
        "Kan" or "杠" => "Kan",
        "Ron" or "和牌" => "Ron",
        "Riichi" or "立直" => "Riichi",
        "Tsumo" or "自摸" => "Tsumo",
        "Pass" or "放弃" => "Pass",
        "Cancel" or "取消" => "Cancel",
        "Confirm" or "确定" or "确认" => "Confirm",
        "Next" or "下一局" or "继续" => "Next",
        // CN EmjAddon row25 (2026.09.15), exact menu text, not the generic 流局 result.
        "Kyushukyuhai" or "九种幺九倒牌" => "Kyushukyuhai",
        _ => null,
    };
}
