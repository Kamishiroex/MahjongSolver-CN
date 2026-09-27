"""Local Mortal JSONL replay bridge. No game callbacks or automatic table reconstruction.

Input is one genuine mjai event per line. Incomplete snapshot histories are not accepted
as start_kyoku. Opponent hands/draws and unrelated metadata are removed before inference.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
import time

from prepare import LOCK, sha256

FIELDS = {
    "start_game": (),
    "start_kyoku": ("bakaze", "dora_marker", "kyoku", "honba", "kyotaku", "oya", "scores", "tehais"),
    "tsumo": ("actor", "pai"), "dahai": ("actor", "pai", "tsumogiri"),
    "chi": ("actor", "target", "pai", "consumed"),
    "pon": ("actor", "target", "pai", "consumed"),
    "daiminkan": ("actor", "target", "pai", "consumed"),
    "ankan": ("actor", "consumed"), "kakan": ("actor", "pai", "consumed"),
    "reach": ("actor",), "reach_accepted": ("actor",), "dora": ("dora_marker",),
    # End-of-round concealed/ura hands are unnecessary for a decision and never forwarded.
    "hora": ("actor", "target"), "ryukyoku": (), "end_kyoku": (), "end_game": (),
}
TILES = {f"{i}{s}" for s in "mps" for i in range(1, 10)} | {"5mr", "5pr", "5sr", "E", "S", "W", "N", "P", "F", "C"}


def public_event(event, player):
    if type(player) is not int or player not in range(4):
        raise ValueError("MORTAL_PLAYER_ID")
    if not isinstance(event, dict) or event.get("type") not in FIELDS:
        raise ValueError("MORTAL_MJAI_EVENT_REQUIRED: partial current-state snapshots cannot be replayed")
    kind = event["type"]
    clean = {"type": kind}
    for key in FIELDS[kind]:
        if key not in event:
            raise ValueError("MORTAL_EVENT_FIELD_MISSING: " + key)
        clean[key] = event[key]
    for key in ("actor", "target", "oya"):
        if key in clean and (type(clean[key]) is not int or clean[key] not in range(4)):
            raise ValueError("MORTAL_EVENT_PLAYER")
    if kind == "start_kyoku":
        hands = clean["tehais"]
        if not isinstance(hands, list) or len(hands) != 4 or not isinstance(hands[player], list) or len(hands[player]) != 13:
            raise ValueError("MORTAL_START_HAND_REQUIRED")
        if any(tile not in TILES for tile in hands[player]):
            raise ValueError("MORTAL_OWN_HAND_UNKNOWN")
        if clean["bakaze"] not in ("E", "S", "W", "N") or type(clean["kyoku"]) is not int or clean["kyoku"] not in range(1, 5):
            raise ValueError("MORTAL_ROUND_INVALID")
        if any(type(clean[k]) is not int or clean[k] not in range(256) for k in ("honba", "kyotaku")):
            raise ValueError("MORTAL_COUNTER_INVALID")
        if not isinstance(clean["scores"], list) or len(clean["scores"]) != 4 or any(type(s) is not int or not -(2**31) <= s < 2**31 for s in clean["scores"]):
            raise ValueError("MORTAL_SCORES_INVALID")
        clean["tehais"] = [list(hands[i]) if i == player else ["?"] * 13 for i in range(4)]
    if kind == "tsumo" and clean["actor"] != player:
        clean["pai"] = "?"
    for key in ("pai", "dora_marker"):
        if key in clean and clean[key] not in TILES and not (kind == "tsumo" and clean["actor"] != player and clean[key] == "?"):
            raise ValueError("MORTAL_PUBLIC_TILE_UNKNOWN")
    if "consumed" in clean and (not isinstance(clean["consumed"], list) or any(t not in TILES for t in clean["consumed"])):
        raise ValueError("MORTAL_CONSUMED_TILE_INVALID")
    if "consumed" in clean and len(clean["consumed"]) != {"chi": 2, "pon": 2, "daiminkan": 3, "ankan": 4, "kakan": 3}[kind]:
        raise ValueError("MORTAL_CONSUMED_COUNT")
    if kind == "dahai" and type(clean["tsumogiri"]) is not bool:
        raise ValueError("MORTAL_TSUMOGIRI_UNKNOWN")
    return clean


def load_engine(directory, threads=2):
    root = Path(directory).resolve()
    manifest = json.loads((root / "runtime.json").read_text(encoding="utf-8"))
    if manifest.get("schema") != 1 or manifest.get("source_commit") != LOCK["source_commit"] or manifest.get("model_revision") != LOCK["model_revision"]:
        raise ValueError("MORTAL_RUNTIME_IDENTITY")
    for name in (LOCK["checkpoint"], "runtime/model.py", "runtime/engine.py", "runtime/libriichi.pyd"):
        if manifest.get("files", {}).get(name) != sha256(root / name):
            raise ValueError("MORTAL_RUNTIME_HASH: " + name)
    if sha256(root / LOCK["checkpoint"]) != LOCK["checkpoint_sha256"]:
        raise ValueError("MORTAL_CHECKPOINT_HASH")
    sys.path.insert(0, str(root / "runtime"))
    import torch
    import numpy
    if torch.__version__ != LOCK["torch_version"] or numpy.__version__ != LOCK["numpy_version"]:
        raise ValueError("MORTAL_PYTHON_DEPENDENCIES")
    torch.set_num_threads(threads)
    from model import Brain, DQN
    from engine import MortalEngine
    from libriichi.consts import obs_shape, ACTION_SPACE
    if list(obs_shape(4)) != LOCK["observation_shape"] or ACTION_SPACE != LOCK["action_space"]:
        raise ValueError("MORTAL_OBSERVATION_GEOMETRY")
    state = torch.load(root / LOCK["checkpoint"], weights_only=True, map_location="cpu")
    cfg = state["config"]
    if cfg["control"]["version"] != 4:
        raise ValueError("MORTAL_MODEL_VERSION")
    brain = Brain(version=4, **{k: cfg["resnet"][k] for k in ("conv_channels", "num_blocks")}).eval()
    dqn = DQN(version=4).eval()
    brain.load_state_dict(state["mortal"], strict=True)
    dqn.load_state_dict(state["current_dqn"], strict=True)
    return MortalEngine(brain, dqn, is_oracle=False, version=4, device=torch.device("cpu"),
                        enable_amp=False, enable_quick_eval=False,
                        enable_rule_based_agari_guard=True, name="MJCN-Mortal-V4-582500-offline")


def prefer_available_win(response, cans, player):
    """MJCN policy: take a currently legal win, even if Mortal prefers placement gambling.

    These cans come from a continuous replay, not guessed yaku/furiten. Future live use
    must additionally intersect them with the current CN menu before any input is sent.
    """
    if cans.can_tsumo_agari or cans.can_ron_agari:
        target = player if cans.can_tsumo_agari else cans.target_actor
        return {"type": "hora", "actor": player, "target": target}
    return response


class ReplaySession:
    def __init__(self, engine, player, *, prefer_win=True):
        from libriichi.mjai import Bot
        from libriichi.state import PlayerState
        if type(player) is not int or player not in range(4):
            raise ValueError("MORTAL_PLAYER_ID")
        self.player = player
        self.bot = Bot(engine, player)
        self.state = PlayerState(player)
        self.active = False
        self.failed = False
        self.prefer_win = prefer_win
        self.last_raw_response = None

    def feed(self, event):
        if self.failed:
            raise ValueError("MORTAL_SESSION_FAILED: start a new process with a complete replay")
        try:
            clean = public_event(event, self.player)
            kind = clean["type"]
            if kind == "start_game" and self.active:
                raise ValueError("MORTAL_GAME_RESET_DURING_ROUND")
            if kind == "start_kyoku":
                if self.active:
                    raise ValueError("MORTAL_ROUND_NOT_ENDED")
                self.active = True
            elif kind not in ("start_game", "end_game") and not self.active:
                raise ValueError("MORTAL_START_KYOKU_REQUIRED")
            line = json.dumps(clean, separators=(",", ":"))
            cans = self.state.update(line)
            began = time.perf_counter()
            reaction = self.bot.react(line)
            elapsed_ms = (time.perf_counter() - began) * 1000
            if reaction is not None:
                self.state.validate_reaction(reaction)
                response = json.loads(reaction)
                # Refuse NaN/Infinity on the external JSONL channel.
                json.dumps(response, allow_nan=False)
            else:
                response = None
            self.last_raw_response = response
            if self.prefer_win:
                response = prefer_available_win(response, cans, self.player)
                if response is not None:
                    self.state.validate_reaction(json.dumps(response))
            if response is not None and response["type"] == "none" and not cans.can_pass:
                raise ValueError("MORTAL_PASS_NOT_AVAILABLE")
            if kind in ("end_kyoku", "end_game"):
                self.active = False
            return response, elapsed_ms, cans
        except BaseException:
            # Includes native PanicException: a possibly mutated state must not be reused.
            self.failed = True
            raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--player", type=int, required=True, choices=range(4))
    parser.add_argument("--original-win-policy", action="store_true", help="Research only: permit upstream all-last win declines")
    args = parser.parse_args()
    session = ReplaySession(load_engine(args.directory), args.player, prefer_win=not args.original_win_policy)
    for number, line in enumerate(iter(lambda: sys.stdin.readline(65538), ""), 1):
        if len(line) > 65536:
            raise ValueError("MORTAL_LINE_LIMIT")
        if not line.strip():
            continue
        try:
            result, _, _ = session.feed(json.loads(line))
        except Exception as exc:
            print(json.dumps({"error": type(exc).__name__, "line": number, "message": str(exc)[:600]}), file=sys.stderr)
            return 1
        if result is not None:
            print(json.dumps(result, allow_nan=False, separators=(",", ":")), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
