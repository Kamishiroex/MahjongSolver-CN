using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PublicDrawKindTrackerTests
{
    [Fact]
    public void Captured_public_draw_of_green_dragon_is_normal_from_matching_inventories_and_wall()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","draw-origin-normal-20260925.json")));
        var frames=doc.RootElement.GetProperty("frames").EnumerateArray().Select(e=>e.Deserialize<PublicSnapshot>()!).ToArray();
        var delta=doc.RootElement.GetProperty("delta").Deserialize<PublicOwnHandDelta>()!;
        var t=new PublicDrawKindTracker(); Observe(t,frames[0]);
        var result=Observe(t,frames[1],own:new("OWN_UNIQUE_INVENTORY_DELTA",[],null,null,0,"recorded",delta));
        Assert.Equal(32,delta.Tile.Kind34);
        Assert.True(result.OwnDrawKind.IsConfirmed);Assert.Equal("normal",result.OwnDrawKind.Value);
        Assert.Equal(SourceKind.Derived,result.OwnDrawKind.SourceKind);
    }
    private static readonly PublicObservationContext Context = new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
        new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
    private static readonly Guid Session = Guid.Parse("90e46c6c-9239-4a0b-8d80-1c77f47e32a7");
    private static PublicSnapshot Snapshot(long seq, int wall, int[] hand, bool separate = false, params PublicMeldImageGroup[] groups)
    {
        var r = new ObservationReference(seq, DateTimeOffset.UnixEpoch.AddMilliseconds(seq * 100), "synthetic", "constructed inventory transition; not live evidence");
        var faces = groups.SelectMany(g => Enumerable.Range(0, g.KnownFaces).Select(i =>
            new PublicImageTile(new(g.InferredClosedKanKind34 ?? 27), $"{g.GroupPath}/{i}/face", g.GroupPath,
                0, 0, 20, 30, 0, false, true))).ToImmutableArray();
        bool allFaces = groups.All(g => g.VerifiedBackSlots == 0);
        return new() { SessionId = Session, Observation = r, Stability = StabilityState.Stable,
            WallRemaining = Field<int>.Known(wall, r),
            LowerVisibleFaces = Field<ImmutableArray<PublicHandTile>>.Known(hand.Select((t, i) => new PublicHandTile(new(t),
                new(separate && i == hand.Length - 1 ? "Emj/135/9/4" : $"Emj/{140+i}/9/4", i+1, seq))).ToImmutableArray(), r),
            Players = [new(ScreenPosition.Lower) { MeldImages = Field<PublicImageInventory>.Known(new(faces,allFaces,true,allFaces,groups.Length==0,
                groups.Sum(g=>g.VisibleSlots),0,[]) { Groups = groups.ToImmutableArray() }, r) }] };
    }
    private static PublicOwnHandTransitionObservation Delta(string kind, long before, long after, int tile) =>
        new("OWN_UNIQUE_INVENTORY_DELTA", [], null, null, null, "synthetic",
            new(kind, new(tile,false,"synthetic"),before,after));
    private static PublicSnapshot Observe(PublicDrawKindTracker t, PublicSnapshot s, PublicCallEvent? c = null, PublicOwnHandTransitionObservation? own = null) =>
        t.Observe(s, "epoch", c is null ? null : new([c],[]), own, Context);
    private static readonly int[] Waiting = Enumerable.Range(0,13).ToArray();
    private static PublicMeldImageGroup Kan(string kind) => new("Emj/110",4,kind=="ankan"?2:4,kind=="ankan"?2:0,true,
        kind=="ankan"?"PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN":"PUBLIC_KAN_FOUR_FACE_PATTERN",kind=="ankan"?27:null);

    [Fact]
    public void Normal_draw_requires_own_inventory_delta_separate_slot_and_one_wall_tile()
    {
        var t = new PublicDrawKindTracker();
        Observe(t,Snapshot(1,40,Waiting));
        var result = Observe(t,Snapshot(2,39,[..Waiting,25],true),own:Delta("DrawCandidate",1,2,25));
        Assert.True(result.OwnDrawKind.IsConfirmed); Assert.Equal("normal",result.OwnDrawKind.Value);
        var same = Observe(t,Snapshot(3,39,[..Waiting,25],true));
        Assert.Equal("normal",same.OwnDrawKind.Value); Assert.Equal(3,same.OwnDrawKind.Observation!.Sequence);
        Assert.False(Observe(t,Snapshot(4,39,Waiting),own:Delta("DiscardCandidate",3,4,25)).OwnDrawKind.IsConfirmed);
    }

    [Theory]
    [InlineData("ankan")][InlineData("daiminkan")][InlineData("kakan")]
    public void Kan_draw_requires_observed_kan_and_exact_consumed_inventory(string kind)
    {
        var t = new PublicDrawKindTracker();
        int consumed = kind=="ankan"?4:kind=="daiminkan"?3:1;
        var rest = Enumerable.Range(0,10).ToArray();
        PublicMeldImageGroup[] old = kind=="kakan" ? [new("Emj/110",3,3,0,true,"PUBLIC_PON_THREE_FACE_PATTERN",null)] : [];
        Observe(t,Snapshot(1,40,[..rest,..Enumerable.Repeat(27,consumed)],false,old));
        var call = new PublicCallEvent(kind,"bottom",kind=="ankan"?null:"right",kind=="ankan"?null:new(27,false),
            Enumerable.Repeat(new PublicCallTile(27,false),kind=="kakan"?0:consumed).ToImmutableArray(),"Emj/110",null,2,3,"epoch",
            kind=="ankan"?"PUBLIC_ANKAN_TWO_FACE_DERIVED":kind=="kakan"?"PUBLIC_KAKAN_VISIBLE_TRANSITION":"PUBLIC_CALL_VISIBLE_TRANSITION",
            kind=="kakan"?new(27,false):null);
        var result = Observe(t,Snapshot(3,39,[..rest,25],true,Kan(kind)),call);
        Assert.True(result.OwnDrawKind.IsConfirmed); Assert.Equal("rinshan",result.OwnDrawKind.Value);
        Assert.Equal(SourceKind.Derived,result.OwnDrawKind.SourceKind);
        if (kind == "ankan")
        {
            Assert.False(result.Players[0].MeldImages.Value!.Stable);
            Assert.False(result.Players[0].MeldImages.Value!.AllVisibleSlotsDecoded);
            Assert.Equal(2, result.Players[0].MeldImages.Value!.Tiles.Length);
        }
    }

    [Theory]
    [InlineData("back")][InlineData("face")][InlineData("missing")][InlineData("stale")]
    [InlineData("unstable")][InlineData("unreadable")]
    public void Closed_kan_draw_requires_complete_public_geometry_not_just_a_group_label(string kind)
    {
        var t = new PublicDrawKindTracker(); var rest = Enumerable.Range(0,10).ToArray();
        Observe(t, Snapshot(1,40,[..rest,27,27,27,27]));
        var call = new PublicCallEvent("ankan","bottom",null,null,
            Enumerable.Repeat(new PublicCallTile(27,false),4).ToImmutableArray(),"Emj/110",null,2,3,"epoch","PUBLIC_ANKAN_TWO_FACE_DERIVED");
        var s = Snapshot(3,39,[..rest,25],true,Kan("ankan"));
        var field = s.Players[0].MeldImages; var inv = field.Value!; var group = inv.Groups[0];
        inv = kind switch
        {
            "back" => inv with { Groups = [group with { VerifiedBackSlots = 1 }] },
            "face" => inv with { Tiles = inv.Tiles.SetItem(0,inv.Tiles[0] with {Tile = new(28)}) },
            "missing" => inv with { Tiles = [] },
            "unstable" => inv with { Groups = [group with {Stable = false}] },
            "unreadable" => inv with { RegionReadable = false },
            _ => inv,
        };
        field = field with {Value = inv};
        if (kind == "stale") field = field with {Observation = Snapshot(1,40,rest).Observation};
        s = s with {Players = [s.Players[0] with {MeldImages = field}]};
        Assert.False(Observe(t,s,call).OwnDrawKind.IsConfirmed);
    }

    [Theory]
    [InlineData("gap")][InlineData("epoch")][InlineData("session")][InlineData("version")]
    [InlineData("wall")][InlineData("separate")][InlineData("delta")][InlineData("stale")]
    [InlineData("transition")][InlineData("duplicate")][InlineData("unknown-group")]
    public void Unproven_draw_does_not_gain_a_kind(string mode)
    {
        var t = new PublicDrawKindTracker(); Observe(t,Snapshot(1,40,Waiting));
        var s = Snapshot(2,39,[..Waiting,25],mode!="separate"); var context=Context; string epoch="epoch";
        switch(mode) {
            case "gap": s=Snapshot(30,39,[..Waiting,25],true);break;
            case "epoch":epoch="new";break;
            case "session":s=s with { SessionId=Guid.NewGuid() };break;
            case "version":context=context with {ClientVersion="wrong"};break;
            case "wall":s=s with {WallRemaining=Field<int>.Known(40,s.Observation!)};break;
            case "stale":s=s with {WallRemaining=Field<int>.Known(39,Snapshot(1,40,Waiting).Observation!)};break;
            case "transition":s=s with {Stability=StabilityState.Transition};break;
            case "duplicate":s=Snapshot(1,39,[..Waiting,25],true);break;
            case "unknown-group":s=Snapshot(2,39,[..Waiting,25],true,new PublicMeldImageGroup("x",3,2,0,true,"unknown",null));break;
        }
        var result=t.Observe(s,epoch,null,mode=="delta"?null:Delta("DrawCandidate",1,s.Observation!.Sequence,25),context);
        Assert.False(result.OwnDrawKind.IsConfirmed);
    }

    [Fact]
    public void Mid_hand_existing_kan_cannot_classify_the_next_visible_tile_as_rinshan_or_normal()
    {
        var t=new PublicDrawKindTracker();var hand=Enumerable.Range(0,10).ToArray();
        Observe(t,Snapshot(1,40,hand,false,Kan("ankan")));
        var result=Observe(t,Snapshot(2,39,[..hand,25],true,Kan("ankan")),own:Delta("DrawCandidate",1,2,25));
        Assert.False(result.OwnDrawKind.IsConfirmed);
    }

    [Theory]
    [InlineData("missing")][InlineData("actor")][InlineData("code")][InlineData("old")]
    [InlineData("future")][InlineData("epoch")][InlineData("consumed")][InlineData("wall")]
    public void Kan_draw_rejects_unrelated_or_unconfirmed_events(string mode)
    {
        var t=new PublicDrawKindTracker();var rest=Enumerable.Range(0,10).ToArray();
        Observe(t,Snapshot(1,40,[..rest,27,27,27,27]));
        var c=new PublicCallEvent("ankan","bottom",null,null,[new(27,false),new(27,false),new(27,false),new(27,false)],
            "Emj/110",null,2,3,"epoch","PUBLIC_ANKAN_TWO_FACE_DERIVED");
        switch(mode) {
            case "actor":c=c with {CallerDirection="right"};break;
            case "code":c=c with {Code="candidate"};break;
            case "old":c=c with {FirstObservedSequence=1};break;
            case "future":c=c with {ConfirmedSequence=4};break;
            case "epoch":c=c with {RoundToken="old"};break;
            case "consumed":c=c with {Consumed=[new(28,false),new(28,false),new(28,false),new(28,false)]};break;
        }
        var result=Observe(t,Snapshot(3,mode=="wall"?40:39,[..rest,25],true,Kan("ankan")),mode=="missing"?null:c);
        Assert.False(result.OwnDrawKind.IsConfirmed);
    }

    [Fact]
    public void Observed_discard_after_an_old_kan_allows_the_next_proven_normal_draw()
    {
        var t=new PublicDrawKindTracker();var rest=Enumerable.Range(0,10).ToArray();
        Observe(t,Snapshot(1,40,[..rest,25],true,Kan("ankan")));
        Observe(t,Snapshot(2,40,rest,false,Kan("ankan")),own:Delta("DiscardCandidate",1,2,25));
        var result=Observe(t,Snapshot(3,39,[..rest,24],true,Kan("ankan")),own:Delta("DrawCandidate",2,3,24));
        Assert.Equal("normal",result.OwnDrawKind.Value);Assert.True(result.OwnDrawKind.IsConfirmed);
    }
}
