using Mahjong.Cn;
using Mahjong.Cn.Engines;

namespace Mahjong.Plugin.CN.Experimental;

/// <summary>Maps a native candidate batch to one currently offered game action. Never executes future batch moves.</summary>
internal static class AkochanGlobalActionMapper
{
    internal static ActionChoice Map(IReadOnlyList<AkochanMove> moves, StateSnapshot state, AkochanGlobalSnapshot input)
    {
        if (moves.Count == 0) return Block("EMPTY_MOVES");
        if (input.OurPlayerId != 0 || input.Players.Length != 4 ||
            !input.Hand.Select(t => t.Id).Order().SequenceEqual(state.Hand.Select(t => (int)t.Id).Order()))
            return Block("HAND_OR_PLAYER_MISMATCH");
        if (moves.Any(m => m.Type != "none" && m.Actor != input.OurPlayerId)) return Block("ACTOR_MISMATCH");
        var move = moves[0];
        if (move.Type == "kyushukyuhai")
        {
            bool eligible = moves.Count == 1 && state.Legal.Can(ActionFlags.Kyushukyuhai) &&
                !state.Legal.Can(ActionFlags.Ron | ActionFlags.Tsumo) &&
                input.Trigger is { Type: "tsumo", Actor: 0 } && input.Hand.Length == 14 &&
                input.Players.All(p => p.Melds.IsEmpty && !p.RiichiDeclared && !p.RiichiEstablished) &&
                input.Players[0].River.IsEmpty && input.Players.All(p => p.River.Length <= 1) &&
                input.Hand.Where(t => t.Id >= 27 || t.Id % 9 is 0 or 8).Select(t => t.Id).Distinct().Count() >= 9;
            return eligible ? new(ActionKind.Kyushukyuhai, Reasoning: "AKOCHAN_GLOBAL: AI 选择九种幺九倒牌，流局重新发牌。")
                : Block("KYUSHUKYUHAI_NOT_OFFERED_OR_INVALID");
        }
        if (move.Type == "reach")
        {
            if (moves.Count != 2 || moves[1].Type != "dahai" || !state.Legal.Can(ActionFlags.Riichi))
                return Block("RIICHI_NOT_OFFERED");
            return Discard(moves[1], true, state, input);
        }
        if (move.Type == "dahai")
            return moves.Count == 1 ? Discard(move, false, state, input) : Block("UNEXPECTED_DISCARD_BATCH");
        if (move.Type == "none")
            return moves.Count == 1 && state.Legal.Can(ActionFlags.Pass)
                ? ActionChoice.Pass("AKOCHAN_GLOBAL: AI 选择放弃当前鸣牌或声明机会。") : Block("PASS_NOT_OFFERED");
        if (move.Type == "hora")
        {
            if (moves.Count != 1) return Block("UNEXPECTED_HORA_BATCH");
            if (move.Tile is not null && move.Tile != input.Trigger.Tile) return Block("HORA_TILE_MISMATCH");
            if (move.Target == input.OurPlayerId && input.Trigger.Type == "tsumo" &&
                input.Trigger.Actor == input.OurPlayerId && state.Legal.Can(ActionFlags.Tsumo))
                return ActionChoice.DeclareTsumo("AKOCHAN_GLOBAL: AI 选择自摸。");
            if (move.Target is >= 1 and <= 3 && input.Trigger.Actor == move.Target &&
                input.Trigger.Type is "dahai" or "kakan" && state.Legal.Can(ActionFlags.Ron))
                return ActionChoice.DeclareRon("AKOCHAN_GLOBAL: AI 选择荣和。");
            return Block("HORA_TARGET_OR_LEGAL_MISMATCH");
        }
        // Native chi/pon candidates may contain the following discard. That later action
        // is recomputed only after the game acknowledges the call and exposes the new hand.
        if (moves.Count > 2 || (moves.Count == 2 &&
            (move.Type is not ("chi" or "pon") || moves[1].Type != "dahai")))
            return Block("UNEXPECTED_CALL_BATCH");
        return Call(move, state, input);
    }

    private static ActionChoice Discard(AkochanMove move, bool reach, StateSnapshot state, AkochanGlobalSnapshot input)
    {
        if (!state.Legal.Can(reach ? ActionFlags.Riichi : ActionFlags.Discard) || move.Tile is not { } tile ||
            !input.Hand.Contains(tile) || (state.Legal.DiscardableTiles.Count > 0 &&
            !state.Legal.DiscardableTiles.Any(t => t.Id == tile.Id))) return Block("DISCARD_NOT_LEGAL");
        if (move.Tsumogiri == true && (input.Trigger.Type != "tsumo" || input.Trigger.Actor != 0 ||
            input.Trigger.Tile != tile)) return Block("DRAWN_TILE_MISMATCH");
        if (move.Tsumogiri == false && input.Trigger.Type == "tsumo" && input.Trigger.Tile == tile &&
            input.Hand.Count(t => t == tile) < 2) return Block("CLOSED_DISCARD_NOT_IN_HAND");
        return new(reach ? ActionKind.Riichi : ActionKind.Discard, Tile.FromId(tile.Id),
            Reasoning: "AKOCHAN_GLOBAL: AI 选择" + (reach ? "立直，打出" : "打出") + tile.ChineseName + "。")
        { DiscardRed = tile.Red, DiscardTsumogiri = input.Trigger.Type == "tsumo" ? move.Tsumogiri : null };
    }

    private static ActionChoice Call(AkochanMove move, StateSnapshot state, AkochanGlobalSnapshot input)
    {
        var (kind, flag, meldKind, count) = move.Type switch
        {
            "chi" => (ActionKind.Chi, ActionFlags.Chi, MeldKind.Chi, 2),
            "pon" => (ActionKind.Pon, ActionFlags.Pon, MeldKind.Pon, 2),
            "daiminkan" => (ActionKind.MinKan, ActionFlags.MinKan, MeldKind.MinKan, 3),
            "ankan" => (ActionKind.AnKan, ActionFlags.AnKan, MeldKind.AnKan, 4),
            "kakan" => (ActionKind.ShouMinKan, ActionFlags.ShouMinKan, MeldKind.ShouMinKan, 0),
            _ => (ActionKind.Pass, ActionFlags.None, default(MeldKind), 0),
        };
        if (flag == ActionFlags.None || !state.Legal.Can(flag)) return Block("CALL_NOT_OFFERED");
        bool self = move.Type is "ankan" or "kakan";
        if (!self && (input.Trigger.Type != "dahai" || move.Target != input.Trigger.Actor ||
            move.Target is not (>= 1 and <= 3) || move.Tile != input.Trigger.Tile))
            return Block("CALL_TARGET_MISMATCH");
        if (move.Type == "chi" && move.Target != 3) return Block("CHI_NOT_FROM_LEFT");
        if (self && (input.Trigger.Type != "tsumo" || input.Trigger.Actor != 0)) return Block("SELF_KAN_CONTEXT");
        if (move.Type == "kakan")
        {
            if (move.Tile is not { } added || !input.Hand.Contains(added) ||
                !input.Players[0].Melds.Any(m => m.Type == "pon" && m.Tiles.Length == 3 &&
                    m.Tiles.All(t => t.Id == added.Id) && SameTiles(m.Tiles, move.Consumed)))
                return Block("KAKAN_NOT_EXISTING_PON");
            var existing = new MeldCandidate(MeldKind.ShouMinKan, Tile.FromId(added.Id), [Tile.FromId(added.Id)], -1);
            return new(kind, Call: existing, Reasoning: "AKOCHAN_GLOBAL: AI 选择加杠。");
        }
        if (move.Consumed.Length != count || !ContainsAll(input.Hand, move.Consumed)) return Block("CALL_HAND_MISMATCH");
        var claim = move.Tile ?? (move.Type == "ankan" ? move.Consumed[0] : (VisibleTile?)null);
        if (claim is null) return Block("CALL_TILE_MISSING");
        if (move.Type != "chi" && move.Consumed.Any(t => t.Id != claim.Value.Id)) return Block("CALL_TILE_SHAPE");
        if (move.Type == "chi")
        {
            var ids = move.Consumed.Select(t => t.Id).Append(claim.Value.Id).Order().ToArray();
            if (ids[0] >= 27 || ids[0] / 9 != ids[2] / 9 || ids[1] != ids[0] + 1 || ids[2] != ids[1] + 1)
                return Block("CHI_TILE_SHAPE");
        }
        // Existing native UI selectors identify only kind IDs. If a call can consume
        // either red or ordinary copies, refuse rather than silently changing AI's choice.
        foreach (int id in move.Consumed.Select(t => t.Id).Distinct())
        {
            var held = input.Hand.Where(t => t.Id == id).ToArray();
            if (held.Select(t => t.Red).Distinct().Count() > 1 && held.Length > move.Consumed.Count(t => t.Id == id))
                return Block("CALL_RED_VARIANT_UNRESOLVED");
        }
        var candidates = move.Type switch
        {
            "chi" => state.Legal.ChiCandidates,
            "pon" => state.Legal.PonCandidates,
            _ => state.Legal.KanCandidates,
        };
        var candidate = candidates.FirstOrDefault(c => c.Kind == meldKind && c.ClaimedTile.Id == claim.Value.Id &&
            c.HandTiles.Select(t => (int)t.Id).Order().SequenceEqual(move.Consumed.Select(t => t.Id).Order()));
        if (candidate.HandTiles is null && move.Type == "ankan")
            candidate = new(MeldKind.AnKan, Tile.FromId(claim.Value.Id), move.Consumed.Select(t => Tile.FromId(t.Id)).ToArray(), -1);
        if (candidate.HandTiles is null) return Block("CALL_CANDIDATE_MISSING");
        // Legacy candidate sources are placeholders (pon/kan=1, chi=3). Preserve tile
        // legality but replace that placeholder with the observed relative player ID.
        candidate = candidate with { FromSeat = self ? -1 : move.Target!.Value };
        return new(kind, move.Type == "ankan" ? Tile.FromId(claim.Value.Id) : null, candidate,
            "AKOCHAN_GLOBAL: AI 选择" + move.Type + "。");
    }

    private static bool SameTiles(IEnumerable<VisibleTile> first, IEnumerable<VisibleTile> second) =>
        first.Select(MjaiTileCodec.Encode).Order().SequenceEqual(second.Select(MjaiTileCodec.Encode).Order());
    private static bool ContainsAll(IEnumerable<VisibleTile> hand, IEnumerable<VisibleTile> required)
    {
        var remaining = hand.ToList();
        foreach (var tile in required) if (!remaining.Remove(tile)) return false;
        return true;
    }
    private static ActionChoice Block(string code) => ActionChoice.Pass("AKOCHAN_BLOCKED: " + code);
}
