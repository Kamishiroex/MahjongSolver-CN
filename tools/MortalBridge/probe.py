"""Exercise the real checkpoint on upstream regression replays, with hidden information masked.

Suggested actions are checked against libriichi legality, not substituted into the recorded
trajectory. This is an interface/latency regression check, NOT a playing-strength evaluation.
"""
from __future__ import annotations

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import statistics
import subprocess
import sys
import time

from prepare import LOCK, source_identity
from runtime import ReplaySession, load_engine, public_event


def cases(source):
    text = (source / "libriichi/src/state/test.rs").read_text(encoding="utf-8")
    pattern = r'let log = r#"((?:(?!"#).)*)"#;\s*let (?:mut )?ps = PlayerState::from_log\((\d), log\);'
    for index, match in enumerate(re.finditer(pattern, text, re.S), 1):
        events = [json.loads(line.strip()) for line in match[1].splitlines() if line.strip()]
        yield f"upstream-state-{index}", int(match[2]), events


def process_check(directory, source):
    """Test real child process transport, consecutive round reset and the captured CN gap."""
    _, _, events = next(c for c in cases(source) if c[1] == 0)
    stream = events + [{"type": "end_kyoku"}] + events + [{"type": "end_kyoku"}, {"type": "end_game"}]
    command = [sys.executable, str(Path(__file__).with_name("runtime.py")),
               "--directory", str(directory.resolve()), "--player", "0"]
    payload = "\n".join(json.dumps(public_event(e, 0)) for e in stream) + "\n"
    result = subprocess.run(command, input=payload, text=True, encoding="utf-8",
                            capture_output=True, timeout=30)
    if result.returncode != 0:
        raise ValueError("MORTAL_JSONL_PROCESS: " + result.stderr[:600])
    responses = [json.loads(line) for line in result.stdout.splitlines()]
    if len(responses) != 4 or sum(r["type"] == "hora" for r in responses) != 2:
        raise ValueError("MORTAL_JSONL_ROUND_RESET")
    fixture_path = Path(__file__).resolve().parents[2] / "tests/Mahjong.Plugin.CN.Gameplay.Tests/fixtures/kyushu-protocol-20260925.json"
    fixture = json.loads(fixture_path.read_text(encoding="utf-8-sig"))["ActualPublicInput"]
    rejected = subprocess.run(command, input=json.dumps(fixture) + "\n", text=True,
                              encoding="utf-8", capture_output=True, timeout=30)
    if rejected.returncode != 1 or rejected.stdout or "MORTAL_MJAI_EVENT_REQUIRED" not in rejected.stderr:
        raise ValueError("MORTAL_PARTIAL_CN_HISTORY_NOT_REJECTED")
    return {"consecutive_rounds": 2, "responses": 4, "hora": 2,
            "actual_cn_partial_snapshot": "rejected_without_response", "game_input_sent": False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    source_identity(args.source)
    started = time.perf_counter()
    engine = load_engine(args.directory)
    load_seconds = time.perf_counter() - started
    timings, results = [], []
    for name, player, events in cases(args.source):
        session = ReplaySession(engine, player)
        actions, opportunities, event_kinds = Counter(), Counter(), Counter()
        declined_wins = []
        for index, event in enumerate(events, 1):
            response, ms, cans = session.feed(event)
            event_kinds[event["type"]] += 1
            for kind in ("discard", "chi", "pon", "ankan", "daiminkan", "kakan", "riichi", "tsumo_agari", "ron_agari", "ryukyoku"):
                opportunities[kind] += bool(getattr(cans, "can_" + kind))
            if response:
                timings.append(ms)
                actions[response["type"]] += 1
                if cans.can_agari:
                    if response["type"] != "hora":
                        raise ValueError("MJCN_WIN_PRIORITY_FAILED")
                    if session.last_raw_response is not None and session.last_raw_response["type"] != "hora":
                        declined_wins.append({"event": index, "type": event["type"],
                                              "suggestion": session.last_raw_response["type"],
                                              "after_mjcn_win_priority": response["type"]})
        results.append({"case": name, "pov": player, "events": len(events),
                        "events_by_type": dict(event_kinds), "legal_responses": dict(actions),
                        "opportunities": dict(opportunities), "raw_engine_declined_wins": declined_wins})
    if len(results) < 7 or not timings:
        raise ValueError("Upstream replay extraction incomplete")
    report = {"status": "offline_pass", "source_commit": LOCK["source_commit"],
              "model_revision": LOCK["model_revision"], "checkpoint_sha256": LOCK["checkpoint_sha256"],
              "load_seconds": load_seconds, "decisions": len(timings),
              "latency_ms": {"median": statistics.median(timings), "p95": sorted(timings)[int((len(timings)-1)*.95)], "max": max(timings)},
              "cases": results, "hidden_opponent_hands_used": False,
              "cn_live_verified": False, "strength_evaluated": False,
              "jsonl_process_test": process_check(args.directory, args.source)}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("status", "decisions", "load_seconds", "latency_ms")}))


if __name__ == "__main__":
    main()
