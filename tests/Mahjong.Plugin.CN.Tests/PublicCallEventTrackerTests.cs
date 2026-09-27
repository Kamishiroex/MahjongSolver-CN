using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

public sealed class PublicCallEventTrackerTests
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    private static PublicCallTrackingResult Observe(PublicCallEventTracker tracker, long s, AddonProbe? addon,
        string? round = "synthetic-round", double? seconds = null) => tracker.Observe(s,
        Epoch.AddSeconds(seconds ?? s * .2), round, addon, RoundTitleCatalog.GameVersion, LowerHandProfile.EmjUldSha256);
    private static PublicTileVisualMark Mark(string name) => name switch
    {
        "darkened" => new(-75, -75, -75, 100, 100, 100, name),
        "red-tinted" => new(0, -75, -75, 100, 100, 100, name),
        "darkened-red-tinted" => new(-75, -150, -150, 100, 100, 100, name),
        "unclassified" => new(12, 12, 12, 100, 100, 100, name),
        _ => new(0, 0, 0, 100, 100, 100, "normal"),
    };
    private static PublicTableFaceCandidate Face(string path, uint icon = 76001, string mark = "normal")
    {
        Assert.True(PublicTableImageReader.TryRoute(path, out var r));
        return new(r.Area, r.Direction, path, r.RootPath, r.DisplayType, 0, 0, 40, 52, 0, false,
            icon, LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex"),
            PublicTableImageReader.VerifiedResourceCode, VisualMark: Mark(mark));
    }
    private static PublicTableFaceCandidate[] Pon(uint first = 76001, uint middle = 76001, uint called = 76001) =>
        [Face("Emj/113/2/4", first), Face("Emj/113/3/4", middle), Face("Emj/113/4/4", called)];
    private static AddonProbe Probe(params PublicTableFaceCandidate[] faces)
    {
        var p = new AddonProbe("Emj", true, true, true, 109, [], null, PublicTableFaces: faces);
        return p with { PublicTableReading = new PublicTableTracker().Observe(1, p) };
    }

    [Fact]
    public void Real_reduced_fixture_preserves_27_source_matches_and_emits_only_14_complete_calls()
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "public-call-transitions-20260924.json")));
        var events = new List<PublicCallEvent>(); int cases = 0;
        foreach (var c in json.RootElement.GetProperty("SourceCalls").EnumerateArray())
        {
            cases++;
            Assert.True(c.GetProperty("SourceResourceUnchanged").GetBoolean());
            Assert.False(c.GetProperty("SourceGeometryAndResourceUnchanged").GetBoolean());
            var tracker = new PublicCallEventTracker(); var output = new List<PublicCallEvent>();
            foreach (var f in c.GetProperty("Frames").EnumerateArray())
            {
                var faces = f.GetProperty("Faces").EnumerateArray().Select(x => new PublicTableFaceCandidate(
                    x.GetProperty("Area").GetString()!, x.GetProperty("ScreenDirection").GetString()!, x.GetProperty("SlotPath").GetString()!,
                    x.GetProperty("GroupPath").GetString()!, x.GetProperty("Template").GetInt32(),
                    x.GetProperty("GeometryRevision").GetInt32() * 2, 0, 40, 52, 0, false,
                    x.GetProperty("IconId").ValueKind == JsonValueKind.Null ? null : x.GetProperty("IconId").GetUInt32(),
                    x.GetProperty("FacePathHash").ValueKind == JsonValueKind.Null ? null : x.GetProperty("FacePathHash").GetUInt32(),
                    x.GetProperty("Code").GetString()!, VisualMark: x.GetProperty("VisualMark").ValueKind == JsonValueKind.Null ? null :
                        x.GetProperty("VisualMark").Deserialize<PublicTileVisualMark>())).ToArray();
                output.AddRange(Observe(tracker, f.GetProperty("Sample").GetInt64(), Probe(faces),
                    seconds: f.GetProperty("ElapsedSeconds").GetDouble()).Events);
            }
            bool complete = c.GetProperty("ShapeAtCapture").GetString() != "PUBLIC_MELD_SHAPE_UNKNOWN";
            if (!complete) Assert.Empty(output);
            else
            {
                var ev = Assert.Single(output);
                Assert.Equal(c.GetProperty("CallerDirection").GetString(), ev.CallerDirection);
                Assert.Equal(c.GetProperty("SourceDirection").GetString(), ev.FromDirection);
                Assert.Equal(c.GetProperty("CalledSample").GetInt64(), ev.FirstObservedSequence);
                Assert.False(ev.HistoryComplete); events.Add(ev);
            }
        }
        Assert.Equal(27, cases); Assert.Equal(14, events.Count);
        Assert.Equal(7, events.Count(e => e.Kind == "chi")); Assert.Equal(6, events.Count(e => e.Kind == "pon"));
        Assert.Single(events, e => e.Kind == "daiminkan");
        Assert.True(events.Single(e => e.FirstObservedSequence == 9744).ClaimedTile!.Value.RedFive);
        Assert.Single(events.Single(e => e.FirstObservedSequence == 12261).Consumed, t => t.RedFive);
    }

    [Fact]
    public void New_source_tint_and_meld_confirm_once_even_when_source_moves_then_becomes_unreadable()
    {
        var t = new PublicCallEventTracker(); var river = Face("Emj/118/4");
        Observe(t, 1, Probe(river));
        var called = river with { VisualMark = Mark("red-tinted"), X = 17 };
        Assert.Empty(Observe(t, 2, Probe([called, .. Pon()])).Events);
        var ev = Assert.Single(Observe(t, 3, Probe([called, .. Pon()])).Events);
        Assert.Equal("pon", ev.Kind); Assert.Equal("bottom", ev.FromDirection); Assert.Equal(2, ev.Consumed.Length);
        var unreadable = called with { IconId = null, FacePathHash = null, Code = "PUBLIC_OCCLUDED_OR_TRANSITION" };
        Assert.Empty(Observe(t, 4, Probe([unreadable, .. Pon()])).Events);
        Assert.Empty(Observe(t, 5, Probe([called, .. Pon()])).Events);
        Assert.Equal(2, ev.FirstObservedSequence); // Emitted immutable event is never erased by a later snapshot failure.
    }

    [Fact]
    public void Earlier_verified_river_identity_can_match_after_resource_rejection_at_same_geometry()
    {
        var t = new PublicCallEventTracker(); var river = Face("Emj/118/4"); Observe(t, 1, Probe(river));
        var called = river with { VisualMark = Mark("red-tinted"), IconId = null, FacePathHash = null, Code = "PUBLIC_OCCLUDED_OR_TRANSITION" };
        Observe(t, 2, Probe([called, .. Pon()]));
        Assert.Equal("pon", Assert.Single(Observe(t, 3, Probe([called, .. Pon()])).Events).Kind);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("round")]
    [InlineData("scene")]
    [InlineData("gap")]
    [InlineData("duplicate")]
    public void Missing_provenance_or_observation_gap_never_replays_old_calls(string variation)
    {
        var t = new PublicCallEventTracker(); var river = Face("Emj/118/4"); Observe(t, 1, Probe(river));
        var called = Probe([river with { VisualMark = Mark("red-tinted") }, .. Pon()]);
        var result = variation switch
        {
            "profile" => t.Observe(2, Epoch.AddSeconds(.4), "r", called),
            "round" => Observe(t, 2, called, round: null), "scene" => Observe(t, 2, called with { Visible = false }),
            "gap" => Observe(t, 2, called, seconds: 5), _ => Observe(t, 1, called),
        };
        Assert.Empty(result.Events); Assert.NotEmpty(result.Issues);
        Assert.Empty(Observe(t, 10, called, seconds: 6).Events);
        Assert.Empty(Observe(t, 11, called, seconds: 6.2).Events);
    }

    [Fact]
    public void Existing_complete_group_is_baseline_and_same_path_pon_extension_is_kakan_not_new_open_kan()
    {
        var t = new PublicCallEventTracker(); Observe(t, 1, Probe(Pon()));
        Assert.Empty(Observe(t, 2, Probe(Pon())).Events);
        var kan = Probe([.. Pon(), Face("Emj/113/5/4")]);
        Assert.Empty(Observe(t, 3, kan).Events);
        var ev = Assert.Single(Observe(t, 4, kan).Events);
        Assert.Equal("kakan", ev.Kind); Assert.Equal(new PublicCallTile(0, false), ev.AddedTile);
        Assert.Equal(3, ev.Consumed.Length); Assert.Null(ev.FromDirection);
        Assert.Empty(Observe(t, 5, kan).Events);
        Assert.Empty(Observe(t, 6, kan, round: "new-round").Events);
    }

    [Theory]
    [InlineData(76001u, true)]
    [InlineData(76005u, false)]
    public void Public_two_face_concealed_kan_derives_only_non_five_inventory(uint icon, bool emits)
    {
        var t = new PublicCallEventTracker(); Observe(t, 1, Probe());
        var group = new[] { Face("Emj/113/2/4", icon), Face("Emj/113/3/4", icon),
            Face("Emj/113/4/4") with { IconId = null, FacePathHash = null, Code = PublicTableImageReader.VerifiedBackCode },
            Face("Emj/113/5/4") with { IconId = null, FacePathHash = null, Code = PublicTableImageReader.VerifiedBackCode } };
        Observe(t, 2, Probe(group)); var result = Observe(t, 3, Probe(group));
        if (emits) { var ev = Assert.Single(result.Events); Assert.Equal("ankan", ev.Kind); Assert.Equal(4, ev.Consumed.Length); Assert.Contains("DERIVED", ev.Code); }
        else { Assert.Empty(result.Events); Assert.Contains(result.Issues, i => i.Code == "CALL_ANKAN_RED_IDENTITY_UNKNOWN"); }
    }

    [Fact]
    public void Two_matching_called_rivers_do_not_guess_the_source()
    {
        var t = new PublicCallEventTracker(); var a = Face("Emj/118/4"); var b = Face("Emj/127/4");
        Observe(t, 1, Probe(a, b)); a = a with { VisualMark = Mark("red-tinted") }; b = b with { VisualMark = Mark("red-tinted") };
        Observe(t, 2, Probe([a, b, .. Pon()]));
        var result = Observe(t, 3, Probe([a, b, .. Pon()])); Assert.Empty(result.Events);
        Assert.Contains(result.Issues, i => i.Code == "CALL_SOURCE_AMBIGUOUS");
    }

    [Fact]
    public void Same_kind_red_and_normal_claims_are_not_interchangeable()
    {
        var t = new PublicCallEventTracker(); var river = Face("Emj/118/4", 76005); Observe(t, 1, Probe(river));
        var called = Probe([river with { VisualMark = Mark("red-tinted") }, .. Pon(76005, 76005, 76035)]);
        Observe(t, 2, called); Assert.Empty(Observe(t, 3, called).Events);
    }
}
