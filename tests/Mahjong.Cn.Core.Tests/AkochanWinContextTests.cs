using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Cn.Engines;
using Xunit;

namespace Mahjong.Cn.Core.Tests;

public sealed class AkochanWinContextTests
{
    [Fact]
    public void Pass_furiten_fields_are_nullable_and_change_decision_identity()
    {
        var s=State();using var json=JsonDocument.Parse(AkochanGlobalEngine.CanonicalInput(s));
        var wire=json.RootElement.GetProperty("record")[1].GetProperty("mjcn_snapshot");
        Assert.Equal(JsonValueKind.Null,wire.GetProperty("own_temporary_furiten").ValueKind);
        Assert.Equal(JsonValueKind.Null,wire.GetProperty("own_riichi_furiten").ValueKind);
        Assert.NotEqual(AkochanGlobalEngine.ComputeInputSha256(s),AkochanGlobalEngine.ComputeInputSha256(s with {OwnTemporaryFuriten=true}));
        Assert.NotEqual(AkochanGlobalEngine.ComputeInputSha256(s with {OwnTemporaryFuriten=false}),AkochanGlobalEngine.ComputeInputSha256(s with {OwnTemporaryFuriten=true}));
    }
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Current_legal_ron_conflicting_with_proven_furiten_is_rejected(bool permanent)
    {
        var s=State() with {LegalActions=Mahjong.Core.ActionFlags.Ron};
        s=s with {Players=s.Players.SetItem(0,s.Players[0] with {RiichiDeclared=true,RiichiEstablished=true}),
            OwnTemporaryFuriten=permanent?null:true,OwnRiichiFuriten=permanent?true:null};
        Assert.Equal("AKOCHAN_GLOBAL_FURITEN_CONTEXT_CONFLICT",Assert.Throws<AkochanException>(()=>AkochanGlobalEngine.CanonicalInput(s)).Code);
    }
    [Fact]
    public void Riichi_pass_without_riichi_is_rejected()
    {
        Assert.Equal("AKOCHAN_GLOBAL_FURITEN_CONTEXT_CONFLICT",Assert.Throws<AkochanException>(()=>
            AkochanGlobalEngine.CanonicalInput(State() with {OwnRiichiFuriten=true})).Code);
    }
    private static AkochanGlobalSnapshot State() => new(0,0,1,0,0,0,40,
        Enumerable.Range(0,14).Select(i=>new VisibleTile(i)).ToImmutableArray(),[new(32)],
        Enumerable.Range(0,4).Select(i=>new AkochanGlobalPlayer(i,i,25000,false,false,null,[],[])).ToImmutableArray(),
        new("tsumo",0,new(13)),[]);

    [Theory]
    [InlineData(null)][InlineData("normal")]
    public void Draw_origin_and_nullable_ippatsu_reach_canonical_input_without_invented_events(string? kind)
    {
        var s=State() with {OwnDrawKind=kind};
        using var json=JsonDocument.Parse(AkochanGlobalEngine.CanonicalInput(s));
        var records=json.RootElement.GetProperty("record");
        Assert.Equal(3,records.GetArrayLength());
        var current=records[1].GetProperty("mjcn_snapshot");
        Assert.Equal(kind,current.GetProperty("own_draw_kind").GetString());
        Assert.Equal(JsonValueKind.Null,current.GetProperty("players")[0].GetProperty("ippatsu").ValueKind);
    }

    [Fact]
    public void Different_draw_kind_invalidates_cached_native_decision()
    {
        var s=State();
        Assert.NotEqual(AkochanGlobalEngine.ComputeInputSha256(s),AkochanGlobalEngine.ComputeInputSha256(s with {OwnDrawKind="normal"}));
    }

    [Fact]
    public void Double_riichi_is_nullable_and_changes_native_cache_identity()
    {
        var s=State();s=s with {Players=s.Players.SetItem(0,s.Players[0] with {RiichiDeclared=true,RiichiEstablished=true})};
        var withDouble=s with {Players=s.Players.SetItem(0,s.Players[0] with {DoubleRiichi=true})};
        using var unknown=JsonDocument.Parse(AkochanGlobalEngine.CanonicalInput(s));
        using var known=JsonDocument.Parse(AkochanGlobalEngine.CanonicalInput(withDouble));
        Assert.Equal(JsonValueKind.Null,unknown.RootElement.GetProperty("record")[1].GetProperty("mjcn_snapshot")
            .GetProperty("players")[0].GetProperty("double_riichi").ValueKind);
        Assert.True(known.RootElement.GetProperty("record")[1].GetProperty("mjcn_snapshot")
            .GetProperty("players")[0].GetProperty("double_riichi").GetBoolean());
        Assert.NotEqual(AkochanGlobalEngine.ComputeInputSha256(s),AkochanGlobalEngine.ComputeInputSha256(withDouble));
    }

    [Theory]
    [InlineData("not-accepted")][InlineData("second-discard")][InlineData("later-river-mark")][InlineData("open-hand")]
    public void Contradictory_double_riichi_is_rejected(string mode)
    {
        var s=State();var p=s.Players[0] with {RiichiDeclared=true,RiichiEstablished=true,DoubleRiichi=true};
        p=mode switch {
            "not-accepted"=>p with {RiichiEstablished=false},
            "second-discard"=>p with {RiichiDiscardIndex=1},
            "later-river-mark"=>p with {River=[new(new(20),false,false),new(new(21),false,false){RiichiDeclaration=true}]},
            _=>p with {Melds=[new("pon",1,new(25),[new(25),new(25),new(25)])]}
        };
        Assert.Equal("AKOCHAN_GLOBAL_WIN_CONTEXT_INVALID",Assert.Throws<AkochanException>(
            ()=>AkochanGlobalEngine.CanonicalInput(s with {Players=s.Players.SetItem(0,p)})).Code);
    }

    [Theory]
    [InlineData("unknown-kind")][InlineData("rinshan-without-kan")][InlineData("ippatsu-without-riichi")]
    [InlineData("rinshan-and-ippatsu")][InlineData("non-draw-trigger")]
    public void Contradictory_context_is_rejected_before_native_start(string mode)
    {
        var s=State();
        switch(mode) {
            case "unknown-kind":s=s with {OwnDrawKind="replacement?"};break;
            case "rinshan-without-kan":s=s with {OwnDrawKind="rinshan"};break;
            case "ippatsu-without-riichi":s=s with {Players=s.Players.SetItem(0,s.Players[0] with {Ippatsu=true})};break;
            case "rinshan-and-ippatsu":s=s with {OwnDrawKind="rinshan",Players=s.Players.SetItem(0,s.Players[0] with {Ippatsu=true,RiichiDeclared=true,RiichiEstablished=true,
                Melds=[new("ankan",null,null,[new(27),new(27),new(27),new(27)])]})};break;
            case "non-draw-trigger":s=s with {OwnDrawKind="normal",Trigger=new("discard",0,null)};break;
        }
        var error=Assert.Throws<AkochanException>(()=>AkochanGlobalEngine.CanonicalInput(s));
        Assert.Equal("AKOCHAN_GLOBAL_WIN_CONTEXT_INVALID",error.Code);
    }
}
