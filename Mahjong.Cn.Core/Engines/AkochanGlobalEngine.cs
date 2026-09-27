using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mahjong.Cn.Engines;

public sealed record AkochanGlobalCandidate(ImmutableArray<AkochanMove> Moves, double Score);
public sealed record AkochanGlobalDecision(ImmutableArray<AkochanGlobalCandidate> Candidates,
    string InputSha256, ImmutableArray<string> Assumptions, string EngineCommit,
    double StartToResponseMilliseconds, string AppliedSnapshotJson)
{
    public string EngineName { get; init; } = "akochan (experimental public snapshot)";
    public bool HistoryComplete { get; init; }
}

/// <summary>
/// Sends current public state to a locally patched native selector. Unlike the offline
/// replay API, this explicitly supports a partial event prefix and does not claim it
/// is a complete real history. Opponent concealed hands have no input field.
/// </summary>
public sealed class AkochanGlobalEngine
{
    public static string CanonicalInput(AkochanGlobalSnapshot snapshot)
    {
        Validate(snapshot);
        static string Tile(VisibleTile t) => MjaiTileCodec.Encode(t);
        var state = new
        {
            schema = 1, context_key = snapshot.ContextKey, our_player = snapshot.OurPlayerId,
            match_rules = snapshot.MatchRules,
            history_slots = snapshot.KnownEvents.Where(e => e.RiverIndex is not null)
                .Select(e => new { sequence = e.Sequence, actor = e.Actor, river_index = e.RiverIndex }),
            round_wind = snapshot.RoundWind, hand_number = snapshot.HandNumber,
            honba = snapshot.Honba, riichi_sticks = snapshot.RiichiSticks,
            dealer = snapshot.DealerPlayerId, wall_remaining = snapshot.WallRemaining,
            hand = snapshot.Hand.OrderBy(x => x.Id).ThenBy(x => x.Red).Select(Tile),
            dora = snapshot.DoraIndicators.Select(Tile), history_complete = snapshot.HistoryComplete,
            legal_actions = (int)snapshot.LegalActions,
            own_draw_kind = snapshot.OwnDrawKind,
            own_temporary_furiten = snapshot.OwnTemporaryFuriten,
            own_riichi_furiten = snapshot.OwnRiichiFuriten,
            players = snapshot.Players.OrderBy(x => x.PlayerId).Select(p => new
            {
                id = p.PlayerId, seat_wind = p.SeatWind, score = p.Score,
                riichi_declared = p.RiichiDeclared, riichi_established = p.RiichiEstablished,
                ippatsu = p.Ippatsu,
                double_riichi = p.DoubleRiichi,
                river = p.River.Select((r, i) => new { pai = Tile(r.Tile), tsumogiri = r.Tsumogiri,
                    claimed = r.WasClaimed, riichi = r.RiichiDeclaration || p.RiichiDiscardIndex == i }),
                melds = p.Melds.Select(m => new { type = m.Type, from = m.FromPlayerId,
                    pai = m.Type == "ankan" ? null : Tile(m.ClaimedTile ?? m.Tiles[0]), tiles = m.Tiles.Select(Tile) }),
            }),
        };
        // start_kyoku here is a transport envelope containing a CURRENT state override;
        // no hypothetical starting hand or invented chronological events are supplied.
        var records = new List<object>
        {
            new { type = "start_game", kyoku_first = snapshot.MatchFirstRound, aka_flag = true },
            new { type = "start_kyoku", bakaze = "ESWN"[snapshot.RoundWind].ToString(),
                kyoku = snapshot.HandNumber, honba = snapshot.Honba, kyotaku = snapshot.RiichiSticks,
                oya = snapshot.DealerPlayerId, mjcn_snapshot = state },
        };
        var known = snapshot.KnownEvents.OrderBy(x => x.Sequence).ToArray();
        foreach (var e in known)
        {
            // Only real public transitions are sent as historical features. Own draws
            // are useful; other concealed draws are deliberately not accepted.
            if (e.Type == "tsumo" && e.Actor != snapshot.OurPlayerId) continue;
            if (ReferenceEquals(e, known.LastOrDefault()) && e.Type == snapshot.Trigger.Type &&
                e.Actor == snapshot.Trigger.Actor && e.Tile == snapshot.Trigger.Tile) continue;
            records.Add(new { type = e.Type, actor = e.Actor, target = e.Target,
                river_index = e.RiverIndex,
                pai = e.Tile is { } tile ? Tile(tile) : null,
                consumed = e.Consumed.Select(Tile), can_act = false });
        }
        var trigger = snapshot.Trigger;
        bool afterCall = trigger.Type == "discard";
        records.Add(new { type = afterCall ? "tsumo" : trigger.Type, actor = trigger.Actor,
            pai = Tile(trigger.Tile ?? snapshot.Hand[0]), mjcn_after_call = afterCall, can_act = true });
        return JsonSerializer.Serialize(new { record = records });
    }

    public static string ComputeInputSha256(AkochanGlobalSnapshot snapshot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalInput(snapshot))));

    public async Task<AkochanGlobalDecision> AnalyzeAsync(AkochanInstallation installation,
        AkochanGlobalSnapshot snapshot, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!installation.HasGlobalSnapshotBridge) throw new AkochanException("AKOCHAN_GLOBAL_BRIDGE_REQUIRED");
        if (!installation.HasObservedFuritenBridge && (snapshot.OwnTemporaryFuriten==true || snapshot.OwnRiichiFuriten==true))
            throw new AkochanException("AKOCHAN_OBSERVED_FURITEN_BRIDGE_REQUIRED");
        string canonical = CanonicalInput(snapshot);
        string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        using var parsed = JsonDocument.Parse(canonical);
        string payload = JsonSerializer.Serialize(new { input_sha256 = sha, record = parsed.RootElement.GetProperty("record") });
        var spec = new EngineProcessSpec(installation.Executable, installation.Directory,
            ["snapshot", installation.Tactics, snapshot.OurPlayerId.ToString(CultureInfo.InvariantCulture)])
        {
            EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("OMP_NUM_THREADS", "2")
                .Add("OMP_THREAD_LIMIT", "2").Add("OMP_DYNAMIC", "FALSE"),
        };
        var watch = Stopwatch.StartNew();
        await using var host = await EngineProcessHost.StartAsync(spec, cancellationToken).ConfigureAwait(false);
        var remaining = timeout - watch.Elapsed;
        if (remaining <= TimeSpan.Zero) throw new EngineProcessException(EngineProcessErrorCode.Timeout);
        string response = await host.ExchangeAsync(payload, remaining, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("input_sha256").GetString() != sha)
            throw new AkochanException("AKOCHAN_GLOBAL_RESPONSE_MISMATCH");
        string applied = root.GetProperty("applied").GetRawText();
        ValidateApplied(root.GetProperty("applied"), snapshot);
        if (installation.HasWinContextBridge) ValidateWinContext(root.GetProperty("applied"), snapshot, installation.HasDoubleRiichiBridge);
        if (installation.HasPartialHistoryFuritenFix && !snapshot.HistoryComplete)
            ValidatePartialFuriten(root.GetProperty("applied"), snapshot, installation.HasObservedFuritenBridge);
        if (installation.HasObservedFuritenBridge) ValidateObservedFuriten(root.GetProperty("applied"), snapshot);
        var candidates = ImmutableArray.CreateBuilder<AkochanGlobalCandidate>();
        var list = root.GetProperty("candidates");
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 256)
            throw new AkochanException("AKOCHAN_GLOBAL_CANDIDATES_INVALID");
        foreach (var item in list.EnumerateArray())
        {
            double score = item.GetProperty("score").GetDouble();
            if (!double.IsFinite(score)) continue;
            candidates.Add(new(AkochanReplay.ParseGlobalMoves(item.GetProperty("moves").GetRawText(), snapshot), score));
        }
        watch.Stop();
        var assumptions = snapshot.Assumptions.ToBuilder();
        if (!snapshot.HistoryComplete) assumptions.Add("事件历史不完整；未观察到的先后顺序不补造，历史防守特征可能缺失。");
        if (snapshot.Players.Any(p => p.Ippatsu is null)) assumptions.Add("一发资格未知时按无一发估值，实际和牌资格仍以游戏按钮为准。");
        if (snapshot.Players.Any(p => p.RiichiDeclared && p.DoubleRiichi is null))
            assumptions.Add("部分立直玩家的双立直资格未知；未补造首巡鸣牌时序。");
        if (snapshot.Players.Any(p => p.DoubleRiichi == true))
            assumptions.Add(installation.HasDoubleRiichiBridge
                ? "双立直已计入本家当前荣和／自摸估值；未来摸牌模拟与对手打点模型尚未加入此额外一番。"
                : "旧引擎未计入双立直额外一番；需要全局桥接 v4。");
        if (!snapshot.HistoryComplete)
            assumptions.Add(installation.HasPartialHistoryFuritenFix
                ? installation.HasObservedFuritenBridge
                    ? "振听使用本人牌河及连续确认的过和（含无役成型牌）；断采期间和时序不明的过和仍未知，荣和仍须游戏允许。"
                    : "振听仅使用已知本人牌河；当前引擎未接入新观察到的过和窗口，需全局桥接 v5。"
                : "旧引擎可能将部分历史误判为立直后振听；请升级到全局桥接 v3。");
        if (snapshot.Players.Any(p => p.River.Any(r => r.Tsumogiri is null))) assumptions.Add("未识别摸切的弃牌按手切估值。");
        if (snapshot.Trigger.Type == "discard") assumptions.Add("鸣牌后弃牌使用当前手牌分析入口，不把它记为一次真实摸牌。");
        if (snapshot.Trigger.Type == "tsumo" && snapshot.OwnDrawKind is null)
            assumptions.Add("当前摸牌来源未确认；岭上不加番，海底沿用引擎剩余牌数判断。");
        if (!installation.HasWinContextBridge)
            assumptions.Add("本地引擎仍为旧版桥接，尚未将本家岭上与一发附加番用于当前和牌估值；需更新本地 AI 引擎。");
        if (snapshot.Players.Any(p => p.Melds.Any(m => m.Type != "ankan" && m.ClaimedTile is null)))
            assumptions.Add("已知副露中未确定的被鸣牌位置用该组代表牌计算；完整副露牌面仍全部计数。");
        return new(candidates.ToImmutable(), sha, assumptions.ToImmutable(), installation.SourceCommit,
            watch.Elapsed.TotalMilliseconds, applied) { HistoryComplete = snapshot.HistoryComplete };
    }

    private static void Validate(AkochanGlobalSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.OurPlayerId is < 0 or > 3 || s.RoundWind is < 0 or > 3 || s.HandNumber is < 1 or > 4 ||
            s.Honba is < 0 or > 100 || s.RiichiSticks is < 0 or > 100 || s.DealerPlayerId is < 0 or > 3 ||
            s.WallRemaining is < 0 or > 70 || s.MatchFirstRound is not (0 or 4) || s.Players.Length != 4 ||
            !s.Players.Select(p => p.PlayerId).Order().SequenceEqual(new[] { 0, 1, 2, 3 }) ||
            !s.Players.Select(p => p.SeatWind).Order().SequenceEqual(new[] { 0, 1, 2, 3 }) ||
            s.Players.Single(p => p.PlayerId == s.DealerPlayerId).SeatWind != 0 ||
            s.DoraIndicators.Length is < 1 or > 5 || s.KnownEvents.Length > 4096)
            throw new AkochanException("AKOCHAN_GLOBAL_STATE_INVALID");
        var own = s.Players.Single(p => p.PlayerId == s.OurPlayerId);
        if (s.OwnRiichiFuriten==true && !own.RiichiEstablished ||
            (s.OwnTemporaryFuriten==true || s.OwnRiichiFuriten==true) && s.LegalActions.HasFlag(Mahjong.Core.ActionFlags.Ron))
            throw new AkochanException("AKOCHAN_GLOBAL_FURITEN_CONTEXT_CONFLICT");
        if (s.OwnDrawKind is not (null or "normal" or "rinshan") ||
            s.OwnDrawKind is not null && s.Trigger.Type != "tsumo" ||
            s.Players.Any(p => p.Ippatsu == true && !p.RiichiEstablished) ||
            s.Players.Any(p => p.DoubleRiichi == true && (!p.RiichiEstablished ||
                p.Melds.Any(m => m.Type != "ankan") || p.RiichiDiscardIndex is > 0 ||
                p.River.Skip(1).Any(r => r.RiichiDeclaration))) ||
            s.OwnDrawKind == "rinshan" && (own.Ippatsu == true || !own.Melds.Any(m => m.Tiles.Length == 4)))
            throw new AkochanException("AKOCHAN_GLOBAL_WIN_CONTEXT_INVALID");
        bool ownTurn = s.Trigger.Type is "tsumo" or "discard";
        if (s.Trigger.Type is not ("tsumo" or "discard" or "dahai" or "kakan") ||
            ownTurn != (s.Trigger.Actor == s.OurPlayerId) || s.Trigger.Actor is < 0 or > 3 ||
            s.Trigger.Type != "discard" && s.Trigger.Tile is null ||
            s.Hand.Length != (ownTurn ? 14 : 13) - own.Melds.Length * 3)
            throw new AkochanException("AKOCHAN_GLOBAL_TRIGGER_INVALID");
        if (s.Trigger.Type == "tsumo" && !s.Hand.Contains(s.Trigger.Tile!.Value))
            throw new AkochanException("AKOCHAN_GLOBAL_DRAW_MISSING");
        var visible = s.Hand.Concat(s.DoraIndicators).ToList();
        var marked = s.Players.SelectMany(p => p.River).Where(r => r.WasClaimed).Select(r => r.Tile).ToList();
        visible.AddRange(s.Players.SelectMany(p => p.River).Select(r => r.Tile));
        foreach (var p in s.Players)
        {
            if (p.River.Length > 40 || p.Melds.Length > 4 || p.RiichiEstablished && !p.RiichiDeclared)
                throw new AkochanException("AKOCHAN_GLOBAL_PLAYER_INVALID");
            foreach (var m in p.Melds)
            {
                if (m.Type is not ("chi" or "pon" or "daiminkan" or "ankan" or "kakan") ||
                    m.Tiles.Length != (m.Type is "chi" or "pon" ? 3 : 4) ||
                    m.FromPlayerId is < 0 or > 3 || m.FromPlayerId == p.PlayerId ||
                    m.ClaimedTile is { } claim && !m.Tiles.Contains(claim))
                    throw new AkochanException("AKOCHAN_GLOBAL_MELD_INVALID");
                var ids = m.Tiles.Select(t => t.Id).Order().ToArray();
                if (m.Type == "chi" ? ids[0] >= 27 || ids[0] / 9 != ids[2] / 9 || ids[1] != ids[0] + 1 || ids[2] != ids[1] + 1 : ids.Any(x => x != ids[0]))
                    throw new AkochanException("AKOCHAN_GLOBAL_MELD_SHAPE_INVALID");
                visible.AddRange(m.Tiles);
                if (m.Type != "ankan")
                {
                    var claimed = m.ClaimedTile ?? m.Tiles[0];
                    if (marked.Remove(claimed)) visible.Remove(claimed);
                }
            }
        }
        foreach (var tile in visible) _ = MjaiTileCodec.Encode(tile);
        if (visible.GroupBy(x => x.Id).Any(g => g.Count() > 4 || g.Count(x => x.Red) > 1))
            throw new AkochanException("AKOCHAN_GLOBAL_VISIBLE_TILE_CONFLICT");
        foreach (var e in s.KnownEvents)
        {
            if (e.Type is not ("tsumo" or "dahai" or "reach" or "reach_accepted" or "chi" or "pon" or "daiminkan" or "ankan" or "kakan") ||
                e.Actor is < 0 or > 3 || e.Target is < 0 or > 3 ||
                e.Type is "dahai" or "kakan" or "chi" or "pon" or "daiminkan" && e.Tile is null)
                throw new AkochanException("AKOCHAN_GLOBAL_HISTORY_EVENT_INVALID");
            if (e.Tile is { } tile) _ = MjaiTileCodec.Encode(tile);
            foreach (var consumed in e.Consumed) _ = MjaiTileCodec.Encode(consumed);
        }
    }

    internal static void ValidatePartialFuriten(JsonElement a, AkochanGlobalSnapshot s, bool observedFuritenBridge=false)
    {
        foreach (var player in s.Players)
        {
            var actual = a.GetProperty("players").EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == player.PlayerId);
            var blocked=observedFuritenBridge && player.PlayerId==s.OurPlayerId && (s.OwnTemporaryFuriten==true || s.OwnRiichiFuriten==true);
            var expected = (blocked ? Enumerable.Range(0,34).Select(i=>new VisibleTile(i,false)) :
                player.River.Select(r=>new VisibleTile(r.Tile.Id,false))).Select(MjaiTileCodec.Encode).Distinct().Order();
            if (!actual.GetProperty("furiten_tiles").EnumerateArray().Select(v => v.GetString()).Order().SequenceEqual(expected))
                throw new AkochanException("AKOCHAN_GLOBAL_FURITEN_APPLY_MISMATCH");
        }
    }

    private static void ValidateObservedFuriten(JsonElement a,AkochanGlobalSnapshot s)
    {
        static bool? Flag(JsonElement e)=>e.ValueKind==JsonValueKind.Null ? null : e.GetBoolean();
        if (!a.TryGetProperty("furiten_context",out var f) ||
            !f.TryGetProperty("temporary",out var t) || Flag(t)!=s.OwnTemporaryFuriten ||
            !f.TryGetProperty("after_riichi",out var r) || Flag(r)!=s.OwnRiichiFuriten)
            throw new AkochanException("AKOCHAN_GLOBAL_FURITEN_CONTEXT_NOT_APPLIED");
    }

    internal static void ValidateWinContext(JsonElement a, AkochanGlobalSnapshot s, bool doubleRiichiBridge = false)
    {
        var own = s.Players.Single(p => p.PlayerId == s.OurPlayerId);
        int last = s.WallRemaining == 0 ? 1 : 0;
        int ippatsu = (own.RiichiEstablished && own.Ippatsu == true ? 1 : 0) +
            (doubleRiichiBridge && own.RiichiEstablished && own.DoubleRiichi == true ? 1 : 0);
        int tsumo = s.Trigger.Type == "tsumo" ? (s.OwnDrawKind == "rinshan" ? 1 : last) + ippatsu : last;
        int ron = last + (s.Trigger.Type == "kakan" ? 1 : 0) + (s.Trigger.Type is "dahai" or "kakan" ? ippatsu : 0);
        if (!a.TryGetProperty("win_context", out var c) ||
            !c.TryGetProperty("own_draw_kind", out var kind) || kind.GetString() != s.OwnDrawKind ||
            !c.TryGetProperty("own_ippatsu", out var value) ||
            (value.ValueKind == JsonValueKind.Null ? (bool?)null : value.GetBoolean()) != own.Ippatsu ||
            !c.TryGetProperty("tsumo_incident_han", out var t) || t.GetInt32() != tsumo ||
            !c.TryGetProperty("ron_incident_han", out var r) || r.GetInt32() != ron)
            throw new AkochanException("AKOCHAN_GLOBAL_WIN_CONTEXT_NOT_APPLIED");
        if (doubleRiichiBridge && (!c.TryGetProperty("own_double_riichi", out var d) ||
            (d.ValueKind == JsonValueKind.Null ? (bool?)null : d.GetBoolean()) != own.DoubleRiichi))
            throw new AkochanException("AKOCHAN_GLOBAL_DOUBLE_RIICHI_NOT_APPLIED");
    }

    private static void ValidateApplied(JsonElement a, AkochanGlobalSnapshot s)
    {
        if (a.GetProperty("round_wind").GetInt32() != s.RoundWind || a.GetProperty("hand_number").GetInt32() != s.HandNumber ||
            a.GetProperty("honba").GetInt32() != s.Honba || a.GetProperty("riichi_sticks").GetInt32() != s.RiichiSticks ||
            a.GetProperty("dealer").GetInt32() != s.DealerPlayerId ||
            a.GetProperty("wall_remaining").GetInt32() != s.WallRemaining ||
            !a.GetProperty("dora").EnumerateArray().Select(x => x.GetString()).SequenceEqual(s.DoraIndicators.Select(MjaiTileCodec.Encode)))
            throw new AkochanException("AKOCHAN_GLOBAL_STATE_NOT_APPLIED");
        foreach (var p in s.Players)
        {
            var actual = a.GetProperty("players")[p.PlayerId];
            if (actual.GetProperty("score").GetInt32() != p.Score || actual.GetProperty("seat_wind").GetInt32() != p.SeatWind ||
                actual.GetProperty("riichi_declared").GetBoolean() != p.RiichiDeclared ||
                actual.GetProperty("riichi_established").GetBoolean() != p.RiichiEstablished ||
                !actual.GetProperty("river").EnumerateArray().Select(x => x.GetProperty("pai").GetString()).SequenceEqual(p.River.Select(r => MjaiTileCodec.Encode(r.Tile))) ||
                actual.GetProperty("melds").GetArrayLength() != p.Melds.Length ||
                actual.GetProperty("hand").GetArrayLength() != (p.PlayerId == s.OurPlayerId ? s.Hand.Length : 0))
                throw new AkochanException("AKOCHAN_GLOBAL_STATE_NOT_APPLIED");
            var expectedHand = p.PlayerId == s.OurPlayerId ? s.Hand : ImmutableArray<VisibleTile>.Empty;
            if (!actual.GetProperty("hand").EnumerateArray().Select(x => x.GetString()).Order().SequenceEqual(expectedHand.Select(MjaiTileCodec.Encode).Order()))
                throw new AkochanException("AKOCHAN_GLOBAL_HAND_NOT_APPLIED");
            for (int i = 0; i < p.River.Length; i++)
            {
                var river = actual.GetProperty("river")[i];
                if (river.GetProperty("tsumogiri").GetBoolean() != (p.River[i].Tsumogiri ?? false) ||
                    river.GetProperty("riichi").GetBoolean() != (p.River[i].RiichiDeclaration || p.RiichiDiscardIndex == i))
                    throw new AkochanException("AKOCHAN_GLOBAL_RIVER_STYLE_NOT_APPLIED");
            }
            for (int i = 0; i < p.Melds.Length; i++)
            {
                var meld = p.Melds[i];
                var native = actual.GetProperty("melds")[i];
                int kind = meld.Type switch { "chi" => 1, "pon" => 2, "daiminkan" => 3, "ankan" => 4, _ => 5 };
                var tiles = native.GetProperty("consumed").EnumerateArray().Select(x => x.GetString()).ToList();
                if (kind != 4) tiles.Add(native.GetProperty("pai").GetString());
                int relative = meld.FromPlayerId is { } from ? (4 + from - p.PlayerId) % 4 : 0;
                if (native.GetProperty("type").GetInt32() != kind || native.GetProperty("target_relative").GetInt32() != relative ||
                    !tiles.Order().SequenceEqual(meld.Tiles.Select(MjaiTileCodec.Encode).Order()))
                    throw new AkochanException("AKOCHAN_GLOBAL_MELD_NOT_APPLIED");
            }
        }
        var expectedVisible = s.DoraIndicators.Concat(s.Players.SelectMany(p => p.River).Select(r => r.Tile)).ToList();
        var marked = s.Players.SelectMany(p => p.River).Where(r => r.WasClaimed).Select(r => r.Tile).ToList();
        foreach (var meld in s.Players.SelectMany(p => p.Melds))
        {
            expectedVisible.AddRange(meld.Tiles);
            if (meld.Type != "ankan" && marked.Remove(meld.ClaimedTile ?? meld.Tiles[0]))
                expectedVisible.Remove(meld.ClaimedTile ?? meld.Tiles[0]);
        }
        if (!a.GetProperty("public_visible").EnumerateArray().Select(x => x.GetString()).Order()
            .SequenceEqual(expectedVisible.Select(MjaiTileCodec.Encode).Order()))
            throw new AkochanException("AKOCHAN_GLOBAL_VISIBLE_COUNTS_NOT_APPLIED");
    }
}
