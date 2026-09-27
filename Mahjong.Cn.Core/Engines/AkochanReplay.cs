using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mahjong.Cn.Engines;

/// <summary>Only fixed error codes and structural positions; never raw imported JSON or engine text.</summary>
public sealed class AkochanProtocolException : Exception
{
    public string Code { get; }
    public int EventIndex { get; }
    public string Position { get; }
    internal AkochanProtocolException(string code, int index, string position)
        : base($"{code} at event {index}, {position}") => (Code, EventIndex, Position) = (code, index, position);
}

public sealed record AkochanMove(string Type, int? Actor, int? Target, VisibleTile? Tile,
    ImmutableArray<VisibleTile> Consumed, bool? Tsumogiri);

/// <summary>
/// Strict OFFLINE single-round import for akochan's native pipe. No CN snapshot-to-event synthesis.
/// Supported public events are start_game/start_kyoku, tsumo/dahai, reach/reach_accepted,
/// chi/pon/daiminkan/ankan/kakan and dora. Terminal/results and multi-round logs are rejected.
/// Structural replay consistency is checked; this does not prove game rules, yaku or action legality.
/// </summary>
/// <remarks>
/// Pinned critter-mj/akochan 53188a0b926fbab38177f88c3cd87d554cf412af:
/// main.cpp:179-218 (pipe), share/make_move.cpp (event constructors),
/// share/types.cpp:412-529 (state consumption), ai_src/selector.cpp:1-106 (Moves batches).
/// https://github.com/critter-mj/akochan/tree/53188a0b926fbab38177f88c3cd87d554cf412af
/// </remarks>
public sealed class AkochanReplay
{
    public const int MaximumEvents = 4096;
    public const int MaximumInputBytes = 1024 * 1024;
    public const int MaximumResponseBytes = 64 * 1024;
    public int PlayerId { get; }
    public ImmutableArray<string> Events { get; }
    public string InputSha256 { get; }
    public string SourceLabel { get; }
    public bool IsSynthetic { get; }
    public bool RequiresKanDoraCounterFix { get; }
    private readonly AkochanMove trigger;
    private readonly ImmutableArray<VisibleTile> ownHand;
    private readonly ImmutableDictionary<int, ImmutableArray<VisibleTile>> ownPons;

    private AkochanReplay(int playerId, ImmutableArray<string> events, string label, bool synthetic, AkochanMove last,
        ImmutableArray<VisibleTile> hand, ImmutableDictionary<int, ImmutableArray<VisibleTile>> pons, bool requiresKanDoraCounterFix)
    {
        PlayerId = playerId; Events = events; SourceLabel = "offline:" + label; IsSynthetic = synthetic; trigger = last;
        ownHand = hand; ownPons = pons;
        RequiresKanDoraCounterFix = requiresKanDoraCounterFix;
        InputSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', events) + "\n")));
    }

    /// <summary>Label is a short ASCII slug. Transport can_act flags are normalized, never inferred game fields.</summary>
    public static AkochanReplay Create(IEnumerable<string> raw, int playerId, string label, bool synthetic)
    {
        Require(playerId is >= 0 and <= 3, "PLAYER_ID_INVALID", -1, "player-id");
        Require(raw is not null, "INPUT_MISSING", -1, "input");
        Require(label is { Length: >= 1 and <= 64 } && label.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') &&
            !label.Contains("live", StringComparison.OrdinalIgnoreCase) && !label.Contains("realtime", StringComparison.OrdinalIgnoreCase),
            "SOURCE_LABEL_INVALID", -1, "source-label");
        var elements = new List<JsonElement>();
        int bytes = 0;
        foreach (string line in raw!)
        {
            int i = elements.Count;
            Require(i < MaximumEvents, "EVENT_LIMIT", i, "input");
            Require(line is not null && line.Length <= MaximumInputBytes, "INPUT_SIZE_LIMIT", i, "input");
            bytes += Encoding.UTF8.GetByteCount(line!) + 1;
            Require(bytes <= MaximumInputBytes, "INPUT_SIZE_LIMIT", i, "input");
            using var document = Parse(line!, i, MaximumInputBytes);
            Require(document.RootElement.ValueKind == JsonValueKind.Object, "EVENT_OBJECT_REQUIRED", i, "$.");
            elements.Add(document.RootElement.Clone());
        }
        Require(elements.Count >= 3, "ROUND_PREFIX_REQUIRED", -1, "input");
        var state = new ReplayState(playerId);
        var output = ImmutableArray.CreateBuilder<string>(elements.Count);
        AkochanMove? last = null;
        bool requiresKanDoraCounterFix = false;
        bytes = 0;
        for (int i = 0; i < elements.Count; i++)
        {
            var item = elements[i];
            string type = Text(item, "type", i);
            if (i >= 2 && type == "tsumo" && Text(elements[i - 1], "type", i - 1) == "dora" &&
                Text(elements[i - 2], "type", i - 2) is "daiminkan" or "kakan") requiresKanDoraCounterFix = true;
            if (i == 0)
            {
                Require(type == "start_game", "ROUND_PREFIX_REQUIRED", i, "type");
                Keys(item, i, "type", "kyoku_first", "aka_flag", "can_act");
                int first = Integer(item, "kyoku_first", i, 0, 4);
                Require(first is 0 or 4, "RULES_UNSUPPORTED", i, "kyoku_first");
                Require(Boolean(item, "aka_flag", i), "RULES_UNSUPPORTED", i, "aka_flag");
            }
            else if (i == 1)
            {
                Require(type == "start_kyoku", "ROUND_PREFIX_REQUIRED", i, "type");
                state.Start(item, i);
            }
            else
            {
                last = ReadMove(item, i, false, playerId);
                state.Apply(last, item, i);
            }
            bool final = i == elements.Count - 1;
            if (item.TryGetProperty("can_act", out _))
                Require(Boolean(item, "can_act", i) == final, "CAN_ACT_CONFLICT", i, "can_act");
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in item.EnumerateObject())
                    if (property.Name != "can_act") property.WriteTo(writer);
                writer.WriteBoolean("can_act", final);
                writer.WriteEndObject();
            }
            string normalized = Encoding.UTF8.GetString(stream.ToArray());
            bytes += Encoding.UTF8.GetByteCount(normalized) + 1;
            Require(bytes <= MaximumInputBytes, "INPUT_SIZE_LIMIT", i, "input");
            output.Add(normalized);
        }
        Require(last is not null && ((last.Type == "tsumo" && last.Actor == playerId) ||
            (last.Type is "dahai" or "kakan" && last.Actor != playerId)), "RESPONSE_TRIGGER_REQUIRED", elements.Count - 1, "type");
        return new(playerId, output.ToImmutable(), label!, synthetic, last!, state.OwnHand, state.OwnPons, requiresKanDoraCounterFix);
    }

    /// <summary>Preserves complete Moves batches. This guard never authorizes game input.</summary>
    public static ImmutableArray<AkochanMove> ParseGlobalMoves(string response, AkochanGlobalSnapshot snapshot,
        bool mortalSingleAction = false)
    {
        var t = snapshot.Trigger;
        var trigger = new AkochanMove(t.Type == "discard" ? "tsumo" : t.Type, t.Actor, null,
            t.Tile ?? snapshot.Hand[0], [], null);
        var pons = snapshot.Players.Single(p => p.PlayerId == snapshot.OurPlayerId).Melds
            .Where(m => m.Type == "pon").ToImmutableDictionary(m => m.Tiles[0].Id, m => m.Tiles);
        // This context validates the response against the exact current hand only.
        // It is never executed or exported as an offline replay.
        var context = new AkochanReplay(snapshot.OurPlayerId, [], "global-response-validator", false,
            trigger, snapshot.Hand, pons, false);
        return ParseMoves(response, context, mortalSingleAction);
    }

    /// <summary>Preserves complete Moves batches. This guard never authorizes game input.</summary>
    public static ImmutableArray<AkochanMove> ParseMoves(string response, AkochanReplay request)
        => ParseMoves(response, request, false);

    private static ImmutableArray<AkochanMove> ParseMoves(string response, AkochanReplay request, bool mortalSingleAction)
    {
        Require(request is not null, "REQUEST_MISSING", -1, "request");
        using var document = Parse(response, -1, MaximumResponseBytes);
        var root = document.RootElement;
        Require(root.ValueKind == JsonValueKind.Array && root.GetArrayLength() is >= 1 and <= 2,
            "MOVES_ARRAY_REQUIRED", -1, "response");
        var moves = ImmutableArray.CreateBuilder<AkochanMove>();
        foreach (var item in root.EnumerateArray())
        {
            int i = moves.Count;
            Require(item.ValueKind == JsonValueKind.Object, "MOVE_OBJECT_REQUIRED", i, "response");
            // Mortal emits one action at a time (and mjai none has no actor).
            // Bind only that actor-less no-op to this request; never repair a supplied
            // wrong actor or invent the post-call discard required by akochan batches.
            AkochanMove move;
            if (mortalSingleAction && item.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "none" &&
                !item.TryGetProperty("actor", out _))
            {
                Keys(item, i, "type");
                move = new("none", request!.PlayerId, null, null, [], null);
            }
            else move = ReadMove(item, i, true, request!.PlayerId);
            Require(move.Actor == request.PlayerId, "MOVE_ACTOR_MISMATCH", i, "actor");
            moves.Add(move);
        }
        var first = moves[0];
        if (moves.Count == 2)
            Require(first.Type is "reach" or "chi" or "pon" && moves[1].Type == "dahai",
                "MOVE_BATCH_UNSUPPORTED", -1, "response");
        else Require(first.Type is "none" or "dahai" or "ankan" or "kakan" or "daiminkan" or "hora" or "kyushukyuhai" ||
            mortalSingleAction && first.Type is "chi" or "pon",
            "MOVE_BATCH_INCOMPLETE", -1, "response");
        var trigger = request!.trigger;
        if (trigger.Type == "tsumo")
            Require(first.Type is "dahai" or "reach" or "ankan" or "kakan" or "hora" or "kyushukyuhai",
                "MOVE_TRIGGER_MISMATCH", 0, "type");
        else if (trigger.Type == "kakan")
            Require(first.Type is "none" or "hora", "MOVE_TRIGGER_MISMATCH", 0, "type");
        else Require(first.Type is "none" or "hora" or "chi" or "pon" or "daiminkan",
            "MOVE_TRIGGER_MISMATCH", 0, "type");
        if (first.Type is "hora" or "chi" or "pon" or "daiminkan")
            Require(first.Target == trigger.Actor && first.Tile == trigger.Tile,
                "MOVE_TRIGGER_MISMATCH", 0, "target-pai");
        if (moves.Count == 2 && first.Type is "chi" or "pon")
            Require(moves[1].Tsumogiri == false, "MOVE_TSUMOGIRI_CONFLICT", 1, "tsumogiri");
        var discard = moves.LastOrDefault(x => x.Type == "dahai");
        if (discard?.Tsumogiri == true)
            Require(trigger.Type == "tsumo" && discard.Tile == trigger.Tile,
                "MOVE_TSUMOGIRI_CONFLICT", moves.Count - 1, "tsumogiri");
        var remaining = request.ownHand.ToList();
        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            if (move.Type is "dahai" or "kakan")
                Require(remaining.Remove(move.Tile!.Value), "MOVE_TILE_MISSING", i, "pai");
            if (move.Type is "chi" or "pon" or "daiminkan" or "ankan")
                foreach (var consumed in move.Consumed)
                    Require(remaining.Remove(consumed), "MOVE_CONSUMED_MISSING", i, "consumed");
            if (move.Type == "kakan")
                Require(request.ownPons.TryGetValue(move.Tile!.Value.Id, out var pon) &&
                    pon.OrderBy(x => x.Red).SequenceEqual(move.Consumed.OrderBy(x => x.Red)),
                    "MOVE_KAKAN_PON_MISSING", i, "consumed");
        }
        return moves.ToImmutable();
    }

    private static AkochanMove ReadMove(JsonElement item, int i, bool output, int player)
    {
        string type = Text(item, "type", i);
        // Pinned akochan make_move.cpp emits an action as ryukyoku + reason,
        // not type=kyushukyuhai. Results with scores/tehais remain forbidden here.
        if (output && type == "ryukyoku")
        {
            Keys(item, i, "type", "reason", "actor");
            Require(Text(item, "reason", i) == "kyushukyuhai", "DRAW_REASON_UNSUPPORTED", i, "reason");
            return new("kyushukyuhai", Integer(item, "actor", i, 0, 3), null, null, [], null);
        }
        string[] fields = type switch
        {
            "tsumo" when !output => ["actor", "pai"],
            "dahai" => ["actor", "pai", "tsumogiri"],
            "reach" => ["actor"],
            "reach_accepted" when !output => ["actor"],
            "dora" when !output => ["dora_marker"],
            "chi" or "pon" or "daiminkan" => ["actor", "target", "pai", "consumed"],
            "ankan" => ["actor", "consumed"],
            "kakan" => ["actor", "pai", "consumed"],
            "hora" when output => ["actor", "target", "pai"],
            "none" when output => ["actor"],
            "kyushukyuhai" when output => ["actor"],
            _ => throw Error("EVENT_UNSUPPORTED", i, "type"),
        };
        Keys(item, i, ["type", .. fields, .. (output ? Array.Empty<string>() : new[] { "can_act" })]);
        foreach (string field in fields) Require(item.TryGetProperty(field, out _), "FIELD_REQUIRED", i, field);
        int? actor = fields.Contains("actor") ? Integer(item, "actor", i, 0, 3) : null;
        int? target = fields.Contains("target") ? Integer(item, "target", i, 0, 3) : null;
        VisibleTile? tile = null;
        if (fields.Contains("pai"))
        {
            if (type == "tsumo" && actor != player)
                Require(MjaiTileCodec.IsUnknownMarker(Text(item, "pai", i)), "HIDDEN_TILE_FORBIDDEN", i, "pai");
            else tile = Tile(item.GetProperty("pai"), i, "pai");
        }
        if (type == "dora") tile = Tile(item.GetProperty("dora_marker"), i, "dora_marker");
        var consumed = ImmutableArray<VisibleTile>.Empty;
        if (fields.Contains("consumed"))
        {
            int count = type == "ankan" ? 4 : type is "kakan" or "daiminkan" ? 3 : 2;
            var array = item.GetProperty("consumed");
            Require(array.ValueKind == JsonValueKind.Array && array.GetArrayLength() == count, "CONSUMED_COUNT", i, "consumed");
            consumed = array.EnumerateArray().Select(x => Tile(x, i, "consumed")).ToImmutableArray();
            var all = tile is { } called ? consumed.Add(called) : consumed;
            if (type == "chi")
            {
                int[] ids = all.Select(x => x.Id).Order().ToArray();
                Require(ids[0] < 27 && ids[0] / 9 == ids[2] / 9 && ids[1] == ids[0] + 1 && ids[2] == ids[1] + 1,
                    "MELD_SHAPE_INVALID", i, "consumed");
                Require(target == (actor + 3) % 4, "CHI_TARGET_INVALID", i, "target");
            }
            else Require(all.All(x => x.Id == all[0].Id), "MELD_SHAPE_INVALID", i, "consumed");
            Require(all.Count(x => x.Red) <= 1, "RED_COUNT_CONFLICT", i, "consumed");
        }
        if (target is not null && type != "hora") Require(target != actor, "TARGET_INVALID", i, "target");
        bool? tsumogiri = type == "dahai" ? Boolean(item, "tsumogiri", i) : null;
        return new(type, actor, target, tile, consumed, tsumogiri);
    }

    private sealed class ReplayState(int player)
    {
        private readonly List<VisibleTile> own = [];
        private readonly Dictionary<(int Actor, int Kind), ImmutableArray<VisibleTile>> pons = [];
        private readonly bool[] reach = new bool[4], accepted = new bool[4], open = new bool[4];
        private readonly int[] melds = new int[4];
        private int turn, kanCount, doraCount = 1;
        private string phase = "draw";
        private AkochanMove? discarded;
        private VisibleTile? drawn;
        private bool hasDrawn;
        private int? reachAwaitingDiscard;
        private string? pendingAnkanEvent;
        internal ImmutableArray<VisibleTile> OwnHand => own.ToImmutableArray();
        internal ImmutableDictionary<int, ImmutableArray<VisibleTile>> OwnPons => pons
            .Where(x => x.Key.Actor == player).ToImmutableDictionary(x => x.Key.Kind, x => x.Value);

        internal void Start(JsonElement item, int i)
        {
            Keys(item, i, "type", "bakaze", "dora_marker", "honba", "kyotaku", "kyoku", "oya", "scores", "tehais", "can_act");
            Require(Text(item, "bakaze", i) is "E" or "S" or "W" or "N", "WIND_INVALID", i, "bakaze");
            Integer(item, "kyoku", i, 1, 4); Integer(item, "honba", i, 0, 100); Integer(item, "kyotaku", i, 0, 100);
            turn = Integer(item, "oya", i, 0, 3);
            Tile(Required(item, "dora_marker", i), i, "dora_marker");
            var scores = Required(item, "scores", i);
            Require(scores.ValueKind == JsonValueKind.Array && scores.GetArrayLength() == 4, "SCORES_INVALID", i, "scores");
            foreach (var score in scores.EnumerateArray())
                Require(score.ValueKind == JsonValueKind.Number && score.TryGetInt32(out int n) && n is >= -1000000 and <= 1000000 && n % 100 == 0,
                    "SCORES_INVALID", i, "scores");
            var hands = Required(item, "tehais", i);
            Require(hands.ValueKind == JsonValueKind.Array && hands.GetArrayLength() == 4, "HANDS_INVALID", i, "tehais");
            for (int actor = 0; actor < 4; actor++)
            {
                var hand = hands[actor];
                Require(hand.ValueKind == JsonValueKind.Array && hand.GetArrayLength() == 13, "HAND_COUNT_INVALID", i, "tehais");
                foreach (var tile in hand.EnumerateArray())
                    if (actor == player) own.Add(Tile(tile, i, "tehais"));
                    else Require(tile.ValueKind == JsonValueKind.String && MjaiTileCodec.IsUnknownMarker(tile.GetString()),
                        "HIDDEN_TILE_FORBIDDEN", i, "tehais");
            }
            CheckOwn(i);
        }

        internal void Apply(AkochanMove move, JsonElement item, int i)
        {
            int actor = move.Actor ?? -1;
            if (reachAwaitingDiscard is not null)
                Require(move.Type == "dahai" && actor == reachAwaitingDiscard, "REACH_ORDER_INVALID", i, "type");
            // Pinned native share/types.cpp: count_tsumo_num requires ankan -> dora ->
            // tsumo immediately. Missing the dora asserts in native code; inserting any
            // other event also makes its replacement-draw accounting incorrect. Enforce
            // the actual transport contract here instead of fabricating/reordering events.
            if (pendingAnkanEvent is not null)
                Require(move.Type == pendingAnkanEvent, "ANKAN_REPLACEMENT_ORDER_INVALID", i, "type");
            switch (move.Type)
            {
                case "tsumo":
                    Require(phase is "draw" or "response" && actor == turn, "EVENT_ORDER_INVALID", i, "actor");
                    if (actor == player) own.Add(move.Tile!.Value);
                    pendingAnkanEvent = null;
                    phase = "discard"; drawn = move.Tile; hasDrawn = true; discarded = null;
                    break;
                case "dahai":
                    Require(phase == "discard" && actor == turn, "EVENT_ORDER_INVALID", i, "actor");
                    Require(move.Tsumogiri != true || hasDrawn, "TSUMOGIRI_CONFLICT", i, "tsumogiri");
                    if (actor == player)
                    {
                        Require(move.Tsumogiri != true || move.Tile == drawn, "TSUMOGIRI_CONFLICT", i, "tsumogiri");
                        Remove(move.Tile!.Value, i);
                    }
                    phase = "response"; discarded = move; drawn = null; hasDrawn = false;
                    reachAwaitingDiscard = null; turn = (actor + 1) % 4;
                    break;
                case "reach":
                    Require(phase == "discard" && actor == turn && !reach[actor] && !open[actor], "REACH_ORDER_INVALID", i, "actor");
                    reach[actor] = true; reachAwaitingDiscard = actor;
                    break;
                case "reach_accepted":
                    Require(reach[actor] && !accepted[actor] && discarded?.Actor == actor,
                        "REACH_ORDER_INVALID", i, "actor");
                    accepted[actor] = true;
                    break;
                case "dora":
                    Require(doraCount <= kanCount && doraCount < 5, "DORA_ORDER_INVALID", i, "dora_marker");
                    doraCount++;
                    if (pendingAnkanEvent == "dora") pendingAnkanEvent = "tsumo";
                    break;
                case "chi": case "pon": case "daiminkan":
                    Require(phase == "response" && discarded is not null && discarded.Actor == move.Target && discarded.Tile == move.Tile && !reach[actor],
                        "CALL_ORDER_INVALID", i, "target-pai");
                    foreach (var tile in move.Consumed) if (actor == player) Remove(tile, i);
                    melds[actor]++;
                    open[actor] = true;
                    if (move.Type == "pon") pons[(actor, move.Tile!.Value.Id)] = move.Consumed.Add(move.Tile.Value);
                    turn = actor; drawn = null; hasDrawn = false;
                    phase = move.Type == "daiminkan" ? "draw" : "discard";
                    if (move.Type == "daiminkan") kanCount++;
                    break;
                case "ankan": case "kakan":
                    Require(phase == "discard" && actor == turn, "KAN_ORDER_INVALID", i, "actor");
                    if (move.Type == "ankan")
                    {
                        foreach (var tile in move.Consumed) if (actor == player) Remove(tile, i);
                        melds[actor]++;
                        pendingAnkanEvent = "dora";
                    }
                    else
                    {
                        Require(pons.TryGetValue((actor, move.Tile!.Value.Id), out var old) &&
                            old.OrderBy(x => x.Red).SequenceEqual(move.Consumed.OrderBy(x => x.Red)),
                            "KAKAN_PON_MISSING", i, "consumed");
                        pons.Remove((actor, move.Tile.Value.Id));
                        if (actor == player) Remove(move.Tile.Value, i);
                    }
                    kanCount++; phase = "draw"; drawn = null; hasDrawn = false; discarded = null;
                    break;
            }
            Require(kanCount <= 4 && melds.All(x => x <= 4), "KAN_OR_MELD_LIMIT", i, "type");
            CheckOwn(i);
        }

        private void Remove(VisibleTile tile, int i) => Require(own.Remove(tile), "OWN_TILE_MISSING", i, "pai-consumed");
        private void CheckOwn(int i)
        {
            Require(own.GroupBy(x => x.Id).All(g => g.Count() <= 4 && g.Count(x => x.Red) <= 1),
                "OWN_TILE_COUNT_CONFLICT", i, "tehais");
            int expected = 13 - melds[player] * 3 + (phase == "discard" && turn == player ? 1 : 0);
            Require(own.Count == expected, "OWN_HAND_COUNT_CONFLICT", i, "tehais");
        }
    }

    private static JsonDocument Parse(string raw, int i, int maximum)
    {
        Require(raw is not null && raw.Length <= maximum && Encoding.UTF8.GetByteCount(raw) <= maximum,
            "JSON_SIZE_LIMIT", i, "json");
        JsonDocument result;
        try { result = JsonDocument.Parse(raw!, new JsonDocumentOptions { MaxDepth = 8 }); }
        catch (JsonException) { throw Error("JSON_INVALID", i, "json"); }
        try { CheckDuplicates(result.RootElement, i); return result; }
        catch { result.Dispose(); throw; }
    }

    private static void CheckDuplicates(JsonElement item, int i)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            { Require(keys.Add(property.Name), "DUPLICATE_KEY", i, "json"); CheckDuplicates(property.Value, i); }
        }
        else if (item.ValueKind == JsonValueKind.Array)
            foreach (var child in item.EnumerateArray()) CheckDuplicates(child, i);
    }

    private static void Keys(JsonElement item, int i, params string[] allowed)
    {
        foreach (var property in item.EnumerateObject())
            Require(allowed.Contains(property.Name, StringComparer.Ordinal), "FIELD_FORBIDDEN", i, "object");
    }
    private static JsonElement Required(JsonElement item, string key, int i)
    { Require(item.TryGetProperty(key, out var value), "FIELD_REQUIRED", i, key); return value; }
    private static string Text(JsonElement item, string key, int i)
    { var value = Required(item, key, i); Require(value.ValueKind == JsonValueKind.String, "FIELD_TYPE", i, key); return value.GetString()!; }
    private static int Integer(JsonElement item, string key, int i, int min, int max)
    { var value = Required(item, key, i); Require(value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _), "FIELD_TYPE", i, key);
        int n = value.GetInt32(); Require(n >= min && n <= max, "FIELD_RANGE", i, key); return n; }
    private static bool Boolean(JsonElement item, string key, int i)
    { var value = Required(item, key, i); Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "FIELD_TYPE", i, key); return value.GetBoolean(); }
    private static VisibleTile Tile(JsonElement item, int i, string position)
    { Require(item.ValueKind == JsonValueKind.String, "TILE_INVALID", i, position);
        Require(MjaiTileCodec.TryDecode(item.GetString(), out var tile), "TILE_INVALID", i, position); return tile; }
    private static void Require([DoesNotReturnIf(false)] bool condition, string code, int i, string position)
    { if (!condition) throw Error(code, i, position); }
    private static AkochanProtocolException Error(string code, int i, string position) => new(code, i, position);
}
