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
struct KnownEvent { sequence: u64, r#type: String, actor: usize, tile: Option<Face>, consumed: Vec<Face> }
#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Public {
    our_player_id: u8, round_wind: usize, hand_number: u8, honba: u8, riichi_sticks: u8,
    dealer_player_id: u8, wall_remaining: u8, hand: Vec<Face>, dora_indicators: Vec<Face>,
    players: Vec<Player>, trigger: Trigger, legal_actions: u32, known_events: Vec<KnownEvent>,
    own_draw_kind: Option<String>, own_temporary_furiten: Option<bool>, own_riichi_furiten: Option<bool>,
}

#[pymethods]
impl PlayerState {
    #[staticmethod]
    pub fn mjcn_from_public(json: &str) -> Result<Self> {
        ensure!(json.len() <= 1_048_576, "MORTAL_INPUT_LIMIT");
        let mut p: Public = serde_json::from_str(json)?;
        ensure!(p.our_player_id == 0 && p.players.len() == 4 && p.dealer_player_id < 4
            && p.round_wind < 4 && (1..=4).contains(&p.hand_number) && p.wall_remaining <= 70
            && (1..=5).contains(&p.dora_indicators.len()), "MORTAL_PUBLIC_INVALID");
        let mut s = Self::new(0);
        s.bakaze = Tile::try_from(27 + p.round_wind)?;
        s.jikaze = Tile::try_from(27 + p.players[0].seat_wind)?;
        s.kyoku = p.hand_number - 1;
        s.honba = p.honba;
        s.kyotaku = p.riichi_sticks;
        s.oya = p.dealer_player_id;
        // Four-player hanchan network; East-only rules remain an explicit model limitation.
        s.is_all_last = p.round_wind > 1 || p.round_wind == 1 && p.hand_number == 4;
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
        // Bind a recorded call only when the subsequent recorded discard unambiguously
        // matches the next slot in that player's river. Gaps stay unbound, never replayed.
        p.known_events.sort_by_key(|e| e.sequence);
        let mut cursor = [0usize; 4];
        let mut calls: [Option<ChiPon>; 4] = Default::default();
        for e in &p.known_events {
            ensure!(e.actor < 4, "MORTAL_EVENT_ACTOR");
            if matches!(e.r#type.as_str(), "chi" | "pon") && e.consumed.len() == 2 {
                if let Some(tile) = e.tile {
                    calls[e.actor] = Some(ChiPon { consumed: [e.consumed[0].tile()?, e.consumed[1].tile()?], target_tile: tile.tile()? });
                }
            } else if e.r#type == "dahai" {
                if let Some(face) = e.tile {
                    let tile = face.tile()?;
                    let matches: Vec<usize> = (cursor[e.actor]..s.kawa[e.actor].len())
                        .filter(|&i| s.kawa[e.actor][i].as_ref().is_some_and(|k| k.sutehai.tile == tile)).collect();
                    // Identical discarded faces cannot locate a missed history prefix.
                    if matches.len() == 1 {
                        let index = matches[0];
                        s.kawa[e.actor][index].as_mut().unwrap().chi_pon = calls[e.actor].take();
                        cursor[e.actor] = index + 1;
                    } else { calls[e.actor] = None; }
                }
            }
        }
        s.update_rank();
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
        }
        s.at_furiten |= p.own_temporary_furiten == Some(true) || p.own_riichi_furiten == Some(true);
        // A current game-offered ron is authoritative; unknown history must not suppress it.
        if p.legal_actions & 8 != 0 { s.at_furiten = false; }
        s.at_turn = (p.players[0].river.len() + usize::from(own_turn)).min(255) as u8;
        let f = p.legal_actions;
        s.last_cans = ActionCandidate { can_discard: own_turn, can_riichi: f & 2 != 0,
            can_tsumo_agari: f & 4 != 0, can_ron_agari: f & 8 != 0, can_ryukyoku: f & 1024 != 0,
            target_actor: p.trigger.actor, ..Default::default() };
        if own_turn {
            ensure!(!s.riichi_accepted[0] || s.last_self_tsumo.is_some(), "MORTAL_RIICHI_DRAW_MISSING");
            s.update_shanten_discards();
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
            "shanten":self.shanten,"wall":self.tiles_left,"furiten":self.at_furiten,"ippatsu":self.at_ippatsu,"double_riichi":self.is_w_riichi,"rinshan":self.at_rinshan}).to_string())
    }
}
