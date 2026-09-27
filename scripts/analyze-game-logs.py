#!/usr/bin/env python3
"""Verify local GameJournal streams and emit a redacted aggregate (standard library only).

Accepts a session directory, a logs directory containing mjcn-game-* sessions, or an
export ZIP containing exactly events.jsonl/errors.jsonl. ZIP members are not extracted.
The Entry hash uses its exact original UTF-8 JSON substring, never reserialized JSON.
No names, chats, paths, reasons, full hands, screenshots or event payloads are exported.
"""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys
import uuid
import zipfile
import base64
import copy
import zlib


MAX_FILE_BYTES = 32 * 1024 * 1024
MAX_TOTAL_BYTES = 256 * 1024 * 1024
MAX_LINE_BYTES = 128 * 1024
MAX_DECODED_BYTES = 512 * 1024
MAX_RECORDS = 65536
MAX_SESSIONS = 256
FILES = ("events.jsonl", "errors.jsonl")
PART = re.compile(r"(events|errors)(?:\.([0-9]{4}))?\.jsonl\Z")
AUXILIARY = {"journal-index.json", "diagnostic-ring.jsonl", "diagnostic-fault.jsonl", "last-stop.json", "recovery-latest.jsonl", "record-closed.json", "export-privacy.json"}
SESSION = re.compile(r"mjcn-game-[0-9]{8}-[0-9]{6}-[0-9a-f]{32}\Z")
KIND = re.compile(r"[A-Za-z0-9_-]{1,80}\Z")
HASH = re.compile(r"[0-9A-F]{64}\Z")
VERSION = re.compile(r"[0-9]+(?:\.[0-9]+){1,5}(?:[-+][A-Za-z0-9.-]+)?\Z")
GUID = re.compile(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\Z")
EVENT_NAMES = (
    "SnapshotObserved", "Draw", "Discard", "Chi", "Pon", "OpenKan", "ClosedKan", "AddedKan",
    "RiichiDeclared", "RiichiEstablished", "Ron", "Tsumo", "ExhaustiveDraw", "TableEntered",
    "TableExited", "RoundStarted", "RoundEnded", "Paused", "Resumed", "ActionSubmitted", "Gap", "Checkpoint",
)
KNOWN_KINDS = set("""session_started session_stopped play_paused play_stopped mode_selected public_event
runtime_observation public_table_observation recovery_checkpoint recovery_log_loaded recovery_applied
recovery_waiting history_gap observation_baseline round_end_candidate round_boundary_candidate draw
draw_unknown_tile discard discard_unknown_tile discard_count_changed riichi_status_changed score_changed
meld_inventory_changed dora_changed dora_display_changed hand_reordered action_submitted action_submission
observation_gap river_visible_change river_visible_baseline public_round_signal public_hand_delta public_call_event public_river_event public_table_changed public_table_heartbeat public_tracking_epoch global_ai_request global_ai_decision global_ai_status global_ai_trace_chunk""".split())
KNOWN_KINDS.update({"table_automation_action", "table_automation_state", "table_match_completed", "table_record_closed", "match_result", "global_ai_error"})
CODE_PREFIXES = ("JOURNAL_", "RECOVERY_", "PUBLIC_", "GAMEPLAY_", "READ_", "REPEATED_", "CALL_", "POLICY_",
                 "INPUT_", "STATE_", "SNAPSHOT_", "MELD_", "TABLE_", "IDENTITY_", "ACCESS_", "MODE_")


class JournalError(ValueError):
    """Only fixed codes, never data or paths."""


def require(value, code):
    if not value:
        raise JournalError(code)


def unique_object(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, "JOURNAL_DUPLICATE_PROPERTY")
        value[key] = item
    return value


def invalid_constant(_):
    raise JournalError("JOURNAL_NONFINITE_JSON")


def decode_payload(entry, bases):
    data = entry["Data"]
    if entry["Schema"] != 2 or "Codec" not in data:
        return
    require(data.get("Codec") == "json-delta-gzip-v1", "JOURNAL_CODEC_INVALID")
    require(entry["Kind"] in {"public_event", "public_table_observation", "runtime_observation", "recovery_checkpoint"}
            or entry["Kind"].startswith("global_ai_"),
            "JOURNAL_CODEC_INVALID")
    try:
        compressed = base64.b64decode(data["Payload"], validate=True)
        inflater = zlib.decompressobj(16 + zlib.MAX_WBITS)
        decoded = inflater.decompress(compressed, MAX_DECODED_BYTES * 4 + 1)
        require(len(decoded) <= MAX_DECODED_BYTES * 4 and inflater.eof and not inflater.unused_data,
                "JOURNAL_DECODE_LIMIT")
        bound_depth(decoded.decode("utf-8"))
        patch = DECODER.decode(decoded.decode("utf-8"))
        kind, basis = entry["Kind"], data["BaseSequence"]
        require(type(basis) is int and 0 <= basis < entry["Sequence"], "JOURNAL_BASE_INVALID")
        if basis == 0:
            current = patch["Full"]
        else:
            require(kind in bases and bases[kind][0] == basis, "JOURNAL_BASE_MISSING")
            current = copy.deepcopy(bases[kind][1])
            for change in patch["Changes"]:
                path = change["Path"]
                require(isinstance(path, list) and len(path) <= 48, "JOURNAL_PATCH_INVALID")
                if not path:
                    current = change["Value"]
                    continue
                parent = current
                for key in path[:-1]:
                    require((isinstance(parent, list) and type(key) is int and 0 <= key < len(parent)) or
                            (isinstance(parent, dict) and type(key) is str and key in parent), "JOURNAL_PATCH_INVALID")
                    parent = parent[key]
                key = path[-1]
                require((isinstance(parent, list) and type(key) is int and 0 <= key < len(parent)) or
                        (isinstance(parent, dict) and type(key) is str), "JOURNAL_PATCH_INVALID")
                if change.get("Remove") is True:
                    require(isinstance(parent, dict) and key in parent, "JOURNAL_PATCH_INVALID")
                    del parent[key]
                else:
                    parent[key] = change["Value"]
        require(isinstance(current, dict) and len(json.dumps(current, ensure_ascii=True, separators=(",", ":")).encode()) <= MAX_DECODED_BYTES,
                "JOURNAL_DECODE_LIMIT")
        bases[kind] = (entry["Sequence"], current)
        entry["Data"] = current
    except (KeyError, TypeError, ValueError, IndexError, zlib.error, UnicodeError) as exc:
        if isinstance(exc, JournalError):
            raise
        raise JournalError("JOURNAL_CODEC_INVALID") from None


DECODER = json.JSONDecoder(object_pairs_hook=unique_object, parse_constant=invalid_constant)


def skip_whitespace(text, index):
    while index < len(text) and text[index] in " \t\r\n":
        index += 1
    return index


def bound_depth(text):
    depth = 0
    quoted = escaped = False
    for c in text:
        if quoted:
            if escaped:
                escaped = False
            elif c == "\\":
                escaped = True
            elif c == '"':
                quoted = False
        elif c == '"':
            quoted = True
        elif c in "[{":
            depth += 1
            require(depth <= 48, "JOURNAL_DEPTH_LIMIT")
        elif c in "]}":
            depth -= 1


def exact_entry(raw):
    """Return Entry object and its original value substring; supports either root property order."""
    try:
        text = raw.decode("utf-8", errors="strict")
        bound_depth(text)
        index = skip_whitespace(text, 0)
        require(index < len(text) and text[index] == "{", "JOURNAL_SCHEMA_INVALID")
        index += 1
        root = {}
        entry_text = None
        while True:
            index = skip_whitespace(text, index)
            require(index < len(text), "JOURNAL_JSON_INVALID")
            if text[index] == "}":
                index += 1
                break
            key, index = DECODER.raw_decode(text, index)
            require(type(key) is str and key not in root, "JOURNAL_DUPLICATE_PROPERTY")
            index = skip_whitespace(text, index)
            require(index < len(text) and text[index] == ":", "JOURNAL_JSON_INVALID")
            start = skip_whitespace(text, index + 1)
            value, index = DECODER.raw_decode(text, start)
            root[key] = value
            if key == "Entry":
                entry_text = text[start:index]
            index = skip_whitespace(text, index)
            require(index < len(text), "JOURNAL_JSON_INVALID")
            if text[index] == "}":
                index += 1
                break
            require(text[index] == ",", "JOURNAL_JSON_INVALID")
            index = skip_whitespace(text, index + 1)
            require(index < len(text) and text[index] != "}", "JOURNAL_JSON_INVALID")
        require(skip_whitespace(text, index) == len(text), "JOURNAL_JSON_INVALID")
        require(set(root) == {"Entry", "Sha256"} and isinstance(root["Entry"], dict) and entry_text is not None,
                "JOURNAL_SCHEMA_INVALID")
        return root["Entry"], root["Sha256"], entry_text.encode("utf-8")
    except (UnicodeError, json.JSONDecodeError, RecursionError, OverflowError):
        raise JournalError("JOURNAL_JSON_INVALID") from None


def utc(value):
    require(type(value) is str and len(value) <= 64, "JOURNAL_TIMESTAMP_INVALID")
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
        require(result.tzinfo is not None, "JOURNAL_TIMESTAMP_INVALID")
        return result.astimezone(timezone.utc)
    except (ValueError, OverflowError):
        raise JournalError("JOURNAL_TIMESTAMP_INVALID") from None


def safe_kind(value):
    # Preserve known machine kinds; unknown future kinds remain separately countable without echoing text.
    return value if value in KNOWN_KINDS else "other:" + hashlib.sha256(value.encode("ascii")).hexdigest()[:12]


def safe_code(value):
    if type(value) is str and len(value) <= 100 and re.fullmatch(r"[A-Z][A-Za-z0-9_]*", value) and value.startswith(CODE_PREFIXES):
        return value
    return "UNCLASSIFIED_CODE"


def public_event_name(value):
    if type(value) is int and 0 <= value < len(EVENT_NAMES):
        return EVENT_NAMES[value]
    return value if type(value) is str and value in EVENT_NAMES else "Unknown"


class Counts:
    def __init__(self):
        self.kinds = Counter()
        self.public_events = Counter()
        self.errors = Counter()
        self.recovery = Counter()
        self.recovery_codes = Counter()
        self.versions = {name: set() for name in ("PluginVersion", "GameVersion", "DalamudVersion", "ClientStructsVersion", "Runtime", "Api")}
        self.history_gap_records = 0
        self.first = self.last = None
        self.clock_regressions = 0

    def observe(self, entry, stream):
        stamp = utc(entry["Utc"])
        self.first = stamp if self.first is None else min(self.first, stamp)
        self.last = stamp if self.last is None else max(self.last, stamp)
        kind, data = entry["Kind"], entry["Data"]
        self.kinds[safe_kind(kind) if stream == "events.jsonl" else safe_code(kind)] += 1
        if stream == "errors.jsonl":
            self.errors[safe_code(kind)] += 1
            if "Code" in data:
                if kind == "REPEATED_ERROR_SUMMARY":
                    require(type(data.get("RepeatedCount")) is int and 0 < data["RepeatedCount"] <= 10000000, "JOURNAL_REPEAT_COUNT_INVALID")
                    self.errors[safe_code(data["Code"])] += data["RepeatedCount"]
                else:
                    self.errors[safe_code(data["Code"])] += 1
            return
        if kind in ("session_started", "recovery_checkpoint"):
            self.version("PluginVersion", data.get("PluginVersion"))
            identity = data.get("Identity")
            if isinstance(identity, dict):
                for name in self.versions:
                    if name != "PluginVersion":
                        self.version(name, identity.get(name))
        gap = kind == "history_gap" or data.get("HistoryGap") is True
        if kind == "public_event":
            event_name = public_event_name(data.get("Kind"))
            self.public_events[event_name] += 1
            gap |= event_name == "Gap" or data.get("Provenance") in (2, "Gap")
        if gap:
            self.history_gap_records += 1
        if kind in ("recovery_log_loaded", "recovery_applied", "recovery_waiting", "recovery_checkpoint"):
            self.recovery[kind] += 1
            if "Code" in data:
                self.recovery_codes[safe_code(data["Code"])] += 1
            if kind == "recovery_log_loaded" and type(data.get("Candidates")) is int and 0 <= data["Candidates"] <= 32:
                self.recovery["loaded_candidates_total"] += data["Candidates"]
            if data.get("AutomaticEnabled") is True:
                self.recovery["unexpected_automatic_enabled"] += 1
            for item in data.get("Errors", []) if isinstance(data.get("Errors"), list) else []:
                self.errors[safe_code(item)] += 1

    def version(self, name, value):
        if name == "Api":
            if type(value) is int and 0 <= value <= 1000:
                self.versions[name].add(value)
        elif type(value) is str and len(value) <= 100 and VERSION.fullmatch(value):
            self.versions[name].add(value)

    def result(self):
        return {"versions": {k: sorted(v) for k, v in self.versions.items()}, "kind_counts": dict(sorted(self.kinds.items())),
                "public_event_kind_counts": dict(sorted(self.public_events.items())), "history_gap_records": self.history_gap_records,
                "recovery_counts": dict(sorted(self.recovery.items())), "recovery_codes": dict(sorted(self.recovery_codes.items())),
                "error_codes": dict(sorted(self.errors.items())), "first_utc": self.first.isoformat() if self.first else None,
                "last_utc": self.last.isoformat() if self.last else None, "clock_regressions": self.clock_regressions}


def reject_links(path):
    for parent in (path, *path.parents):
        try:
            info = parent.lstat()
        except FileNotFoundError:
            continue
        require(not stat.S_ISLNK(info.st_mode) and not getattr(info, "st_file_attributes", 0) &
                getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400), "JOURNAL_LINK_REJECTED")


class Sources:
    def __init__(self, path):
        self.root = Path(path).absolute()
        reject_links(self.root)
        self.archive = None
        self.sessions = []
        self.bytes_read = 0
        if self.root.is_dir():
            if any((self.root / f).exists() for f in FILES):
                directories = [self.root]
            else:
                directories = []
                with os.scandir(self.root) as entries:
                    for index, entry in enumerate(entries):
                        require(index < 4096, "JOURNAL_DIRECTORY_LIMIT")
                        if SESSION.fullmatch(entry.name):
                            directory = self.root / entry.name
                            reject_links(directory)
                            require(directory.is_dir(), "JOURNAL_SESSION_NOT_DIRECTORY")
                            directories.append(directory)
                            require(len(directories) <= MAX_SESSIONS, "JOURNAL_SESSION_LIMIT")
            total = 0
            for directory in sorted(directories):
                session = {}
                for name in FILES:
                    paths = sorted((f for f in directory.iterdir() if PART.fullmatch(f.name) and
                                    f.name.startswith(name.split(".")[0] + ".")), key=lambda f: (f.name != name, f.name))
                    require(paths and paths[0].name == name, "JOURNAL_STREAM_MISSING")
                    for number, file in enumerate(paths):
                        expected = name if number == 0 else name.replace(".jsonl", f".{number:04d}.jsonl")
                        require(file.name == expected, "JOURNAL_SEGMENT_MISSING")
                        reject_links(file)
                        size = file.stat().st_size
                        require(size <= MAX_FILE_BYTES, "JOURNAL_FILE_LIMIT")
                        total += size
                    session[name] = paths
                index = directory / "journal-index.json"
                if index.exists():
                    reject_links(index)
                    require(index.stat().st_size <= 65536, "JOURNAL_INDEX_LIMIT")
                    manifest = DECODER.decode(index.read_text(encoding="utf-8"))
                    require(isinstance(manifest, dict) and manifest.get("Schema") == 2 and isinstance(manifest.get("Files"), dict),
                            "JOURNAL_INDEX_INVALID")
                    for name, size in manifest["Files"].items():
                        require(PART.fullmatch(name) and type(size) is int and size >= 0, "JOURNAL_INDEX_INVALID")
                        file = directory / name
                        reject_links(file)
                        require(file.is_file() and file.stat().st_size >= size, "JOURNAL_SEGMENT_MISSING")
                self.sessions.append(session)
            require(total <= MAX_TOTAL_BYTES, "JOURNAL_TOTAL_LIMIT")
        else:
            require(self.root.is_file() and self.root.stat().st_size <= MAX_TOTAL_BYTES, "JOURNAL_INPUT_INVALID")
            self.archive = zipfile.ZipFile(self.root)
            try:
                members = self.archive.infolist()
                require(2 <= len(members) <= 8192, "JOURNAL_ZIP_MEMBER_COUNT")
                require(len({m.filename for m in members}) == len(members), "JOURNAL_ZIP_DUPLICATE_MEMBER")
                require(set(FILES) <= {m.filename for m in members} and all(PART.fullmatch(m.filename) or m.filename in AUXILIARY for m in members), "JOURNAL_ZIP_MEMBER_NAME")
                for member in members:
                    mode = member.external_attr >> 16
                    require(not member.is_dir() and not member.flag_bits & 1 and not stat.S_ISLNK(mode) and
                            (not stat.S_IFMT(mode) or stat.S_ISREG(mode)), "JOURNAL_ZIP_MEMBER_TYPE")
                    require(member.file_size <= MAX_FILE_BYTES, "JOURNAL_FILE_LIMIT")
                    require(member.compress_type in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED), "JOURNAL_ZIP_COMPRESSION")
            except Exception:
                self.archive.close()
                raise
            require(sum(m.file_size for m in members) <= MAX_TOTAL_BYTES, "JOURNAL_TOTAL_LIMIT")
            session = {}
            for name in FILES:
                names = sorted((m.filename for m in members if PART.fullmatch(m.filename) and
                                m.filename.startswith(name.split(".")[0] + ".")), key=lambda n: (n != name, n))
                for number, part in enumerate(names):
                    require(part == (name if number == 0 else name.replace(".jsonl", f".{number:04d}.jsonl")), "JOURNAL_SEGMENT_MISSING")
                session[name] = names
            if "journal-index.json" in {m.filename for m in members}:
                require(self.archive.getinfo("journal-index.json").file_size <= 65536, "JOURNAL_INDEX_LIMIT")
                manifest = DECODER.decode(self.archive.read("journal-index.json").decode("utf-8"))
                require(isinstance(manifest, dict) and manifest.get("Schema") == 2 and isinstance(manifest.get("Files"), dict),
                        "JOURNAL_INDEX_INVALID")
                by_name = {m.filename: m.file_size for m in members}
                for name, size in manifest["Files"].items():
                    require(PART.fullmatch(name) and type(size) is int and size >= 0, "JOURNAL_INDEX_INVALID")
                    require(name in by_name and by_name[name] >= size, "JOURNAL_SEGMENT_MISSING")
            self.sessions.append(session)
        require(self.sessions, "JOURNAL_NO_SESSIONS")

    def open(self, value):
        return self.archive.open(value) if self.archive else value.open("rb")

    def consume(self, count):
        self.bytes_read += count
        require(self.bytes_read <= MAX_TOTAL_BYTES, "JOURNAL_TOTAL_LIMIT")

    def close(self):
        if self.archive:
            self.archive.close()


def verify_stream(source, value, name, counts):
    sequence = size = tail = 0
    previous = "0" * 64
    session = None
    previous_utc = None
    bases = {}
    parts = value if isinstance(value, list) else [value]
    for part_number, part in enumerate(parts):
        part_size = 0
        with source.open(part) as stream:
            while raw := stream.readline(MAX_LINE_BYTES + 2):
                source.consume(len(raw)); size += len(raw); part_size += len(raw)
                require(part_size <= MAX_FILE_BYTES, "JOURNAL_FILE_LIMIT")
                require(len(raw) <= MAX_LINE_BYTES + (1 if raw.endswith(b"\n") else 0), "JOURNAL_LINE_LIMIT")
                if not raw.endswith(b"\n"):
                    # Only an uncommitted final prefix is recoverable. No part of it is interpreted.
                    tail = len(raw)
                    break
                require(sequence < MAX_RECORDS, "JOURNAL_EVENT_LIMIT")
                entry, hash_value, original = exact_entry(raw[:-1])
                require(set(entry) == {"Schema", "SessionId", "Sequence", "Utc", "Kind", "Data", "PreviousSha256"},
                        "JOURNAL_SCHEMA_INVALID")
                require(type(entry["Schema"]) is int and entry["Schema"] in (1, 2), "JOURNAL_SCHEMA_INVALID")
                require(type(entry["Sequence"]) is int and entry["Sequence"] == sequence + 1 and
                        entry["PreviousSha256"] == previous, "JOURNAL_CHAIN_INVALID")
                require(type(entry["SessionId"]) is str and GUID.fullmatch(entry["SessionId"]), "JOURNAL_SESSION_INVALID")
                identity = uuid.UUID(entry["SessionId"])
                require(identity.int != 0 and (session is None or session == identity), "JOURNAL_SESSION_INVALID")
                require(type(entry["Kind"]) is str and KIND.fullmatch(entry["Kind"]) and isinstance(entry["Data"], dict),
                        "JOURNAL_SCHEMA_INVALID")
                require(type(hash_value) is str and HASH.fullmatch(hash_value), "JOURNAL_HASH_INVALID")
                require(hashlib.sha256(original).hexdigest().upper() == hash_value, "JOURNAL_HASH_MISMATCH")
                stamp = utc(entry["Utc"])
                if previous_utc is not None and stamp < previous_utc:
                    counts.clock_regressions += 1
                previous_utc = stamp
                decode_payload(entry, bases)
                counts.observe(entry, name)
                sequence += 1; previous = hash_value; session = identity
        require(not tail or part_number == len(parts) - 1, "JOURNAL_INCOMPLETE_MIDDLE_SEGMENT")
    return {"records": sequence, "bytes": size, "partial": tail != 0, "ignored_tail_bytes": tail,
            "last_verified_sha256": previous}, session


def analyze(path):
    source = None
    try:
        source = Sources(path)
        counts = Counts()
        sessions = []
        seen = set()
        for index, files in enumerate(source.sessions):
            streams = {}
            identities = set()
            for name in FILES:
                result, identity = verify_stream(source, files[name], name, counts)
                streams[name] = result
                if identity is not None:
                    identities.add(identity)
            require(len(identities) <= 1, "JOURNAL_CROSS_STREAM_SESSION_MISMATCH")
            require(not identities.intersection(seen), "JOURNAL_DUPLICATE_SESSION")
            seen.update(identities)
            sessions.append({"number": index + 1, "streams": streams})
        partial = any(s["partial"] for session in sessions for s in session["streams"].values())
        return {"schema": 1, "format": "mjcn-game-log-summary", "integrity_passed": True, "partial": partial,
                "status": "Partial" if partial else "VerifiedPrefix", "history_complete": False,
                "note": "Hash integrity verifies recorded bytes, not complete Mahjong history or event semantics.",
                "session_count": len(sessions), "bytes_read": source.bytes_read, **counts.result(), "sessions": sessions}
    finally:
        if source is not None:
            source.close()


def write_summary(path, summary, source):
    destination = Path(path).absolute()
    original = Path(source).absolute()
    reject_links(destination)
    reject_links(original)
    destination, original = destination.resolve(), original.resolve()
    require(destination != original and not (original.is_dir() and destination.is_relative_to(original)), "JOURNAL_OUTPUT_SCOPE")
    require(not destination.exists() and destination.suffix.lower() == ".json", "JOURNAL_OUTPUT_EXISTS_OR_TYPE")
    with destination.open("x", encoding="utf-8", newline="\n") as handle:
        json.dump(summary, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("--output", type=Path, default=Path("summary.json"), help="New JSON summary outside the source; never overwrites")
    args = parser.parse_args()
    try:
        summary = analyze(args.input)
        write_summary(args.output, summary, args.input)
        print(json.dumps({key: summary[key] for key in ("status", "session_count", "versions", "kind_counts",
                         "history_gap_records", "recovery_counts", "recovery_codes", "error_codes", "first_utc", "last_utc")}, ensure_ascii=False))
        # 2 explicitly signals a verified prefix with a truncated tail; it is not a complete success.
        return 2 if summary["partial"] else 0
    except (JournalError, OSError, zipfile.BadZipFile, RuntimeError, EOFError, ValueError, OverflowError):
        code = sys.exc_info()[1]
        safe = str(code) if isinstance(code, JournalError) else "JOURNAL_LOCAL_READ_FAILED"
        print(json.dumps({"integrity_passed": False, "error": safe}), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
