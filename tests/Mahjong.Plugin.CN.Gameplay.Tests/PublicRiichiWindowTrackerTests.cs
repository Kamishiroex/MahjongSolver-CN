using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PublicRiichiWindowTrackerTests
{
    [Fact]
    public void First_discard_without_earlier_calls_becomes_double_riichi_only_after_acceptance()
    {
        var tracker=new PublicRiichiWindowTracker();
        Assert.False(Start(tracker).Players[0].DoubleRiichi.IsConfirmed);
        var result=Observe(tracker,Snapshot(13,true,true,true));
        Assert.True(result.Players[0].DoubleRiichi.IsConfirmed);
        Assert.True(result.Players[0].DoubleRiichi.Value);
        Assert.Equal(result.Players[0].DoubleRiichi,result.OurDoubleRiichi);
        Assert.False(result.RoundId.IsConfirmed); // Current table proof does not invent an opening.
        Assert.Equal(SynchronizationState.HistoryGap,result.Synchronization);
    }

    private static PublicSnapshot WithPon(PublicSnapshot s)
    {
        var tiles=Enumerable.Range(1,3).Select(i=>Tile(2,i) with {Tile=new VisibleTile(20),GroupPath="Emj/114"}).ToImmutableArray();
        var inv=new PublicImageInventory(tiles,true,true,true,false,3,0,[]) {
            Groups=[new("Emj/114",3,3,0,true,"PUBLIC_PON_THREE_FACE_PATTERN",null)]};
        return s with {Players=s.Players.SetItem(2,s.Players[2] with {MeldImages=Field<PublicImageInventory>.Known(inv,s.Observation!)})};
    }

    private static PublicSnapshot WithClosedKan(PublicSnapshot s)
    {
        var tiles=Enumerable.Range(1,2).Select(i=>Tile(2,i) with {Tile=new VisibleTile(20),GroupPath="Emj/114"}).ToImmutableArray();
        // Exactly the assembler's DTO: backs are not decoded faces, so global
        // image completeness remains false although the public group is stable.
        var inv=new PublicImageInventory(tiles,false,true,false,false,4,0,[]) {
            Groups=[new("Emj/114",4,2,2,true,"PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN",20)]};
        return s with {Players=s.Players.SetItem(2,s.Players[2] with {MeldImages=Field<PublicImageInventory>.Known(inv,s.Observation!)})};
    }

    [Fact]
    public void Closed_kan_public_backs_cancel_ippatsu_without_erasing_known_double_riichi()
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);Observe(tracker,Snapshot(13,true,true,true));
        var result=Observe(tracker,WithClosedKan(Snapshot(14,true,true,true)));
        Assert.True(result.OurDoubleRiichi.IsConfirmed);Assert.True(result.OurDoubleRiichi.Value);
        Assert.True(result.Players[0].Ippatsu.IsConfirmed);Assert.False(result.Players[0].Ippatsu.Value);
        Assert.False(result.Players[2].MeldImages.Value!.AllVisibleSlotsDecoded);
        Assert.Equal(2,result.Players[2].MeldImages.Value!.Tiles.Length);
    }

    [Theory]
    [InlineData("unverified-back")][InlineData("mismatched-face")][InlineData("unknown-component")]
    [InlineData("rejection")][InlineData("unstable-group")][InlineData("missing-group")]
    public void Incomplete_closed_kan_does_not_gain_a_public_inventory_proof(string mode)
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);var s=WithClosedKan(Snapshot(13,true,true,true));
        var inv=s.Players[2].MeldImages.Value!;var g=inv.Groups[0];
        inv=mode switch {
            "unverified-back"=>inv with {Groups=[g with {VerifiedBackSlots=1}]},
            "mismatched-face"=>inv with {Tiles=inv.Tiles.SetItem(0,inv.Tiles[0] with {Tile=new VisibleTile(21)})},
            "unknown-component"=>inv with {UnknownComponents=1},
            "rejection"=>inv with {RejectionCodes=["PUBLIC_OCCLUDED_OR_TRANSITION"]},
            "unstable-group"=>inv with {Groups=[g with {Stable=false}]},
            _=>inv with {Groups=[]}
        };
        s=s with {Players=s.Players.SetItem(2,s.Players[2] with {MeldImages=Field<PublicImageInventory>.Known(inv,s.Observation!)})};
        var p=Observe(tracker,s).Players[0];Assert.False(p.DoubleRiichi.IsConfirmed);Assert.False(p.Ippatsu.IsConfirmed);
    }

    [Fact]
    public void Call_after_double_riichi_ends_ippatsu_but_not_double_riichi()
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);
        Observe(tracker,Snapshot(13,true,true,true));
        var result=Observe(tracker,WithPon(Snapshot(14,true,true,true)));
        Assert.True(result.OurDoubleRiichi.IsConfirmed);Assert.True(result.OurDoubleRiichi.Value);
        Assert.True(result.Players[0].Ippatsu.IsConfirmed);Assert.False(result.Players[0].Ippatsu.Value);
    }

    [Fact]
    public void Call_observed_before_declaration_proves_not_double_riichi()
    {
        var tracker=new PublicRiichiWindowTracker();
        Observe(tracker,WithPon(Snapshot(10)));
        Observe(tracker,WithPon(Snapshot(11,true,true)));
        var result=Observe(tracker,WithPon(Snapshot(12,true,true,true)));
        Assert.True(result.OurDoubleRiichi.IsConfirmed);Assert.False(result.OurDoubleRiichi.Value);
    }

    [Fact]
    public void Restart_with_existing_meld_cannot_guess_call_order()
    {
        var tracker=new PublicRiichiWindowTracker();
        Observe(tracker,WithPon(Snapshot(11,true,true)));
        var result=Observe(tracker,WithPon(Snapshot(12,true,true,true)));
        Assert.True(result.Players[0].RiichiEstablished.Value);
        Assert.False(result.OurDoubleRiichi.IsConfirmed);
    }

    [Fact]
    public void Restart_with_complete_empty_meld_table_can_recover_double_riichi_without_ippatsu()
    {
        var tracker=new PublicRiichiWindowTracker();
        Observe(tracker,Snapshot(11,true,true));
        var result=Observe(tracker,Snapshot(12,true,true,true));
        Assert.True(result.OurDoubleRiichi.IsConfirmed);Assert.True(result.OurDoubleRiichi.Value);
        Assert.False(result.Players[0].Ippatsu.IsConfirmed);
    }

    [Theory]
    [InlineData("epoch")][InlineData("session")][InlineData("gap")][InlineData("clear")]
    public void Lost_continuity_does_not_reuse_double_riichi_after_a_call(string mode)
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);
        Observe(tracker,Snapshot(13,true,true,true));
        var s=WithPon(Snapshot(mode=="gap"?40:14,true,true,true));
        if(mode=="clear") tracker.Clear();
        if(mode=="session") s=s with {SessionId=Guid.NewGuid()};
        var result=Observe(tracker,s,mode=="epoch"?"new":"epoch");
        Assert.False(result.OurDoubleRiichi.IsConfirmed);
    }

    [Theory]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public void Each_screen_position_requires_first_discard_and_current_acceptance(int actor)
    {
        var tracker=new PublicRiichiWindowTracker();var s=Snapshot(20);
        var p=s.Players[actor];var r=s.Observation!;
        p=p with {RiichiDeclared=Field<bool>.Known(true,r),RiichiEstablished=Field<bool>.Known(true,r),
            RiverImages=Field<PublicImageInventory>.Known(p.RiverImages.Value! with {
                Tiles=[Tile(actor,1,true)],VisibleSlots=1,ObservedEmpty=false},r)};
        s=s with {Players=s.Players.SetItem(actor,p)};
        Assert.True(Observe(tracker,s).Players[actor].DoubleRiichi.Value);
        // An earlier visible discard is sufficient negative proof, including mid-hand recovery.
        tracker.Clear();p=p with {RiverImages=p.RiverImages with {Value=p.RiverImages.Value! with {
            Tiles=[Tile(actor,1),Tile(actor,2,true)],VisibleSlots=2}}};
        var result=Observe(tracker,s with {Players=s.Players.SetItem(actor,p)}).Players[actor];
        Assert.True(result.DoubleRiichi.IsConfirmed);Assert.False(result.DoubleRiichi.Value);
    }

    [Theory]
    [InlineData("stale-meld")][InlineData("unknown-meld")][InlineData("unstable-meld")]
    [InlineData("transition")][InlineData("conflict")][InlineData("version")]
    public void Untrusted_double_riichi_evidence_is_not_emitted(string mode)
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);
        var s=Snapshot(13,true,true,true);var context=Context;
        var p=s.Players[2];
        switch(mode) {
            case "stale-meld":p=p with {MeldImages=p.MeldImages with {Observation=Snapshot(9).Observation}};break;
            case "unknown-meld":p=p with {MeldImages=Field<PublicImageInventory>.Unknown("gap")};break;
            case "unstable-meld":p=p with {MeldImages=p.MeldImages with {Value=p.MeldImages.Value! with {Stable=false}}};break;
            case "transition":s=s with {Stability=StabilityState.Transition};break;
            case "conflict":s=s with {Players=s.Players.SetItem(0,s.Players[0] with {
                DoubleRiichi=Field<bool>.Conflict(false,s.Observation!,"conflict")})};break;
            case "version":context=context with {ClientVersion="wrong"};break;
        }
        s=s with {Players=s.Players.SetItem(2,p)};
        Assert.False(tracker.Observe(s,"epoch",context).OurDoubleRiichi.IsConfirmed);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Recorded_public_window_proves_continuation_then_expires_without_fabricated_events(int index)
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures","riichi-window-20260925.json")));
        var item=fixture.RootElement.GetProperty("Cases")[index];int actor=item.GetProperty("Actor").GetInt32();
        var tracker=new PublicRiichiWindowTracker();var positives=new List<long>();var expired=new List<long>();
        var notDouble=new List<long>();
        foreach(var row in item.GetProperty("Snapshots").EnumerateArray()) {
            var input=row.Deserialize<PublicSnapshot>()!;var result=Observe(tracker,input);var p=result.Players[actor];
            Assert.Equal(SynchronizationState.HistoryGap,result.Synchronization);
            if(p.Ippatsu.IsConfirmed && p.Ippatsu.Value) {
                Assert.True(p.RiichiEstablished.IsConfirmed);Assert.True(p.RiichiEstablished.Value);
                positives.Add(input.Observation!.Sequence);
            }
            if(p.Ippatsu.IsConfirmed && !p.Ippatsu.Value && p.RiichiDeclared.Value) expired.Add(input.Observation!.Sequence);
            if(p.RiichiDeclared.Value && p.DoubleRiichi.IsConfirmed) {
                Assert.False(p.DoubleRiichi.Value); // Actual declarations are the sixth/fifth visible discard.
                notDouble.Add(input.Observation!.Sequence);
            }
        }
        Assert.NotEmpty(expired);
        Assert.NotEmpty(notDouble);
        if(index==0) {Assert.NotEmpty(positives);Assert.True(expired.Max()>positives.Min());}
        else {
            // The second excerpt has 2.89 s and 2.56 s gaps. Never invent the
            // unchanged frames omitted by the old change-only journal.
            Assert.Empty(positives);
            var times=item.GetProperty("Snapshots").EnumerateArray().Select(s=>s.GetProperty("Observation")
                .GetProperty("ObservedAtUtc").GetDateTimeOffset()).ToArray();
            Assert.Contains(times.Zip(times.Skip(1),(a,b)=>b-a),gap=>gap>TimeSpan.FromSeconds(2));
        }
    }

    private static readonly PublicObservationContext Context = new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
        new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
    private static readonly Guid Session = Guid.Parse("43454545-4343-4545-4545-444444444444");
    private static PublicImageTile Tile(int actor, int order, bool sideways=false) => new(new VisibleTile(order),
        sideways ? $"Emj/{117+3*actor}/4" : $"Emj/{118+3*actor}{(order==1 ? "" : order.ToString("0000"))}/4",
        "",0,0,20,30,0,false,true) { DisplayPosition=new(1,order,order,sideways) };
    private static PublicSnapshot Snapshot(long seq, bool declared=false, bool discard=false, bool continued=false, bool ended=false)
    {
        var reference=new ObservationReference(seq,DateTimeOffset.UnixEpoch.AddMilliseconds(seq*100),"test","constructed public inventories");
        Field<T> K<T>(T v)=>Field<T>.Known(v,reference);
        PublicImageInventory Inventory(params PublicImageTile[] tiles)=>new(tiles.ToImmutableArray(),true,true,true,tiles.Length==0,tiles.Length,0,[]);
        return new() { SessionId=Session,Observation=reference,Stability=StabilityState.Stable,
            Synchronization=SynchronizationState.HistoryGap,RoundWind=K(0),HandNumber=K(1),Honba=K(0),DealerPlayerId=K(0),
            Players=Enumerable.Range(0,4).Select(i=>new PlayerPublicState((ScreenPosition)i) {
                RiichiDeclared=K(i==0 && declared),PlayerId=K(i),SeatWind=K(i),Score=K(25000),
                RiverImages=K(Inventory(i==0 && discard ? ended ? [Tile(0,1,true),Tile(0,2)] : [Tile(0,1,true)] :
                    i==1 && continued ? [Tile(1,1)] : [])), MeldImages=K(Inventory()) }).ToImmutableArray() };
    }
    private static PublicSnapshot Observe(PublicRiichiWindowTracker tracker, PublicSnapshot s, string epoch="epoch") => tracker.Observe(s,epoch,Context);
    private static void Unknown(PlayerPublicState p) { Assert.False(p.RiichiEstablished.IsConfirmed);Assert.False(p.Ippatsu.IsConfirmed); }
    private static PublicSnapshot Start(PublicRiichiWindowTracker tracker)
    { Observe(tracker,Snapshot(10));Observe(tracker,Snapshot(11,true));return Observe(tracker,Snapshot(12,true,true)); }

    [Fact]
    public void Declaration_alone_is_unknown_continuation_proves_acceptance_and_positive_ippatsu()
    {
        var tracker=new PublicRiichiWindowTracker();Unknown(Start(tracker).Players[0]);
        var result=Observe(tracker,Snapshot(13,true,true,true));var player=result.Players[0];
        Assert.True(player.RiichiEstablished.IsConfirmed);Assert.True(player.RiichiEstablished.Value);
        Assert.True(player.Ippatsu.IsConfirmed);Assert.True(player.Ippatsu.Value);
        Assert.Equal(SynchronizationState.HistoryGap,result.Synchronization);
        Assert.False(result.RoundId.IsConfirmed);
        Assert.Equal(SourceKind.Derived,player.Ippatsu.SourceKind);
    }

    [Fact]
    public void Mid_hand_declaration_baseline_can_prove_acceptance_but_not_positive_ippatsu()
    {
        var tracker=new PublicRiichiWindowTracker();Observe(tracker,Snapshot(12,true,true));
        var player=Observe(tracker,Snapshot(13,true,true,true)).Players[0];
        Assert.True(player.RiichiEstablished.Value);Assert.True(player.RiichiEstablished.IsConfirmed);
        Assert.False(player.Ippatsu.IsConfirmed);
    }

    [Fact]
    public void Next_own_discard_ends_the_window_and_cannot_rearm()
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);
        Assert.True(Observe(tracker,Snapshot(13,true,true,true)).Players[0].Ippatsu.Value);
        foreach(var seq in new[]{14,15}) {
            var p=Observe(tracker,Snapshot(seq,true,true,true,true)).Players[0];
            Assert.True(p.RiichiEstablished.Value);Assert.True(p.Ippatsu.IsConfirmed);Assert.False(p.Ippatsu.Value);
        }
    }

    [Theory]
    [InlineData("chi")][InlineData("pon")][InlineData("kan")]
    public void Changed_meld_inventory_permanently_cancels_ippatsu(string type)
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);
        var s=Snapshot(13,true,true,true);var p=s.Players[2];int count=type=="kan"?4:3;
        var inventory=new PublicImageInventory(Enumerable.Range(1,count).Select(i=>Tile(2,i) with {GroupPath="Emj/114"}).ToImmutableArray(),
            true,true,true,false,count,0,[]) {Groups=[new("Emj/114",count,count,0,true,type switch {
                "chi"=>"PUBLIC_CHI_THREE_FACE_PATTERN","pon"=>"PUBLIC_PON_THREE_FACE_PATTERN",_=>"PUBLIC_KAN_FOUR_FACE_PATTERN"},null)]};
        s=s with {Players=s.Players.SetItem(2,p with {MeldImages=Field<PublicImageInventory>.Known(inventory,s.Observation!)})};
        p=Observe(tracker,s).Players[0];Assert.True(p.Ippatsu.IsConfirmed);Assert.False(p.Ippatsu.Value);
    }

    [Theory]
    [InlineData("epoch")][InlineData("session")][InlineData("gap")][InlineData("reverse")]
    [InlineData("duplicate")][InlineData("version")][InlineData("clear")]
    public void Observation_boundaries_do_not_carry_old_window(string mode)
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);Observe(tracker,Snapshot(13,true,true,true));
        var s=Snapshot(14,true,true,true);var context=Context;string epoch="epoch";
        switch(mode) {
            case "epoch":epoch="next";break;case "session":s=s with {SessionId=Guid.NewGuid()};break;
            case "gap":s=Snapshot(40,true,true,true);break;case "reverse":s=Snapshot(12,true,true,true);break;
            case "duplicate":s=Snapshot(13,true,true,true);break;case "version":context=context with {ClientVersion="wrong"};break;
            case "clear":tracker.Clear();break;
        }
        Unknown(tracker.Observe(s,epoch,context).Players[0]);
    }

    [Theory]
    [InlineData("unknown-river")][InlineData("stale-river")][InlineData("unstable-river")]
    [InlineData("duplicate-order")][InlineData("missing-slot")][InlineData("unknown-meld")]
    [InlineData("transition")][InlineData("declaration-conflict")][InlineData("same-frame")]
    public void Untrusted_current_table_cannot_emit_positive_facts(string mode)
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);
        var s=Snapshot(13,true,true,true);var p=s.Players[1];
        switch(mode) {
            case "unknown-river":p=p with {RiverImages=Field<PublicImageInventory>.Unknown("gap")};break;
            case "stale-river":p=p with {RiverImages=p.RiverImages with {Observation=Snapshot(10).Observation}};break;
            case "unstable-river":p=p with {RiverImages=p.RiverImages with {Value=p.RiverImages.Value! with {Stable=false}}};break;
            case "duplicate-order":p=p with {RiverImages=p.RiverImages with {Value=p.RiverImages.Value! with {
                VisibleSlots=2,Tiles=[Tile(1,1),Tile(1,2) with {DisplayPosition=new(1,1,1,false)}]}}};break;
            case "missing-slot":p=p with {RiverImages=p.RiverImages with {Value=p.RiverImages.Value! with {VisibleSlots=2}}};break;
            case "unknown-meld":p=p with {MeldImages=Field<PublicImageInventory>.Unknown("gap")};break;
            case "transition":s=s with {Stability=StabilityState.Transition};break;
            case "declaration-conflict":s=s with {Players=s.Players.SetItem(0,s.Players[0] with {
                RiichiDeclared=Field<bool>.Conflict(true,s.Observation!,"contradictory stick")})};break;
            case "same-frame":s=Snapshot(12,true,true,true);break;
        }
        s=s with {Players=s.Players.SetItem(1,p)};Unknown(Observe(tracker,s).Players[0]);
    }

    [Fact]
    public void Missing_sample_during_animation_suspends_output_and_resumes_only_with_complete_current_table()
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);
        Unknown(Observe(tracker,Snapshot(13,true,true) with {Stability=StabilityState.Transition}).Players[0]);
        Assert.True(Observe(tracker,Snapshot(14,true,true,true)).Players[0].Ippatsu.Value);
    }

    [Fact]
    public void Confirmed_negative_ippatsu_is_never_overridden_by_unchanged_melds()
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);var s=Snapshot(13,true,true,true);
        s=s with {Players=s.Players.SetItem(0,s.Players[0] with {Ippatsu=Field<bool>.Known(false,s.Observation!)})};
        Assert.False(Observe(tracker,s).Players[0].Ippatsu.Value);
        var p=Observe(tracker,Snapshot(14,true,true,true)).Players[0];Assert.True(p.Ippatsu.IsConfirmed);Assert.False(p.Ippatsu.Value);
    }

    [Theory]
    [InlineData("replaced-tile")][InlineData("shrunk-river")][InlineData("stale-declaration")]
    [InlineData("legacy-source")][InlineData("wrong-uld")]
    public void Contradictions_cannot_reuse_positive_window(string mode)
    {
        var tracker=new PublicRiichiWindowTracker();Start(tracker);Observe(tracker,Snapshot(13,true,true,true));
        var s=Snapshot(14,true,true,true);var context=Context;
        switch(mode) {
            case "replaced-tile":s=s with {Players=s.Players.SetItem(0,s.Players[0] with {RiverImages=s.Players[0].RiverImages with {
                Value=s.Players[0].RiverImages.Value! with {Tiles=[Tile(0,1,true) with {Tile=new VisibleTile(9)}]}}})};break;
            case "shrunk-river":s=Snapshot(14,true,true,false);break;
            case "stale-declaration":s=s with {Players=s.Players.SetItem(0,s.Players[0] with {
                RiichiDeclared=Field<bool>.Known(true,Snapshot(10).Observation!)})};break;
            case "legacy-source":s=s with {Players=s.Players.SetItem(1,s.Players[1] with {
                RiverImages=s.Players[1].RiverImages with {SourceKind=SourceKind.LegacyAssumption}})};break;
            case "wrong-uld":context=context with {UldSha256="wrong"};break;
        }
        Unknown(tracker.Observe(s,"epoch",context).Players[0]);
    }

    [Fact]
    public void Continuously_observed_delayed_declaration_animation_does_not_lose_its_pre_declaration_baseline()
    {
        var tracker=new PublicRiichiWindowTracker();Observe(tracker,Snapshot(10));
        for(int seq=11;seq<=40;seq++) Observe(tracker,Snapshot(seq,true) with {Stability=StabilityState.Transition});
        Unknown(Observe(tracker,Snapshot(41,true,true)).Players[0]);
        Assert.True(Observe(tracker,Snapshot(42,true,true,true)).Players[0].Ippatsu.Value);
    }
}
