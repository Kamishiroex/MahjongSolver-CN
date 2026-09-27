using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed class RoundTitleCatalogTests
{
    [Theory]
    [InlineData(121451u, 0xBBC15A27u, 0, 1, "东一局")]
    [InlineData(121451u, 0x2D2D7E34u, 0, 1, "东一局")]
    [InlineData(121452u, 0xFC6120F7u, 0, 2, "东二局")]
    [InlineData(121452u, 0x14A042F1u, 0, 2, "东二局")]
    [InlineData(121453u, 0xC1010947u, 0, 3, "东三局")]
    [InlineData(121453u, 0x03DB56B2u, 0, 3, "东三局")]
    [InlineData(121454u, 0x7321D557u, 0, 4, "东四局")]
    [InlineData(121454u, 0x67BA3B7Bu, 0, 4, "东四局")]
    [InlineData(121455u, 0x4E41FCE7u, 1, 1, "南一局")]
    [InlineData(121455u, 0x70C12F38u, 1, 1, "南一局")]
    [InlineData(121456u, 0x09E18637u, 1, 2, "南二局")]
    [InlineData(121456u, 0x494C13FDu, 1, 2, "南二局")]
    [InlineData(121457u, 0x3481AF87u, 1, 3, "南三局")]
    [InlineData(121457u, 0x5E3707BEu, 1, 3, "南三局")]
    [InlineData(121458u, 0xB6D13856u, 1, 4, "南四局")]
    [InlineData(121458u, 0x818EC86Fu, 1, 4, "南四局")]
    public void Local_visually_reviewed_texture_pairs_match_exact_round(uint icon, uint hash, int wind, int hand, string label)
    {
        Assert.True(RoundTitleCatalog.TryDecode(RoundTitleCatalog.GameVersion, Sample() with { IconId = icon, TexturePathHash = hash }, out var value));
        Assert.Equal(wind, value.RoundWind); Assert.Equal(hand, value.HandNumber); Assert.Equal(label, value.Label);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("path")]
    [InlineData("code")]
    [InlineData("parts-list")]
    [InlineData("part-count")]
    [InlineData("part-id")]
    [InlineData("uv")]
    [InlineData("size")]
    [InlineData("asset")]
    [InlineData("texture-kind")]
    [InlineData("icon-hash-mismatch")]
    [InlineData("rank-not-round")]
    public void Unknown_version_shape_hash_and_adjacent_nonround_icons_are_rejected(string variation)
    {
        var candidate = Sample(); string version = RoundTitleCatalog.GameVersion;
        switch (variation)
        {
            case "version": version = "2026.09.99.0000.0000"; break;
            case "path": candidate = candidate with { Path = "Emj/20" }; break;
            case "code": candidate = candidate with { Code = "ROUND_TITLE_NOT_VISIBLE" }; break;
            case "parts-list": candidate = candidate with { PartsListId = 29 }; break;
            case "part-count": candidate = candidate with { PartCount = 2 }; break;
            case "part-id": candidate = candidate with { SelectedPart = 1 }; break;
            case "uv": candidate = candidate with { U = 1 }; break;
            case "size": candidate = candidate with { Height = 79 }; break;
            case "asset": candidate = candidate with { AssetId = 32 }; break;
            case "texture-kind": candidate = candidate with { TextureType = 3 }; break;
            case "icon-hash-mismatch": candidate = candidate with { IconId = 121451 }; break;
            case "rank-not-round": candidate = candidate with { IconId = 121459, TexturePathHash = 0x96F5DC2C }; break;
        }
        Assert.False(RoundTitleCatalog.TryDecode(version, candidate, out _));
    }

    private static RoundTitleResourceCandidate Sample() => new("Emj/19", "ROUND_TITLE_RESOURCE_CANDIDATE",
        0, 1, 0, 0, 0, 640, 80, 0, 1, 121452, 346047217);
}
