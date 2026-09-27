using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.PublicState;

/// <summary>Version-specific meaning of public river shading, independently checked against own-hand transitions.</summary>
internal static class PublicDiscardSemantics
{
    internal const string Evidence = "docs/cn/LIVE-DISCARD-STYLE-EVIDENCE.md; CN 2026.09.15; " +
        "102 independent hand-discard cases and 64 unique drawn-tile-discard cases; " +
        "docs/cn/PUBLIC-CALL-TRANSITIONS-20260924.md; 24 clean called-color overlay transitions";

    internal static Field<bool> Tsumogiri(PublicTileVisualMark? mark, PublicObservationContext? context,
        ObservationReference observation)
    {
        if (context?.ClientVersion != RuntimeIdentity.TargetGame || context.UldSha256 != LowerHandProfile.EmjUldSha256 ||
            context.Profile?.ClientVersion != context.ClientVersion || context.Profile.UldSha256 != context.UldSha256)
            return Field<bool>.Unknown("摸切颜色语义不适用于当前版本或未经核对的 ULD。");
        if (mark?.ResponseHighlight == true)
            return Field<bool>.Unknown("当前响应菜单的牌河高亮动画，包括亮度归零阶段；不能据此推定摸切或被鸣。");
        if (mark is null || mark.MultiplyRed != 100 || mark.MultiplyGreen != 100 || mark.MultiplyBlue != 100)
            return Field<bool>.Unknown("缺少已核对的牌河颜色，或正在颜色过渡。");
        bool? value = (mark.AddRed, mark.AddGreen, mark.AddBlue, mark.Style) switch
        {
            (0, 0, 0, "normal") => false,
            (-75, -75, -75, "darkened") => true,
            (0, -75, -75, "red-tinted") => false,
            (-75, -150, -150, "darkened-red-tinted") => true,
            _ => null,
        };
        if (value is null) return Field<bool>.Unknown("当前颜色叠加／过渡尚未映射，不能以非灰色推定手切。");
        return Field<bool>.Known(value.Value, observation with
        {
            Source = "PublicDiscardSemantics/Emj-public-river-shading",
            Evidence = Evidence,
            DerivationInputs = ["public river template Res 3 AddRGB/MultiplyRGB/Style"],
        }, SourceKind.Derived);
    }

    internal static Field<bool> WasClaimed(PublicTileVisualMark? mark, PublicObservationContext? context,
        ObservationReference observation)
    {
        var shading = Tsumogiri(mark, context, observation);
        if (!shading.IsConfirmed) return Field<bool>.Unknown(shading.Reason ?? "牌河被鸣标记未知。");
        return Field<bool>.Known(mark!.Style is "red-tinted" or "darkened-red-tinted",
            shading.Observation! with { Source = "PublicDiscardSemantics/Emj-called-river-overlay" }, SourceKind.Derived);
    }
}
