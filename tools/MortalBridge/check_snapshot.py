"""Offline native snapshot tests: real CN capture plus explicitly synthetic action windows."""
import argparse
import copy
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from runtime import load_engine
from snapshot import infer, tile_id


def face(t):
    return {"Id": tile_id(t), "Red": t.endswith("r")}


def sample(hand, flags, trigger, own_melds=None):
    return {"OurPlayerId": 0, "RoundWind": 0, "HandNumber": 1, "Honba": 0, "RiichiSticks": 0,
            "DealerPlayerId": 0, "WallRemaining": 65, "Hand": [face(t) for t in hand],
            "DoraIndicators": [face("9m")], "HistoryComplete": False, "KnownEvents": [],
            "Assumptions": ["Explicitly synthetic offline protocol test."], "ContextKey": "synthetic-mortal-protocol",
            "Utc": "2026-09-26T00:00:00+00:00",
            "LegalActions": flags, "Trigger": trigger, "OwnDrawKind": "normal" if trigger["Type"] == "tsumo" else None,
            "OwnTemporaryFuriten": False, "OwnRiichiFuriten": False,
            "Players": [{"PlayerId": i, "SeatWind": i, "Score": 25000, "RiichiDeclared": False,
                         "RiichiEstablished": False, "Ippatsu": False, "DoubleRiichi": False,
                         "RiichiDiscardIndex": None, "River": [], "Melds": own_melds or [] if i == 0 else []} for i in range(4)]}


def checks(root, repository, protocol_inputs=None):
    engine = load_engine(root)
    from libriichi.state import PlayerState
    import numpy as np
    base = "1m 2m 3m 5p 5p 4s 5s E E E S S S".split()
    start = sample(base + ["P"], 1 | 2, {"Type": "tsumo", "Actor": 0, "Tile": face("P")})
    # Initial draw has complete observable state: every model channel must match native replay.
    native = PlayerState(0)
    native.update(json.dumps({"type": "start_kyoku", "bakaze": "E", "kyoku": 1, "honba": 0,
        "kyotaku": 0, "oya": 0, "scores": [25000]*4, "dora_marker": "9m", "tehais": [base]+[["?"]*13 for _ in range(3)]}))
    native.update(json.dumps({"type": "tsumo", "actor": 0, "pai": "P"}))
    exact = copy.deepcopy(start); exact["WallRemaining"] = 69
    imported = PlayerState.mjcn_from_public(json.dumps(exact))
    a, am = native.encode_obs(4, False); b, bm = imported.encode_obs(4, False)
    diff = np.argwhere(np.abs(a-b) > 1e-6)
    if len(diff):
        raise ValueError("Initial feature mismatch: " + str(diff[:20].tolist()))
    assert np.array_equal(am, bm), "Initial action mask mismatch"
    results = [{"case": "initial-draw-all-34408-features", "result": "pass"}]
    cases = [("discard-riichi", start, {"dahai", "reach"})]
    for label, f, t, need in (("chi-and-ron", 32|8|512, "6s", {"hora"}),
                              ("chi-pass", 32|512, "6s", {"chi", "none"}),
                              ("pon-pass", 16|512, "E", {"pon", "none"}),
                              ("open-kan", 128|512, "E", {"daiminkan", "none"})):
        s = sample(base, f, {"Type": "dahai", "Actor": 3, "Tile": face(t)})
        s["Players"][3]["River"] = [{"Tile": face(t), "Tsumogiri": False, "WasClaimed": False, "RiichiDeclaration": False}]
        cases.append((label, s, need))
    cases.append(("closed-kan", sample(base+["E"], 1|64, {"Type":"tsumo","Actor":0,"Tile":face("E")}), {"ankan", "dahai"}))
    meld = {"Type":"pon", "Tiles":[face("E")]*3, "FromPlayerId":3,"ClaimedTile":face("E")}
    opened = [t for t in base if t != "E"] + ["E"]
    cases.append(("added-kan", sample(opened,1|256,{"Type":"tsumo","Actor":0,"Tile":face("E")},[meld]), {"kakan", "dahai"}))
    cases.append(("after-pon", sample(opened,1,{"Type":"discard","Actor":0,"Tile":None},[meld]), {"dahai"}))
    cases.append(("tsumo-priority", sample(base+["6s"],4|1,{"Type":"tsumo","Actor":0,"Tile":face("6s")}), {"hora"}))
    red = copy.deepcopy(start); red["Hand"][3] = face("5pr")
    cases.append(("red-five", red, {"dahai", "reach"}))
    riichi = copy.deepcopy(start); riichi["Players"][0].update(RiichiDeclared=True,RiichiEstablished=True);riichi["LegalActions"]=1
    cases.append(("riichi-forced-draw", riichi, {"dahai"}))
    captured = json.loads((repository/"tests/Mahjong.Plugin.CN.Gameplay.Tests/fixtures/kyushu-protocol-20260925.json").read_text(encoding="utf-8-sig"))["ActualPublicInput"]
    cases.append(("actual-cn-old-menu-mask", captured, {"dahai"}))
    current_kyushu = copy.deepcopy(captured); current_kyushu["LegalActions"] |= 1024
    cases.append(("same-hand-with-explicit-kyushu-menu", current_kyushu, {"ryukyoku", "dahai"}))
    for name in ("discard", "pon"):
        captured = json.loads((repository/f"tests/Mahjong.Cn.Core.Tests/fixtures/mortal-{name}-20260926.json").read_text(encoding="utf-8"))["ActualPublicInput"]
        cases.append((f"actual-cn-mortal-{name}", captured, {"dahai"} if name == "discard" else {"pon", "none"}))
    if protocol_inputs:
        protocol_inputs.write_text(json.dumps({"Cases":[{"Name":n,"Snapshot":s,"RequiredTypes":sorted(e)}
            for n,s,e in cases]},ensure_ascii=False,indent=2),encoding="utf-8")
    for name, s, expected in cases:
        cs, applied, warnings = infer(engine,s)
        kinds = {c["moves"][0]["type"] for c in cs}
        assert expected <= kinds, (name, expected, kinds)
        assert applied["hand"] == [sum(t["Id"] == i for t in s["Hand"]) for i in range(34)]
        assert applied["river_counts"] == [len(p["River"]) for p in s["Players"]]
        assert applied["meld_counts"] == [len(p["Melds"]) for p in s["Players"]]
        assert applied["wall"] == s["WallRemaining"]
        assert applied["selection_origin"] == ("game-offered-win-priority" if s["LegalActions"] & 12 else "model-ranked")
        if name == "riichi-forced-draw": assert all(c["moves"][0].get("pai") == "P" for c in cs)
        if expected == {"hora"}: assert len(cs)==1
        results.append({"case":name,"result":"pass","candidate_types":sorted(kinds)})
    broken = copy.deepcopy(start); broken["Hand"] = [face("1m")]*14
    try: infer(engine, broken)
    except Exception: results.append({"case":"fifth-copy-rejected","result":"pass"})
    else: raise AssertionError("Invalid inventory accepted")
    return results


if __name__ == "__main__":
    p=argparse.ArgumentParser();p.add_argument("--directory",type=Path,required=True);p.add_argument("--repository",type=Path,required=True);p.add_argument("--report",type=Path,required=True)
    p.add_argument("--protocol-inputs",type=Path)
    args=p.parse_args();results=checks(args.directory,args.repository,args.protocol_inputs)
    args.report.write_text(json.dumps({"passed":len(results),"cases":results,"cn_live_verified":False},indent=2),encoding="utf-8")
    print(json.dumps({"passed":len(results)}))
