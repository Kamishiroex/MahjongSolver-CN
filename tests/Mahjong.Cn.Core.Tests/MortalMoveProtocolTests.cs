using System.Text.Json;
using Mahjong.Cn.Engines;
using Xunit;

namespace Mahjong.Cn.Core.Tests;

public sealed class MortalMoveProtocolTests
{
    private static AkochanGlobalSnapshot CapturedPon()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "mortal-pon-20260926.json")));
        return doc.RootElement.GetProperty("ActualPublicInput").Deserialize<AkochanGlobalSnapshot>()!;
    }

    private const string Pon = """[{"type":"pon","actor":0,"target":1,"pai":"E","consumed":["E","E"]}]""";

    [Fact]
    public void Captured_pon_accepts_Mortal_single_action_but_preserves_akochan_batch_contract()
    {
        var input = CapturedPon();
        var move = Assert.Single(AkochanReplay.ParseGlobalMoves(Pon, input, mortalSingleAction: true));
        Assert.Equal("pon", move.Type);
        Assert.Equal(1, move.Target);
        Assert.Equal(2, move.Consumed.Length);
        var error = Assert.Throws<AkochanProtocolException>(() => AkochanReplay.ParseGlobalMoves(Pon, input));
        Assert.Equal("MOVE_BATCH_INCOMPLETE", error.Code);
    }

    [Fact]
    public void Mortal_chi_is_one_action_and_does_not_invent_a_following_discard()
    {
        var input = CapturedPon() with { Trigger = new("dahai", 3, new(12)) };
        const string chi = """[{"type":"chi","actor":0,"target":3,"pai":"4p","consumed":["2p","3p"]}]""";
        Assert.Equal("chi", Assert.Single(AkochanReplay.ParseGlobalMoves(chi, input, true)).Type);
    }

    [Fact]
    public void Actorless_mjai_pass_is_bound_to_current_player_only_for_Mortal()
    {
        const string pass = """[{"type":"none"}]""";
        var move = Assert.Single(AkochanReplay.ParseGlobalMoves(pass, CapturedPon(), true));
        Assert.Equal("none", move.Type);
        Assert.Equal(0, move.Actor);
        Assert.Throws<AkochanProtocolException>(() => AkochanReplay.ParseGlobalMoves(pass, CapturedPon()));
    }

    [Theory]
    [InlineData("""[{"type":"none","actor":1}]""", "MOVE_ACTOR_MISMATCH")]
    [InlineData("""[{"type":"none","extra":1}]""", "FIELD_FORBIDDEN")]
    [InlineData("""[{"type":"reach","actor":0}]""", "MOVE_BATCH_INCOMPLETE")]
    [InlineData("""[{"type":"pon","actor":0,"target":3,"pai":"E","consumed":["E","E"]}]""", "MOVE_TRIGGER_MISMATCH")]
    [InlineData("""[{"type":"pon","actor":0,"target":1,"pai":"E","consumed":["E","P"]}]""", "MELD_SHAPE_INVALID")]
    public void Mortal_still_rejects_invalid_actions(string response, string code)
    {
        var error = Assert.Throws<AkochanProtocolException>(() => AkochanReplay.ParseGlobalMoves(response, CapturedPon(), true));
        Assert.Equal(code, error.Code);
    }
}
