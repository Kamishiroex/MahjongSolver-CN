using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PublicFuritenTrackerTests
{
    private static PublicSnapshot WithOwnClosedKan(PublicSnapshot s)
    {
        var tiles = Enumerable.Range(1, 2).Select(i => new PublicImageTile(new VisibleTile(20),
            $"Emj/115/{i}", "Emj/115", 0, 0, 20, 30, 0, false, true)).ToImmutableArray();
        var inventory = new PublicImageInventory(tiles, false, true, false, false, 4, 0, [])
        {
            Groups = [new("Emj/115", 4, 2, 2, true, "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN", 20)],
        };
        return s with
        {
            LowerVisibleFaces = s.LowerVisibleFaces with { Value = s.LowerVisibleFaces.Value.Take(10).ToImmutableArray() },
            Players = s.Players.SetItem(0, s.Players[0] with
            {
                MeldImages = Field<PublicImageInventory>.Known(inventory, s.Observation!),
            }),
        };
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_closed_kan_does_not_hide_a_new_missed_ron_and_never_reads_back_faces(bool riichi)
    {
        var tracker = new PublicFuritenTracker();
        Observe(tracker, WithOwnClosedKan(Snapshot(0, riichi: riichi)));
        Observe(tracker, WithOwnClosedKan(Snapshot(1, true, riichi: riichi)));
        var result = Observe(tracker, WithOwnClosedKan(Snapshot(2, continued: true, riichi: riichi)));
        Assert.True((riichi ? result.OurRiichiFuriten : result.OurTemporaryFuriten).Value);
        Assert.False(result.Players[0].MeldImages.Value!.AllVisibleSlotsDecoded);
        Assert.Equal(2, result.Players[0].MeldImages.Value!.Tiles.Length);
    }

    [Theory]
    [InlineData("back")][InlineData("face")][InlineData("duplicate")][InlineData("slot")]
    [InlineData("unstable")][InlineData("missing")][InlineData("rejection")][InlineData("unreadable")]
    public void Invalid_closed_kan_cannot_arm_a_furiten_proof(string kind)
    {
        var tracker = new PublicFuritenTracker();
        var s = WithOwnClosedKan(Snapshot(1, true));
        var inventory = s.Players[0].MeldImages.Value!;
        var group = inventory.Groups[0];
        inventory = kind switch
        {
            "back" => inventory with { Groups = [group with { VerifiedBackSlots = 1 }] },
            "face" => inventory with { Tiles = inventory.Tiles.SetItem(0, inventory.Tiles[0] with { Tile = new(21) }) },
            "duplicate" => inventory with { Groups = [group, group] },
            "slot" => inventory with { VisibleSlots = 5 },
            "unstable" => inventory with { Groups = [group with { Stable = false }] },
            "missing" => inventory with { Groups = [] },
            "rejection" => inventory with { RejectionCodes = ["PUBLIC_OCCLUDED_OR_TRANSITION"] },
            _ => inventory with { RegionReadable = false },
        };
        s = s with { Players = s.Players.SetItem(0, s.Players[0] with { MeldImages = Field<PublicImageInventory>.Known(inventory, s.Observation!) }) };
        Observe(tracker, s);
        Assert.False(Observe(tracker, WithOwnClosedKan(Snapshot(2, continued: true))).OurTemporaryFuriten.IsConfirmed);
    }

    [Fact]
    public void Recorded_stale_ron_rows_never_rearm_a_window_after_the_table_moves_on()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "furiten-stale-ron-20260925.json")));
        var tracker = new PublicFuritenTracker();
        int checkedLater = 0;
        foreach (var element in doc.RootElement.GetProperty("Snapshots").EnumerateArray())
        {
            var snapshot = element.Deserialize<PublicSnapshot>()!;
            var menu = snapshot.VisibleActionMenu.Value!;
            // Public DTO replay only: the original raw list flags were not recorded.
            var addon = Addon(true) with { PublicActionMenu = new(menu.Code, menu.Visible,
                menu.AllVisibleRowsDecoded, menu.Rows.Select(x => new PublicActionMenuRow(
                    x.Path, x.ScreenY, x.Action.ToString(), x.Enabled, x.Code)).ToArray()) };
            var result = tracker.Observe(snapshot, "recorded-epoch", addon, Context);
            if (snapshot.Observation!.Sequence < 2962) continue;
            checkedLater++;
            Assert.NotEqual("PublicFuritenTracker/enabled-ron", result.OurTemporaryFuriten.Observation?.Source);
            Assert.NotEqual("PublicFuritenTracker/enabled-ron", result.OurRiichiFuriten.Observation?.Source);
            Assert.False(result.OurRiichiFuriten.IsConfirmed); // This capture lacks established-riichi evidence.
        }
        Assert.True(checkedLater >= 5);
    }

    [Fact]
    public void Real_previously_missed_ron_is_not_mislabelled_furiten()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","ron-false-furiten-20260925.json")));
        var s=doc.RootElement.GetProperty("PublicSnapshot").Deserialize<PublicSnapshot>()!;
        var menu=s.VisibleActionMenu.Value!;
        var addon=Addon(true) with {PublicActionMenu=new(menu.Code,menu.Visible,menu.AllVisibleRowsDecoded,
            menu.Rows.Select(x=>new PublicActionMenuRow(x.Path,x.ScreenY,x.Action.ToString(),x.Enabled,x.Code)).ToArray())};
        var result=new PublicFuritenTracker().Observe(s,"real-current-window",addon,Context);
        // An isolated current menu cannot prove it is a newly opened window.
        Assert.False(result.OurTemporaryFuriten.IsConfirmed);
        Assert.False(result.OurRiichiFuriten.IsConfirmed);
        Assert.Equal(SynchronizationState.HistoryGap,result.Synchronization);
    }

    [Theory]
    [InlineData("disabled")][InlineData("partial")][InlineData("stale")][InlineData("legacy")][InlineData("refresh")]
    public void Untrusted_menu_never_arms_a_pass_window(string mode)
    {
        var t=new PublicFuritenTracker();var s=Snapshot(1,true);var addon=Addon(true);var f=s.VisibleActionMenu;
        t.Observe(Snapshot(0),"epoch",Addon(false),Context);
        switch(mode){
            case "disabled":f=f with {Value=f.Value! with {Rows=f.Value.Rows.SetItem(0,f.Value.Rows[0] with {Enabled=false})}};break;
            case "partial":f=f with {Value=f.Value! with {AllVisibleRowsDecoded=false}};break;
            case "stale":f=f with {Observation=Snapshot(0).Observation};break;
            case "legacy":f=f with {SourceKind=SourceKind.LegacyAssumption};break;
            case "refresh":addon=addon with {PublicActionMenu=addon.PublicActionMenu! with {ListState=new(2,2,0,2,true,false,true)}};break;
        }
        var result=t.Observe(s with {VisibleActionMenu=f},"epoch",addon,Context);
        Assert.False(result.OurTemporaryFuriten.IsConfirmed);
        Assert.False(Observe(t,Snapshot(2,continued:true)).OurTemporaryFuriten.IsConfirmed);
    }
    private static readonly PublicObservationContext Context=new(RuntimeIdentity.TargetGame,LowerHandProfile.EmjUldSha256,
        new(RuntimeIdentity.TargetGame,LowerHandProfile.EmjUldSha256,[]));
    private static readonly Guid Session=Guid.Parse("11754545-4343-4545-4545-444444444444");
    internal static PublicSnapshot Snapshot(int seq,bool ron=false,bool continued=false,bool? riichi=false)
    {
        var r=new ObservationReference(seq,DateTimeOffset.UnixEpoch.AddMilliseconds(seq*100),"constructed-public-state","synthetic; not live validation");
        Field<T> K<T>(T v)=>Field<T>.Known(v,r);
        PublicImageInventory Inventory(int actor,int count)=>new(Enumerable.Range(1,count).Select(i=>new PublicImageTile(new VisibleTile(20+i),
            $"Emj/{118+actor*3}{i}/4","",0,0,20,30,0,false,true){DisplayPosition=new(1,i,i,false)}).ToImmutableArray(),true,true,true,count==0,count,0,[]);
        return new(){SessionId=Session,Observation=r,Stability=StabilityState.Stable,Synchronization=SynchronizationState.HistoryGap,
            WallRemaining=K(continued?38:39),
            LowerVisibleFaces=K(Enumerable.Range(0,13).Select(i=>new PublicHandTile(new VisibleTile(i),new($"hand/{i}",i,seq))).ToImmutableArray()),
            Players=Enumerable.Range(0,4).Select(i=>new PlayerPublicState((ScreenPosition)i){
                RiichiDeclared=riichi is null?Field<bool>.Unknown("gap"):K(i==0 && riichi.Value),
                RiichiEstablished=riichi is null?Field<bool>.Unknown("gap"):K(i==0 && riichi.Value),
                RiverImages=K(Inventory(i,i==3||i==2&&continued?1:0)),MeldImages=K(Inventory(i,0))}).ToImmutableArray(),
            VisibleActionMenu=K(new PublicActionMenuObservation(ron,ron,ron?
                [new("Emj/104/3/3",0,seq,PublicMenuAction.Ron,true,"ACTION_MENU_LABEL_CANDIDATE"),
                 new("Emj/104/3/30001",40,seq,PublicMenuAction.Pass,true,"ACTION_MENU_LABEL_CANDIDATE")]:[],
                ron?"ACTION_MENU_VISIBLE_CANDIDATE":"ACTION_MENU_NOT_VISIBLE"))};
    }
    internal static AddonProbe Addon(bool ron)=>new("Emj",true,true,true,109,[],null,
        PublicActionMenu:new(ron?"ACTION_MENU_VISIBLE_CANDIDATE":"ACTION_MENU_NOT_VISIBLE",ron,ron,
            ron?[new("Emj/104/3/3",0,"Ron",true,"ACTION_MENU_LABEL_CANDIDATE"),new("Emj/104/3/30001",40,"Pass",true,"ACTION_MENU_LABEL_CANDIDATE")]:[]));
    private static PublicSnapshot Observe(PublicFuritenTracker t,PublicSnapshot s,string token="epoch")
    {
        // Constructed tests explicitly observe a closed menu before sample 1.
        if(s.Observation!.Sequence==1)t.Observe(Snapshot(0),token,Addon(false),Context);
        return t.Observe(s,token,Addon(s.VisibleActionMenu.Value?.Visible==true),Context);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Enabled_ron_is_current_negative_evidence_not_a_pass(bool riichi)
    {
        var r=Observe(new(),Snapshot(1,true,riichi:riichi));
        Assert.True(r.OurTemporaryFuriten.IsConfirmed);Assert.False(r.OurTemporaryFuriten.Value);
        Assert.True(r.OurRiichiFuriten.IsConfirmed);Assert.False(r.OurRiichiFuriten.Value);
    }
    [Fact]
    public void Menu_closing_alone_never_proves_a_pass()
    {
        var t=new PublicFuritenTracker();Observe(t,Snapshot(1,true));
        Assert.False(Observe(t,Snapshot(2)).OurTemporaryFuriten.IsConfirmed);
    }
    [Fact]
    public void Continued_opponent_discard_proves_temporary_furiten_and_draw_clears_it()
    {
        var t=new PublicFuritenTracker();Observe(t,Snapshot(1,true));
        var result=Observe(t,Snapshot(2,continued:true));
        Assert.True(result.OurTemporaryFuriten.IsConfirmed);Assert.True(result.OurTemporaryFuriten.Value);
        Assert.False(result.OurRiichiFuriten.Value);Assert.Equal(SynchronizationState.HistoryGap,result.Synchronization);
        Assert.True(Observe(t,Snapshot(3,continued:true)).OurTemporaryFuriten.Value);
        var draw=Snapshot(4,continued:true);
        draw=draw with {OwnDrawKind=Field<string>.Known("normal",draw.Observation!)};
        result=Observe(t,draw);Assert.True(result.OurTemporaryFuriten.IsConfirmed);Assert.False(result.OurTemporaryFuriten.Value);
        Assert.False(Observe(t,Snapshot(5,continued:true)).OurTemporaryFuriten.IsConfirmed);
    }
    [Fact]
    public void Riichi_pass_is_not_cleared_by_own_draw_and_never_invents_acceptance_event()
    {
        var t=new PublicFuritenTracker();Observe(t,Snapshot(1,true,riichi:true));
        var result=Observe(t,Snapshot(2,continued:true,riichi:true));
        Assert.True(result.OurRiichiFuriten.IsConfirmed);Assert.True(result.OurRiichiFuriten.Value);
        var draw=Snapshot(3,continued:true,riichi:true);draw=draw with {OwnDrawKind=Field<string>.Known("normal",draw.Observation!)};
        result=Observe(t,draw);Assert.True(result.OurRiichiFuriten.Value);Assert.False(result.OurTemporaryFuriten.Value);
        Assert.Contains("ron-offered:1",result.OurRiichiFuriten.Observation!.DerivationInputs);
        Assert.False(result.RoundId.IsConfirmed);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void No_observed_ron_never_guesses_missing_pass_history(bool riichi)
    {
        var t=new PublicFuritenTracker();Observe(t,Snapshot(1,riichi:riichi));
        var result=Observe(t,Snapshot(2,continued:true,riichi:riichi));
        Assert.False(result.OurTemporaryFuriten.IsConfirmed);
        if(riichi)Assert.False(result.OurRiichiFuriten.IsConfirmed);
    }
    [Fact]
    public void Unknown_riichi_at_offer_does_not_guess_duration()
    {
        var t=new PublicFuritenTracker();Observe(t,Snapshot(1,true,riichi:null));
        var result=Observe(t,Snapshot(2,continued:true,riichi:true));
        Assert.False(result.OurTemporaryFuriten.IsConfirmed);Assert.False(result.OurRiichiFuriten.IsConfirmed);
    }
    [Theory]
    [InlineData("epoch")][InlineData("session")][InlineData("gap")][InlineData("clear")]
    [InlineData("reverse")][InlineData("duplicate")][InlineData("version")][InlineData("scene")]
    public void Boundaries_do_not_carry_observed_passes(string mode)
    {
        var t=new PublicFuritenTracker();Observe(t,Snapshot(1,true,riichi:true));Observe(t,Snapshot(2,continued:true,riichi:true));
        var s=Snapshot(3,continued:true,riichi:true);var context=Context;var addon=Addon(false);string token="epoch";
        switch(mode){case "epoch":token="new";break;case "session":s=s with {SessionId=Guid.NewGuid()};break;
            case "gap":s=Snapshot(25,continued:true,riichi:true);break;case "clear":t.Clear();break;
            case "reverse":s=Snapshot(1,continued:true,riichi:true);break;case "duplicate":s=Snapshot(2,continued:true,riichi:true);break;
            case "version":context=context with {ClientVersion="wrong"};break;case "scene":addon=addon with {Visible=false};break;}
        Assert.False(t.Observe(s,token,addon,context).OurRiichiFuriten.IsConfirmed);
    }
    [Theory]
    [InlineData("hand")][InlineData("own-river")][InlineData("stale-river")][InlineData("missing-meld")]
    [InlineData("transition")][InlineData("many-draws")][InlineData("wall-reset")]
    public void Ambiguous_progress_does_not_prove_missed_ron(string mode)
    {
        var t=new PublicFuritenTracker();Observe(t,Snapshot(1,true));var s=Snapshot(2,continued:true);var p=s.Players[0];
        switch(mode){
            case "hand":s=s with {LowerVisibleFaces=s.LowerVisibleFaces with {Value=s.LowerVisibleFaces.Value.SetItem(0,new(new(30),new("hand/0",0,2)))}};break;
            case "own-river":p=p with {RiverImages=s.Players[2].RiverImages};break;
            case "stale-river":p=p with {RiverImages=p.RiverImages with {Observation=Snapshot(1).Observation}};break;
            case "missing-meld":p=p with {MeldImages=Field<PublicImageInventory>.Unknown("gap")};break;
            case "transition":s=s with {Stability=StabilityState.Transition};break;
            case "many-draws":s=s with {WallRemaining=Field<int>.Known(35,s.Observation!)};break;
            case "wall-reset":s=s with {WallRemaining=Field<int>.Known(70,s.Observation!)};break;
        }
        s=s with {Players=s.Players.SetItem(0,p)};Assert.False(Observe(t,s).OurTemporaryFuriten.IsConfirmed);
    }
    [Fact]
    public void Sorting_does_not_hide_a_pass_or_clear_temporary_furiten()
    {
        var t=new PublicFuritenTracker();Observe(t,Snapshot(1,true));var s=Snapshot(2,continued:true);
        s=s with {LowerVisibleFaces=s.LowerVisibleFaces with {Value=s.LowerVisibleFaces.Value.Reverse().ToImmutableArray()}};
        Assert.True(Observe(t,s).OurTemporaryFuriten.Value);
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Ron_conflicting_with_a_proven_pass_is_explicit_conflict(bool riichi)
    {
        var t=new PublicFuritenTracker();Observe(t,Snapshot(1,true,riichi:riichi));Observe(t,Snapshot(2,continued:true,riichi:riichi));
        var result=Observe(t,Snapshot(3,true,true,riichi));
        Assert.Equal(Availability.Conflict,result.OurTemporaryFuriten.Availability);
        Assert.Equal(Availability.Conflict,result.OurRiichiFuriten.Availability);
    }

    [Fact]
    public void Lingering_ron_rows_after_draw_do_not_erase_riichi_pass_or_rearm_old_window()
    {
        var t=new PublicFuritenTracker();Observe(t,Snapshot(1,true,riichi:true));
        var s=Snapshot(2,true,riichi:true);
        s=s with {OwnDrawKind=Field<string>.Known("normal",s.Observation!),WallRemaining=Field<int>.Known(38,s.Observation!),
            LowerVisibleFaces=s.LowerVisibleFaces with {Value=s.LowerVisibleFaces.Value.Add(new(new(30),new("draw",14,2)))}};
        var result=Observe(t,s);
        Assert.True(result.OurRiichiFuriten.IsConfirmed);Assert.True(result.OurRiichiFuriten.Value);
        Assert.False(result.OurTemporaryFuriten.Value);
        var later=Snapshot(3,true,true,true);
        later=later with {Players=later.Players.SetItem(0,later.Players[0] with {RiverImages=later.Players[2].RiverImages})};
        result=Observe(t,later);
        Assert.True(result.OurRiichiFuriten.IsConfirmed);Assert.True(result.OurRiichiFuriten.Value);
        Assert.NotEqual(Availability.Conflict,result.OurRiichiFuriten.Availability);
        Assert.False(result.OurTemporaryFuriten.IsConfirmed);
    }

    [Fact]
    public void An_already_visible_ron_at_start_does_not_prove_a_new_window()
    {
        var t=new PublicFuritenTracker();var s=Snapshot(1,true);
        var result=t.Observe(s,"epoch",Addon(true),Context);
        Assert.False(result.OurTemporaryFuriten.IsConfirmed);
        Assert.False(Observe(t,Snapshot(2,continued:true)).OurTemporaryFuriten.IsConfirmed);
    }
}
