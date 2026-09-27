using System.Text.Json;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PublicDiscardSemanticsTests
{
    private static PublicObservationContext Context => new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
        new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
    private static ObservationReference Source => new(1, DateTimeOffset.UnixEpoch, "fixture", "actual public observation");

    [Theory]
    [InlineData(0, "normal")]
    [InlineData(23, "unclassified")]
    [InlineData(45, "unclassified")]
    public void Response_highlight_never_claims_hand_discard_or_uncalled_even_at_zero(short brightness, string style)
    {
        var mark = new PublicTileVisualMark(brightness, brightness, brightness, 100, 100, 100, style)
        { ResponseHighlight = true };
        var discard = PublicDiscardSemantics.Tsumogiri(mark, Context, Source);
        var claimed = PublicDiscardSemantics.WasClaimed(mark, Context, Source);
        Assert.False(discard.HasValue); Assert.False(claimed.HasValue);
        Assert.Contains("高亮", discard.Reason);
        Assert.Equal(discard.Reason, claimed.Reason);
        var ordinary = new PublicTileVisualMark(0, 0, 0, 100, 100, 100, "normal");
        Assert.True(PublicDiscardSemantics.Tsumogiri(ordinary, Context, Source).IsConfirmed);
        Assert.False(PublicDiscardSemantics.Tsumogiri(ordinary, Context, Source).Value);
    }

    [Fact]
    public void Captured_inventory_transitions_independently_agree_with_river_shading()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "own-discard-style-20260924.json")));
        int handDiscards = 0, draws = 0, ambiguous = 0;
        foreach (var sample in json.RootElement.GetProperty("Cases").EnumerateArray())
        {
            int[] Read(string name) => sample.GetProperty(name).EnumerateArray()
                .Select(x => x[0].GetInt32() + x[1].GetInt32() * 100).Order().ToArray();
            int[] before = Read("beforeHand"), drawn = Read("drawHand"), after = Read("afterHand");
            static int[] Difference(int[] a, int[] b)
            {
                var remainder = a.ToList();
                foreach (int tile in b) Assert.True(remainder.Remove(tile));
                return remainder.ToArray();
            }
            int newTile = Assert.Single(Difference(drawn, before));
            int discarded = Assert.Single(Difference(drawn, after));
            var mark = sample.GetProperty("mark").Deserialize<PublicTileVisualMark>()!;
            var decoded = PublicDiscardSemantics.Tsumogiri(mark, Context, Source);
            Assert.True(decoded.IsConfirmed);
            Assert.NotEmpty(decoded.Observation!.DerivationInputs);
            if (newTile != discarded)
            {
                handDiscards++;
                Assert.False(decoded.Value);
            }
            else if (!before.Contains(newTile))
            {
                draws++;
                Assert.True(decoded.Value);
            }
            else ambiguous++; // Same-kind tiles are not independent evidence of physical tile identity.
        }
        Assert.Equal(102, handDiscards);
        Assert.Equal(64, draws);
        Assert.Equal(5, ambiguous);
    }

    [Theory]
    [InlineData(10, 10, 10, "unclassified")]
    [InlineData(-75, -75, -75, "normal")]
    public void Overlays_animation_or_inconsistent_label_remain_unknown(short r, short g, short b, string style)
        => Assert.False(PublicDiscardSemantics.Tsumogiri(new(r, g, b, 100, 100, 100, style), Context, Source).HasValue);

    [Fact]
    public void Version_or_uld_mismatch_cannot_use_the_mapping()
    {
        var normal = new PublicTileVisualMark(0, 0, 0, 100, 100, 100, "normal");
        Assert.False(PublicDiscardSemantics.Tsumogiri(normal, null, Source).HasValue);
        Assert.False(PublicDiscardSemantics.Tsumogiri(normal, Context with { ClientVersion = "other" }, Source).HasValue);
        Assert.False(PublicDiscardSemantics.Tsumogiri(normal, Context with { UldSha256 = "other" }, Source).HasValue);
        Assert.False(PublicDiscardSemantics.Tsumogiri(normal with { MultiplyRed = 99 }, Context, Source).HasValue);
    }

    [Theory]
    [InlineData(0, -75, -75, "red-tinted", false)]
    [InlineData(-75, -150, -150, "darkened-red-tinted", true)]
    public void Independently_observed_called_overlay_preserves_original_draw_shading(short r, short g, short b, string style, bool draw)
    {
        var mark = new PublicTileVisualMark(r, g, b, 100, 100, 100, style);
        Assert.Equal(draw, PublicDiscardSemantics.Tsumogiri(mark, Context, Source).Value);
        Assert.True(PublicDiscardSemantics.WasClaimed(mark, Context, Source).Value);
        Assert.True(PublicDiscardSemantics.WasClaimed(mark, Context, Source).IsConfirmed);
        Assert.False(PublicDiscardSemantics.WasClaimed(mark with { Style = "unclassified" }, Context, Source).HasValue);
    }
}
