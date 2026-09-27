using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PublicRiverEventTrackerTests
{
    private static readonly PublicObservationContext Context = new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
        new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-24T09:00:00Z");
    private static readonly PublicTileVisualMark Normal = new(0, 0, 0, 100, 100, 100, "normal");
    private static readonly PublicTileVisualMark Dark = new(-75, -75, -75, 100, 100, 100, "darkened");
    private static readonly PublicTileVisualMark Red = new(0, -75, -75, 100, 100, 100, "red-tinted");

    [Fact]
    public void Exact_live_river_DTOs_keep_animation_pending_then_observe_bottom_and_right_discards()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "river-prefix-example-20260924.json")));
        var t = new PublicRiverEventTracker(); var values = new List<PublicRiverHistoryObservation>();
        foreach (var frame in file.RootElement.GetProperty("Frames").EnumerateArray())
            values.Add(t.Observe(frame.GetProperty("Sample").GetInt64(), frame.GetProperty("Utc").GetDateTimeOffset(),
                frame.GetProperty("Addon").Deserialize<AddonProbe>(), Context, "captured-south1"));
        Assert.True(values[0].InitializedFromEmpty); Assert.Empty(values[1].NewEvents);
        Assert.Equal(1, values[1].UnresolvedSlotCount);
        var first = Assert.Single(values[2].NewEvents); Assert.Equal("bottom", first.ScreenDirection); Assert.Equal(28, first.Tile.Kind34);
        var second = Assert.Single(values[3].NewEvents); Assert.Equal("right", second.ScreenDirection); Assert.Equal(6, second.Tile.Kind34);
        Assert.Equal(1, first.DiscardOrdinal); Assert.Equal(2, second.DiscardOrdinal);
        Assert.True(values[3].HasContiguousDiscardPrefix); Assert.False(values[3].HistoryComplete);
    }

    [Fact]
    public void Empty_baseline_and_unique_new_slots_define_order_not_display_position()
    {
        var t = new PublicRiverEventTracker(); var start = Observe(t, 1, Probe());
        Assert.True(start.HasContiguousDiscardPrefix); Assert.False(start.HistoryComplete);
        var a = Row("bottom", 118, 2, order: 5); var b = Row("left", 127, 30, mark: Dark, sideways: true);
        var first = Assert.Single(Observe(t, 2, Probe(a)).NewEvents);
        Assert.Equal(1, first.DiscardOrdinal); Assert.False(first.Tsumogiri);
        var second = Assert.Single(Observe(t, 3, Probe(a, b)).NewEvents);
        Assert.Equal("left", second.ScreenDirection); Assert.Equal(2, second.DiscardOrdinal);
        Assert.True(second.Tsumogiri); Assert.True(second.IsSideways);
        Assert.Empty(Observe(t, 4, Probe(a, b)).NewEvents);
    }

    [Fact]
    public void Starting_midround_only_preserves_inventory_never_invents_prior_discard_events()
    {
        var t = new PublicRiverEventTracker(); var result = Observe(t, 1, Probe(Row("bottom", 118, 2)));
        Assert.False(result.InitializedFromEmpty); Assert.True(result.HasHistoryGap); Assert.Empty(result.NewEvents);
        Assert.Null(Assert.Single(result.Slots).DiscardOrdinal);
        Assert.False(Observe(t, 2, Probe()).HasContiguousDiscardPrefix);
    }

    [Fact]
    public void Old_slot_occlusion_preserves_history_without_filling_current_decoded_tile()
    {
        var t = new PublicRiverEventTracker(); var a = Row("bottom", 118, 2);
        Observe(t, 1, Probe()); Observe(t, 2, Probe(a));
        var result = Observe(t, 3, Probe(a with { Decoded = false }));
        Assert.False(result.HasHistoryGap); Assert.True(result.HasContiguousDiscardPrefix);
        var old = Assert.Single(result.Slots); Assert.Equal(2, old.Tile.Kind34); Assert.False(old.CurrentDecoded);
        Assert.Equal(2, old.LastDecodedSample); Assert.Empty(result.NewEvents);
        Assert.Empty(Observe(t, 4, Probe(a)).NewEvents);
    }

    [Fact]
    public void Multiple_new_slots_have_no_invented_temporal_order_even_when_stability_arrives_separately()
    {
        var t = new PublicRiverEventTracker(); Observe(t, 1, Probe());
        var a = Row("bottom", 118, 2); var b = Row("right", 121, 3);
        var first = Observe(t, 2, Probe(a, b with { Stable = false }));
        Assert.True(first.HasHistoryGap); Assert.Empty(first.NewEvents);
        var result = Observe(t, 3, Probe(a, b));
        Assert.Empty(result.NewEvents); Assert.Equal(2, result.Slots.Length);
        Assert.All(result.Slots, s => Assert.Null(s.DiscardOrdinal));
    }

    [Fact]
    public void Unknown_new_slot_stays_pending_until_independently_stable_resources_exist()
    {
        var t = new PublicRiverEventTracker(); Observe(t, 1, Probe()); var a = Row("bottom", 118, 2);
        var unknown = Observe(t, 2, Probe(a with { Decoded = false }));
        Assert.Empty(unknown.Slots); Assert.Equal(1, unknown.UnresolvedSlotCount); Assert.False(unknown.HasContiguousDiscardPrefix);
        Assert.Empty(Observe(t, 3, Probe(a with { Stable = false })).NewEvents);
        Assert.Equal("DiscardObserved", Assert.Single(Observe(t, 4, Probe(a)).NewEvents).Kind);
    }

    [Fact]
    public void Verified_resource_identity_conflict_is_kept_even_when_normalized_tile_is_missing()
    {
        var t = new PublicRiverEventTracker(); Observe(t, 1, Probe()); var a = Row("bottom", 118, 2);
        Observe(t, 2, Probe(a)); var changed = Probe(a with { Kind = 3 });
        changed = changed with { PublicTableReading = changed.PublicTableReading! with { Tiles = [] } };
        var result = Observe(t, 3, changed);
        Assert.True(result.HasHistoryGap); Assert.Equal(2, Assert.Single(result.Slots).Tile.Kind34);
        Assert.Contains(result.Issues, i => i.Code == "RIVER_EXISTING_SLOT_IDENTITY_CHANGED");
    }

    [Fact]
    public void Red_overlay_produces_only_one_called_mark_and_never_another_discard()
    {
        var t = new PublicRiverEventTracker(); Observe(t, 1, Probe()); var a = Row("bottom", 118, 2);
        Observe(t, 2, Probe(a)); var marked = a with { Mark = Red };
        var update = Observe(t, 3, Probe(marked));
        Assert.Equal("CalledMarkObserved", Assert.Single(update.NewEvents).Kind);
        Assert.True(Assert.Single(update.Slots).WasClaimed); Assert.False(update.HasHistoryGap);
        Assert.Empty(Observe(t, 4, Probe(marked)).NewEvents);
        Assert.True(Observe(t, 5, Probe(a)).HasHistoryGap);
    }

    [Theory]
    [InlineData("kind")] [InlineData("red")] [InlineData("missing")] [InlineData("gap")]
    public void Identity_conflict_reduction_or_long_gap_keeps_old_history_and_records_issue(string mode)
    {
        var t = new PublicRiverEventTracker(); Observe(t, 1, Probe()); var a = Row("bottom", 118, 4);
        Observe(t, 2, Probe(a));
        var probe = mode switch
        {
            "kind" => Probe(a with { Kind = 5 }), "red" => Probe(a with { RedFive = true }),
            "missing" => Probe(), _ => Probe(a, Row("right", 121, 2)),
        };
        var r = t.Observe(3, Epoch.AddMilliseconds(mode == "gap" ? 5000 : 300), probe, Context, "r1");
        Assert.True(r.HasHistoryGap); Assert.NotEmpty(r.Issues); Assert.Empty(r.NewEvents);
        Assert.Equal(4, r.Slots.Single(s => s.SlotPath == a.Path).Tile.Kind34);
        Assert.False(r.Slots.Single(s => s.SlotPath == a.Path).Tile.RedFive);
    }

    [Theory]
    [InlineData("version")] [InlineData("boundary")] [InlineData("scene")] [InlineData("read")]
    public void Invalid_context_or_scene_preserves_an_explicit_gap_and_does_not_reuse_freshness(string mode)
    {
        var t = new PublicRiverEventTracker(); Observe(t, 1, Probe()); var a = Row("bottom", 118, 2); Observe(t, 2, Probe(a));
        var result = t.Observe(3, Epoch.AddMilliseconds(300), Probe(a) with
        { Present = mode != "scene", Error = mode == "read" ? "READ_FAILED" : null },
            mode == "version" ? Context with { ClientVersion = "other" } : Context, mode == "boundary" ? null : "r1");
        Assert.True(result.HasHistoryGap); Assert.Empty(result.NewEvents); Assert.False(Assert.Single(result.Slots).CurrentDecoded);
        Assert.False(Observe(t, 4, Probe(a)).HasContiguousDiscardPrefix);
    }

    [Fact]
    public void Valid_new_round_token_can_start_a_new_empty_prefix_without_previous_round_slots()
    {
        var t = new PublicRiverEventTracker(); Observe(t, 1, Probe(Row("bottom", 118, 2)));
        var next = t.Observe(2, Epoch.AddMilliseconds(200), Probe(), Context, "r2");
        Assert.True(next.InitializedFromEmpty); Assert.True(next.HasContiguousDiscardPrefix); Assert.Empty(next.Slots);
    }

    [Fact]
    public void Unmapped_animated_color_does_not_default_to_hand_discard()
    {
        var t = new PublicRiverEventTracker(); Observe(t, 1, Probe());
        var result = Observe(t, 2, Probe(Row("bottom", 118, 2, mark: Normal with { AddRed = 5 })));
        Assert.Null(Assert.Single(result.NewEvents).Tsumogiri);
    }

    [Fact]
    public void Confirmed_response_highlight_retracts_zero_phase_shading_without_inventing_an_event_or_conflict()
    {
        var tracker = new PublicRiverEventTracker();
        var row = Row("bottom", 118, 2);
        Observe(tracker, 1, Probe());
        var initial = Observe(tracker, 2, Probe(row));
        Assert.False(Assert.Single(initial.Slots).Tsumogiri);
        var highlight = Normal with { ResponseHighlight = true };
        var animation = Observe(tracker, 3, Probe(row with { Mark = highlight, Stable = false }));
        Assert.Null(Assert.Single(animation.Slots).Tsumogiri);
        Assert.Null(Assert.Single(animation.Slots).WasClaimed);
        Assert.Empty(animation.NewEvents);
        Assert.False(animation.HasHistoryGap);
        Assert.False(Assert.Single(animation.Slots).CurrentDecoded);
        var zeroAgain = Observe(tracker, 4, Probe(row with { Mark = highlight }));
        Assert.Null(Assert.Single(zeroAgain.Slots).Tsumogiri);
        Assert.Null(Assert.Single(zeroAgain.Slots).WasClaimed);
        Assert.Empty(zeroAgain.NewEvents);
        var after = Observe(tracker, 5, Probe(row with { Mark = Dark }));
        Assert.True(Assert.Single(after.Slots).Tsumogiri);
        Assert.False(Assert.Single(after.Slots).WasClaimed);
        Assert.Empty(after.NewEvents);
        Assert.False(after.HasHistoryGap);
        Assert.Equal(1, Assert.Single(after.Slots).DiscardOrdinal);
    }

    [Fact]
    public void Highlight_does_not_revoke_a_previously_confirmed_called_mark()
    {
        var tracker = new PublicRiverEventTracker();
        var row = Row("bottom", 118, 2);
        Observe(tracker, 1, Probe()); Observe(tracker, 2, Probe(row));
        Assert.Equal("CalledMarkObserved", Assert.Single(Observe(tracker, 3, Probe(row with { Mark = Red })).NewEvents).Kind);
        var animation = Observe(tracker, 4, Probe(row with { Mark = Normal with { ResponseHighlight = true } }));
        Assert.True(Assert.Single(animation.Slots).WasClaimed);
        Assert.Null(Assert.Single(animation.Slots).Tsumogiri);
        Assert.Empty(animation.NewEvents);
        Assert.False(animation.HasHistoryGap);
    }

    private sealed record TestRow(string Direction, string Path, int Kind, bool RedFive, int Order,
        PublicTileVisualMark Mark, bool Sideways, bool Stable = true, bool Decoded = true);
    private static TestRow Row(string direction, int id, int kind, int order = 1,
        PublicTileVisualMark? mark = null, bool sideways = false) => new(direction, $"Emj/{id}/4", kind, false, order, mark ?? Normal, sideways);
    private static PublicRiverHistoryObservation Observe(PublicRiverEventTracker t, int s, AddonProbe p)
        => t.Observe(s, Epoch.AddMilliseconds(s * 100), p, Context, "r1");
    private static AddonProbe Probe(params TestRow[] rows)
    {
        var raw = rows.Select(r =>
        {
            uint icon = r.RedFive ? (uint)(76035 + (r.Kind - 4) / 9) : 76001u + (uint)r.Kind;
            uint hash = LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex");
            return new PublicTableFaceCandidate("river-" + r.Direction, r.Direction, r.Path, r.Path[..^2], 1023,
                0, 0, 40, 52, 0, false, r.Decoded ? icon : null, r.Decoded ? hash : null,
                r.Decoded ? PublicTableImageReader.VerifiedResourceCode : "PUBLIC_OCCLUDED_OR_TRANSITION",
                new(1, r.Order, r.Order, r.Sideways), r.Mark);
        }).ToArray();
        var decoded = rows.Where(r => r.Decoded).Select(r => new PublicTableTile("river-" + r.Direction, r.Direction,
            r.Path, r.Path[..^2], 0, 0, 40, 52, 0, false, r.Kind, r.RedFive, "公开牌", r.Stable,
            new(1, r.Order, r.Order, r.Sideways), r.Mark)).ToImmutableArray();
        var areas = new[] { "bottom", "right", "top", "left" }.Select(d => new PublicTableAreaStatus("river-" + d,
            "PUBLIC_AREA_VISIBLE_CANDIDATE", true, true, true, rows.All(r => r.Direction != d), rows.Count(r => r.Direction == d), 0, true)).ToImmutableArray();
        return new("Emj", true, true, true, 1, [], null, PublicTableFaces: raw,
            PublicTableReading: new("PUBLIC_IMAGES_STABLE_CANDIDATE", "fixture", true, decoded, [], [], areas));
    }
}
