using Mahjong.Cn.Engines;
using Xunit;

namespace Mahjong.Cn.Core.Tests;

public sealed class MjaiTileCodecTests
{
    // Independent protocol table read from pinned akochan share/types.cpp:315-399.
    // IDs here are this repository's VisibleTile 0..33, not akochan's internal 1..37 IDs.
    public static IEnumerable<object[]> KnownTiles()
    {
        (int Id, string Token)[] ordinary =
        [
            (0, "1m"), (1, "2m"), (2, "3m"), (3, "4m"), (4, "5m"),
            (5, "6m"), (6, "7m"), (7, "8m"), (8, "9m"),
            (9, "1p"), (10, "2p"), (11, "3p"), (12, "4p"), (13, "5p"),
            (14, "6p"), (15, "7p"), (16, "8p"), (17, "9p"),
            (18, "1s"), (19, "2s"), (20, "3s"), (21, "4s"), (22, "5s"),
            (23, "6s"), (24, "7s"), (25, "8s"), (26, "9s"),
            (27, "E"), (28, "S"), (29, "W"), (30, "N"), (31, "P"), (32, "F"), (33, "C"),
        ];
        foreach (var (id, token) in ordinary) yield return [id, false, token];
        yield return [4, true, "5mr"];
        yield return [13, true, "5pr"];
        yield return [22, true, "5sr"];
    }

    [Theory]
    [MemberData(nameof(KnownTiles))]
    public void All_34_kinds_and_three_red_fives_have_exact_protocol_tokens(int id, bool red, string token)
    {
        var expected = new VisibleTile(id, red);
        Assert.Equal(token, MjaiTileCodec.Encode(expected));
        Assert.True(MjaiTileCodec.TryDecode(token, out var decoded));
        Assert.Equal(expected, decoded);
        Assert.False(MjaiTileCodec.IsUnknownMarker(token));
    }

    [Fact]
    public void Legal_protocol_table_is_complete_and_one_to_one()
    {
        var cases = KnownTiles().ToArray();
        Assert.Equal(37, cases.Length);
        Assert.Equal(37, cases.Select(x => (string)x[2]).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(34, cases.Where(x => !(bool)x[1]).Select(x => (int)x[0]).Distinct().Count());
        Assert.Equal(3, cases.Count(x => (bool)x[1]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("?")]
    [InlineData("??")]
    [InlineData(" ? ")]
    [InlineData(" 1m")]
    [InlineData("1m ")]
    [InlineData("1m\n")]
    [InlineData("\tE")]
    [InlineData("1m\0")]
    [InlineData("0m")]
    [InlineData("10m")]
    [InlineData("01m")]
    [InlineData("-1m")]
    [InlineData("1M")]
    [InlineData("5P")]
    [InlineData("5Sr")]
    [InlineData("5mR")]
    [InlineData("5mr extra")]
    [InlineData("5mrr")]
    [InlineData("0mr")]
    [InlineData("4mr")]
    [InlineData("6pr")]
    [InlineData("9sr")]
    [InlineData("r5m")]
    [InlineData("1z")]
    [InlineData("7z")]
    [InlineData("e")]
    [InlineData("s")]
    [InlineData("w")]
    [InlineData("n")]
    [InlineData("p")]
    [InlineData("f")]
    [InlineData("c")]
    [InlineData("Pr")]
    [InlineData("Cr")]
    [InlineData("East")]
    [InlineData("東")]
    [InlineData("白")]
    [InlineData("１m")]
    [InlineData("٥m")]
    public void Malformed_alias_unknown_or_wrong_case_never_decodes(string? token)
    {
        Assert.False(MjaiTileCodec.TryDecode(token, out _));
    }

    [Fact]
    public void Unknown_marker_has_a_separate_exact_recognizer_and_never_encodes_as_a_known_tile()
    {
        Assert.True(MjaiTileCodec.IsUnknownMarker("?"));
        Assert.False(MjaiTileCodec.TryDecode("?", out _));
        foreach (string? other in new[] { null, "", "??", " ?", "? ", "？", "E" })
            Assert.False(MjaiTileCodec.IsUnknownMarker(other));
        Assert.All(KnownTiles(), row => Assert.NotEqual("?", MjaiTileCodec.Encode(new((int)row[0], (bool)row[1]))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(34)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Invalid_tile_kind_is_rejected_by_both_conversions(int kind)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MjaiTileCodec.Encode(new(kind)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MjaiTileCodec.DoraIndicatorForActualDora(new(kind)));
    }

    [Fact]
    public void Every_non_five_red_flag_is_rejected_instead_of_normalized_into_a_valid_tile()
    {
        foreach (int kind in Enumerable.Range(0, 34).Where(x => x is not (4 or 13 or 22)))
        {
            Assert.Throws<ArgumentException>(() => MjaiTileCodec.Encode(new(kind, true)));
            Assert.Throws<ArgumentException>(() => MjaiTileCodec.DoraIndicatorForActualDora(new(kind, true)));
        }
    }

    public static IEnumerable<object[]> DoraCycles()
    {
        // Explicit expected predecessor pairs from pinned dora_to_dora_marker :209-218.
        (string Actual, string Indicator)[] cases =
        [
            ("1m", "9m"), ("2m", "1m"), ("3m", "2m"), ("4m", "3m"), ("5m", "4m"),
            ("6m", "5m"), ("7m", "6m"), ("8m", "7m"), ("9m", "8m"),
            ("1p", "9p"), ("2p", "1p"), ("3p", "2p"), ("4p", "3p"), ("5p", "4p"),
            ("6p", "5p"), ("7p", "6p"), ("8p", "7p"), ("9p", "8p"),
            ("1s", "9s"), ("2s", "1s"), ("3s", "2s"), ("4s", "3s"), ("5s", "4s"),
            ("6s", "5s"), ("7s", "6s"), ("8s", "7s"), ("9s", "8s"),
            ("E", "N"), ("S", "E"), ("W", "S"), ("N", "W"),
            ("P", "C"), ("F", "P"), ("C", "F"),
            ("5mr", "4m"), ("5pr", "4p"), ("5sr", "4s"),
        ];
        foreach (var (actual, indicator) in cases) yield return [actual, indicator];
    }

    [Theory]
    [MemberData(nameof(DoraCycles))]
    public void Actual_dora_uses_separate_suit_wind_and_dragon_cycles_and_never_returns_red(string actual, string indicator)
    {
        Assert.True(MjaiTileCodec.TryDecode(actual, out var tile));
        var result = MjaiTileCodec.DoraIndicatorForActualDora(tile);
        Assert.Equal(indicator, MjaiTileCodec.Encode(result));
        Assert.False(result.Red);
    }
}
