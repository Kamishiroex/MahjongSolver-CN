using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;
using Xunit;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PublicOwnHandTransitionTrackerTests
{
    private static readonly PublicObservationContext Context = new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
        new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-24T09:00:00Z");

    [Fact]
    public void Exact_live_public_fixture_tracks_north_draw_and_south_discard_without_complete_history()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "own-visible-transition-20260924.json")));
        var tracker = new PublicOwnHandTransitionTracker(); var results = new List<PublicOwnHandTransitionObservation>();
        foreach (var f in json.RootElement.GetProperty("Frames").EnumerateArray())
            results.Add(tracker.Observe(f.GetProperty("Sample").GetInt64(), f.GetProperty("Utc").GetDateTimeOffset(),
                f.GetProperty("Addon").Deserialize<AddonProbe>()!, Context, "south1-example"));
        Assert.Null(results[0].Transition);
        Assert.Equal("DrawCandidate", results[1].Transition!.Kind); Assert.Equal(30, results[1].Transition!.Tile.Kind34);
        Assert.Equal("DiscardCandidate", results[2].Transition!.Kind); Assert.Equal(28, results[2].Transition!.Tile.Kind34);
        Assert.All(results, r => { Assert.False(r.HistoryComplete); Assert.False(r.LegalTurnKnown); });
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Meld_adjusted_hand_sizes_allow_unique_draw_then_discard(int melds)
    {
        var t = new PublicOwnHandTransitionTracker(); int[] kinds = Enumerable.Range(0, 13 - 3 * melds).ToArray();
        Observe(t, 1, Probe(kinds, melds));
        var draw = Observe(t, 2, Probe([.. kinds, 25], melds, separate: true));
        Assert.Equal("DrawCandidate", draw.Transition?.Kind); Assert.Equal(25, draw.DrawnTileCandidate?.Kind34);
        var discard = Observe(t, 3, Probe(kinds, melds));
        Assert.Equal("DiscardCandidate", discard.Transition?.Kind); Assert.Null(discard.DrawnTileCandidate);
    }

    [Fact]
    public void Drawing_an_existing_kind_does_not_claim_physical_copy_or_tsumogiri()
    {
        var t = new PublicOwnHandTransitionTracker(); int[] hand = Enumerable.Range(0, 13).ToArray();
        Observe(t, 1, Probe(hand)); Observe(t, 2, Probe([.. hand, 4], separate: true));
        var result = Observe(t, 3, Probe(hand));
        Assert.Equal(4, result.Transition!.Tile.Kind34); Assert.False(result.Transition.PhysicalCopyKnown);
    }

    [Fact]
    public void Red_five_is_distinct_from_normal_five_in_draw_and_discard_inventory()
    {
        var t = new PublicOwnHandTransitionTracker(); int[] hand = Enumerable.Range(0, 13).ToArray();
        Observe(t, 1, Probe(hand)); var drawn = Probe([.. hand, 4], separate: true);
        var reading = drawn.LowerHandReading!;
        var tile = reading.Tiles[^1] with { IconId = 76035, RedFive = true, ChineseName = "赤5万",
            FacePathHash = LowerHandImageReader.ClientTexturePathHash("ui/icon/076000/076035.tex") };
        drawn = drawn with { LowerHandReading = reading with { Tiles = reading.Tiles.SetItem(reading.Tiles.Length - 1, tile) } };
        Assert.True(Observe(t, 2, drawn).Transition!.Tile.RedFive);
        Assert.True(Observe(t, 3, Probe(hand)).Transition!.Tile.RedFive);
    }

    [Fact]
    public void Sorting_only_does_not_produce_a_delta()
    {
        var t = new PublicOwnHandTransitionTracker(); int[] hand = Enumerable.Range(0, 13).ToArray();
        Observe(t, 1, Probe(hand));
        Assert.Null(Observe(t, 2, Probe(hand.Reverse().ToArray())).Transition);
    }

    [Fact]
    public void Short_unstable_animation_does_not_destroy_the_pre_draw_baseline()
    {
        var t = new PublicOwnHandTransitionTracker(); int[] hand = Enumerable.Range(0, 13).ToArray();
        Observe(t, 1, Probe(hand)); var unstable = Probe([.. hand, 25], separate: true);
        Observe(t, 2, unstable with { LowerHandReading = unstable.LowerHandReading! with { Stable = false, Code = "LOWER_STABILIZING" } });
        Assert.Equal("DrawCandidate", Observe(t, 3, Probe([.. hand, 25], separate: true)).Transition?.Kind);
    }

    [Fact]
    public void Unstable_interval_over_two_seconds_does_not_bridge_a_draw()
    {
        var t = new PublicOwnHandTransitionTracker(); int[] hand = Enumerable.Range(0, 13).ToArray();
        Observe(t, 1, Probe(hand)); var unstable = Probe(hand);
        unstable = unstable with { LowerHandReading = unstable.LowerHandReading! with { Stable = false } };
        for (int i = 2; i <= 22; i++) Observe(t, i, unstable);
        Assert.Null(Observe(t, 23, Probe([.. hand, 25], separate: true)).Transition);
    }

    [Theory]
    [InlineData("error")] [InlineData("scene")] [InlineData("version")] [InlineData("boundary")] [InlineData("gap")]
    public void Errors_version_scene_round_and_sample_gaps_do_not_bridge_old_inventory(string mode)
    {
        var t = new PublicOwnHandTransitionTracker(); int[] hand = Enumerable.Range(0, 13).ToArray();
        Observe(t, 1, Probe(hand));
        if (mode is "error" or "scene") Observe(t, 2, Probe(hand) with { Error = mode == "error" ? "READ" : null, Present = mode != "scene" });
        if (mode == "version") t.Observe(2, Epoch.AddMilliseconds(200), Probe(hand), Context with { ClientVersion = "different" }, "r1");
        var result = t.Observe(3, Epoch.AddMilliseconds(mode == "gap" ? 4000 : 300), Probe([.. hand, 25], separate: true), Context, mode == "boundary" ? "r2" : "r1");
        Assert.Null(result.Transition); Assert.Null(result.DrawnTileCandidate);
    }

    [Fact]
    public void Chi_consumed_two_tiles_allows_extra_tile_discard_without_slot135()
    {
        var t = new PublicOwnHandTransitionTracker(); int[] hand = Enumerable.Range(0, 13).ToArray();
        Observe(t, 1, Probe(hand));
        var afterCall = WithChi(Probe(hand[2..], 1));
        var call = Observe(t, 2, afterCall);
        Assert.Equal("PostCallExtraTileCandidate", call.Phase); Assert.Null(call.Transition); Assert.Null(call.SeparateSlotTile);
        var afterDiscard = WithChi(Probe(hand[3..], 1));
        Assert.Equal("DiscardCandidate", Observe(t, 3, afterDiscard).Transition?.Kind);
    }

    [Fact]
    public void Meld_change_consuming_one_tile_cannot_be_misreported_as_discard()
    {
        var t = new PublicOwnHandTransitionTracker(); int[] hand = Enumerable.Range(0, 11).ToArray();
        Observe(t, 1, Probe(hand, 1, separate: true));
        var after = Probe(hand[..10], 1); var table = after.PublicTableReading!;
        var group = table.MeldGroups[0] with { ShapeCode = "PUBLIC_KAN_FOUR_FACE_PATTERN", VisibleSlots = 4, KnownFaces = 4 };
        table = table with { MeldGroups = [group], Tiles = [.. table.Tiles, table.Tiles[0] with { SlotPath = "extra" }],
            Areas = [table.Areas[0] with { VisibleSlots = 4 }] };
        var result = Observe(t, 2, after with { PublicTableReading = table });
        Assert.Null(result.Transition); Assert.Equal("OWN_MELD_CHANGED_BASELINE_RESET", result.Code);
    }

    [Fact]
    public void Wrong_total_or_unverified_meld_count_cannot_expose_a_draw()
    {
        var t = new PublicOwnHandTransitionTracker();
        Assert.Equal("OWN_HAND_MELD_COUNT_CONFLICT", Observe(t, 1, Probe([1, 2, 3])).Code);
        var probe = Probe(Enumerable.Range(0, 13).ToArray());
        Assert.Equal("OWN_MELD_COUNT_UNVERIFIED", Observe(t, 2, probe with { PublicTableReading = probe.PublicTableReading! with { Areas = [] } }).Code);
    }

    private static PublicOwnHandTransitionObservation Observe(PublicOwnHandTransitionTracker t, long s, AddonProbe p)
        => t.Observe(s, Epoch.AddMilliseconds(s * 100), p, Context, "r1");
    private static AddonProbe WithChi(AddonProbe p)
    {
        var table = p.PublicTableReading!;
        return p with { PublicTableReading = table with
        {
            MeldGroups = [table.MeldGroups[0] with { ShapeCode = "PUBLIC_CHI_THREE_FACE_PATTERN" }],
            Tiles = table.Tiles.Select((tile, i) => tile with { Kind34 = i }).ToImmutableArray(),
        } };
    }
    private static AddonProbe Probe(int[] kinds, int melds = 0, bool separate = false)
    {
        var hand = kinds.Select((kind, i) =>
        {
            uint icon = 76001u + (uint)kind;
            uint hash = LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex");
            LowerTileCatalog.TryDecode(icon, hash, out var decoded);
            string path = separate && i == kinds.Length - 1 ? "Emj/135/9/4" : i == 0 ? "Emj/134/9/4" : $"Emj/{1340000 + i}/9/4";
            return new DecodedLowerFace(path, i + 1, icon, hash, kind, false, decoded.ChineseName);
        }).ToImmutableArray();
        var groups = Enumerable.Range(0, melds).Select(i => new PublicMeldGroup($"Emj/{112 + i * 10000}", "bottom", 3, 3, true, true,
            [], [], 0, "PUBLIC_PON_THREE_FACE_PATTERN")).ToImmutableArray();
        var faces = groups.SelectMany((g, i) => Enumerable.Range(0, 3).Select(j => new PublicTableTile("meld-bottom", "bottom",
            g.GroupPath + "/" + j + "/4", g.GroupPath, 0, 0, 40, 52, 0, false, 27 + i, false, "公开字牌", true))).ToImmutableArray();
        PublicTableAreaStatus area = new("meld-bottom", "PUBLIC_AREA_VISIBLE_CANDIDATE", true, true, true, melds == 0,
            melds * 3, 0, true, melds, 4);
        return new("Emj", true, true, true, 1, [], null, LowerHandReading: new("STABLE_VISIBLE_FACES", "fixture", true, hand),
            PublicTableReading: new("PUBLIC_IMAGES_STABLE_CANDIDATE", "fixture", true, faces, [], groups, [area]));
    }
}
