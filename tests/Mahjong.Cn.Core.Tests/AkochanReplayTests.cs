using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Cn.Engines;
using Xunit;

namespace Mahjong.Cn.Core.Tests;

/// <summary>Synthetic masked single-round records; no real player's log or opponent hand.</summary>
public sealed class AkochanReplayTests
{
    private const string StartGame = "{\"type\":\"start_game\",\"kyoku_first\":4,\"aka_flag\":true}";
    private static readonly string[] Initial = ["1m", "2m", "3m", "4m", "5m", "6m", "7m", "8m", "9m", "1p", "2p", "3p", "E"];

    private static string StartRound(int player = 0, int dealer = 0, string[]? hand = null)
    {
        string[][] hands = Enumerable.Range(0, 4).Select(i => i == player ? hand ?? Initial : Enumerable.Repeat("?", 13).ToArray()).ToArray();
        return JsonSerializer.Serialize(new { type = "start_kyoku", bakaze = "E", dora_marker = "P", honba = 0,
            kyotaku = 0, kyoku = 1, oya = dealer, scores = new[] { 25000, 25000, 25000, 25000 }, tehais = hands });
    }

    private static string Tsumo(int actor, string tile) => JsonSerializer.Serialize(new { type = "tsumo", actor, pai = tile });
    private static string Dahai(int actor, string tile, bool tsumogiri = false) =>
        JsonSerializer.Serialize(new { type = "dahai", actor, pai = tile, tsumogiri });
    private static string Call(string type, int actor, int target, string tile, params string[] consumed) =>
        JsonSerializer.Serialize(new { type, actor, target, pai = tile, consumed });
    private static string Kan(string type, int actor, string? tile, params string[] consumed) => tile is null
        ? JsonSerializer.Serialize(new { type, actor, consumed }) : JsonSerializer.Serialize(new { type, actor, pai = tile, consumed });
    private static AkochanReplay Opening() => AkochanReplay.Create([StartGame, StartRound(), Tsumo(0, "4p")], 0, "synthetic-opening", true);
    private static AkochanReplay OpponentDiscard(int actor = 3, string tile = "3m", string[]? hand = null) =>
        AkochanReplay.Create([StartGame, StartRound(dealer: actor, hand: hand), Tsumo(actor, "?"), Dahai(actor, tile)], 0, "public-response", true);
    private static string Batch(params string[] moves) => "[" + string.Join(',', moves) + "]";
    private static void Fails(string code, Action body)
    {
        var error = Assert.Throws<AkochanProtocolException>(body);
        Assert.Equal(code, error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("SECRET", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("1m", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_is_offline_immutable_masked_and_normalizes_only_transport_flags()
    {
        var input = new List<string> { StartGame, StartRound(), Tsumo(0, "4p") };
        var replay = AkochanReplay.Create(input, 0, "akochan-public-opening", true);
        input.Clear();
        Assert.Equal(3, replay.Events.Length);
        Assert.Equal("offline:akochan-public-opening", replay.SourceLabel);
        Assert.True(replay.IsSynthetic);
        Assert.Equal(0, replay.PlayerId);
        for (int i = 0; i < replay.Events.Length; i++)
        {
            using var doc = JsonDocument.Parse(replay.Events[i]);
            Assert.Equal(i == replay.Events.Length - 1, doc.RootElement.GetProperty("can_act").GetBoolean());
            Assert.DoesNotContain('\n', replay.Events[i]);
        }
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', replay.Events) + "\n")));
        Assert.Equal(expected, replay.InputSha256);
        Assert.Equal(replay.InputSha256, AkochanReplay.Create(replay.Events, 0, "again", false).InputSha256);
    }

    [Theory]
    [InlineData("names", "[\"SECRET\"]")]
    [InlineData("haiyama", "[\"1m\"]")]
    [InlineData("uradora_markers", "[\"P\"]")]
    [InlineData("chat", "\"SECRET\"")]
    [InlineData("arbitrary", "null")]
    public void Unlisted_fields_are_rejected_not_silently_stripped(string key, string value)
    {
        string bad = StartGame[..^1] + ",\"" + key + "\":" + value + "}";
        Fails("FIELD_FORBIDDEN", () => AkochanReplay.Create([bad, StartRound(), Tsumo(0, "4p")], 0, "private-reject", false));
    }

    [Theory]
    [InlineData("{\"type\":\"tsumo\",\"actor\":0,\"actor\":1,\"pai\":\"4p\"}", "DUPLICATE_KEY")]
    [InlineData("{\"type\":\"tsumo\",\"actor\":\"SECRET\",\"pai\":\"4p\"}", "FIELD_TYPE")]
    [InlineData("{\"type\":\"tsumo\",\"actor\":4,\"pai\":\"4p\"}", "FIELD_RANGE")]
    [InlineData("{\"type\":\"tsumo\",\"actor\":0,\"pai\":\"?\"}", "TILE_INVALID")]
    [InlineData("{\"type\":\"tsumo\",\"actor\":0}", "FIELD_REQUIRED")]
    [InlineData("{\"type\":\"tsumo\",\"actor\":0,\"pai\":null}", "TILE_INVALID")]
    [InlineData("{\"type\":\"tsumo\",\"actor\":0,\"pai\":\"4p\",\"can_act\":0}", "FIELD_TYPE")]
    [InlineData("{\"type\":\"tsumo\",\"actor\":0,\"pai\":\"4p\",\"can_act\":false}", "CAN_ACT_CONFLICT")]
    [InlineData("{\"type\":\"end_kyoku\"}", "EVENT_UNSUPPORTED")]
    [InlineData("{\"type\":\"ryukyoku\"}", "EVENT_UNSUPPORTED")]
    [InlineData("{\"type\":\"start_kyoku\"}", "EVENT_UNSUPPORTED")]
    [InlineData("{\"type\":\"none\",\"actor\":0}", "EVENT_UNSUPPORTED")]
    [InlineData("[]", "EVENT_OBJECT_REQUIRED")]
    [InlineData("{SECRET", "JSON_INVALID")]
    public void Input_schema_failures_return_only_structural_errors(string bad, string code) =>
        Fails(code, () => AkochanReplay.Create([StartGame, StartRound(), bad], 0, "bad-event", false));

    [Fact]
    public void Known_opponent_initial_tiles_or_draws_are_forbidden_even_in_synthetic_imports()
    {
        string round = StartRound().Replace("\"?\"", "\"1m\"", StringComparison.Ordinal);
        Fails("HIDDEN_TILE_FORBIDDEN", () => AkochanReplay.Create([StartGame, round, Tsumo(0, "4p")], 0, "hidden", true));
        Fails("HIDDEN_TILE_FORBIDDEN", () => AkochanReplay.Create([StartGame, StartRound(dealer: 1), Tsumo(1, "4p"), Dahai(1, "E")], 0, "hidden", true));
    }

    [Fact]
    public void Unknown_tsumogiri_is_not_filled_with_false()
    {
        string prefix = "{\"type\":\"dahai\",\"actor\":1,\"pai\":\"E\"";
        foreach (string suffix in new[] { "}", ",\"tsumogiri\":null}", ",\"tsumogiri\":0}" })
            Assert.Throws<AkochanProtocolException>(() => AkochanReplay.Create(
                [StartGame, StartRound(dealer: 1), Tsumo(1, "?"), prefix + suffix], 0, "missing-bool", false));
    }

    [Fact]
    public void Round_prefix_hand_count_tile_multiplicity_and_rules_are_checked()
    {
        Fails("ROUND_PREFIX_REQUIRED", () => AkochanReplay.Create([StartRound(), Tsumo(0, "4p"), Dahai(0, "4p", true)], 0, "prefix", true));
        Fails("RULES_UNSUPPORTED", () => AkochanReplay.Create([StartGame.Replace("true", "false"), StartRound(), Tsumo(0, "4p")], 0, "rules", true));
        Fails("HAND_COUNT_INVALID", () => AkochanReplay.Create([StartGame, StartRound(hand: Initial[..12]), Tsumo(0, "4p")], 0, "short-hand", true));
        Fails("OWN_TILE_COUNT_CONFLICT", () => AkochanReplay.Create([StartGame, StartRound(hand: Enumerable.Repeat("1m", 13).ToArray()), Tsumo(0, "4p")], 0, "duplicates", true));
        Fails("SCORES_INVALID", () => AkochanReplay.Create([StartGame, StartRound().Replace("25000", "\"SECRET\""), Tsumo(0, "4p")], 0, "score-type", true));
    }

    [Fact]
    public void Input_limits_and_source_label_do_not_depend_on_native_engine()
    {
        Fails("EVENT_LIMIT", () => AkochanReplay.Create(Enumerable.Repeat(StartGame, AkochanReplay.MaximumEvents + 1), 0, "limit", true));
        Fails("INPUT_SIZE_LIMIT", () => AkochanReplay.Create([new string(' ', AkochanReplay.MaximumInputBytes + 1)], 0, "limit", true));
        foreach (string label in new[] { "live-table", "realtime", "C:\\SECRET", "../SECRET", "line\nbreak", "", new string('x', 65) })
            Fails("SOURCE_LABEL_INVALID", () => AkochanReplay.Create([StartGame, StartRound(), Tsumo(0, "4p")], 0, label, false));
    }

    [Fact]
    public void History_requires_turn_order_owned_tiles_and_an_actual_pipe_trigger()
    {
        Fails("EVENT_ORDER_INVALID", () => AkochanReplay.Create([StartGame, StartRound(), Tsumo(1, "?"), Dahai(1, "E")], 0, "turn", true));
        Fails("OWN_TILE_MISSING", () => AkochanReplay.Create([StartGame, StartRound(), Tsumo(0, "4p"), Dahai(0, "C")], 0, "missing", true));
        Fails("TSUMOGIRI_CONFLICT", () => AkochanReplay.Create([StartGame, StartRound(), Tsumo(0, "4p"), Dahai(0, "E", true)], 0, "draw", true));
        Fails("RESPONSE_TRIGGER_REQUIRED", () => AkochanReplay.Create([StartGame, StartRound(), Tsumo(0, "4p"), Dahai(0, "E")], 0, "not-trigger", true));
    }

    [Fact]
    public void Chi_history_then_discard_and_opponent_response_preserves_own_hand_accounting()
    {
        var replay = AkochanReplay.Create([StartGame, StartRound(dealer: 3), Tsumo(3, "?"), Dahai(3, "3m"),
            Call("chi", 0, 3, "3m", "1m", "2m"), Dahai(0, "E"), Tsumo(1, "?"), Dahai(1, "9p")], 0, "chi-history", true);
        Assert.Equal(8, replay.Events.Length);
        Fails("CHI_TARGET_INVALID", () => AkochanReplay.Create([StartGame, StartRound(dealer: 2), Tsumo(2, "?"), Dahai(2, "3m"),
            Call("chi", 0, 2, "3m", "1m", "2m")], 0, "chi-target", true));
    }

    [Theory]
    [InlineData("ankan")]
    [InlineData("daiminkan")]
    public void Concealed_and_open_kan_with_dora_and_rinshan_are_supported(string type)
    {
        var replay = KanDoraReplay(type);
        Assert.NotEmpty(replay.Events);
        Assert.Equal(type == "daiminkan", replay.RequiresKanDoraCounterFix);
    }

    internal static AkochanReplay KanDoraReplay(string type)
    {
        string[] hand = type == "ankan"
            ? ["1m", "1m", "1m", "1m", "2m", "3m", "4m", "5m", "6m", "7m", "8m", "9m", "E"]
            : ["1m", "1m", "1m", "2m", "3m", "4m", "5m", "6m", "7m", "8m", "9m", "E", "1p"];
        var events = new List<string> { StartGame, StartRound(dealer: type == "ankan" ? 0 : 3, hand: hand) };
        if (type == "ankan") events.AddRange([Tsumo(0, "1p"), Kan("ankan", 0, null, "1m", "1m", "1m", "1m")]);
        else events.AddRange([Tsumo(3, "?"), Dahai(3, "1m"), Call("daiminkan", 0, 3, "1m", "1m", "1m", "1m")]);
        events.Add("{\"type\":\"dora\",\"dora_marker\":\"F\"}");
        events.Add(Tsumo(0, "2p"));
        return AkochanReplay.Create(events, 0, "kan-history", true);
    }

    [Fact]
    public void Added_kan_with_public_dora_and_replacement_requires_the_native_fix()
    {
        var prefix = KakanRequest().Events.Select(e => e.Replace(",\"can_act\":true", "", StringComparison.Ordinal)
            .Replace(",\"can_act\":false", "", StringComparison.Ordinal));
        var replay = AkochanReplay.Create(prefix.Concat(["{\"type\":\"dora\",\"dora_marker\":\"F\"}",
            Tsumo(2, "?"), Dahai(2, "9p")]), 0, "kakan-dora-counter", true);
        Assert.True(replay.RequiresKanDoraCounterFix);
        Assert.False(Opening().RequiresKanDoraCounterFix);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Concealed_kan_without_the_native_required_dora_is_rejected_before_process_start(int actor)
    {
        string[] hand = actor == 0
            ? ["1m", "1m", "1m", "1m", "2m", "3m", "4m", "5m", "6m", "7m", "8m", "9m", "E"]
            : Initial;
        var events = new List<string> { StartGame, StartRound(dealer: actor, hand: hand),
            Tsumo(actor, actor == 0 ? "1p" : "?"), Kan("ankan", actor, null, "1m", "1m", "1m", "1m"),
            Tsumo(actor, actor == 0 ? "2p" : "?") };
        if (actor != 0) events.Add(Dahai(actor, "E"));
        Fails("ANKAN_REPLACEMENT_ORDER_INVALID", () => AkochanReplay.Create(events, 0, "missing-kan-dora", true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Intervening_events_cannot_disguise_the_ankan_dora_replacement_sequence(bool afterDora)
    {
        string[] hand = ["1m", "1m", "1m", "1m", "2m", "3m", "4m", "5m", "6m", "7m", "8m", "9m", "E"];
        var events = new List<string> { StartGame, StartRound(hand: hand), Tsumo(0, "1p"),
            Kan("ankan", 0, null, "1m", "1m", "1m", "1m") };
        if (afterDora) events.Add("{\"type\":\"dora\",\"dora_marker\":\"F\"}");
        events.Add("{\"type\":\"reach_accepted\",\"actor\":1}");
        events.Add(Tsumo(0, "2p"));
        Fails("ANKAN_REPLACEMENT_ORDER_INVALID", () => AkochanReplay.Create(events, 0, "interrupted-kan", true));
    }

    [Fact]
    public void Every_consecutive_ankan_requires_its_own_dora_and_replacement_draw()
    {
        string[] hand = ["1m", "1m", "1m", "1m", "2m", "2m", "2m", "2m", "3m", "4m", "5m", "6m", "E"];
        var events = new List<string> { StartGame, StartRound(hand: hand), Tsumo(0, "1p"),
            Kan("ankan", 0, null, "1m", "1m", "1m", "1m"), "{\"type\":\"dora\",\"dora_marker\":\"F\"}",
            Tsumo(0, "2p"), Kan("ankan", 0, null, "2m", "2m", "2m", "2m") };
        Fails("ANKAN_REPLACEMENT_ORDER_INVALID", () => AkochanReplay.Create(
            events.Append(Tsumo(0, "3p")), 0, "second-kan-dora-missing", true));
        events.Add("{\"type\":\"dora\",\"dora_marker\":\"C\"}");
        events.Add(Tsumo(0, "3p"));
        Assert.Equal(events.Count, AkochanReplay.Create(events, 0, "consecutive-kans", true).Events.Length);
    }

    [Fact]
    public void Correct_ankan_dora_does_not_authorize_another_players_replacement_draw()
    {
        string[] hand = ["1m", "1m", "1m", "1m", "2m", "3m", "4m", "5m", "6m", "7m", "8m", "9m", "E"];
        Fails("EVENT_ORDER_INVALID", () => AkochanReplay.Create([StartGame, StartRound(hand: hand), Tsumo(0, "1p"),
            Kan("ankan", 0, null, "1m", "1m", "1m", "1m"), "{\"type\":\"dora\",\"dora_marker\":\"F\"}",
            Tsumo(1, "?"), Dahai(1, "E")], 0, "wrong-rinshan-actor", true));
    }

    private static AkochanReplay KakanRequest() => AkochanReplay.Create(
        [StartGame, StartRound(dealer: 1), Tsumo(1, "?"), Dahai(1, "1m"), Call("pon", 2, 1, "1m", "1m", "1m"),
         Dahai(2, "E"), Tsumo(3, "?"), Dahai(3, "N"), Tsumo(0, "4p"), Dahai(0, "4p", true),
         Tsumo(1, "?"), Dahai(1, "S"), Tsumo(2, "?"), Kan("kakan", 2, "1m", "1m", "1m", "1m")],
         0, "kakan-history", true);

    [Fact]
    public void Kakan_requires_existing_pon_and_only_triggers_none_or_hora_response()
    {
        var request = KakanRequest();
        Assert.Single(AkochanReplay.ParseMoves("[{\"type\":\"none\",\"actor\":0}]", request));
        Fails("MOVE_TRIGGER_MISMATCH", () => AkochanReplay.ParseMoves(Batch(Call("daiminkan", 0, 2, "1m", "1m", "1m", "1m")), request));
        Fails("KAKAN_PON_MISSING", () => AkochanReplay.Create([StartGame, StartRound(), Tsumo(0, "4p"),
            Kan("kakan", 0, "1m", "1m", "1m", "1m")], 0, "missing-pon", true));
    }

    [Fact]
    public void Reach_declaration_discard_and_acceptance_have_distinct_events()
    {
        var events = new[] { StartGame, StartRound(), Tsumo(0, "4p"), "{\"type\":\"reach\",\"actor\":0}",
            Dahai(0, "4p", true), "{\"type\":\"reach_accepted\",\"actor\":0}", Tsumo(1, "?"), Dahai(1, "E") };
        Assert.Equal(8, AkochanReplay.Create(events, 0, "reach-history", true).Events.Length);
        Fails("REACH_ORDER_INVALID", () => AkochanReplay.Create([StartGame, StartRound(), Tsumo(0, "4p"),
            "{\"type\":\"reach_accepted\",\"actor\":0}"], 0, "bad-reach", true));
    }

    [Fact]
    public void Output_preserves_reach_and_discard_as_one_complete_batch()
    {
        var moves = AkochanReplay.ParseMoves(Batch("{\"type\":\"reach\",\"actor\":0}", Dahai(0, "4p", true)), Opening());
        Assert.Equal(new[] { "reach", "dahai" }, moves.Select(x => x.Type));
        Assert.All(moves, x => Assert.Equal(0, x.Actor));
        Assert.Null(moves[0].Tsumogiri);
        Assert.True(moves[1].Tsumogiri);
        Assert.Equal(new VisibleTile(12), moves[1].Tile);
    }

    [Theory]
    [InlineData("chi")]
    [InlineData("pon")]
    public void Output_preserves_call_and_discard_batch(string type)
    {
        string call = type == "chi" ? Call(type, 0, 3, "3m", "1m", "2m") : Call(type, 0, 3, "3m", "3m", "3m");
        var hand = Initial.ToArray();
        if (type == "pon") hand[0] = "3m";
        var request = OpponentDiscard(hand: hand);
        var moves = AkochanReplay.ParseMoves(Batch(call, Dahai(0, "E")), request);
        Assert.Equal(2, moves.Length);
        Assert.Equal(2, moves[0].Consumed.Length);
        Assert.False(moves[1].Tsumogiri);
        Fails("MOVE_BATCH_INCOMPLETE", () => AkochanReplay.ParseMoves(Batch(call), request));
    }

    [Theory]
    [InlineData("{}", "MOVES_ARRAY_REQUIRED")]
    [InlineData("[]", "MOVES_ARRAY_REQUIRED")]
    [InlineData("[{}, {}, {}]", "MOVES_ARRAY_REQUIRED")]
    [InlineData("[null]", "MOVE_OBJECT_REQUIRED")]
    [InlineData("[{\"type\":\"dahai\",\"actor\":1,\"pai\":\"4p\",\"tsumogiri\":true}]", "MOVE_ACTOR_MISMATCH")]
    [InlineData("[{\"type\":\"dahai\",\"actor\":0,\"pai\":\"?\",\"tsumogiri\":true}]", "TILE_INVALID")]
    [InlineData("[{\"type\":\"dahai\",\"actor\":0,\"pai\":\"4p\",\"tsumogiri\":0}]", "FIELD_TYPE")]
    [InlineData("[{\"type\":\"dahai\",\"actor\":0,\"actor\":0,\"pai\":\"4p\",\"tsumogiri\":true}]", "DUPLICATE_KEY")]
    [InlineData("[{\"type\":\"reach\",\"actor\":0}]", "MOVE_BATCH_INCOMPLETE")]
    [InlineData("[{\"type\":\"tsumo\",\"actor\":0,\"pai\":\"4p\"}]", "EVENT_UNSUPPORTED")]
    [InlineData("[{\"type\":\"dahai\",\"actor\":0,\"pai\":\"E\",\"tsumogiri\":true}]", "MOVE_TSUMOGIRI_CONFLICT")]
    [InlineData("[{\"type\":\"none\",\"actor\":0,\"debug\":\"SECRET\"}]", "FIELD_FORBIDDEN")]
    public void Malformed_or_mismatched_output_has_no_raw_error_leak(string response, string code) =>
        Fails(code, () => AkochanReplay.ParseMoves(response, Opening()));

    [Fact]
    public void Output_bounds_target_and_consumed_shape_are_checked()
    {
        Fails("JSON_SIZE_LIMIT", () => AkochanReplay.ParseMoves(new string(' ', AkochanReplay.MaximumResponseBytes + 1), Opening()));
        Fails("CONSUMED_COUNT", () => AkochanReplay.ParseMoves(Batch(Call("pon", 0, 3, "3m", "3m"), Dahai(0, "E")), OpponentDiscard()));
        Fails("MELD_SHAPE_INVALID", () => AkochanReplay.ParseMoves(Batch(Call("pon", 0, 3, "3m", "3m", "4m"), Dahai(0, "E")), OpponentDiscard()));
        Fails("MOVE_TRIGGER_MISMATCH", () => AkochanReplay.ParseMoves(Batch(Call("pon", 0, 2, "3m", "3m", "3m"), Dahai(0, "E")), OpponentDiscard()));
        Fails("MOVE_TSUMOGIRI_CONFLICT", () => AkochanReplay.ParseMoves(Batch(Call("chi", 0, 3, "3m", "1m", "2m"), Dahai(0, "E", true)), OpponentDiscard()));
    }

    [Theory]
    [InlineData("[{\"type\":\"ryukyoku\",\"reason\":\"kyushukyuhai\",\"actor\":0}]")]
    [InlineData("[{\"type\":\"kyushukyuhai\",\"actor\":0}]")]
    public void Native_kyushukyuhai_and_legacy_alias_are_preserved_as_a_typed_single_move(string response)
    {
        var move = Assert.Single(AkochanReplay.ParseMoves(response, Opening()));
        Assert.Equal("kyushukyuhai", move.Type);
        Assert.Equal(0, move.Actor);
        Assert.Null(move.Tile);
        Assert.Empty(move.Consumed);
        Fails("MOVE_TRIGGER_MISMATCH", () => AkochanReplay.ParseMoves(response, OpponentDiscard()));
        Fails("MOVE_BATCH_UNSUPPORTED", () => AkochanReplay.ParseMoves(
            Batch("{\"type\":\"kyushukyuhai\",\"actor\":0}", Dahai(0, "4p", true)), Opening()));
    }

    [Theory]
    [InlineData("fanpai")]
    [InlineData("suufonrenda")]
    [InlineData("suukaikan")]
    [InlineData("sanchahou")]
    [InlineData("unknown")]
    public void Other_draw_results_are_not_player_declaration_actions(string reason)
        => Fails("DRAW_REASON_UNSUPPORTED", () => AkochanReplay.ParseMoves(
            "[{\"type\":\"ryukyoku\",\"reason\":\"" + reason + "\",\"actor\":0}]", Opening()));

    [Fact]
    public void Native_draw_rejects_other_actor_and_terminal_result_payload()
    {
        Fails("MOVE_ACTOR_MISMATCH", () => AkochanReplay.ParseMoves(
            "[{\"type\":\"ryukyoku\",\"reason\":\"kyushukyuhai\",\"actor\":1}]", Opening()));
        Assert.Throws<AkochanProtocolException>(() => AkochanReplay.ParseMoves(
            "[{\"type\":\"ryukyoku\",\"reason\":\"kyushukyuhai\",\"actor\":0,\"tehais\":[],\"scores\":[]}]", Opening()));
    }

    [Fact]
    public void Output_discard_checks_exact_red_identity_and_own_inventory()
    {
        Fails("MOVE_TILE_MISSING", () => AkochanReplay.ParseMoves(Batch(Dahai(0, "C")), Opening()));
        Fails("MOVE_TILE_MISSING", () => AkochanReplay.ParseMoves(Batch(Dahai(0, "5mr")), Opening()));
        var redDraw = AkochanReplay.Create([StartGame, StartRound(), Tsumo(0, "5mr")], 0, "red-draw", true);
        Assert.True(Assert.Single(AkochanReplay.ParseMoves(Batch(Dahai(0, "5mr", true)), redDraw)).Tile!.Value.Red);
        Fails("MOVE_TSUMOGIRI_CONFLICT", () => AkochanReplay.ParseMoves(Batch(Dahai(0, "5m", true)), redDraw));
    }

    [Fact]
    public void Output_calls_remove_consumed_tiles_before_validating_followup_discard()
    {
        Fails("MOVE_CONSUMED_MISSING", () => AkochanReplay.ParseMoves(
            Batch(Call("pon", 0, 3, "3m", "3m", "3m"), Dahai(0, "E")), OpponentDiscard()));
        Fails("MOVE_TILE_MISSING", () => AkochanReplay.ParseMoves(
            Batch(Call("chi", 0, 3, "3m", "1m", "2m"), Dahai(0, "1m")), OpponentDiscard()));
        var request = OpponentDiscard(tile: "5m");
        Fails("MOVE_CONSUMED_MISSING", () => AkochanReplay.ParseMoves(
            Batch(Call("pon", 0, 3, "5m", "5m", "5mr"), Dahai(0, "E")), request));
    }

    [Fact]
    public void Output_kakan_uses_an_existing_pon_instead_of_consuming_it_again_from_closed_hand()
    {
        string[] hand = ["1m", "1m", "2m", "3m", "4m", "5m", "6m", "7m", "8m", "9m", "1p", "2p", "E"];
        var request = AkochanReplay.Create([StartGame, StartRound(dealer: 3, hand: hand), Tsumo(3, "?"), Dahai(3, "1m"),
            Call("pon", 0, 3, "1m", "1m", "1m"), Dahai(0, "E"),
            Tsumo(1, "?"), Dahai(1, "S"), Tsumo(2, "?"), Dahai(2, "W"), Tsumo(3, "?"), Dahai(3, "N"),
            Tsumo(0, "1m")], 0, "own-kakan", true);
        var moves = AkochanReplay.ParseMoves(Batch(Kan("kakan", 0, "1m", "1m", "1m", "1m")), request);
        Assert.Equal("kakan", Assert.Single(moves).Type);
        Fails("MOVE_KAKAN_PON_MISSING", () => AkochanReplay.ParseMoves(
            Batch(Kan("kakan", 0, "1m", "1m", "1m", "1m")), Opening()));
    }
}
