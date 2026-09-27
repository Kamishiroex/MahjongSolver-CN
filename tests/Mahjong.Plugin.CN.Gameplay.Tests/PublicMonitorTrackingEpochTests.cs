using System.Collections.Immutable;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Journaling;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Synthetic guarded UI resources exercise the actual monitor-to-tracker pipeline.</summary>
public sealed class PublicMonitorTrackingEpochTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
    private static readonly PublicObservationContext Context = PublicObservationAssembler.CreateAuditedContext(
        new(RuntimeIdentity.TargetGame, 15, "fixture", RuntimeIdentity.TargetDalamud, RuntimeIdentity.TargetStructs,
            "ChineseSimplified", "fixture", null), LowerHandProfile.EmjUldSha256)!;
    private static readonly string[] Directions = ["bottom", "right", "top", "left"];
    private static readonly PublicTileVisualMark Normal = new(0, 0, 0, 100, 100, 100, "normal");

    private static PublicTableFaceCandidate Face(string path, int kind, int order = 1, bool decoded = true, bool called = false)
    {
        Assert.True(PublicTableImageReader.TryRoute(path, out var route));
        uint icon = 76001u + (uint)kind;
        return new(route.Area, route.Direction, path, route.RootPath, route.DisplayType, 100 + order * 40, 200,
            40, 52, 0, false, decoded ? icon : null,
            decoded ? LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex") : null,
            decoded ? PublicTableImageReader.VerifiedResourceCode : "PUBLIC_OCCLUDED_OR_TRANSITION",
            route.Area.StartsWith("river-", StringComparison.Ordinal) ? new(1, order, order, false) : null,
            called ? new(0, -75, -75, 100, 100, 100, "red-tinted") : Normal);
    }

    private sealed class Capture
    {
        internal PublicMonitorSession Monitor { get; } = new();
        private readonly LowerHandTracker lower = new();
        internal Capture() => Monitor.Start(Context);
        internal void Observe(int sample, PublicTableFaceCandidate[] faces, int handCount = 13, string? error = null, int honba = 0,
            int? timeMilliseconds = null, bool? ownRiichi = null, PublicActionMenuCandidate? menu = null)
        {
            var statuses = new List<PublicStatusCandidate>
            {
                new("Emj/23", "TopBlackStickCount", null, honba, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
                new("Emj/105/2/2", "CenterCounterLeft", null, 4, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
                new("Emj/105/3/2", "CenterCounterRight", null, 0, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
            };
            for (int player = 0; player < 4; player++)
            {
                int panel = 38 + player * 2;
                statuses.Add(new($"Emj/{panel}/{(player == 0 ? 9 : 10)}", "SeatWind", Directions[player], player,
                    new[] { "东", "南", "西", "北" }[player], "PUBLIC_STATUS_VALUE_CANDIDATE"));
                statuses.Add(new($"Emj/{panel}/{(player == 0 ? 12 : 13)}/2", "PlayerScore", Directions[player], 25000,
                    null, "PUBLIC_STATUS_VALUE_CANDIDATE"));
            }
            var hand = Enumerable.Range(0, handCount).Select(index =>
            {
                uint icon = 76001u + (uint)index;
                return new HandFaceCandidate(index == 13 ? "Emj/135/9/4" : $"Emj/{1340001 + index}/9/4",
                    100 + index * 45, 600, 40, 52, icon, LowerHandProfile.VerifiedIconStatus,
                    LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex"));
            }).ToArray();
            var areas = Directions.SelectMany(direction => new[] { "river-", "meld-" }.Select(prefix =>
            {
                string name = prefix + direction;
                var rows = faces.Where(x => x.Area == name).ToArray();
                return new PublicTableAreaStatus(name, "PUBLIC_AREA_VISIBLE_CANDIDATE", true, true, true,
                    rows.Length == 0, rows.Length, 0, VisibleGroups: prefix == "meld-" ? rows.Select(x => x.GroupPath).Distinct().Count() : 0);
            })).ToArray();
            var addon = new AddonProbe("Emj", true, true, true, 109, [], error, LowerHandFaces: hand,
                PublicTableFaces: faces, PublicTableAreas: areas, PublicStatusCandidates: statuses,
                RoundTitleResource: new("Emj/19", "ROUND_TITLE_RESOURCE_CANDIDATE", 0, 1, 0, 0, 0, 640, 80, 0, 1, 121451, 0xBBC15A27));
            addon = addon with { LowerHandReading = lower.Observe(sample, addon), PublicActionMenu = menu,
                PublicRiichiCandidates = ownRiichi is null ? null : Directions.Select((d,i)=>new PublicRiichiCandidate(d,
                    $"Emj/{100+i}/2",i==0 && ownRiichi.Value,i==0 && ownRiichi.Value
                        ? "PUBLIC_RIICHI_STICK_CANDIDATE" : "PUBLIC_RIICHI_STICK_HIDDEN_OWNER")).ToArray() };
            Monitor.Observe(new(sample, Start.AddMilliseconds(timeMilliseconds ?? sample * 100), "synthetic", "synthetic", [addon]));
        }
    }

    [Fact]
    public void Actual_monitor_observes_ron_then_progress_and_resets_pass_history_on_restart()
    {
        var capture=new Capture();var discard=Face("Emj/127/4",20);
        var ron=PublicFuritenTrackerTests.Addon(true).PublicActionMenu;
        var closed=PublicFuritenTrackerTests.Addon(false).PublicActionMenu;
        // Actual reader menu rows use node 2 / cloned 21001, not synthetic IDs.
        ron=ron! with {Rows=[new("Emj/104/3/2",0,"Ron",true,"ACTION_MENU_LABEL_CANDIDATE"),
            new("Emj/104/3/21001",40,"Pass",true,"ACTION_MENU_LABEL_CANDIDATE")]};
        for(int n=1;n<=3;n++)capture.Observe(n,[discard],ownRiichi:false,menu:closed);
        capture.Observe(4,[discard],ownRiichi:false,menu:ron);
        Assert.True(capture.Monitor.Current!.OurTemporaryFuriten.IsConfirmed);
        Assert.False(capture.Monitor.Current.OurTemporaryFuriten.Value);
        for(int n=5;n<=8;n++)capture.Observe(n,[discard],ownRiichi:false,menu:closed);
        Assert.False(capture.Monitor.Current!.OurTemporaryFuriten.IsConfirmed);
        var continued=Face("Emj/124/4",21);
        for(int n=9;n<=12;n++)capture.Observe(n,[discard,continued],ownRiichi:false,menu:closed);
        Assert.True(capture.Monitor.Current!.OurTemporaryFuriten.IsConfirmed);
        Assert.True(capture.Monitor.Current.OurTemporaryFuriten.Value);
        capture.Monitor.Stop("restart");capture.Monitor.Start(Context);
        for(int n=13;n<=16;n++)capture.Observe(n,[discard,continued],ownRiichi:false,menu:closed);
        Assert.False(capture.Monitor.Current!.OurTemporaryFuriten.IsConfirmed);
    }

    [Fact]
    public void Actual_monitor_chain_adds_current_riichi_window_and_clears_it_on_restart()
    {
        var capture=new Capture();
        for(int n=1;n<=4;n++)capture.Observe(n,[],ownRiichi:false);
        var declaration=Face("Emj/117/4",3) with {RiverPosition=new(1,1,1,true)};
        for(int n=5;n<=8;n++)capture.Observe(n,[declaration],ownRiichi:true);
        Assert.False(capture.Monitor.Current!.Players[0].RiichiEstablished.IsConfirmed);
        var continuation=Face("Emj/121/4",5);
        for(int n=9;n<=12;n++)capture.Observe(n,[declaration,continuation],ownRiichi:true);
        var p=capture.Monitor.Current!.Players[0];
        Assert.True(p.RiichiEstablished.IsConfirmed);Assert.True(p.RiichiEstablished.Value);
        Assert.True(p.Ippatsu.IsConfirmed);Assert.True(p.Ippatsu.Value);
        Assert.True(p.DoubleRiichi.IsConfirmed);Assert.True(p.DoubleRiichi.Value);
        Assert.Equal(p.DoubleRiichi,capture.Monitor.Current.OurDoubleRiichi);
        capture.Monitor.Stop("test restart");capture.Monitor.Start(Context);
        for(int n=13;n<=16;n++)capture.Observe(n,[declaration,continuation],ownRiichi:true);
        p=capture.Monitor.Current!.Players[0];
        Assert.False(p.RiichiEstablished.IsConfirmed);Assert.False(p.Ippatsu.IsConfirmed);
        Assert.False(p.DoubleRiichi.IsConfirmed);
    }

    [Fact]
    public void Actual_assembler_keeps_closed_kan_faces_partial_but_tracker_preserves_double_riichi()
    {
        var capture=new Capture();
        for(int n=1;n<=4;n++)capture.Observe(n,[],ownRiichi:false);
        var declaration=Face("Emj/117/4",3) with {RiverPosition=new(1,1,1,true)};
        for(int n=5;n<=8;n++)capture.Observe(n,[declaration],ownRiichi:true);
        var continuation=Face("Emj/121/4",5);
        for(int n=9;n<=12;n++)capture.Observe(n,[declaration,continuation],ownRiichi:true);
        var kan=new[] {Face("Emj/114/2/4",20) with {Code=PublicTableImageReader.VerifiedBackCode,IconId=null,FacePathHash=null},
            Face("Emj/114/3/4",20) with {Code=PublicTableImageReader.VerifiedBackCode,IconId=null,FacePathHash=null},
            Face("Emj/114/4/4",20),Face("Emj/114/5/4",20)};
        for(int n=13;n<=16;n++)capture.Observe(n,[declaration,continuation,..kan],ownRiichi:true);
        var snapshot=capture.Monitor.Current!;var meld=snapshot.Players[2].MeldImages.Value!;
        Assert.False(meld.AllVisibleSlotsDecoded);Assert.False(meld.Stable);
        Assert.Equal("PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN",Assert.Single(meld.Groups).ShapeCode);
        Assert.Equal(2,meld.Tiles.Length);
        Assert.True(snapshot.OurDoubleRiichi.IsConfirmed);Assert.True(snapshot.OurDoubleRiichi.Value);
        Assert.True(snapshot.Players[0].Ippatsu.IsConfirmed);Assert.False(snapshot.Players[0].Ippatsu.Value);
        capture.Monitor.Stop("restart");capture.Monitor.Start(Context);
        for(int n=17;n<=20;n++)capture.Observe(n,[declaration,continuation,..kan],ownRiichi:true);
        Assert.False(capture.Monitor.Current!.OurDoubleRiichi.IsConfirmed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Midhand_and_startup_read_failure_establish_partial_baseline_then_track_next_unique_discard(bool startupError)
    {
        var capture = new Capture();
        var old = Face("Emj/118/4", 4);
        int start = startupError ? 2 : 1;
        if (startupError) capture.Observe(1, [old], error: "BUDGET_EXCEEDED");
        capture.Observe(start, [old]);
        Assert.Null(capture.Monitor.TrackingEpochToken);
        capture.Observe(start + 1, [old]);
        Assert.NotNull(capture.Monitor.TrackingEpochToken);
        Assert.Empty(capture.Monitor.RiverProgress!.NewEvents);
        Assert.False(capture.Monitor.RiverProgress.InitializedFromEmpty);
        Assert.True(capture.Monitor.RiverProgress.HasHistoryGap);
        var added = Face("Emj/127/4", 12);
        capture.Observe(start + 2, [old, added]);
        Assert.Empty(capture.Monitor.RiverProgress.NewEvents);
        capture.Observe(start + 3, [old, added]);
        var discard = Assert.Single(capture.Monitor.RiverProgress.NewEvents);
        Assert.Equal("DiscardObserved", discard.Kind); Assert.Equal("left", discard.ScreenDirection); Assert.Equal(12, discard.Tile.Kind34);
        Assert.Null(discard.DiscardOrdinal);
        Assert.False(capture.Monitor.Current!.RoundId.IsConfirmed);
        Assert.Equal(SynchronizationState.HistoryGap, capture.Monitor.Current.Synchronization);
        Assert.False(capture.Monitor.RiverProgress.HistoryComplete);
        Assert.Equal(capture.Monitor.TrackingEpochToken, capture.Monitor.RiverProgress.RoundToken);
    }

    [Fact]
    public void Old_raw_slot_that_decodes_later_is_baseline_inventory_and_never_a_new_discard()
    {
        var capture = new Capture();
        var unknown = Face("Emj/118/4", 4, decoded: false);
        capture.Observe(1, [unknown]); capture.Observe(2, [unknown]);
        Assert.NotNull(capture.Monitor.TrackingEpochToken);
        Assert.Empty(capture.Monitor.RiverProgress!.NewEvents);
        Assert.Equal(1, capture.Monitor.RiverProgress.UnresolvedSlotCount);
        var decoded = Face("Emj/118/4", 4);
        capture.Observe(3, [decoded]); capture.Observe(4, [decoded]);
        Assert.Empty(capture.Monitor.RiverProgress.NewEvents);
        Assert.Equal(4, Assert.Single(capture.Monitor.RiverProgress.Slots).Tile.Kind34);
        Assert.Null(capture.Monitor.RiverProgress.Slots[0].DiscardOrdinal);
    }

    [Fact]
    public void Partial_empty_baseline_retains_gap_and_real_events_without_round_wide_ordinals()
    {
        var capture = new Capture();
        capture.Observe(1, []); capture.Observe(2, []);
        Assert.True(capture.Monitor.RiverProgress!.InitializedFromEmpty);
        Assert.True(capture.Monitor.RiverProgress.HasHistoryGap);
        Assert.False(capture.Monitor.RiverProgress.HasContiguousDiscardPrefix);
        var face = Face("Emj/127/4", 12);
        capture.Observe(3, [face]); capture.Observe(4, [face]);
        var discard = Assert.Single(capture.Monitor.RiverProgress.NewEvents);
        Assert.Equal("DiscardObserved", discard.Kind); Assert.Equal(12, discard.Tile.Kind34);
        Assert.Null(discard.DiscardOrdinal);
        Assert.Null(Assert.Single(capture.Monitor.RiverProgress.Slots).DiscardOrdinal);
        Assert.Contains(capture.Monitor.RiverProgress.Issues, issue => issue.Code == "RIVER_PARTIAL_OBSERVATION_EPOCH");
        Assert.True(capture.Monitor.RiverProgress.HasHistoryGap);
        Assert.False(capture.Monitor.RiverProgress.HasContiguousDiscardPrefix);
        Assert.False(capture.Monitor.Current!.RoundId.IsConfirmed);
    }

    [Fact]
    public async Task Journal_records_each_epoch_change_once_as_small_partial_history_marker()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mjcn-epoch-journal-" + Guid.NewGuid().ToString("N"));
        var journal = new GameJournal(directory);
        try
        {
            var capture = new Capture();
            var plugin = (Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(Plugin).GetField("journal", flags)!.SetValue(plugin, journal);
            typeof(Plugin).GetField("journalActive", flags)!.SetValue(plugin, true);
            typeof(Plugin).GetField("journalPublicMonitor", flags)!.SetValue(plugin, capture.Monitor);
            var record = typeof(Plugin).GetMethod("RecordPublicTrackingEpoch", flags)!;
            void LogTwice() { record.Invoke(plugin, []); record.Invoke(plugin, []); }
            LogTwice();
            capture.Observe(1, []); LogTwice();
            capture.Observe(2, []); LogTwice();
            string? first = capture.Monitor.TrackingEpochToken;
            capture.Observe(3, [], error: "READ_ERROR"); LogTwice();
            capture.Observe(4, []); LogTwice();
            capture.Observe(5, []); LogTwice();
            string? second = capture.Monitor.TrackingEpochToken;
            Assert.NotNull(first); Assert.NotNull(second); Assert.NotEqual(first, second);
            capture.Monitor.Stop("scene left"); LogTwice();
            await journal.CompleteAsync(); Assert.Null(journal.Fault);
            var rows = await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "events.jsonl"));
            Assert.True(rows.IntegrityPassed); Assert.Equal(4, rows.Lines.Length);
            Assert.All(rows.Lines, row =>
            {
                Assert.Equal("public_tracking_epoch", row.Entry.Kind);
                Assert.False(row.Entry.Data.GetProperty("HistoryComplete").GetBoolean());
                Assert.False(row.Entry.Data.GetProperty("RoundIdConfirmed").GetBoolean());
                Assert.False(row.Entry.Data.TryGetProperty("Snapshot", out _));
                Assert.True(row.Entry.Data.GetRawText().Length < 1024);
            });
            Assert.Equal(first, rows.Lines[0].Entry.Data.GetProperty("Token").GetString());
            Assert.Equal(first, rows.Lines[1].Entry.Data.GetProperty("PreviousToken").GetString());
            Assert.Equal(second, rows.Lines[2].Entry.Data.GetProperty("Token").GetString());
            Assert.Equal(second, rows.Lines[3].Entry.Data.GetProperty("PreviousToken").GetString());
        }
        finally
        {
            await journal.CompleteAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Partial_epoch_enables_own_draw_transition_without_confirmed_opening()
    {
        var capture = new Capture(); var old = Face("Emj/118/4", 4);
        capture.Observe(1, [old]); capture.Observe(2, [old]);
        string? epoch = capture.Monitor.TrackingEpochToken;
        capture.Observe(3, [old], handCount: 14);
        Assert.Equal(epoch, capture.Monitor.TrackingEpochToken);
        capture.Observe(4, [old], handCount: 14);
        var draw = Assert.IsType<PublicOwnHandDelta>(capture.Monitor.OwnHandProgress!.Transition);
        Assert.Equal("DrawCandidate", draw.Kind); Assert.Equal(13, draw.Tile.Kind34);
        Assert.False(draw.HistoryComplete); Assert.False(capture.Monitor.Current!.RoundId.IsConfirmed);
    }

    [Fact]
    public void Partial_epoch_enables_independently_confirmed_public_pon_without_inventing_opening()
    {
        var capture = new Capture(); var source = Face("Emj/118/4", 0);
        capture.Observe(1, [source]); capture.Observe(2, [source]);
        var changed = new[] { Face("Emj/118/4", 0, called: true), Face("Emj/113/2/4", 0),
            Face("Emj/113/3/4", 0), Face("Emj/113/4/4", 0) };
        capture.Observe(3, changed); capture.Observe(4, changed);
        var call = Assert.Single(capture.Monitor.CallProgress!.Events);
        Assert.Equal("pon", call.Kind); Assert.Equal("right", call.CallerDirection); Assert.Equal("bottom", call.FromDirection);
        Assert.Equal(capture.Monitor.TrackingEpochToken, call.RoundToken);
        Assert.False(call.HistoryComplete); Assert.False(capture.Monitor.Current!.RoundId.IsConfirmed);
    }

    [Theory]
    [InlineData("read")] [InlineData("identity")] [InlineData("gap")]
    public void Recovered_epoch_rebaselines_all_existing_raw_slots_and_never_replays_old_events(string boundary)
    {
        var capture = new Capture(); var first = Face("Emj/118/4", 4); var second = Face("Emj/127/4", 12);
        capture.Observe(1, [first]); capture.Observe(2, [first]);
        var before = capture.Monitor.TrackingEpochToken;
        capture.Observe(3, [first, second], error: boundary == "read" ? "READ_ERROR" : null,
            honba: boundary == "identity" ? 1 : 0, timeMilliseconds: boundary == "gap" ? 3000 : 300);
        Assert.Null(capture.Monitor.TrackingEpochToken);
        capture.Observe(4, [first, second], honba: boundary == "identity" ? 1 : 0, timeMilliseconds: 3100);
        capture.Observe(5, [first, second], honba: boundary == "identity" ? 1 : 0, timeMilliseconds: 3200);
        Assert.NotNull(capture.Monitor.TrackingEpochToken); Assert.NotEqual(before, capture.Monitor.TrackingEpochToken);
        Assert.Empty(capture.Monitor.RiverProgress!.NewEvents);
        Assert.All(capture.Monitor.RiverProgress.Slots, slot => Assert.Null(slot.DiscardOrdinal));
    }

    [Fact]
    public void Scene_exit_immediately_clears_tracking_epoch()
    {
        var capture = new Capture(); var first = Face("Emj/118/4", 4);
        capture.Observe(1, [first]); capture.Observe(2, [first]);
        Assert.NotNull(capture.Monitor.TrackingEpochToken);
        capture.Monitor.Observe(new(3, Start.AddMilliseconds(300), "synthetic", "exit", []));
        Assert.Null(capture.Monitor.TrackingEpochToken); Assert.Null(capture.Monitor.RiverProgress);
    }
}
