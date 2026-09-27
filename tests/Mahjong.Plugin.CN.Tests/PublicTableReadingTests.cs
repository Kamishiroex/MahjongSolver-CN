using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed class PublicTableReadingTests
{
    private static PublicTableFaceCandidate Face(string path = "Emj/118/4", uint icon = 76001, string area = "river-bottom") =>
        new(area, area[6..], path, path[..path.LastIndexOf('/')], 1021, 100, 200, 31.2f, 40.56f, 0, false,
            icon, LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex"), PublicTableImageReader.VerifiedResourceCode);
    private static AddonProbe Probe(params PublicTableFaceCandidate[] faces) =>
        new("Emj", true, true, true, 109, [], null, PublicTableFaces: faces);
    private static PublicActionMenuCandidate ResponseMenu(string action = "Chi") => new(
        "ACTION_MENU_VISIBLE_CANDIDATE", true, true,
        [new("Emj/104/3/2", 100, action, true, "ACTION_MENU_LABEL_CANDIDATE", 0),
         new("Emj/104/3/21001", 140, "Pass", true, "ACTION_MENU_LABEL_CANDIDATE", 1)],
        new(2, 2, 0, 5, false, false, true));

    [Fact]
    public void Two_observations_decode_red_and_normal_as_candidates_without_inventing_history()
    {
        var tracker = new PublicTableTracker();
        var input = Probe(Face(icon: 76005), Face("Emj/1180001/4", 76035));
        Assert.All(tracker.Observe(1, input).Tiles, x => Assert.False(x.Stable));
        var stable = tracker.Observe(2, input);
        Assert.True(stable.Stable); Assert.False(stable.HistoryComplete); Assert.False(stable.CurrentDisplayComplete);
        Assert.Equal(2, stable.Tiles.Length); Assert.All(stable.Tiles, x => Assert.Equal(4, x.Kind34));
        Assert.Single(stable.Tiles, x => x.RedFive); Assert.All(stable.Tiles, x => Assert.Equal("Candidate", x.Quality));
    }

    [Fact]
    public void Changed_removed_and_reused_slots_do_not_preserve_old_tiles_or_promote_clone_order_to_time()
    {
        var t = new PublicTableTracker();
        var old = Probe(Face("Emj/1180020/4"), Face("Emj/118/4", 76002));
        t.Observe(1, old); Assert.True(t.Observe(2, old).Stable);
        var next = t.Observe(3, Probe(Face("Emj/118/4", 76003)));
        var tile = Assert.Single(next.Tiles); Assert.Equal(2, tile.Kind34); Assert.False(tile.Stable);
        Assert.False(next.HistoryComplete);
    }

    [Fact]
    public void A_bad_public_region_does_not_erase_independent_visible_tiles_but_no_complete_claim_is_made()
    {
        var t = new PublicTableTracker();
        var input = Probe(Face(), Face("Emj/121/4", 76002, "river-right") with
        { IconId = null, FacePathHash = null, Code = "PUBLIC_SHELL_NOT_FRONT" });
        t.Observe(1, input);
        var next = t.Observe(2, input);
        Assert.True(Assert.Single(next.Tiles).Stable); Assert.False(next.Stable);
        Assert.Equal("PUBLIC_SHELL_NOT_FRONT", Assert.Single(next.Rejections).Code);
    }

    [Theory]
    [InlineData("duplicate-sequence")]
    [InlineData("scene-exit")]
    [InlineData("error")]
    [InlineData("EmjL")]
    [InlineData("resource")]
    [InlineData("duplicate-path")]
    public void Contradiction_and_scene_changes_clear_stability(string variation)
    {
        var t = new PublicTableTracker(); var good = Probe(Face());
        t.Observe(1, good); t.Observe(2, good);
        var bad = variation switch
        {
            "scene-exit" => good with { Present = false }, "error" => good with { Error = "READ_FAILED" },
            "EmjL" => good with { Name = "EmjL" }, "resource" => Probe(Face() with { FacePathHash = 123 }),
            "duplicate-path" => Probe(Face(), Face()), _ => good,
        };
        Assert.Empty(t.Observe(variation == "duplicate-sequence" ? 2 : 3, bad).Tiles);
        Assert.False(t.Observe(4, good).Stable); Assert.True(t.Observe(5, good).Stable);
    }

    [Fact]
    public void Display_inventory_does_not_apply_a_physical_four_copy_rule_to_repeated_public_images()
    {
        var t = new PublicTableTracker();
        var faces = Enumerable.Range(0, 5).Select(i => Face($"Emj/{1180001 + i}/4", i == 4 ? 76035u : 76005u)).ToArray();
        var result = t.Observe(1, Probe(faces));
        Assert.Equal(5, result.Tiles.Length); Assert.Empty(result.Rejections);
        Assert.False(result.CurrentDisplayComplete); Assert.False(result.HistoryComplete);
    }

    [Fact]
    public void Claimed_river_image_and_meld_image_are_not_double_counted_as_a_physical_conflict()
    {
        var faces = new[] { Face(icon: 76005), Face("Emj/1180001/4", 76005),
            Face("Emj/112/2/9/4", 76005, "meld-bottom"), Face("Emj/112/3/9/4", 76005, "meld-bottom"),
            Face("Emj/112/4/9/4", 76035, "meld-bottom") };
        var tracker = new PublicTableTracker(); tracker.Observe(1, Probe(faces));
        var reading = tracker.Observe(2, Probe(faces));
        Assert.Equal(5, reading.Tiles.Length); Assert.Empty(reading.Rejections); Assert.True(reading.Stable);
    }

    [Fact]
    public void Position_and_visual_style_are_preserved_but_not_promoted_to_event_semantics()
    {
        var mark = new PublicTileVisualMark(0, -75, -75, 100, 100, 100, "red-tinted");
        var face = Face(icon: 76005) with { RiverPosition = new(1, 3, 3, true), VisualMark = mark };
        var t = new PublicTableTracker(); t.Observe(1, Probe(face));
        var tile = Assert.Single(t.Observe(2, Probe(face)).Tiles);
        Assert.Equal(face.RiverPosition, tile.RiverPosition); Assert.Equal(mark, tile.VisualMark);
        Assert.False(tile.RedFive); Assert.False(tile.VisualMark!.CalledTileMeaningVerified);
        Assert.False(tile.VisualMark.TsumogiriMeaningVerified);
    }

    [Fact]
    public void Confirmed_response_highlight_including_zero_phase_remains_stable_for_600_frames()
    {
        var tracker = new PublicTableTracker();
        var area = new PublicTableAreaStatus("river-top", "PUBLIC_AREA_VISIBLE_CANDIDATE", true, true, true, false, 1, 0);
        var face = Face("Emj/124/4", 76024, "river-top") with
        {
            RiverPosition = new(1, 1, 1, false),
            VisualMark = new(84, 84, 84, 100, 100, 100, "unclassified"),
        };
        AddonProbe Sample(short brightness) => Probe(face with
        { VisualMark = new(brightness, brightness, brightness, 100, 100, 100, brightness == 0 ? "normal" : "unclassified") }) with
        { PublicTableAreas = [area], PublicActionMenu = ResponseMenu() };

        Assert.False(tracker.Observe(1, Sample(0)).Stable);
        Assert.True(Assert.Single(tracker.Observe(2, Sample(23)).Tiles).VisualMark!.ResponseHighlight);
        for (int sequence = 3; sequence <= 600; sequence++)
        {
            short brightness = (short)((sequence % 4) switch { 0 => 0, 1 => 23, 2 => 45, _ => 0 });
            var reading = tracker.Observe(sequence, Sample(brightness));
            Assert.True(reading.Stable);
            Assert.True(Assert.Single(reading.Areas).Stable);
            var tile = Assert.Single(reading.Tiles);
            Assert.Equal(23, tile.Kind34);
            Assert.Equal(brightness, tile.VisualMark!.AddRed);
            Assert.Equal(brightness == 0 ? "normal" : "unclassified", tile.VisualMark.Style);
            Assert.True(tile.VisualMark.ResponseHighlight);
            Assert.False(tile.VisualMark.CalledTileMeaningVerified);
            Assert.False(tile.VisualMark.TsumogiriMeaningVerified);
        }
    }

    [Theory]
    [InlineData(-75, -75, -75, "darkened", 100)]
    [InlineData(0, -75, -75, "red-tinted", 100)]
    [InlineData(25, 24, 25, "unclassified", 100)]
    [InlineData(25, 25, 25, "unclassified", 99)]
    public void Genuine_tint_or_multiplier_change_ends_highlight_and_requires_new_confirmation(
        short r, short g, short b, string style, byte multiply)
    {
        var tracker = new PublicTableTracker();
        AddonProbe Input(PublicTileVisualMark mark) => Probe(Face() with { VisualMark = mark }) with { PublicActionMenu = ResponseMenu() };
        var positive = new PublicTileVisualMark(23, 23, 23, 100, 100, 100, "unclassified");
        tracker.Observe(1, Input(positive)); tracker.Observe(2, Input(positive));
        Assert.True(tracker.Observe(3, Input(positive)).Stable);
        var changed = Input(new(r, g, b, multiply, 100, 100, style));
        var reading = tracker.Observe(4, changed);
        Assert.False(reading.Stable);
        Assert.False(Assert.Single(reading.Tiles).VisualMark!.ResponseHighlight);
        Assert.True(tracker.Observe(5, changed).Stable);
        Assert.False(Assert.Single(tracker.Observe(6, Input(positive)).Tiles).VisualMark!.ResponseHighlight);
        Assert.True(Assert.Single(tracker.Observe(7, Input(positive)).Tiles).VisualMark!.ResponseHighlight);
    }

    [Theory]
    [InlineData("menu-closed")]
    [InlineData("menu-changed")]
    [InlineData("geometry")]
    [InlineData("resource")]
    [InlineData("clear")]
    [InlineData("scene-exit")]
    public void Response_highlight_is_not_reused_across_menu_resource_geometry_or_reader_boundaries(string change)
    {
        var tracker = new PublicTableTracker();
        var face = Face() with { VisualMark = new(23, 23, 23, 100, 100, 100, "unclassified") };
        var input = Probe(face) with { PublicActionMenu = ResponseMenu() };
        tracker.Observe(1, input); tracker.Observe(2, input); tracker.Observe(3, input);
        var next = change switch
        {
            "menu-closed" => input with { PublicActionMenu = ResponseMenu() with { Visible = false } },
            "menu-changed" => input with { PublicActionMenu = ResponseMenu("Pon") },
            "geometry" => input with { PublicTableFaces = [face with { X = face.X + 1 }] },
            "resource" => input with { PublicTableFaces = [Face(icon: 76002) with { VisualMark = face.VisualMark }] },
            _ => input,
        };
        if (change == "clear") tracker.Clear();
        if (change == "scene-exit") tracker.Observe(4, input with { Present = false });
        var reading = tracker.Observe(5, next);
        Assert.False(Assert.Single(reading.Tiles).VisualMark!.ResponseHighlight);
        Assert.False(reading.Stable);
    }

    [Theory]
    [InlineData("Tsumo")]
    [InlineData("Riichi")]
    [InlineData("Pass")]
    public void Other_menus_do_not_authorize_brightness_normalization(string action)
    {
        var tracker = new PublicTableTracker();
        AddonProbe Input(short value) => Probe(Face() with { VisualMark = new(value, value, value, 100, 100, 100, "unclassified")
            { ResponseHighlight = true } }) with { PublicActionMenu = ResponseMenu(action) };
        tracker.Observe(1, Input(23));
        var reading = tracker.Observe(2, Input(45));
        Assert.False(reading.Stable);
        Assert.False(Assert.Single(reading.Tiles).VisualMark!.ResponseHighlight);
    }

    [Fact]
    public void Pulse_tag_is_per_slot_and_does_not_hide_a_new_tile_in_the_same_river()
    {
        var tracker = new PublicTableTracker();
        var ordinary = Face() with { VisualMark = new(0, 0, 0, 100, 100, 100, "normal") };
        var flashing = Face("Emj/1180001/4", 76002) with { VisualMark = new(23, 23, 23, 100, 100, 100, "unclassified") };
        var input = Probe(ordinary, flashing) with { PublicActionMenu = ResponseMenu() };
        tracker.Observe(1, input); tracker.Observe(2, input);
        var stable = tracker.Observe(3, input);
        Assert.True(stable.Stable);
        Assert.False(stable.Tiles.Single(t => t.SlotPath == ordinary.SlotPath).VisualMark!.ResponseHighlight);
        Assert.True(stable.Tiles.Single(t => t.SlotPath == flashing.SlotPath).VisualMark!.ResponseHighlight);
        var added = input with { PublicTableFaces = [ordinary, flashing, Face("Emj/1180002/4", 76003)] };
        var changed = tracker.Observe(4, added);
        Assert.Equal(3, changed.Tiles.Length);
        Assert.False(changed.Stable);
        Assert.True(tracker.Observe(5, added).Stable);
    }

    [Fact]
    public void River_semantic_style_changes_still_require_two_samples()
    {
        var tracker = new PublicTableTracker();
        var area = new PublicTableAreaStatus("river-bottom", "PUBLIC_AREA_VISIBLE_CANDIDATE", true, true, true, false, 1, 0);
        PublicTileVisualMark[] marks =
        [
            new(0, 0, 0, 100, 100, 100, "normal"),
            new(-75, -75, -75, 100, 100, 100, "darkened"),
            new(-75, -150, -150, 100, 100, 100, "darkened-red-tinted"),
            new(0, -75, -75, 100, 100, 100, "red-tinted"),
            new(84, 84, 84, 100, 100, 100, "unclassified"),
            new(0, 0, 0, 100, 100, 100, "normal"),
        ];
        long sequence = 0;
        foreach (var mark in marks)
        {
            var input = Probe(Face() with { VisualMark = mark }) with { PublicTableAreas = [area] };
            var first = tracker.Observe(++sequence, input);
            Assert.False(first.Stable);
            Assert.False(Assert.Single(first.Areas).Stable);
            var confirmed = tracker.Observe(++sequence, input);
            Assert.True(confirmed.Stable);
            Assert.True(Assert.Single(confirmed.Areas).Stable);
            Assert.Equal(mark, Assert.Single(confirmed.Tiles).VisualMark);
        }
    }

    [Theory]
    [InlineData(117, 68, 79, 2, 68, true)]
    [InlineData(118, 68, 45, 2, 68, false)]
    [InlineData(120, 79, 136, 2, 68, true)]
    [InlineData(121, 45, 136, 2, 68, false)]
    [InlineData(123, 136, 101, 2, 68, true)]
    [InlineData(124, 136, 135, 2, 68, false)]
    [InlineData(126, 100, 68, 2, 68, true)]
    [InlineData(126, 101, 102, 2, 102, true)]
    [InlineData(127, 135, 68, 2, 68, false)]
    public void Fixed_river_anchors_correct_sideways_origins_in_all_four_directions(int family,
        float x, float y, int row, float along, bool sideways)
    {
        Assert.True(PublicTableImageReader.TryRoute($"Emj/{family * 10000 + 21}/4", out var route));
        Assert.True(PublicRiverSpatialMapper.TryAnchor(route, x, y, out var anchor));
        Assert.Equal(row, anchor.DisplayRow); Assert.Equal(along, anchor.Along); Assert.Equal(sideways, anchor.IsSideways);
    }

    [Fact]
    public void Geometric_order_ignores_clone_order_and_sideways_width_and_does_not_fill_missing_columns()
    {
        var rows = new[] { Face("Emj/118/4"), Face("Emj/1170008/4"), Face("Emj/1180001/4") };
        var anchors = new Dictionary<string, PublicRiverAnchor>
        {
            [rows[0].SlotPath] = new(2, 0, false), [rows[1].SlotPath] = new(1, 68, true),
            [rows[2].SlotPath] = new(1, 113, false), // 45-wide sideways tile; earlier images need not be present.
        };
        var mapped = PublicRiverSpatialMapper.Assign(rows, anchors);
        Assert.Equal(new PublicRiverPosition(2, 1, 3, false), mapped[0].RiverPosition);
        Assert.Equal(new PublicRiverPosition(1, 1, 1, true), mapped[1].RiverPosition);
        Assert.Equal(new PublicRiverPosition(1, 2, 2, false), mapped[2].RiverPosition);
    }

    [Fact]
    public void Missing_or_ambiguous_geometry_leaves_order_unknown_without_removing_visible_faces()
    {
        var rows = new[] { Face(), Face("Emj/1180001/4") };
        var anchors = new Dictionary<string, PublicRiverAnchor> { [rows[0].SlotPath] = new(1, 0, false) };
        Assert.All(PublicRiverSpatialMapper.Assign(rows, anchors), x => Assert.Null(x.RiverPosition));
        anchors[rows[1].SlotPath] = new(1, 0, false);
        Assert.All(PublicRiverSpatialMapper.Assign(rows, anchors), x => Assert.Null(x.RiverPosition));
        Assert.True(PublicTableImageReader.TryRoute("Emj/118/4", out var route));
        Assert.False(PublicRiverSpatialMapper.TryAnchor(route, 0, 10, out _));
        Assert.False(PublicRiverSpatialMapper.TryAnchor(route, float.NaN, 0, out _));
    }

    [Fact]
    public void Closed_kan_kind_comes_from_two_equal_public_faces_and_two_verified_backs_not_hidden_icons()
    {
        var back = Face("Emj/112/2/9/4", area: "meld-bottom") with
        { GroupPath = "Emj/112", Code = PublicTableImageReader.VerifiedBackCode, IconId = null, FacePathHash = null };
        var faces = new[] { back, back with { SlotPath = "Emj/112/3/9/4" },
            Face("Emj/112/4/9/4", 76005, "meld-bottom") with { GroupPath = "Emj/112" },
            Face("Emj/112/5/9/4", 76035, "meld-bottom") with { GroupPath = "Emj/112" } };
        var t = new PublicTableTracker(); t.Observe(1, Probe(faces)); var result = t.Observe(2, Probe(faces));
        var group = Assert.Single(result.MeldGroups);
        Assert.Equal(2, group.VerifiedBackSlots); Assert.Equal(2, group.KnownFaces);
        Assert.Equal("PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN", group.ShapeCode); Assert.Equal(4, group.InferredClosedKanKind34);
        Assert.True(group.Stable); Assert.False(group.AllVisibleSlotsDecoded); Assert.Empty(result.Rejections);
        Assert.Equal(2, result.Tiles.Length); // The two hidden faces are never materialized as observed tiles.
        faces[3] = Face("Emj/112/5/9/4", 76006, "meld-bottom") with { GroupPath = "Emj/112" };
        var bad = Assert.Single(t.Observe(3, Probe(faces)).MeldGroups);
        Assert.Null(bad.InferredClosedKanKind34); Assert.False(bad.Stable);
    }

    [Fact]
    public void Layout_and_ui_stage_change_require_two_new_samples_and_clear_allows_new_session_sequence()
    {
        var t = new PublicTableTracker(); var p = Probe(Face()); t.Observe(10, p); t.Observe(11, p);
        var scaled = Probe(Face() with { X = 140, Width = 43.68f });
        Assert.False(t.Observe(12, scaled).Stable); Assert.True(t.Observe(13, scaled).Stable);
        Assert.False(t.Observe(14, scaled with { AtkValueCount = 110 }).Stable);
        t.Clear(); Assert.False(t.Observe(1, p).Stable); Assert.True(t.Observe(2, p).Stable);
    }

    [Fact]
    public void Meld_group_complete_images_are_distinct_from_kind_or_claimed_from_and_a_new_unknown_fourth_slot_blocks_recovery()
    {
        var t = new PublicTableTracker();
        var faces = Enumerable.Range(2, 3).Select(i => Face($"Emj/112/{i}/9/4", 76001, "meld-bottom") with
        { GroupPath = "Emj/112", ScreenDirection = "bottom" }).ToArray();
        var input = Probe(faces); t.Observe(1, input);
        var stable = Assert.Single(t.Observe(2, input).MeldGroups);
        Assert.Equal(3, stable.VisibleSlots); Assert.True(stable.AllVisibleSlotsDecoded); Assert.True(stable.Stable);
        var unknownFourth = Face("Emj/112/5/9/4", area: "meld-bottom") with
        { GroupPath = "Emj/112", Code = "PUBLIC_FACE_NOT_VISIBLE", IconId = null, FacePathHash = null };
        var changed = Assert.Single(t.Observe(3, Probe([.. faces, unknownFourth])).MeldGroups);
        Assert.Equal(4, changed.VisibleSlots); Assert.Equal(3, changed.KnownFaces);
        Assert.False(changed.AllVisibleSlotsDecoded); Assert.False(changed.Stable);
        Assert.Contains("PUBLIC_FACE_NOT_VISIBLE", changed.ErrorCodes);
    }

    [Fact]
    public void Old_record_with_no_area_evidence_is_not_known_empty_and_explicit_empty_area_requires_two_samples()
    {
        var t = new PublicTableTracker(); Assert.Empty(t.Observe(1, Probe()).Areas);
        var area = new PublicTableAreaStatus("river-bottom", "PUBLIC_AREA_EMPTY_CANDIDATE", true, true, true, true, 0, 0);
        var input = Probe() with { PublicTableAreas = [area] };
        Assert.False(Assert.Single(t.Observe(2, input).Areas).Stable);
        var stable = Assert.Single(t.Observe(3, input).Areas);
        Assert.True(stable.ObservedEmptyCandidate); Assert.True(stable.Stable);
        Assert.False(t.Observe(4, input).HistoryComplete);
        Assert.Empty(t.Observe(5, Probe()).Areas);
        var hidden = input with { PublicTableAreas = [area with { ContainerVisible = false }] };
        Assert.False(Assert.Single(t.Observe(6, hidden).Areas).Stable);
        Assert.False(Assert.Single(t.Observe(7, hidden).Areas).Stable);
    }
}
