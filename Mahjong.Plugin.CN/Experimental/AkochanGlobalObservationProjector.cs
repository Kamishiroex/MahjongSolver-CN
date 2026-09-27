using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.Engines;
using Mahjong.Cn.PublicState;
using Mahjong.Core;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Experimental;

internal sealed record AkochanGlobalProjection(AkochanGlobalSnapshot? Snapshot, string? Error)
{
    internal LegalActions? ResponseLegal { get; init; }
    internal string? ErrorDetail { get; init; }
}

/// <summary>
/// An experimental CURRENT public table projection, not an invented mjai replay. Missing
/// public chronology is retained as uncertainty. No opponent concealed face is read here.
/// Framework-thread only; the returned engine input is immutable.
/// </summary>
internal sealed class AkochanGlobalObservationProjector
{
    private static readonly string[] Directions = ["bottom", "right", "top", "left"];
    private PublicSnapshot? current;
    private AddonProbe? addon;
    private PublicRiverHistoryObservation? riverProgress;
    private PublicOwnHandTransitionObservation? ownProgress;
    private string? boundary;
    private string? confirmedRound;
    private string? trackingEpoch;
    private readonly Dictionary<string, PublicCallEvent> calls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AkochanGlobalEvent Action, string Slot)> events = new(StringComparer.Ordinal);
    private readonly AkochanResponseWindow responseWindow = new();
    private readonly Dictionary<string, PublicImageTile> riverCache = new(StringComparer.Ordinal);

    internal void Clear()
    {
        current = null; addon = null; riverProgress = null; ownProgress = null;
        boundary = confirmedRound = null; calls.Clear(); events.Clear(); riverCache.Clear();
        trackingEpoch = null;
        responseWindow.Clear();
    }

    internal void Observe(PublicSnapshot? snapshot, AddonProbe? publicAddon,
        PublicRiverHistoryObservation? rivers, PublicCallTrackingResult? callProgress,
        PublicRoundProgress? round = null, PublicOwnHandTransitionObservation? ownHand = null,
        string? trackingEpochToken = null)
    {
        if (snapshot?.Observation is not { } observation || publicAddon is not
            { Name: "Emj", Present: true, Visible: true, Ready: true, Error: null })
        { Clear(); return; }
        string next = $"{snapshot.SessionId:N}:{Value(snapshot.RoundWind)}:{Value(snapshot.HandNumber)}:{Value(snapshot.Honba)}:{Value(snapshot.DealerPlayerId)}";
        string? roundId = snapshot.RoundId.IsConfirmed ? snapshot.RoundId.Value : null;
        if (boundary != next || trackingEpoch != trackingEpochToken || confirmedRound is not null && roundId is not null && confirmedRound != roundId ||
            current?.Observation is { } previous && observation.Sequence <= previous.Sequence)
        { calls.Clear(); events.Clear(); riverCache.Clear(); responseWindow.Clear(); }
        boundary = next; confirmedRound = roundId ?? confirmedRound;
        trackingEpoch = trackingEpochToken;
        current = snapshot; addon = publicAddon; riverProgress = rivers; ownProgress = ownHand;
        // Retain earlier resource-verified faces only while the same public display slot is present.
        var present = (publicAddon.PublicTableFaces ?? []).Select(x => x.SlotPath).ToHashSet(StringComparer.Ordinal);
        foreach (string path in riverCache.Keys.Where(x => !present.Contains(x)).ToArray()) riverCache.Remove(path);
        foreach (var player in snapshot.Players)
            if (player.RiverImages.Value is { } inventory)
                foreach (var tile in inventory.Tiles.Where(x => x.Stable)) riverCache[tile.SlotPath] = tile;
        if (callProgress is not null)
            foreach (var call in callProgress.Events.Where(x => trackingEpoch is not null ? x.RoundToken == trackingEpoch :
                roundId is null || x.RoundToken == roundId))
            {
                calls[call.GroupPath] = call;
                Remember(new(call.ConfirmedSequence, call.Kind, Direction(call.CallerDirection),
                    call.FromDirection is null ? null : Direction(call.FromDirection),
                    call.AddedTile is { } added ? Tile(added) : call.ClaimedTile is { } claimed ? Tile(claimed) : null,
                    call.Consumed.Select(Tile).ToImmutableArray()), call.GroupPath);
            }
        if (rivers is not null && (trackingEpoch is not null ? rivers.RoundToken == trackingEpoch :
            roundId is null || rivers.RoundToken == roundId))
            foreach (var change in rivers.NewEvents)
                Remember(new(change.Sample, change.Kind == "DiscardObserved" ? "dahai" : "called_mark",
                    Direction(change.ScreenDirection), null, Tile(change.Tile), []), change.SlotPath);
        if (round is not null)
            foreach (var signal in round.Signals)
                if (signal.Actor is { } actor)
                    Remember(new(signal.Observation.Sequence, signal.Kind == PublicRoundSignalKind.RiichiStickObserved
                        ? "riichi_stick" : "riichi_discard", (int)actor, null, signal.Tile, []), signal.SlotPath ?? "");
        if (ownHand?.Transition is { } delta)
            Remember(new(delta.AfterSample, delta.Kind == "DrawCandidate" ? "own_draw_candidate" : "own_discard_candidate",
                0, null, Tile(delta.Tile), []), "own");
        responseWindow.Observe(snapshot, publicAddon, events.Values, rivers?.UnresolvedSlotCount > 0);
    }

    internal AkochanGlobalProjection Project(StateSnapshot runtime, string contextKey)
    {
        if (current?.Observation is not { } observation || addon is null)
            return Fail("AKOCHAN_PUBLIC_TABLE_UNAVAILABLE");
        if (current.Stability != StabilityState.Stable || !current.LowerVisibleFaces.IsConfirmed ||
            current.LowerVisibleFaces.Value.IsDefaultOrEmpty)
            return Fail("AKOCHAN_PUBLIC_HAND_UNSTABLE");
        var ownTiles = current.LowerVisibleFaces.Value.Select(x => x.Tile).ToImmutableArray();
        if (!ownTiles.Select(x => x.Id).Order().SequenceEqual(runtime.Hand.Select(x => (int)x.Id).Order()))
            return Fail("AKOCHAN_PUBLIC_RUNTIME_HAND_CONFLICT");
        var assumptions = ImmutableArray.CreateBuilder<string>();
        assumptions.Add("实验全桌输入；未观测的跨玩家事件顺序、临时振听与首巡状态不视为已知。");
        assumptions.Add("赤五及精确终局计分规则沿用引擎配置；对手暗手保持未知。");
        int matchFirstRound = 0;
        if (current.Rules.MatchType.IsConfirmed && current.Rules.MatchType.Value is "east" or "hanchan")
            matchFirstRound = current.Rules.MatchType.Value == "east" ? 4 : 0;
        else assumptions.Add("当前实际对局类型未读到，暂按东南战分析；未使用排队设置推定本局类型。");
        if (current.Rules.OpenTanyao is { IsConfirmed: true, Value: false })
            return Fail("AKOCHAN_RULES_UNSUPPORTED_NO_OPEN_TANYAO") with
            { ErrorDetail = "本局为不带食断的亲友桌；当前 akochan 固定支持食断，不能按错误规则估值。" };
        if (current.Synchronization != SynchronizationState.Synchronized)
            assumptions.Add("公开事件历史不完整；以当前公开桌面和同局已记录事件分析，不伪造完整回放。");
        try
        {
            if (current.OurTemporaryFuriten.Availability==Availability.Conflict || current.OurRiichiFuriten.Availability==Availability.Conflict)
                return Fail("AKOCHAN_PUBLIC_FURITEN_CONFLICT");
            int ownWind = Number(current.Players.Single(x => x.Position == ScreenPosition.Lower).SeatWind,
                runtime.SeatInfoKnown ? runtime.OurSeat : 0, "本人门风", assumptions, 0, 3);
            var players = ImmutableArray.CreateBuilder<AkochanGlobalPlayer>(4);
            for (int id = 0; id < 4; id++)
            {
                var player = current.Players.Single(x => (int)x.Position == id);
                if (player.RiichiDeclared.Availability == Availability.Conflict ||
                    player.RiichiEstablished.Availability == Availability.Conflict)
                    return Fail("AKOCHAN_PUBLIC_RIICHI_CONFLICT");
                int wind = Number(player.SeatWind, (ownWind + id) % 4, $"玩家{id}门风", assumptions, 0, 3);
                int score = Number(player.Score, 25000, $"玩家{id}点数", assumptions, -100000, 200000);
                var river = River(player, assumptions);
                var melds = Melds(player, assumptions);
                var stick = addon.PublicRiichiCandidates?.SingleOrDefault(x => x.ScreenDirection == Directions[id]);
                bool declared = Fresh(player.RiichiDeclared, observation) ? player.RiichiDeclared.Value :
                    stick is { StickVisible: true, Code: "PUBLIC_RIICHI_STICK_CANDIDATE" } ||
                    (id == 0 && runtime.OurRiichi);
                int? riichiIndex = river.Select((x, index) => (x, index)).Where(x => x.x.RiichiDeclaration)
                    .Select(x => (int?)x.index).SingleOrDefault();
                bool established = Fresh(player.RiichiEstablished, observation) ? player.RiichiEstablished.Value : declared;
                if (!Fresh(player.RiichiEstablished, observation))
                    assumptions.Add(declared ? $"玩家{id}公开立直棒按已立直参与防守；成立时刻未独立确认。"
                        : $"玩家{id}未读到正向立直标志，暂按未立直分析。");
                players.Add(new(id, wind, score, declared, established, riichiIndex, river, melds)
                { Ippatsu = Fresh(player.Ippatsu, observation) ? player.Ippatsu.Value : null,
                    DoubleRiichi = Fresh(player.DoubleRiichi, observation) ? player.DoubleRiichi.Value : null });
            }
            if (players.Select(x => x.SeatWind).Distinct().Count() != 4 ||
                players.Any(x => x.SeatWind != (ownWind + x.PlayerId) % 4))
                return Fail("AKOCHAN_PUBLIC_SEAT_CONFLICT");
            int m = players[0].Melds.Length;
            if (ownTiles.Length != 13 - 3 * m && ownTiles.Length != 14 - 3 * m)
                return Fail("AKOCHAN_PUBLIC_OWN_MELD_HAND_COUNT_CONFLICT");
            ActionFlags legal = runtime.Legal.Flags;
            if (ownTiles.Length == 14 - 3 * m && EnabledKan())
            {
                legal &= ~ActionFlags.MinKan; // The existing popup labels every kan with a generic MinKan bit.
                if (ownTiles.GroupBy(x => x.Id).Any(g => g.Count() == 4)) legal |= ActionFlags.AnKan;
                if (players[0].Melds.Any(meld => meld.Type == "pon" && ownTiles.Any(t => t.Id == meld.Tiles[0].Id)))
                    legal |= ActionFlags.ShouMinKan;
            }
            var indicators = Dora(assumptions);
            var trigger = Trigger(runtime, players.ToImmutable(), assumptions);
            if (trigger is null) return Fail("AKOCHAN_PUBLIC_TRIGGER_UNAVAILABLE") with { ErrorDetail = responseWindow.Diagnostic };
            // A declaration menu can outlive the discard that reduced the hand to
            // 13/10/7/4/1. Do not submit that transition as an own-turn engine input:
            // engine validation would permanently pause play instead of waiting for
            // the next consistent frame. Likewise a response needs a waiting hand.
            bool ownTurn = trigger.Type is "tsumo" or "discard";
            if (ownTiles.Length != (ownTurn ? 14 : 13) - 3 * m)
                return Fail("AKOCHAN_PUBLIC_TRIGGER_HAND_TRANSITION") with
                { ErrorDetail = "动作菜单与当前手牌张数处于切换中，等待下一次一致采样。" };
            LegalActions? responseLegal = null;
            if (trigger.Type is "dahai" or "kakan")
            {
                // Current decoded menu grants permission; tile/source identities come from
                // the bound public discard, not the upstream AtkValue heuristics.
                legal = ResponseMenuFlags();
                // Dispatch row indices are computed from the current runtime flags.
                // A disagreement must wait, otherwise even a valid AI Pass can select
                // the wrong row. Candidate identities may differ; menu permissions may not.
                if (legal != runtime.Legal.Flags) return Fail("AKOCHAN_PUBLIC_MENU_LEGAL_CONFLICT");
                var derived = Mahjong.Engine.CallCandidateDeriver.Derive(runtime.Hand, Mahjong.Core.Tile.FromId(trigger.Tile!.Value.Id), trigger.Actor);
                responseLegal = new(legal, [],
                    legal.HasFlag(ActionFlags.Pon) ? derived.Pon : [],
                    legal.HasFlag(ActionFlags.Chi) ? derived.Chi : [],
                    legal.HasFlag(ActionFlags.MinKan) ? derived.Kan : []);
            }
            int dealer = Number(current.DealerPlayerId, players.Single(x => x.SeatWind == 0).PlayerId,
                "庄家", assumptions, 0, 3);
            if (players[dealer].SeatWind != 0) return Fail("AKOCHAN_PUBLIC_DEALER_CONFLICT");
            var input = new AkochanGlobalSnapshot(0,
                Number(current.RoundWind, runtime.SeatInfoKnown ? runtime.RoundWind : 0, "场风", assumptions, 0, 3),
                Number(current.HandNumber, 1, "局数", assumptions, 1, 4),
                Number(current.Honba, 0, "本场", assumptions, 0, 99),
                Number(current.RiichiSticks, 0, "供托", assumptions, 0, 99), dealer,
                Number(current.WallRemaining, Math.Clamp(runtime.WallRemaining, 0, 70), "剩余牌山", assumptions, 0, 70),
                ownTiles, indicators, players.ToImmutable(), trigger, assumptions.Distinct().ToImmutableArray())
            {
                ContextKey = contextKey + ":" + boundary + ":epoch=" + (trackingEpoch ?? "unbound"),
                Utc = observation.ObservedAtUtc, LegalActions = legal,
                MatchFirstRound = matchFirstRound,
                MatchRules = DomanMatchRules.FromObservedMatch(
                    current.Rules.MatchType.IsConfirmed ? current.Rules.MatchType.Value : null,
                    current.Rules.OpenTanyao.IsConfirmed ? current.Rules.OpenTanyao.Value : null),
                OwnTemporaryFuriten = Fresh(current.OurTemporaryFuriten,observation) ? current.OurTemporaryFuriten.Value : null,
                OwnRiichiFuriten = Fresh(current.OurRiichiFuriten,observation) ? current.OurRiichiFuriten.Value : null,
                // UI stick/called-mark changes and candidate hand transitions are not
                // mjai actions. Their current meaning already lives in the snapshot;
                // do not manufacture accepted reach/draw events from those signals.
                OwnDrawKind = trigger.Type == "tsumo" && current.OwnDrawKind.IsConfirmed &&
                    current.OwnDrawKind.Observation?.Sequence == current.Observation?.Sequence &&
                    current.OwnDrawKind.Observation?.ObservedAtUtc == current.Observation?.ObservedAtUtc ? current.OwnDrawKind.Value : null,
                HistoryComplete = false, KnownEvents = events.Values.Select(x => x.Action with
                {
                    RiverIndex = x.Action.Type == "dahai" && x.Action.Actor is >= 0 and < 4
                        ? players[x.Action.Actor].River.Select((tile, index) => (tile, index))
                            .Where(t => t.tile.SlotPath == x.Slot && t.tile.Tile == x.Action.Tile)
                            .Select(t => (int?)t.index).SingleOrDefault() : null,
                }).Where(x =>
                    x.Type is "dahai" or "chi" or "pon" or "daiminkan" or "ankan" or "kakan")
                    .OrderBy(x => x.Sequence).ToImmutableArray(),
            };
            // A called river image is the same physical tile now in its meld. Never count it twice.
            var physical = ownTiles.Concat(indicators).Concat(input.Players.SelectMany(x =>
                x.River.Where(d => !d.WasClaimed).Select(d => d.Tile).Concat(x.Melds.SelectMany(meld => meld.Tiles))));
            if (physical.GroupBy(x => x.Id).Any(g => g.Count() > 4) ||
                physical.Where(x => x.Red).GroupBy(x => x.Id).Any(g => g.Count() > 1))
                return Fail("AKOCHAN_PUBLIC_PHYSICAL_TILE_CONFLICT");
            return new(input, null) { ResponseLegal = responseLegal };
        }
        catch (ProjectionException ex) { return Fail(ex.Message); }
        catch (InvalidOperationException) { return Fail("AKOCHAN_PUBLIC_DUPLICATE_FIELD_CONFLICT"); }
    }

    private ImmutableArray<AkochanGlobalDiscard> River(PlayerPublicState player, ImmutableArray<string>.Builder assumptions)
    {
        int id = (int)player.Position;
        var result = new Dictionary<string, (AkochanGlobalDiscard Tile, int Order)>(StringComparer.Ordinal);
        var inventory = player.RiverImages.Value;
        if (player.RiverImages.Availability == Availability.Conflict) throw new ProjectionException("AKOCHAN_PUBLIC_RIVER_CONFLICT");
        if (riverProgress is { RoundToken: not null } history && (trackingEpoch is not null && history.RoundToken == trackingEpoch ||
            current!.RoundId.IsConfirmed && history.RoundToken == current.RoundId.Value))
            foreach (var slot in history.Slots.Where(x => x.ScreenDirection == Directions[id]))
                result[slot.SlotPath] = (new(Tile(slot.Tile), slot.Tsumogiri, slot.WasClaimed == true)
                { SlotPath = slot.SlotPath, ObservedSequence = slot.FirstObservedSample, RiichiDeclaration = slot.IsSideways }, slot.DisplayOrder);
        foreach (var tile in riverCache.Values.Where(t => inventory?.Tiles.Any(x => x.SlotPath == t.SlotPath) == true ||
            addon!.PublicTableFaces?.Any(x => x.SlotPath == t.SlotPath && x.Area == "river-" + Directions[id]) == true))
        {
            if (result.TryGetValue(tile.SlotPath, out var old) && old.Tile.Tile != tile.Tile)
                throw new ProjectionException("AKOCHAN_PUBLIC_RIVER_IDENTITY_CONFLICT");
            result[tile.SlotPath] = (new(tile.Tile, tile.Tsumogiri.IsConfirmed ? tile.Tsumogiri.Value : old.Tile?.Tsumogiri,
                tile.WasClaimed.IsConfirmed ? tile.WasClaimed.Value : old.Tile?.WasClaimed == true)
            { SlotPath = tile.SlotPath, ObservedSequence = old.Tile?.ObservedSequence,
                RiichiDeclaration = tile.DisplayPosition?.IsSideways == true }, tile.DisplayPosition?.ReadOrder ?? int.MaxValue);
        }
        foreach (var call in calls.Values.Where(x => x.FromDirection == Directions[id] && x.SourceRiverSlot is not null))
            if (result.TryGetValue(call.SourceRiverSlot!, out var source))
                result[call.SourceRiverSlot!] = (source.Tile with { WasClaimed = true }, source.Order);
        if (inventory is null || !inventory.AllVisibleSlotsDecoded || result.Count < inventory.VisibleSlots)
            assumptions.Add($"玩家{id}牌河部分槽位尚未识别；纳入当前可见与同局已记录的{result.Count}张，缺失牌不猜测。");
        if (result.Values.Any(x => x.Tile.Tsumogiri is null))
            assumptions.Add($"玩家{id}部分摸切／手切未知；引擎按未知处理或采用其默认值。");
        return result.Values.OrderBy(x => x.Order).ThenBy(x => x.Tile.SlotPath, StringComparer.Ordinal).Select(x => x.Tile).ToImmutableArray();
    }

    private ImmutableArray<AkochanGlobalMeld> Melds(PlayerPublicState player, ImmutableArray<string>.Builder assumptions)
    {
        int id = (int)player.Position;
        if (player.MeldImages.Availability == Availability.Conflict) throw new ProjectionException("AKOCHAN_PUBLIC_MELD_CONFLICT");
        var inventory = player.MeldImages.Value;
        if (inventory is null || !inventory.RegionReadable)
        {
            if (id == 0) throw new ProjectionException("AKOCHAN_PUBLIC_OWN_MELD_UNAVAILABLE");
            assumptions.Add($"玩家{id}副露区域未识别，暂不加入未知副露。"); return [];
        }
        var result = ImmutableArray.CreateBuilder<AkochanGlobalMeld>();
        foreach (var group in inventory.Groups)
        {
            var faces = inventory.Tiles.Where(x => x.GroupPath == group.GroupPath && x.Stable).ToArray();
            string type = group.ShapeCode switch
            {
                "PUBLIC_CHI_THREE_FACE_PATTERN" => "chi", "PUBLIC_PON_THREE_FACE_PATTERN" => "pon",
                "PUBLIC_KAN_FOUR_FACE_PATTERN" => "daiminkan", "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN" => "ankan", _ => "",
            };
            if (!group.Stable || type.Length == 0 || faces.Length != group.KnownFaces)
            {
                if (id == 0) throw new ProjectionException("AKOCHAN_PUBLIC_OWN_MELD_INCOMPLETE");
                assumptions.Add($"玩家{id}副露{group.GroupPath}未识别完整，暂未纳入。"); continue;
            }
            calls.TryGetValue(group.GroupPath, out var call);
            VisibleTile? claimed = null; int? from = null;
            var tiles = faces.Select(x => x.Tile).ToImmutableArray();
            if (type == "ankan")
            {
                if (group.InferredClosedKanKind34 is not { } kind || faces.Length != 2 || faces.Any(x => x.Tile.Id != kind))
                    throw new ProjectionException("AKOCHAN_PUBLIC_CLOSED_KAN_CONFLICT");
                tiles = tiles.AddRange(Enumerable.Repeat(new VisibleTile(kind), 2));
                if (kind is 4 or 13 or 22) assumptions.Add($"玩家{id}暗杠背面赤牌身份未知，隐藏的两张暂按普通五分析。");
            }
            else
            {
                string claimedPath = group.GroupPath + (id == 0 ? "/4/9/4" : "/4/4");
                claimed = faces.SingleOrDefault(x => x.SlotPath == claimedPath)?.Tile;
                if (call is not null)
                {
                    type = call.Kind; from = call.FromDirection is { } direction ? Direction(direction) : null;
                    claimed = call.ClaimedTile is { } observed ? Tile(observed) : claimed;
                }
                if (type == "chi" && from is null) from = (id + 3) % 4;
                if (from is null && claimed is { } publicTile)
                    from = RecoverCalledFromCurrentRivers(id, publicTile);
                if (from is null) assumptions.Add($"玩家{id}副露{group.GroupPath}来源未捕获；不把推测记成已观测来源。");
                if (claimed is null) assumptions.Add($"玩家{id}副露{group.GroupPath}被鸣牌位置未知。");
                if (type == "daiminkan" && call is null)
                    assumptions.Add($"玩家{id}四张公开杠按明杠结构估值；未观测大明杠／加杠的历史区别。");
            }
            if (tiles.Length != (type is "chi" or "pon" ? 3 : 4)) throw new ProjectionException("AKOCHAN_PUBLIC_MELD_TILE_COUNT_CONFLICT");
            result.Add(new(type, from, claimed, tiles) { GroupPath = group.GroupPath });
        }
        if (id == 0 && !inventory.ObservedEmpty && inventory.VisibleSlots > 0 && result.Count == 0)
            throw new ProjectionException("AKOCHAN_PUBLIC_OWN_MELD_INCOMPLETE");
        return result.ToImmutable();
    }

    private int? RecoverCalledFromCurrentRivers(int caller, VisibleTile claimed)
    {
        // Current called-color + physical tile identity, not an invented chronological call.
        // Require all four complete rivers, including known called flags, so an omitted same-kind
        // tile cannot make an ambiguous source look unique. A red five is a distinct physical tile.
        if (current is null || current.Players.Length != 4) return null;
        var matches = new List<int>();
        foreach (var p in current.Players)
        {
            if (p.RiverImages.Value is not { RegionReadable: true, AllVisibleSlotsDecoded: true } river ||
                river.Tiles.Any(t => !t.Stable || !t.WasClaimed.IsConfirmed)) return null;
            foreach (var t in river.Tiles)
                if (t.WasClaimed.Value && t.Tile == claimed) matches.Add((int)p.Position);
        }
        return matches.Count == 1 && matches[0] != caller ? matches[0] : null;
    }

    private AkochanGlobalTrigger? Trigger(StateSnapshot runtime, ImmutableArray<AkochanGlobalPlayer> players,
        ImmutableArray<string>.Builder assumptions)
    {
        if (runtime.Legal.Can(ActionFlags.Discard) || runtime.Legal.Can(ActionFlags.Tsumo) || runtime.Legal.Can(ActionFlags.Riichi) ||
            current!.LowerVisibleFaces.Value.Length == 14 - 3 * players[0].Melds.Length && EnabledKan())
        {
            var separate = current!.LowerVisibleFaces.Value.Where(x => x.Slot.Path == "Emj/135/9/4").ToArray();
            VisibleTile? draw = separate.Length == 1 ? separate[0].Tile : ownProgress?.DrawnTileCandidate is { } known ? Tile(known) : null;
            if (draw is not null) return new("tsumo", 0, draw);
            assumptions.Add("当前无独立摸牌身份，按可弃牌窗口分析；不把最右侧排序牌当作摸牌。");
            return new("discard", 0, null);
        }
        if (responseWindow.Assumption is { } assumption) assumptions.Add(assumption);
        return responseWindow.Trigger;
    }

    private ActionFlags ResponseMenuFlags()
    {
        ActionFlags flags = ActionFlags.None;
        if (addon?.PublicActionMenu is not { Visible: true, AllVisibleRowsDecoded: true } menu) return flags;
        foreach (var row in menu.Rows.Where(r => r.Enabled == true && r.Code == "ACTION_MENU_LABEL_CANDIDATE"))
            flags |= row.Action switch
            {
                "Pon" => ActionFlags.Pon, "Chi" => ActionFlags.Chi, "Kan" => ActionFlags.MinKan,
                "Ron" => ActionFlags.Ron, "Pass" => ActionFlags.Pass, _ => ActionFlags.None,
            };
        return flags;
    }

    private ImmutableArray<VisibleTile> Dora(ImmutableArray<string>.Builder assumptions)
    {
        if (current!.DoraDisplay.Availability == Availability.Conflict || current.DoraMode.Availability == Availability.Conflict)
            throw new ProjectionException("AKOCHAN_PUBLIC_DORA_CONFLICT");
        if (current.DoraDisplay.Availability != Availability.Known || !current.DoraDisplay.HasValue ||
            current.DoraDisplay.Value.IsDefaultOrEmpty || current.DoraMode.Availability != Availability.Known || !current.DoraMode.HasValue)
            throw new ProjectionException("AKOCHAN_PUBLIC_DORA_UNAVAILABLE");
        if (!current.DoraDisplay.IsConfirmed || !current.DoraMode.IsConfirmed)
            assumptions.Add("宝牌使用当前资源与标签候选读值；映射尚未完成独立验证。");
        return current.DoraMode.Value == DoraDisplayMode.ActualDora
            ? current.DoraDisplay.Value.Select(MjaiTileCodec.DoraIndicatorForActualDora).ToImmutableArray()
            : current.DoraMode.Value == DoraDisplayMode.Indicator ? current.DoraDisplay.Value : [];
    }

    private bool EnabledKan() => addon?.PublicActionMenu is { Visible: true, AllVisibleRowsDecoded: true } menu &&
        menu.Rows.Any(row => row.Action == "Kan" && row.Enabled == true && row.Code == "ACTION_MENU_LABEL_CANDIDATE");

    private static int Number(Field<int> field, int fallback, string name, ImmutableArray<string>.Builder assumptions, int min, int max)
    {
        if (field.Availability == Availability.Conflict) throw new ProjectionException("AKOCHAN_PUBLIC_FIELD_CONFLICT:" + name);
        if (field.Availability == Availability.Known && field.HasValue)
        {
            if (field.Value < min || field.Value > max) throw new ProjectionException("AKOCHAN_PUBLIC_FIELD_RANGE:" + name);
            if (!field.IsConfirmed) assumptions.Add(name + "使用当前候选读值，其映射尚未完成独立验证。");
            return field.Value;
        }
        assumptions.Add(name + "未知，实验暂用" + fallback + "（非观测值）。"); return fallback;
    }
    private static bool Fresh<T>(Field<T> field, ObservationReference observation) => field.IsConfirmed &&
        field.Observation!.Sequence == observation.Sequence && field.Observation.ObservedAtUtc == observation.ObservedAtUtc;
    private static string Value(Field<int> field) => field.Availability == Availability.Known && field.HasValue ? field.Value.ToString() : "?";
    private void Remember(AkochanGlobalEvent value, string slot)
    {
        events[$"{value.Sequence}:{value.Type}:{value.Actor}:{slot}"] = (value, slot);
        if (events.Count > 512)
            foreach (var key in events.OrderBy(x => x.Value.Action.Sequence).Take(events.Count - 512).Select(x => x.Key).ToArray()) events.Remove(key);
    }
    private static int Direction(string value) => Array.IndexOf(Directions, value);
    private static VisibleTile Tile(PublicCallTile tile) => new(tile.Kind34, tile.RedFive);
    private static VisibleTile Tile(LowerTileIdentity tile) => new(tile.Kind34, tile.RedFive);
    private static AkochanGlobalProjection Fail(string error) => new(null, error);
    private sealed class ProjectionException(string message) : Exception(message);
}
