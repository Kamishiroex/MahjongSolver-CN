"""Current-public-state adapter. No fabricated mjai history, no concealed opponent hands.

The trained network has no unknown-history token. Partial river features are explicitly
reported as approximation; current table inventory and game action permissions are exact
inputs and never inferred by the model. Each request reconstructs a fresh native state.
"""
import json
import math
import sys
from pathlib import Path
from runtime import load_engine
from prepare import LOCK


def tile(face):
    i = face["Id"]
    return "ESWNPFC"[i - 27] if i >= 27 else f"{i % 9 + 1}{'mps'[i // 9]}" + ("r" if face.get("Red") else "")


def tile_id(t):
    return 27 + "ESWNPFC".index(t) if len(t) == 1 else 9 * "mps".index(t[1]) + int(t[0]) - 1


def action_tile(i):
    if i >= 34:
        return "5" + "mps"[i - 34] + "r"
    return tile({"Id": i})


def choose_consumed(hand, ids):
    pool = list(hand)
    selected = []
    for i in ids:
        choices = sorted((t for t in pool if tile_id(t) == i), key=lambda t: not t.endswith("r"))
        if not choices:
            return None
        selected.append(choices[0])
        pool.remove(choices[0])
    return selected


def infer(engine, snapshot):
    from libriichi.state import PlayerState
    import numpy as np
    # C# validates the full public inventory before transport; Rust independently checks
    # limits/counts and rejects physical fifth copies. No session state survives a request.
    state = PlayerState.mjcn_from_public(json.dumps(snapshot, separators=(",", ":")))
    flags = snapshot["LegalActions"]
    hand = [tile(t) for t in snapshot["Hand"]]
    trigger = snapshot["Trigger"]
    target, face = trigger["Actor"], trigger.get("Tile")
    claim = tile(face) if face is not None else None
    warnings = list(snapshot.get("Assumptions", []))
    if not snapshot.get("HistoryComplete", False):
        warnings.append("凡夫使用当前公开桌面；缺失事件顺序不补造，历史特征采用不完整输入，可能影响棋力。")
    if any(d.get("Tsumogiri") is None for p in snapshot["Players"] for d in p["River"]):
        warnings.append("部分摸切未知：凡夫模型无未知通道，未确认手切特征留空。")
    warnings.append("模型按天凤四人半庄训练；国服东风战终局名次估值尚未专门训练。")
    # The network ranks current legal actions; available wins have explicit MJCN priority.
    # Bypass encoding on a finished hand so model yaku assumptions never veto game-offered wins.
    if flags & 12:
        move = {"type": "hora", "actor": 0, "target": 0 if flags & 4 else target, "pai": claim}
        return [{"moves": [move], "score": 0.0}], json.loads(state.mjcn_summary()), warnings
    obs, mask = state.encode_obs(4, False)
    mask = np.array(mask, copy=True)
    mask[:37] &= bool(flags & 1)
    mask[37] &= bool(flags & 2)
    mask[43] = False
    mask[44] &= bool(flags & 1024)
    mask[45] = bool(flags & 512) and trigger["Type"] != "tsumo"
    if not mask.any():
        if flags & 512:
            return [{"moves": [{"type": "none"}], "score": 0.0}], json.loads(state.mjcn_summary()), warnings
        raise ValueError("MORTAL_NO_LEGAL_MASK")
    _, qs, _, _ = engine.react_batch([obs], [mask], None)
    candidates = []
    for i in np.flatnonzero(mask):
        i = int(i)
        score = float(qs[0][i])
        if not math.isfinite(score):
            raise ValueError("MORTAL_NONFINITE_SCORE")
        move = {"actor": 0}
        if i < 37:
            move.update(type="dahai", pai=action_tile(i), tsumogiri=trigger["Type"] == "tsumo" and claim == action_tile(i))
        elif i == 37:
            declared = state.mjcn_for_riichi()
            robs, rmask = declared.encode_obs(4, False)
            rmask[37:] = False
            if not rmask.any():
                continue
            _, rqs, _, _ = engine.react_batch([robs], [rmask], None)
            for d in sorted(np.flatnonzero(rmask), key=lambda d: -rqs[0][d]):
                t = action_tile(int(d))
                candidates.append({"moves": [{"type": "reach", "actor": 0},
                    {"type": "dahai", "actor": 0, "pai": t, "tsumogiri": trigger["Type"] == "tsumo" and claim == t}],
                    "score": score})
            continue
        elif i in (38, 39, 40, 41):
            base = tile_id(claim)
            ids = {38: [base + 1, base + 2], 39: [base - 1, base + 1], 40: [base - 2, base - 1], 41: [base, base]}[i]
            consumed = choose_consumed(hand, ids)
            if consumed is None:
                continue
            move.update(type="pon" if i == 41 else "chi", target=target, pai=claim, consumed=consumed)
        elif i == 42:
            if trigger["Type"] == "dahai" and flags & 128:
                move.update(type="daiminkan", target=target, pai=claim, consumed=choose_consumed(hand, [tile_id(claim)] * 3))
            else:
                kobs, kmask = state.encode_obs(4, True)
                _, kqs, _, _ = engine.react_batch([kobs], [kmask], None)
                for k in sorted(np.flatnonzero(kmask[:34]), key=lambda k: -kqs[0][k]):
                    consumed = choose_consumed(hand, [int(k)] * 4)
                    if flags & 64 and consumed:
                        kmove = {"type": "ankan", "actor": 0, "consumed": consumed}
                    else:
                        pon = next((m for m in snapshot["Players"][0]["Melds"] if m["Type"] == "pon" and m["Tiles"][0]["Id"] == k), None)
                        if not (flags & 256 and pon):
                            continue
                        kmove = {"type": "kakan", "actor": 0, "pai": choose_consumed(hand, [int(k)])[0], "consumed": [tile(t) for t in pon["Tiles"]]}
                    candidates.append({"moves": [kmove], "score": score})
                continue
        elif i == 44:
            move.update(type="ryukyoku", reason="kyushukyuhai")
        elif i == 45:
            move = {"type": "none"}
        else:
            raise ValueError("MORTAL_ACTION_UNSUPPORTED")
        candidates.append({"moves": [move], "score": score})
    if not candidates:
        raise ValueError("MORTAL_NO_CANDIDATES")
    return sorted(candidates, key=lambda c: -c["score"]), json.loads(state.mjcn_summary()), warnings


def main():
    root = Path(sys.argv[1]).resolve()
    engine = load_engine(root)
    for line in iter(lambda: sys.stdin.readline(1048578), ""):
        if len(line) > 1048576:
            raise ValueError("MORTAL_INPUT_LIMIT")
        request = json.loads(line)
        try:
            candidates, summary, warnings = infer(engine, request["snapshot"])
            response = {"schema": 1, "input_sha256": request["input_sha256"], "candidates": candidates,
                        "applied": summary, "assumptions": warnings, "engine_commit": LOCK["source_commit"]}
        except Exception as exc:
            response = {"schema": 1, "input_sha256": request.get("input_sha256"), "error": str(exc)[:600]}
        print(json.dumps(response, ensure_ascii=True, allow_nan=False), flush=True)

if __name__ == "__main__":
    main()
