"""Independent public-event reducer and full-tensor differential fixtures.

All events here are synthetic rule-valid sequences, not CN capture claims. Native
replay is the oracle. The reducer never reads native hand/history/state summaries;
only the oracle's current legal permission bits seed the synthetic game menu.
"""
import copy
import json
import sys
from pathlib import Path
# CPython's portable _pth intentionally omits the working/script directory.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from check_snapshot import face, sample
from rules import encode_public_obs, match_context


def event(kind, actor=0, **values):
    return dict(type=kind, actor=actor, **values)


def draw(actor, tile="?"):
    return event("tsumo", actor, pai=tile)


def discard(actor, tile, tsumogiri=True):
    return event("dahai", actor, pai=tile, tsumogiri=tsumogiri)


BASE = "1m 2m 3m 5p 5p 4s 5s E E E S S S".split()


def base_fixtures():
    cycle = [draw(0, "P"), discard(0, "P"), draw(1), discard(1, "7p"),
             draw(2), discard(2, "8p"), draw(3), discard(3, "9p")]
    for dealer in range(4):
        prefix = [e for actor in range(dealer,4) for e in (draw(actor),discard(actor,f"{6+actor}p"))] if dealer else []
        yield f"opening-seat-{dealer}", BASE, prefix + [draw(0, "P")], dealer
    repeated = copy.deepcopy(cycle * 2 + [draw(0, "P")])
    repeated[3]["tsumogiri"] = False
    yield "midgame-repeated-faces", BASE, repeated, 0
    chi_base = BASE[:-1] + ["3s"]
    chi = [draw(1), discard(1,"7p"), draw(2), discard(2,"8p"), draw(3), discard(3,"6s"),
           event("chi",0,target=3,pai="6s",consumed=["4s","5s"])]
    yield "after-chi-kuikae", chi_base, chi, 1
    yield "after-chi-discard-next-draw", chi_base, chi + [discard(0,"S",False),
           draw(1),discard(1,"1p"),draw(2),discard(2,"2p"),draw(3),discard(3,"3p"),draw(0,"4p")], 1
    pon = [draw(1), discard(1,"E"), event("pon",0,target=1,pai="E",consumed=["E","E"])]
    yield "after-pon-kuikae", BASE, pon, 1
    yield "after-pon-discard-next-draw", BASE, pon + [discard(0,"S",False),
           draw(1),discard(1,"1p"),draw(2),discard(2,"2p"),draw(3),discard(3,"3p"),draw(0,"4p")], 1
    yield "opponent-pon-skipped-turn", BASE, [draw(0,"P"),discard(0,"P"),
           event("pon",2,target=0,pai="P",consumed=["P","P"]),discard(2,"1p",False),
           draw(3),discard(3,"2p"),draw(0,"3p"),discard(0,"3p"),
           draw(1),discard(1,"4p"),draw(2),discard(2,"5p"),draw(3),discard(3,"6p"),draw(0,"3p")], 0
    yield "own-daiminkan-rinshan", BASE, [draw(1),discard(1,"E"),
           event("daiminkan",0,target=1,pai="E",consumed=["E"]*3),
           event("dora",dora_marker="2p"),draw(0,"4p")], 1
    yield "own-ankan-rinshan", BASE, [draw(0,"E"),event("ankan",consumed=["E"]*4),
           event("dora",dora_marker="2p"),draw(0,"4p")], 0
    yield "own-kakan-rinshan", BASE, pon + [discard(0,"S",False),draw(1),discard(1,"1p"),
           draw(2),discard(2,"2p"),draw(3),discard(3,"3p"),draw(0,"4p"),
           event("kakan",pai="E",consumed=["E"]*3),event("dora",dora_marker="4m"),draw(0,"5s")], 1
    reach = [draw(0,"P"),event("reach"),discard(0,"P"),event("reach_accepted")]
    yield "own-riichi-declared-before-discard", BASE, reach[:2], 0
    yield "own-riichi-established-next-draw", BASE, reach + [draw(1),discard(1,"7p"),
           draw(2),discard(2,"8p"),draw(3),discard(3,"9p"),draw(0,"C")], 0
    yield "opponent-riichi", BASE, [draw(0,"P"),discard(0,"P"),draw(1),event("reach",1),
           discard(1,"7p",False),event("reach_accepted",1),draw(2),discard(2,"8p"),draw(3),discard(3,"9p"),draw(0,"C")], 0
    # A discard existed before the new dora indicator. Its historical dora bit must stay false.
    yield "opponent-kan-new-dora-after-discard", BASE, [draw(0,"P"),discard(0,"P"),
           draw(1),discard(1,"3p"),draw(2),event("ankan",2,consumed=["N"]*4),
           event("dora",dora_marker="2p"),draw(2),discard(2,"1p"),draw(3),discard(3,"2s"),draw(0,"C")], 0
    yield "opponent-chi", BASE, [draw(0,"6s"),discard(0,"6s"),
           event("chi",1,target=0,pai="6s",consumed=["4s","5s"]),discard(1,"7p",False),
           draw(2),discard(2,"8p"),draw(3),discard(3,"9p"),draw(0,"C")], 0
    red = BASE.copy();red[3]="5pr"
    yield "own-red-five-pon", red, [draw(1),discard(1,"5p"),
           event("pon",0,target=1,pai="5p",consumed=["5pr","5p"])], 1
    yield "own-riichi-ankan-rinshan", BASE, reach + [draw(1),discard(1,"7p"),
           draw(2),discard(2,"8p"),draw(3),discard(3,"9p"),draw(0,"E"),
           event("ankan",consumed=["E"]*4),event("dora",dora_marker="2p"),draw(0,"4p")], 0


def fixtures():
    for name,base,events,dealer in base_fixtures():
        yield name,base,events,dealer
        if name.startswith("own-") and name.endswith("rinshan"):
            yield name+"-next-turn",base,events+[discard(0,events[-1]["pai"]),
                draw(1),discard(1,"7m"),draw(2),discard(2,"8m"),draw(3),discard(3,"9m"),draw(0,"7m")],dealer


def reduce_public(base, events, dealer):
    s = sample(base, 0, {"Type":"tsumo","Actor":0,"Tile":None})
    s.update(DealerPlayerId=dealer, WallRemaining=70, HistoryComplete=True, MatchFirstRound=0,
             MatchRules={"SchemaVersion":1,"ProfileId":"doman-public-v1","MatchType":"hanchan","ScheduledHands":8})
    for i,p in enumerate(s["Players"]): p["SeatWind"]=(i-dealer)%4
    hand = list(base); pending_kan=False
    for seq,e in enumerate(events,1):
        kind,actor=e["type"],e.get("actor",0); player=s["Players"][actor]
        known={"Sequence":seq,"Type":kind,"Actor":actor,"Target":e.get("target"),
               "Tile":face(e.get("pai",e.get("dora_marker"))) if e.get("pai",e.get("dora_marker")) not in (None,"?") else None,
               "Consumed":[face(t) for t in e.get("consumed",[])],"RiverIndex":None}
        if kind=="tsumo":
            s["WallRemaining"]-=1
            if actor==0:
                hand.append(e["pai"]);s["Trigger"]={"Type":"tsumo","Actor":0,"Tile":face(e["pai"])}
                s["OwnDrawKind"]="rinshan" if pending_kan else "normal";pending_kan=False
        elif kind=="dahai":
            declaration=player["RiichiDeclared"] and not player["RiichiEstablished"]
            known["RiverIndex"]=len(player["River"])
            if declaration:player["RiichiDiscardIndex"]=known["RiverIndex"]
            player["River"].append({"Tile":face(e["pai"]),"Tsumogiri":e["tsumogiri"],"WasClaimed":False,"RiichiDeclaration":declaration})
            if actor==0:hand.remove(e["pai"]);player["Ippatsu"]=False
            s["Trigger"]={"Type":"dahai","Actor":actor,"Tile":face(e["pai"])};s["OwnDrawKind"]=None
        elif kind in ("chi","pon","daiminkan","ankan","kakan"):
            for p in s["Players"]:p["Ippatsu"]=False
            if kind in ("chi","pon","daiminkan"):
                s["Players"][e["target"]]["River"][-1]["WasClaimed"]=True
                tiles=e["consumed"]+[e["pai"]]
                player["Melds"].append({"Type":kind,"Tiles":[face(t) for t in tiles],"FromPlayerId":e["target"],"ClaimedTile":face(e["pai"])})
                used=e["consumed"]
            elif kind=="ankan":
                player["Melds"].append({"Type":kind,"Tiles":[face(t) for t in e["consumed"]],"FromPlayerId":None,"ClaimedTile":None});used=e["consumed"]
            else:
                meld=next(m for m in player["Melds"] if m["Type"]=="pon" and m["Tiles"][0]["Id"]==face(e["pai"])["Id"])
                meld["Type"]="kakan";meld["Tiles"].append(face(e["pai"]));used=[e["pai"]]
            if actor==0:
                for t in used:hand.remove(t)
                pending_kan=kind in ("ankan","daiminkan","kakan")
                if not pending_kan:s["Trigger"]={"Type":"discard","Actor":0,"Tile":None};s["OwnDrawKind"]=None
        elif kind=="dora":s["DoraIndicators"].append(face(e["dora_marker"]))
        elif kind=="reach":
            player["RiichiDeclared"]=True
            player["DoubleRiichi"]=not player["River"] and not any(p["Melds"] for p in s["Players"])
        elif kind=="reach_accepted":
            player["RiichiEstablished"]=True;player["Ippatsu"]=True;player["Score"]-=1000;s["RiichiSticks"]+=1
        s["KnownEvents"].append(known)
    s["Hand"]=[face(t) for t in hand]
    return s


def native_replay(PlayerState, base, events, dealer):
    pid=(-dealer)%4
    state=PlayerState(pid)
    hands=[["?"]*13 for _ in range(4)];hands[pid]=base
    state.update(json.dumps(dict(type="start_kyoku",bakaze="E",kyoku=1,honba=0,kyotaku=0,
                               oya=(dealer+pid)%4,scores=[25000]*4,dora_marker="9m",tehais=hands)))
    for e in events:
        e=copy.deepcopy(e)
        for field in ("actor","target"):
            if field in e:e[field]=(e[field]+pid)%4
        cans=state.update(json.dumps(e))
    flags=sum(bit for key,bit in [("can_discard",1),("can_riichi",2),("can_tsumo_agari",4),
                ("can_ron_agari",8),("can_pon",16),("can_chi",32),("can_ankan",64),
                ("can_daiminkan",128),("can_kakan",256),("can_pass",512),("can_ryukyoku",1024)] if getattr(cans,key))
    return state,flags


def difference(a, am, b, bm):
    import numpy as np
    changed=np.argwhere(np.abs(a-b)>1e-6)
    rows=sorted(set(int(r) for r,_ in changed))
    def channel(row):
        if row < 35:return "hand-score-seat-round"
        if row < 132:return "own-discard-history"
        if row < 717:return "opponent-discard-history-and-calls"
        if row < 836:return "visible-table-inventory"
        if row < 845:return "opponent-last-hand-discard"
        if row < 854:return "opponent-riichi-discard"
        if row < 874:return "waits-furiten-riichi-current-claim"
        if row < 879:return "own-discard-and-shanten-cache"
        return "legal-actions-and-lookahead"
    return {"different_values":len(changed),"different_rows":rows,
            "different_channels":sorted({channel(r) for r in rows}),
            "mask_differences":np.flatnonzero(am!=bm).tolist(),
            "examples":[{"row":int(r),"tile":int(c),"native":float(a[r,c]),"snapshot":float(b[r,c])} for r,c in changed[:16]]}


def compare_all(PlayerState):
    results=[]
    for name,base,events,dealer in fixtures():
        native,flags=native_replay(PlayerState,base,events,dealer)
        snapshot=reduce_public(base,events,dealer);snapshot["LegalActions"]=flags
        imported=PlayerState.mjcn_from_public(json.dumps(snapshot))
        a,am=native.encode_obs(4,False);b,bm=encode_public_obs(imported,snapshot)
        delta=difference(a,am,b,bm)
        results.append(dict(case=name,kind="complete",**delta))
        # A new importer instance is the recovery path: no old in-process state may be needed.
        again=PlayerState.mjcn_from_public(json.dumps(copy.deepcopy(snapshot)))
        c,cm=encode_public_obs(again,snapshot)
        recovery = difference(b,bm,c,cm)
        assert recovery["different_values"]==0 and not recovery["mask_differences"]
        results[-1]["fresh_import_state_equal"]=True
        reasons={"opponent-pon-skipped-turn":"Missing call chronology cannot reconstruct skipped turn slots or call placement.",
                 "opponent-kan-new-dora-after-discard":"Missing dora chronology cannot recover dora-at-discard bits.",
                 "midgame-repeated-faces":"Unknown tedashi/tsumogiri cannot recover hand-discard flags and last tedashi.",
                 "own-riichi-established-next-draw":"Missing own draw/discard chronology cannot recover the native declaration-time cache."}
        if name in reasons:
            partial=copy.deepcopy(snapshot);partial["HistoryComplete"]=False;partial["KnownEvents"]=[]
            if name=="midgame-repeated-faces":
                for p in partial["Players"]:
                    for d in p["River"]:d["Tsumogiri"]=None
            restored=PlayerState.mjcn_from_public(json.dumps(partial))
            c,cm=encode_public_obs(restored,partial)
            delta=difference(a,am,c,cm)
            assert delta["different_values"]>0, "The incomplete-history fixture must expose its information loss"
            results.append(dict(case=name+"-missing-history",kind="partial",reason=reasons[name],**delta))
    return results


def check_rules_and_rejections(PlayerState):
    import numpy as np
    from snapshot import infer
    class InputRecorder:
        def __init__(self):self.inputs=[]
        def react_batch(self, observations, masks, invisible):
            self.inputs.extend((o.copy(),m.copy()) for o,m in zip(observations,masks))
            return [int(np.flatnonzero(m)[0]) for m in masks],np.zeros((len(masks),46)),masks,[True]*len(masks)
    checks=[]
    for kind in ("east","hanchan"):
        for wind in range(1 if kind=="east" else 2):
            for hand in range(1,5):
                for action in ("normal","riichi","kan"):
                    s=sample(BASE+["E" if action=="kan" else "P"],1|(64 if action=="kan" else 2 if action=="riichi" else 0),
                        {"Type":"tsumo","Actor":0,"Tile":face("E" if action=="kan" else "P")})
                    s.update(RoundWind=wind,HandNumber=hand,MatchFirstRound=4 if kind=="east" else 0,
                        MatchRules={"SchemaVersion":1,"ProfileId":"doman-public-v1","MatchType":kind,"ScheduledHands":4 if kind=="east" else 8})
                    native=PlayerState.mjcn_from_public(json.dumps(s));before,bmask=native.encode_obs(4,False)
                    engine=InputRecorder();_,applied,_=infer(engine,s)
                    assert len(engine.inputs)==(1 if action=="normal" else 2)
                    expected=((4 if kind=="east" else 0)+4*wind+hand-1)/7
                    for observed,mask in engine.inputs:
                        assert np.allclose(observed[27],expected)
                        assert np.array_equal(observed[19:27],before[19:27]), "Actual kyoku/bakaze/jikaze must not change"
                    after,_=native.encode_obs(4,False);assert np.array_equal(after,before)
                    assert applied["all_last"]==(wind*4+hand==(4 if kind=="east" else 8))
                    checks.append(f"{kind}-{wind}-{hand}-{action}-actual-model-input")
    # Fail closed for falsely complete or contradictory traces, rather than silently repairing them.
    named={n:reduce_public(b,e,d) for n,b,e,d in fixtures()}
    def reject(label,s):
        try:PlayerState.mjcn_from_public(json.dumps(s))
        except Exception:checks.append(label)
        else:raise AssertionError("Contradictory history accepted: "+label)
    s=copy.deepcopy(named["midgame-repeated-faces"]);s["KnownEvents"].pop(0);reject("missing-draw",s)
    s=copy.deepcopy(named["midgame-repeated-faces"]);s["KnownEvents"][1]["RiverIndex"]=1;reject("wrong-repeated-river-slot",s)
    s=copy.deepcopy(named["midgame-repeated-faces"]);s["KnownEvents"][1]["Sequence"]=1;reject("duplicate-event-sequence",s)
    s=copy.deepcopy(named["after-pon-discard-next-draw"]);s["KnownEvents"]=[e for e in s["KnownEvents"] if e["Type"]!="pon"];reject("missing-call",s)
    s=copy.deepcopy(named["own-riichi-established-next-draw"]);s["KnownEvents"]=[e for e in s["KnownEvents"] if e["Type"]!="reach"];reject("missing-reach",s)
    s=copy.deepcopy(named["opening-seat-0"]);s["MatchFirstRound"]=4
    try:match_context(s)
    except ValueError:checks.append("conflicting-match-length")
    else:raise AssertionError("Conflicting match rules accepted")
    return checks


if __name__=="__main__":
    import argparse,sys
    from pathlib import Path
    p=argparse.ArgumentParser();p.add_argument("--native-directory",type=Path,required=True);p.add_argument("--report",type=Path,required=True)
    p.add_argument("--allow-differences",action="store_true")
    args=p.parse_args();sys.path.insert(0,str(args.native_directory.resolve()))
    from libriichi.state import PlayerState
    cases=compare_all(PlayerState)
    additional=[] if args.allow_differences else check_rules_and_rejections(PlayerState)
    report={"features_per_case":34408,"cases":cases,"input_and_rejection_checks":additional,"cn_live_verified":False}
    args.report.write_text(json.dumps(report,indent=2),encoding="utf-8")
    for c in cases:print(c["case"],c["different_values"],"mask",c["mask_differences"],"rows",c["different_rows"])
    if not args.allow_differences:assert all(c["different_values"]==0 and not c["mask_differences"] for c in cases if c["kind"]=="complete")
