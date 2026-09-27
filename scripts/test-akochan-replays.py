#!/usr/bin/env python3
"""Run the built C# CLI and real local akochan on six pinned public replay prefixes.

Python standard library only. No build, game access, fake engine or network request.
Only names and opponents' concealed information are removed; no events are invented.
The persistent report contains public-sample decisions and safe codes, never full logs,
player names, input paths or subprocess output. Expected move TYPES test interoperability,
not strategy quality, winning correctness or live-game legality.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
import time


SOURCE_SHA256 = "c3ea8f287e43bdac64e016046f4402ea99b5392ccc061438f42915f7484127f7"
ENGINE_COMMIT = "53188a0b926fbab38177f88c3cd87d554cf412af"
ENGINE_REPOSITORY = "https://github.com/critter-mj/akochan"
MAX_SOURCE_BYTES = 1024 * 1024
MAX_CONSOLE_BYTES = 1024 * 1024
TIMEOUT_SECONDS = 65
PLAYER_ID = 0
SAMPLES = ((3, ("dahai",)), (6, ("none",)), (35, ("dahai",)),
           (82, ("pon", "dahai")), (91, ("hora",)), (185, ("dahai",)))
HASH = re.compile(r"[0-9A-Fa-f]{64}\Z")
SAFE_CODE = re.compile(r"[A-Za-z][A-Za-z0-9_]{0,79}\Z")


class ReplayCheckError(Exception):
    """Only machine-controlled, non-sensitive codes are exposed."""


def require(condition, code):
    if not condition:
        raise ReplayCheckError(code)


def no_duplicates(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "DUPLICATE_JSON_KEY")
        result[key] = value
    return result


def parse_json(raw):
    try:
        return json.loads(raw, object_pairs_hook=no_duplicates,
                          parse_constant=lambda _: (_ for _ in ()).throw(ReplayCheckError("NONFINITE_JSON")))
    except (UnicodeError, json.JSONDecodeError, RecursionError):
        raise ReplayCheckError("INVALID_JSON") from None


def bounded_read(path, maximum, code):
    require(path.is_file() and not path.is_symlink(), code)
    with path.open("rb") as handle:
        raw = handle.read(maximum + 1)
    require(len(raw) <= maximum, code)
    return raw


def sha256_file(path):
    require(path.is_file(), "CLI_NOT_FOUND")
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def load_source(path):
    raw = bounded_read(path, MAX_SOURCE_BYTES, "SOURCE_INVALID_OR_TOO_LARGE")
    require(hashlib.sha256(raw).hexdigest() == SOURCE_SHA256, "SOURCE_HASH_MISMATCH")
    events = [parse_json(line) for line in raw.splitlines()]
    require(len(events) >= max(n for n, _ in SAMPLES) and
            all(isinstance(event, dict) for event in events), "SOURCE_EVENT_SHAPE")
    require(events[0].get("type") == "start_game", "SOURCE_PREFIX_INVALID")
    return events


def masked_prefix(events, final_line):
    start = max((index for index, event in enumerate(events[:final_line])
                 if event.get("type") == "start_kyoku"), default=-1)
    require(start >= 1, "SOURCE_ROUND_START_MISSING")
    # Deep copy only selected source events. Do not synthesize skipped events or a start_kyoku.
    selected = json.loads(json.dumps([events[0], *events[start:final_line]]))
    selected[0].pop("names", None)
    for event in selected:
        require(not any(key in event for key in ("names", "haiyama", "uradora_markers", "hora_tehais")),
                "SOURCE_PRIVATE_FIELD_UNSUPPORTED")
        if event["type"] == "start_kyoku":
            hands = event.get("tehais")
            require(isinstance(hands, list) and len(hands) == 4 and
                    all(isinstance(hand, list) and len(hand) == 13 for hand in hands), "SOURCE_HAND_SHAPE")
            for actor in range(4):
                if actor != PLAYER_ID:
                    # Same 13 slots; '?' explicitly preserves unknown concealed information.
                    hands[actor] = ["?"] * 13
        elif event["type"] == "tsumo":
            actor = event.get("actor")
            require(type(actor) is int and 0 <= actor <= 3 and isinstance(event.get("pai"), str), "SOURCE_DRAW_SHAPE")
            if actor != PLAYER_ID:
                event["pai"] = "?"
    final = selected[-1]
    require((final.get("type") == "tsumo" and final.get("actor") == PLAYER_ID) or
            (final.get("type") in ("dahai", "kakan") and final.get("actor") != PLAYER_ID), "SOURCE_TRIGGER_INVALID")
    lines = [json.dumps(event, ensure_ascii=True, separators=(",", ":")) for event in selected]
    require(sum(len(line.encode("utf-8")) + 1 for line in lines) <= MAX_SOURCE_BYTES, "PREFIX_TOO_LARGE")
    normalized = []
    for index, event in enumerate(selected):
        event = dict(event)
        event.pop("can_act", None)
        event["can_act"] = index == len(selected) - 1
        normalized.append(json.dumps(event, ensure_ascii=True, separators=(",", ":")))
    expected_hash = hashlib.sha256(("\n".join(normalized) + "\n").encode("utf-8")).hexdigest()
    return lines, expected_hash


def kill_owned_process_tree(process):
    if process.poll() is not None:
        return
    if os.name == "nt":
        try:
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                           stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           timeout=5, check=False, creationflags=subprocess.CREATE_NO_WINDOW)
        except (OSError, subprocess.TimeoutExpired):
            process.kill()
    else:
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        process.kill()
        process.wait(timeout=5)


def safe_cli_error(raw):
    try:
        value = parse_json(raw)
        if isinstance(value, dict) and value.get("liveGameInput") is False:
            code = value.get("error")
            if isinstance(code, str) and SAFE_CODE.fullmatch(code):
                return code
    except ReplayCheckError:
        pass
    return "CLI_NONZERO_EXIT"


def visible_tile(value):
    if value is None:
        return None
    require(isinstance(value, dict) and set(value) <= {"Id", "Red", "ChineseName"}, "RESULT_TILE_SCHEMA")
    kind, red = value.get("Id"), value.get("Red")
    require(type(kind) is int and 0 <= kind <= 33 and type(red) is bool and
            (not red or kind in (4, 13, 22)), "RESULT_TILE_SCHEMA")
    return {"Id": kind, "Red": red}


def validate_result(value, expected_hash, manifest_hash):
    require(isinstance(value, dict), "RESULT_OBJECT_REQUIRED")
    require(value.get("EngineCommit") == ENGINE_COMMIT, "RESULT_ENGINE_COMMIT_MISMATCH")
    require(value.get("IsSynthetic") is False and value.get("IsLiveGameDecision") is False,
            "RESULT_PROVENANCE_MISMATCH")
    require(value.get("SourceLabel") == "offline:user-import", "RESULT_SOURCE_LABEL_MISMATCH")
    require(isinstance(value.get("InputSha256"), str) and value["InputSha256"].lower() == expected_hash,
            "RESULT_INPUT_HASH_MISMATCH")
    require(isinstance(value.get("InstallationManifestSha256"), str) and
            value["InstallationManifestSha256"].lower() == manifest_hash, "RESULT_INSTALLATION_HASH_MISMATCH")
    moves = value.get("Moves")
    require(isinstance(moves, list) and 1 <= len(moves) <= 2, "RESULT_MOVES_SCHEMA")
    normalized = []
    for move in moves:
        require(isinstance(move, dict) and set(move) == {"Type", "Actor", "Target", "Tile", "Consumed", "Tsumogiri"},
                "RESULT_MOVES_SCHEMA")
        require(move["Type"] in {"none", "dahai", "reach", "chi", "pon", "daiminkan", "ankan", "kakan", "hora", "kyushukyuhai"}
                and type(move["Actor"]) is int and move["Actor"] == PLAYER_ID, "RESULT_MOVES_SCHEMA")
        require(move["Target"] is None or type(move["Target"]) is int and 0 <= move["Target"] <= 3, "RESULT_MOVES_SCHEMA")
        require(move["Tsumogiri"] is None or type(move["Tsumogiri"]) is bool, "RESULT_MOVES_SCHEMA")
        require(isinstance(move["Consumed"], list) and len(move["Consumed"]) <= 4, "RESULT_MOVES_SCHEMA")
        normalized.append({"Type": move["Type"], "Actor": move["Actor"], "Target": move["Target"],
                           "Tile": visible_tile(move["Tile"]), "Consumed": [visible_tile(t) for t in move["Consumed"]],
                           "Tsumogiri": move["Tsumogiri"]})
    metrics = {}
    for key in ("StartToResponseMilliseconds", "PeakWorkingSetBytes", "CpuMilliseconds"):
        number = value.get(key)
        require(number is None or type(number) in (int, float) and math.isfinite(number) and number >= 0,
                "RESULT_METRICS_SCHEMA")
        metrics[key] = number
    return {"moves": normalized, "input_sha256": expected_hash, "engine_commit": ENGINE_COMMIT,
            "installation_manifest_sha256": manifest_hash, "source_label": "offline:user-import",
            "is_synthetic": False, "is_live_game_decision": False, "metrics": metrics}


def run_sample(args, events, final_line, expected_types, manifest_hash):
    result = {"source_prefix_lines": final_line, "expected_move_types": list(expected_types),
              "passed": False, "exit_code": None, "safe_error": None}
    process = None
    try:
        lines, expected_hash = masked_prefix(events, final_line)
        result["input_event_count"] = len(lines)
        temp_root = Path(tempfile.gettempdir()).resolve()
        with tempfile.TemporaryDirectory(prefix="mjcn-public-replay-", dir=temp_root) as temporary:
            directory = Path(temporary).resolve()
            require(directory.parent == temp_root and directory.name.startswith("mjcn-public-replay-"), "TEMP_SCOPE_INVALID")
            input_file = directory / "events.jsonl"
            input_file.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
            output_file, error_file = directory / "stdout.bin", directory / "stderr.bin"
            options = {"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {"start_new_session": True}
            started = time.monotonic()
            with output_file.open("wb") as stdout, error_file.open("wb") as stderr:
                process = subprocess.Popen([str(args.dotnet), str(args.cli), str(args.engine), str(input_file), str(PLAYER_ID)],
                                           stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr, shell=False, **options)
                try:
                    result["exit_code"] = process.wait(timeout=TIMEOUT_SECONDS)
                except subprocess.TimeoutExpired:
                    kill_owned_process_tree(process)
                    raise ReplayCheckError("CLI_TIMEOUT") from None
            result["wall_seconds"] = round(time.monotonic() - started, 3)
            stdout = bounded_read(output_file, MAX_CONSOLE_BYTES, "CLI_OUTPUT_TOO_LARGE")
            stderr = bounded_read(error_file, MAX_CONSOLE_BYTES, "CLI_OUTPUT_TOO_LARGE")
            if result["exit_code"] != 0:
                raise ReplayCheckError(safe_cli_error(stderr))
            actual = validate_result(parse_json(stdout), expected_hash, manifest_hash)
            result["actual"] = actual
            require(tuple(move["Type"] for move in actual["moves"]) == expected_types, "MOVE_TYPES_UNEXPECTED")
            result["passed"] = True
    except ReplayCheckError as error:
        result["safe_error"] = str(error)
    except (OSError, ValueError, KeyError, TypeError, subprocess.SubprocessError):
        result["safe_error"] = "LOCAL_RUN_FAILED"
    finally:
        if process is not None:
            kill_owned_process_tree(process)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("source", "dotnet", "cli", "engine", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    for name in ("source", "dotnet", "cli", "engine", "output"):
        setattr(args, name, getattr(args, name).resolve())
    report = {"schema": 1, "suite": "akochan-pinned-public-replays", "passed": False,
              "source_sha256": SOURCE_SHA256, "engine_commit": ENGINE_COMMIT,
              "source_repository": ENGINE_REPOSITORY, "timeout_per_sample_seconds": TIMEOUT_SECONDS,
              "player_id": PLAYER_ID, "uses_actual_cli_and_native_engine": True,
              "strategy_quality_asserted": False, "live_game_validation": False, "samples": []}
    try:
        require(args.output not in {args.source, args.dotnet, args.cli, Path(__file__).resolve()} and
                not args.output.is_relative_to(args.engine) and not args.output.is_relative_to(args.source.parent) and
                not args.output.is_relative_to(args.cli.parent), "OUTPUT_SCOPE_INVALID")
        require(args.dotnet.is_file() and args.cli.is_file(), "CLI_NOT_FOUND")
        require(args.cli.suffix.lower() == ".dll", "CLI_ASSEMBLY_REQUIRED")
        manifest_raw = bounded_read(args.engine / "akochan-installation.json", 512 * 1024, "INSTALLATION_MANIFEST_INVALID")
        manifest = parse_json(manifest_raw)
        require(isinstance(manifest, dict) and manifest.get("sourceCommit") == ENGINE_COMMIT and
                manifest.get("sourceRepository") == ENGINE_REPOSITORY, "INSTALLATION_IDENTITY_MISMATCH")
        manifest_hash = hashlib.sha256(manifest_raw).hexdigest()
        report["installation_manifest_sha256"] = manifest_hash
        report["cli_assembly_sha256"] = sha256_file(args.cli)
        report["cn_core_assembly_sha256"] = sha256_file(args.cli.parent / "Mahjong.Cn.Core.dll")
        events = load_source(args.source)
        for final_line, expected_types in SAMPLES:
            result = run_sample(args, events, final_line, expected_types, manifest_hash)
            report["samples"].append(result)
            print(json.dumps({"sample": final_line, "passed": result["passed"], "safe_error": result["safe_error"]}), flush=True)
        report["passed"] = len(report["samples"]) == len(SAMPLES) and all(x["passed"] for x in report["samples"])
        args.output.parent.mkdir(parents=True, exist_ok=True)
        # Explicit output is replaceable for reproducible reruns; protected inputs were checked above.
        with args.output.open("w", encoding="utf-8", newline="\n") as handle:
            json.dump(report, handle, ensure_ascii=True, indent=2)
            handle.write("\n")
        return 0 if report["passed"] else 1
    except ReplayCheckError as error:
        print(json.dumps({"passed": False, "safe_error": str(error)}), file=sys.stderr)
    except (OSError, ValueError, KeyError, TypeError):
        print(json.dumps({"passed": False, "safe_error": "LOCAL_SETUP_FAILED"}), file=sys.stderr)
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
