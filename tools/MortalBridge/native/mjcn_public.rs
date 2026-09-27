// SPDX-License-Identifier: AGPL-3.0-or-later
// MJCN public-state importer for Equim-chan/Mortal 0cff2b52982be5b1163aa9a62fb01f03ce91e0d2.
// Uses current visible inventory directly, never synthesizes a starting hand or hidden draws.
use super::{PlayerState, action::ActionCandidate, item::{KawaItem, Sutehai, ChiPon}, update::MoveType};
use crate::tile::Tile;
use anyhow::{Result, ensure};
use pyo3::prelude::*;
use serde::Deserialize;

#[derive(Deserialize, Clone, Copy)]
#[serde(rename_all = "PascalCase")]
struct Face { id: usize, red: bool }
impl Face {
    fn tile(self) -> Result<Tile> {
        ensure!(self.id < 34 && (!self.red || matches!(self.id, 4 | 13 | 22)), "MORTAL_TILE_INVALID");
        Ok(Tile::try_from(if self.red { 34 + self.id / 9 } else { self.id })?)
    }
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Discard { tile: Face, tsumogiri: Option<bool>, was_claimed: bool, riichi_declaration: bool }
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Meld { r#type: String, tiles: Vec<Face> }
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Player {
    player_id: usize, seat_wind: usize, score: i32, riichi_declared: bool,
    riichi_established: bool, riichi_discard_index: Option<usize>, ippatsu: Option<bool>, double_riichi: Option<bool>,
    river: Vec<Discard>, melds: Vec<Meld>,
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Trigger { r#type: String, actor: u8, tile: Option<Face> }
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct KnownEvent {
    sequence: u64, r#type: String, actor: usize, target: Option<u8>, tile: Option<Face>, consumed: Vec<Face>,
    river_index: Option<usize>,
}
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Public {
    our_player_id: u8, round_wind: usize, hand_number: u8, honba: u8, riichi_sticks: u8,
    dealer_player_id: u8, wall_remaining: u8, hand: Vec<Face>, dora_indicators: Vec<Face>,
    players: Vec<Player>, trigger: Trigger, legal_actions: u32, known_events: Vec<KnownEvent>,
    own_draw_kind: Option<String>, own_temporary_furiten: Option<bool>, own_riichi_furiten: Option<bool>,
    #[serde(default)] match_first_round: u8,
    #[serde(default)] history_complete: bool,
}

#[pymethods]
impl PlayerState {
    #[staticmethod]
    pub fn mjcn_feature_schema() -> u8 { 2 }

    #[staticmethod]
    pub fn mjcn_from_public(json: &str) -> Result<Self> {
        ensure!(json.len() <= 1_048_576, "MORTAL_INPUT_LIMIT");
        let mut p: Public = serde_json::from_str(json)?;
        ensure!(p.our_player_id == 0 && p.players.len() == 4 && p.dealer_player_id < 4
            && p.round_wind < 4 && (1..=4).contains(&p.hand_number) && p.wall_remaining <= 70
            && matches!(p.match_first_round, 0 | 4) && p.known_events.len() <= 512
            && (1..=5).contains(&p.dora_indicators.len()), "MORTAL_PUBLIC_INVALID");
        let mut s = Self::new(0);
        s.bakaze = Tile::try_from(27 + p.round_wind)?;
        s.jikaze = Tile::try_from(27 + p.players[0].seat_wind)?;
        s.kyoku = p.hand_number - 1;
        s.honba = p.honba;
        s.kyotaku = p.riichi_sticks;
        s.oya = p.dealer_player_id;
        // MatchFirstRound is an engine schedule offset, never an instruction to change bakaze.
        s.is_all_last = p.match_first_round as usize + p.round_wind * 4 + p.hand_number as usize >= 8;
        s.tiles_left = p.wall_remaining;
        for face in &p.dora_indicators { s.add_dora_indicator(face.tile()?)?; }
        s.is_menzen = true;
        // First own discard with no calls on the table is the observable double-riichi window.
        s.can_w_riichi = p.players[0].river.is_empty() && p.players.iter().all(|x| x.melds.is_empty());
        s.is_w_riichi = p.players[0].double_riichi.unwrap_or(false);
        s.at_rinshan = p.own_draw_kind.as_deref() == Some("rinshan");
        s.at_ippatsu = p.players[0].ippatsu.unwrap_or(false);
        ensure!(p.players[0].melds.len() <= 4, "MORTAL_MELD_LIMIT");
        s.tehai_len_div3 = 4 - p.players[0].melds.len() as u8;
        let own_turn = matches!(p.trigger.r#type.as_str(), "tsumo" | "discard");
        ensure!(p.hand.len() == (if own_turn {14} else {13}) - 3 * p.players[0].melds.len(), "MORTAL_HAND_COUNT");
        for (id, player) in p.players.iter().enumerate() {
            ensure!(player.player_id == id && player.seat_wind < 4 && player.river.len() <= 24
                && player.melds.len() <= 4, "MORTAL_PLAYER_OR_RIVER_LIMIT");
            s.scores[id] = player.score;
            s.riichi_declared[id] = player.riichi_declared;
            s.riichi_accepted[id] = player.riichi_established;
            for meld in &player.melds {
                let tiles: Vec<Tile> = meld.tiles.iter().map(|t| t.tile()).collect::<Result<_>>()?;
                let kan = matches!(meld.r#type.as_str(), "ankan" | "daiminkan" | "kakan");
                ensure!(tiles.len() == if kan {4} else {3}, "MORTAL_MELD_SIZE");
                ensure!(matches!(meld.r#type.as_str(), "chi" | "pon" | "ankan" | "daiminkan" | "kakan"), "MORTAL_MELD_TYPE");
                let first = tiles.iter().map(|t| t.deaka().as_u8()).min().unwrap();
                for &tile in &tiles { s.witness_tile(tile)?; s.update_doras_owned(id, tile); }
                if meld.r#type == "ankan" { s.ankan_overview[id].push(Tile::try_from(first as usize)?); }
                else { s.fuuro_overview[id].push(tiles.iter().copied().collect()); }
                if kan { s.kans_on_board += 1; }
                if id == 0 {
                    match meld.r#type.as_str() {
                        "chi" => s.chis.push(first),
                        "pon" => s.pons.push(first),
                        "ankan" => s.ankans.push(first),
                        _ => s.minkans.push(first),
                    }
                    s.is_menzen &= meld.r#type == "ankan";
                }
            }
            for (index, discard) in player.river.iter().enumerate() {
                let tile = discard.tile.tile()?;
                // A called river tile is the same physical tile in the meld above.
                if !discard.was_claimed { s.witness_tile(tile)?; }
                let item = Sutehai { tile, is_dora: s.dora_factor[tile.deaka().as_usize()] > 0,
                    is_tedashi: discard.tsumogiri == Some(false),
                    is_riichi: discard.riichi_declaration || player.riichi_discard_index == Some(index) };
                s.kawa[id].push(Some(KawaItem { chi_pon: None, kan: Default::default(), sutehai: item }));
                s.kawa_overview[id].push(tile);
                if item.is_tedashi { s.last_tedashis[id] = Some(item); }
                if item.is_riichi { s.riichi_sutehais[id] = Some(item); }
                if id == 0 { s.discarded_tiles[tile.deaka().as_usize()] = true; }
            }
        }
        apply_history(&mut s, &mut p)?;
        for face in &p.hand { let tile = face.tile()?; s.witness_tile(tile)?; s.move_tile(tile, MoveType::Tsumo)?; }
        let drawn = if p.trigger.r#type == "tsumo" { p.trigger.tile.map(|t| t.tile()).transpose()? } else { None };
        if let Some(tile) = drawn {
            ensure!(p.trigger.actor == 0, "MORTAL_DRAW_ACTOR");
            s.move_tile(tile, MoveType::Discard)?;
            s.update_shanten(); s.update_waits_and_furiten();
            s.move_tile(tile, MoveType::Tsumo)?;
            s.last_self_tsumo = Some(tile);
        } else {
            s.update_shanten();
            if !own_turn { s.update_waits_and_furiten(); }
            else if let Some(call) = s.intermediate_chi_pon.clone() {
                // Native chi/pon keep waits from the actual pre-call 13-tile hand.
                // Reconstruct only that visible hand using the recorded consumed pair.
                let mut before = s.clone();
                before.tehai_len_div3 += 1;
                for tile in call.consumed { before.tehai[tile.deaka().as_usize()] += 1; }
                before.update_shanten(); before.update_waits_and_furiten();
                s.waits = before.waits; s.at_furiten = before.at_furiten;
                let tile = call.target_tile.deaka().as_usize();
                s.forbidden_tiles[tile] = s.tehai[tile] > 0;
                let a = call.consumed[0].deaka().as_usize();
                let b = call.consumed[1].deaka().as_usize();
                if a != b {
                    if tile < a.min(b) && a.max(b) % 9 < 8 { s.forbidden_tiles[a.max(b)+1] = s.tehai[a.max(b)+1] > 0; }
                    if tile > a.max(b) && a.min(b) % 9 > 0 { s.forbidden_tiles[a.min(b)-1] = s.tehai[a.min(b)-1] > 0; }
                }
            }
        }
        s.at_furiten |= p.own_temporary_furiten == Some(true) || p.own_riichi_furiten == Some(true);
        // A current game-offered ron is authoritative; unknown history must not suppress it.
        if p.legal_actions & 8 != 0 { s.at_furiten = false; }
        // Calls add discards without a draw; own concealed kans add a draw without
        // an extra discard. Counting only river length is wrong after these actions.
        let chi_pon = p.players[0].melds.iter().filter(|m| matches!(m.r#type.as_str(), "chi" | "pon")).count();
        let ankan = p.players[0].melds.iter().filter(|m| m.r#type == "ankan").count();
        s.at_turn = (p.players[0].river.len() + usize::from(own_turn) + ankan).saturating_sub(chi_pon) as u8;
        if p.history_complete {
            s.at_turn = p.known_events.iter().filter(|e| e.r#type == "tsumo" && e.actor == 0).count() as u8;
        }
        let f = p.legal_actions;
        s.last_cans = ActionCandidate { can_discard: own_turn, can_riichi: f & 2 != 0,
            can_tsumo_agari: f & 4 != 0, can_ron_agari: f & 8 != 0, can_ryukyoku: f & 1024 != 0,
            target_actor: p.trigger.actor, ..Default::default() };
        if own_turn {
            ensure!(!s.riichi_accepted[0] || s.last_self_tsumo.is_some(), "MORTAL_RIICHI_DRAW_MISSING");
            s.update_shanten_discards();
            restore_riichi_discard_cache(&mut s, &p)?;
            for id in 0..34 {
                if f & 64 != 0 && s.tehai[id] == 4 { s.ankan_candidates.push(Tile::try_from(id)?); }
                if f & 256 != 0 && s.tehai[id] > 0 && s.pons.contains(&(id as u8)) { s.kakan_candidates.push(Tile::try_from(id)?); }
            }
            s.last_cans.can_ankan = !s.ankan_candidates.is_empty();
            s.last_cans.can_kakan = !s.kakan_candidates.is_empty();
        } else if let Some(face) = p.trigger.tile {
            let tile = face.tile()?;
            s.last_kawa_tile = Some(tile);
            if f & 32 != 0 && p.trigger.actor == 3 { s.set_can_chi_from_tile(tile); }
            s.last_cans.can_pon = f & 16 != 0 && s.tehai[tile.deaka().as_usize()] >= 2;
            s.last_cans.can_daiminkan = f & 128 != 0 && s.tehai[tile.deaka().as_usize()] >= 3;
        }
        // Player IDs on the wire are screen-relative. Native tie-breaking uses the
        // initial East/South/West/North identity; do not always give our player first.
        s.player_id = (p.hand_number - 1 + 4 - p.dealer_player_id) % 4;
        s.last_cans.target_actor = (p.trigger.actor + s.player_id) % 4;
        s.update_rank();
        Ok(s)
    }

    pub fn mjcn_for_riichi(&self) -> Result<Self> {
        ensure!(self.last_cans.can_riichi, "MORTAL_RIICHI_NOT_AVAILABLE");
        let mut s = self.clone();
        s.riichi_declared[0] = true;
        s.last_cans.can_riichi = false;
        Ok(s)
    }

    /// Public diagnostics only; never contains opponents' concealed hands.
    pub fn mjcn_summary(&self) -> Result<String> {
        Ok(serde_json::json!({"hand":self.tehai.as_slice(),"seen":self.tiles_seen.as_slice(),
            "dora_owned":self.doras_owned,"riichi":self.riichi_declared,"scores":self.scores,
            "river_counts":self.kawa_overview.iter().map(|r|r.len()).collect::<Vec<_>>(),
            "meld_counts":(0..4).map(|i|self.fuuro_overview[i].len()+self.ankan_overview[i].len()).collect::<Vec<_>>(),
            "shanten":self.shanten,"wall":self.tiles_left,"furiten":self.at_furiten,"ippatsu":self.at_ippatsu,"double_riichi":self.is_w_riichi,"rinshan":self.at_rinshan,
            "turn":self.at_turn,"rank":self.rank,"all_last":self.is_all_last,"round_wind":self.bakaze.as_usize()-27,
            "forbidden":self.forbidden_tiles.as_slice(),"kawa":self.kawa.iter().map(|k|k.iter().collect::<Vec<_>>()).collect::<Vec<_>>()}).to_string())
    }
}

// Rebuild chronology features only from an explicitly complete recorded sequence.
// Partial captures retain gaps: a matching face alone does not prove a repeated tile's slot.
fn apply_history(s: &mut PlayerState, p: &mut Public) -> Result<()> {
    p.known_events.sort_by_key(|e| e.sequence);
    let mut cursor = [0usize; 4];
    let mut calls: [Option<ChiPon>; 4] = Default::default();
    let mut kans: [tinyvec::ArrayVec<[Tile; 4]>; 4] = Default::default();
    let mut doras = vec![p.dora_indicators[0].tile()?];
    if p.history_complete {
        ensure!(p.known_events.windows(2).all(|w| w[0].sequence < w[1].sequence), "MORTAL_HISTORY_ORDER");
        ensure!(p.known_events.first().is_some_and(|e| e.r#type == "tsumo" && e.actor == p.dealer_player_id as usize)
            && p.known_events.iter().filter(|e| e.r#type == "tsumo").count() == (70 - p.wall_remaining) as usize,
            "MORTAL_HISTORY_DRAW_COVERAGE");
        for i in 0..4 {
            ensure!(p.known_events.iter().filter(|e| e.actor == i && matches!(e.r#type.as_str(), "chi" | "pon" | "ankan" | "daiminkan")).count() == p.players[i].melds.len(),
                "MORTAL_HISTORY_MELD_COVERAGE");
            ensure!(p.known_events.iter().any(|e| e.actor == i && e.r#type == "reach") == p.players[i].riichi_declared
                && p.known_events.iter().any(|e| e.actor == i && e.r#type == "reach_accepted") == p.players[i].riichi_established,
                "MORTAL_HISTORY_RIICHI_COVERAGE");
        }
        s.kawa.iter_mut().for_each(|k| k.clear());
        s.last_tedashis.fill(None); s.riichi_sutehais.fill(None);
    }
    // This padding is defined by the known dealer, not guessed discard chronology.
    if p.history_complete { s.pad_kawa_at_start(); }
    else {
        for i in 0..p.dealer_player_id as usize { s.kawa[i].insert(0, None); }
    }
    for e in &p.known_events {
        ensure!(e.actor < 4 && e.target.is_none_or(|t| t < 4), "MORTAL_EVENT_ACTOR");
        match e.r#type.as_str() {
            "chi" | "pon" => {
                ensure!(e.consumed.len() == 2 && e.tile.is_some(), "MORTAL_CALL_EVENT");
                calls[e.actor] = Some(ChiPon { consumed: [e.consumed[0].tile()?, e.consumed[1].tile()?], target_tile: e.tile.unwrap().tile()? });
                if p.history_complete && e.r#type == "pon" {
                    ensure!(e.target.is_some(), "MORTAL_CALL_TARGET");
                    s.pad_kawa_for_pon_or_daiminkan(e.actor as u8, e.target.unwrap());
                }
            }
            "ankan" | "daiminkan" | "kakan" => {
                let tile = if e.r#type == "ankan" {
                    ensure!(e.consumed.len() == 4, "MORTAL_KAN_EVENT"); e.consumed[0].tile()?.deaka()
                } else { ensure!(e.tile.is_some(), "MORTAL_KAN_EVENT"); e.tile.unwrap().tile()? };
                ensure!(kans[e.actor].len() < 4, "MORTAL_KAN_EVENT_LIMIT");
                kans[e.actor].push(tile);
                if p.history_complete && e.r#type == "daiminkan" {
                    ensure!(e.target.is_some(), "MORTAL_CALL_TARGET");
                    s.pad_kawa_for_pon_or_daiminkan(e.actor as u8, e.target.unwrap());
                }
            }
            "dora" if p.history_complete => {
                ensure!(e.tile.is_some() && doras.len() < p.dora_indicators.len(), "MORTAL_DORA_HISTORY");
                let tile = e.tile.unwrap().tile()?;
                ensure!(tile == p.dora_indicators[doras.len()].tile()?, "MORTAL_DORA_HISTORY");
                doras.push(tile);
            }
            "dahai" => {
                ensure!(e.tile.is_some(), "MORTAL_DISCARD_EVENT");
                let tile = e.tile.unwrap().tile()?;
                let river = &p.players[e.actor].river;
                let index = if p.history_complete { Some(cursor[e.actor]) }
                    else if let Some(i) = e.river_index { Some(i) }
                    else {
                        let matches: Vec<_> = (cursor[e.actor]..river.len()).filter(|&i| river[i].tile.tile().ok() == Some(tile)).collect();
                        if matches.len() == 1 { Some(matches[0]) } else { None }
                    };
                if let Some(i) = index {
                    ensure!(i >= cursor[e.actor] && i < river.len() && river[i].tile.tile()? == tile
                        && e.river_index.is_none_or(|x| x == i), "MORTAL_EVENT_RIVER_CONFLICT");
                    if p.history_complete {
                        ensure!(river[i].tsumogiri.is_some(), "MORTAL_HISTORY_TSUMOGIRI_UNKNOWN");
                        let item = Sutehai { tile, is_dora: doras.iter().any(|d| d.next() == tile.deaka()),
                            is_tedashi: river[i].tsumogiri == Some(false),
                            is_riichi: river[i].riichi_declaration || p.players[e.actor].riichi_discard_index == Some(i) };
                        if item.is_tedashi { s.last_tedashis[e.actor] = Some(item); }
                        if item.is_riichi { s.riichi_sutehais[e.actor] = Some(item); }
                        s.kawa[e.actor].push(Some(KawaItem { chi_pon: calls[e.actor].take(), kan: std::mem::take(&mut kans[e.actor]), sutehai: item }));
                    } else {
                        // A gap before this slot cannot bind the earlier pending call to it.
                        if i == cursor[e.actor] {
                            let offset = usize::from(e.actor < p.dealer_player_id as usize);
                            let item = s.kawa[e.actor][i + offset].as_mut().unwrap();
                            item.chi_pon = calls[e.actor].take(); item.kan = std::mem::take(&mut kans[e.actor]);
                        } else { calls[e.actor] = None; kans[e.actor].clear(); }
                    }
                    cursor[e.actor] = i + 1;
                } else { calls[e.actor] = None; kans[e.actor].clear(); }
            }
            _ => (),
        }
    }
    if p.history_complete {
        ensure!((0..4).all(|i| cursor[i] == p.players[i].river.len()) && doras.len() == p.dora_indicators.len(), "MORTAL_HISTORY_INCOMPLETE");
    }
    if p.trigger.r#type == "discard" && p.known_events.last().is_some_and(|e| e.actor == 0 && matches!(e.r#type.as_str(), "chi" | "pon")) {
        s.intermediate_chi_pon = calls[0].take();
    }
    Ok(())
}

// Native tsumo deliberately preserves the pre-declaration discard feature cache
// once reach is accepted. Recover it from recorded OWN visible actions only.
// With a partial trace this cache is unknowable; do not invent an earlier hand.
fn restore_riichi_discard_cache(s: &mut PlayerState, p: &Public) -> Result<()> {
    if !p.history_complete || !s.riichi_accepted[0] { return Ok(()); }
    let reach = p.known_events.iter().position(|e| e.actor == 0 && e.r#type == "reach")
        .ok_or_else(|| anyhow::anyhow!("MORTAL_RIICHI_HISTORY"))?;
    let mut before = s.clone();
    for e in p.known_events[reach+1..].iter().rev().filter(|e| e.actor == 0) {
        match e.r#type.as_str() {
            "tsumo" | "dahai" => {
                let t = e.tile.ok_or_else(|| anyhow::anyhow!("MORTAL_OWN_HISTORY_TILE_UNKNOWN"))?.tile()?.deaka().as_usize();
                if e.r#type == "tsumo" {
                    ensure!(before.tehai[t] > 0, "MORTAL_OWN_HISTORY_CONFLICT"); before.tehai[t] -= 1;
                } else {
                    ensure!(before.tehai[t] < 4, "MORTAL_OWN_HISTORY_CONFLICT"); before.tehai[t] += 1;
                }
            }
            "ankan" => {
                for f in &e.consumed {
                    let t = f.tile()?.deaka().as_usize();
                    ensure!(before.tehai[t] < 4, "MORTAL_OWN_HISTORY_CONFLICT"); before.tehai[t] += 1;
                }
                before.tehai_len_div3 += 1;
            }
            "chi" | "pon" | "daiminkan" | "kakan" => anyhow::bail!("MORTAL_CALL_AFTER_RIICHI"),
            _ => (),
        }
    }
    let draw = p.known_events[..reach].iter().rev().find(|e| e.actor == 0 && e.r#type == "tsumo")
        .and_then(|e| e.tile).ok_or_else(|| anyhow::anyhow!("MORTAL_RIICHI_DRAW_HISTORY"))?.tile()?.deaka().as_usize();
    ensure!(before.tehai.iter().map(|&n| n as usize).sum::<usize>() == 3 * before.tehai_len_div3 as usize + 2
        && before.tehai[draw] > 0, "MORTAL_RIICHI_HISTORY_HAND");
    before.tehai[draw] -= 1; before.update_shanten(); before.tehai[draw] += 1;
    before.update_shanten_discards();
    s.keep_shanten_discards = before.keep_shanten_discards;
    s.next_shanten_discards = before.next_shanten_discards;
    s.has_next_shanten_discard = before.has_next_shanten_discard;
    Ok(())
}
