#!/usr/bin/env python3
"""Stream one mjcn-local-capture session directory or ZIP into a redacted summary.

Python standard library only; ZIP members are never extracted. No hand sequence,
source path, text content, marker, coordinate, or memory address is printed.
"""
from __future__ import annotations

import argparse
from collections import Counter
from contextlib import contextmanager
from datetime import datetime
import hashlib
import json
from pathlib import Path
import re
import stat
import sys
import zipfile
import zlib


MAX_TOTAL_BYTES = 256 * 1024 * 1024
MAX_LINE_BYTES = 4 * 1024 * 1024
MAX_EXPANDED_BYTES = 2 * 1024 * 1024 * 1024
MAX_FRAMES = 7200
MAX_METADATA_BYTES = 1024 * 1024
SEGMENT_NAME = re.compile(r"segment-([0-9]{6})\.jsonl(?:\.gz)?\Z")
KINDS = {"header", "frame", "review", "lifecycle", "status"}
ADDON_NAME = re.compile(r"Emj[A-Za-z0-9_]{0,24}\Z")
ENVELOPE_KEYS = {"SchemaVersion", "Sequence", "Utc", "Kind", "Payload"}
PUBLIC_AREAS = {"lower-meld-candidate", "river-bottom", "river-right", "river-top", "river-left", "dora-display",
                "lower-row-geometry"}
VERSION = re.compile(r"[0-9]+(?:\.[0-9]+){1,5}(?:[-+][A-Za-z0-9.-]+)?\Z")
CODE = re.compile(r"([A-Z][A-Z0-9_]{0,79})(?:[:\uff1a]|\Z)")


class SessionError(ValueError):
    """Errors deliberately contain no input content or filesystem paths."""


def require(condition, message):
    if not condition:
        raise SessionError(message)


def object_value(value, location):
    require(isinstance(value, dict), "Expected object: " + location)
    return value


def integer(value, location, minimum=0):
    require(type(value) is int and value >= minimum, "Invalid integer: " + location)
    return value


def boolean(value, location):
    require(type(value) is bool, "Invalid boolean: " + location)
    return value


def array(value, location, maximum=8192):
    require(isinstance(value, list) and len(value) <= maximum, "Invalid bounded array: " + location)
    return value


def timestamp(value):
    require(isinstance(value, str) and len(value) <= 64, "Invalid timestamp")
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError:
        raise SessionError("Invalid timestamp") from None
    require(result.tzinfo is not None, "Timestamp lacks timezone")
    return result


def code(value, fallback="UNCLASSIFIED_ERROR"):
    if value is None:
        return None
    require(isinstance(value, str), "Invalid status/error type")
    match = CODE.match(value)
    return match.group(1) if match else fallback


def no_duplicate_keys(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON key")
        result[key] = value
    return result


def json_object(raw, location):
    try:
        value = json.loads(raw.decode("utf-8"), object_pairs_hook=no_duplicate_keys,
                           parse_constant=lambda _: (_ for _ in ()).throw(SessionError("Non-finite JSON number")))
    except (UnicodeError, json.JSONDecodeError, RecursionError):
        raise SessionError("Invalid JSON: " + location) from None
    return object_value(value, location)


class SessionSource:
    """One fixed, flat member listing; reads remain bounded even if a directory grows."""
    def __init__(self, path):
        self.path = Path(path)
        self.archive = None
        self.sizes = {}
        self.actual_bytes = 0
        self.expanded_bytes = 0
        self.ignored_archives = 0
        require(not self.path.is_symlink(), "Symlink input is not supported")
        if self.path.is_dir():
            self.directory = True
            for entry in self.path.iterdir():
                info = entry.lstat()
                require(stat.S_ISREG(info.st_mode) and not entry.is_symlink()
                        and not getattr(info, "st_file_attributes", 0) & 0x400,
                        "Session directory contains a non-regular entry")
                if entry.name in {"capture.zip", "capture.partial.zip"}:
                    self.ignored_archives += 1
                    continue
                self._add(entry.name, info.st_size)
        else:
            self.directory = False
            try:
                self.archive = zipfile.ZipFile(self.path)
                for info in self.archive.infolist():
                    require(not info.is_dir() and not info.flag_bits & 1
                            and not stat.S_ISLNK(info.external_attr >> 16),
                            "ZIP contains a directory, symlink or encrypted entry")
                    self._add(info.filename, info.file_size)
            except Exception:
                if self.archive:
                    self.archive.close()
                raise
        require("session.json" in self.sizes, "Missing session marker")
        self.segments = sorted(name for name in self.sizes if SEGMENT_NAME.fullmatch(name))
        require(self.segments, "No segment files")
        self.compressed = self.segments[0].endswith(".gz")
        suffix = ".jsonl.gz" if self.compressed else ".jsonl"
        require(self.segments == [f"segment-{i:06d}{suffix}" for i in range(1, len(self.segments) + 1)],
                "Segment numbering must be contiguous from 000001")
        require(sum(self.sizes.values()) <= MAX_TOTAL_BYTES, "Session exceeds 256 MiB limit")

    def _add(self, name, size):
        require(name in {"session.json", "manifest.json"} or SEGMENT_NAME.fullmatch(name),
                "Unrecognized or unsafe entry name")
        require(name not in self.sizes and name.casefold() not in {n.casefold() for n in self.sizes},
                "Duplicate entry name")
        require(0 <= size <= MAX_TOTAL_BYTES, "Entry exceeds size limit")
        require(len(self.sizes) < 4096, "Too many entries")
        self.sizes[name] = size

    @contextmanager
    def open(self, name):
        if self.archive is not None:
            handle = self.archive.open(name)
        else:
            member = self.path / name
            require(not member.is_symlink(), "Session entry changed to symlink")
            handle = member.open("rb")
        try:
            yield handle
        finally:
            handle.close()

    def consume(self, count):
        self.actual_bytes += count
        require(self.actual_bytes <= MAX_TOTAL_BYTES, "Actual input exceeds 256 MiB limit")

    def expand(self, count):
        self.expanded_bytes += count
        require(self.expanded_bytes <= MAX_EXPANDED_BYTES, "Expanded records exceed 2 GiB limit")

    def metadata(self, name):
        require(self.sizes[name] <= MAX_METADATA_BYTES, "Metadata exceeds 1 MiB limit")
        with self.open(name) as handle:
            raw = handle.read(MAX_METADATA_BYTES + 1)
        self.consume(len(raw))
        require(len(raw) <= MAX_METADATA_BYTES, "Metadata exceeds 1 MiB limit")
        return json_object(raw, "metadata")

    def close(self):
        if self.archive is not None:
            self.archive.close()


def load_catalog(path):
    with Path(path).open("rb") as handle:
        raw = handle.read(MAX_LINE_BYTES + 1)
    require(len(raw) <= MAX_LINE_BYTES, "Catalog exceeds size limit")
    data = json_object(raw, "catalog")
    require(type(data.get("SchemaVersion")) is int and data["SchemaVersion"] == 1,
            "Unsupported catalog schema")
    pairs = {}
    icons = set()
    for item in array(data.get("Resources"), "catalog resources", 4096):
        item = object_value(item, "catalog resource")
        icon = integer(item.get("IconId"), "catalog IconId", 1)
        digest = item.get("StandardCrc32")
        require(isinstance(digest, str) and re.fullmatch(r"[0-9A-Fa-f]{8}", digest), "Invalid catalog CRC")
        key = (icon, int(digest, 16))
        require(key not in pairs, "Duplicate catalog resource pair")
        pairs[key] = boolean(item.get("MahjongSemanticVisuallyConfirmed"), "catalog confirmation")
        icons.add(icon)
    return data.get("GameVersion"), pairs, icons


def public_identity(header):
    identity = object_value(header.get("Identity"), "header Identity")
    result = {}
    for key in ("GameVersion", "DalamudVersion", "ClientStructsVersion", "Runtime"):
        value = identity.get(key)
        require(isinstance(value, str) and len(value) <= 100 and VERSION.fullmatch(value),
                "Invalid public version field")
        result[key] = value
    result["Api"] = integer(identity.get("Api"), "Api", 1)
    for container, key in ((identity, "DalamudCommit"), (header, "UpstreamCommit")):
        value = container.get(key)
        require(isinstance(value, str) and re.fullmatch(r"[0-9a-fA-F]{40}", value), "Invalid public commit")
        result[key] = value
    value = header.get("PluginVersion")
    require(isinstance(value, str) and len(value) <= 100 and VERSION.fullmatch(value), "Invalid plugin version")
    result["PluginVersion"] = value
    language = identity.get("Language")
    require(language in {"ChineseSimplified", "ChineseTraditional", "Japanese", "English", "German", "French", "Korean"},
            "Unrecognized public language")
    result["Language"] = language
    result["ErrorCode"] = code(identity.get("Error"))
    return result


def public_capture_scope(header):
    """Validate legacy and at-start mode metadata without inferring game actions."""
    result = {}
    for field in ("LowerHandCaptureEnabled", "PublicLayoutCaptureEnabled", "FullGameStateVerified"):
        result[field] = boolean(header.get(field), "header capture scope")
    require(not result["FullGameStateVerified"], "Header describes unsupported verified game-state scope")
    legacy = "AutomationEnabled" in header
    mode_field, enabled_field = "GameplayModeAtCaptureStart", "AutomationEnabledAtCaptureStart"
    current = mode_field in header or enabled_field in header
    require(legacy or current, "Missing header automation metadata")
    if legacy:
        result["AutomationEnabled"] = boolean(header["AutomationEnabled"], "legacy automation metadata")
    if current:
        mode = header.get(mode_field)
        require(isinstance(mode, str) and mode in ("Off", "Manual", "Automatic"),
                "Invalid gameplay mode metadata")
        enabled = boolean(header.get(enabled_field), "at-start automation metadata")
        require(enabled == (mode == "Automatic"), "Conflicting gameplay mode and automation metadata")
        if legacy:
            require(result["AutomationEnabled"] == enabled, "Conflicting legacy and at-start automation metadata")
        result[mode_field] = mode
        result[enabled_field] = enabled
    return result


class Statistics:
    def __init__(self, catalog):
        self.catalog_version, self.catalog_pairs, self.catalog_icons = catalog
        self.identity = None
        self.counts = Counter()
        self.kinds = Counter()
        self.errors = Counter()
        self.recognition_codes = Counter()
        self.status_codes = Counter()
        self.face_statuses = Counter()
        self.catalog_counts = Counter()
        self.layout_statuses = Counter()
        self.layouts = Counter()
        self.layout_frames = Counter()
        self.lifecycle_events = Counter()
        self.atk_value_counts = Counter()
        self.capture_scope = {}
        self.readings = Counter()
        self.reviews = Counter()
        self.last_frame_sequence = 0
        self.first_time = self.last_time = None
        self.first_frame_time = self.last_frame_time = None
        self.frame_gap_count = self.frame_sequence_gap_count = 0
        self.clock_regressions = 0
        self.min_frame_interval = self.max_frame_interval = None

    def face(self, raw):
        face = object_value(raw, "candidate")
        self.counts["candidate_samples"] += 1
        require(isinstance(face.get("DiagnosticStatus"), str), "Missing candidate status")
        self.face_statuses[code(face["DiagnosticStatus"], "UNCLASSIFIED_STATUS")] += 1
        icon, digest = face.get("IconId"), face.get("FacePathHash")
        if icon is None or digest is None:
            self.catalog_counts["missing_icon_or_hash"] += 1
            return
        integer(icon, "IconId"); integer(digest, "FacePathHash")
        require(icon <= 0xFFFFFFFF and digest <= 0xFFFFFFFF, "Candidate scalar outside uint32")
        if self.identity["GameVersion"] != self.catalog_version:
            category = "version_mismatch_not_compared"
        elif (icon, digest) in self.catalog_pairs:
            category = "exact_pair_known_semantics" if self.catalog_pairs[(icon, digest)] else "exact_pair_unknown_semantics"
        else:
            category = "hash_mismatch" if icon in self.catalog_icons else "unknown_icon"
        self.catalog_counts[category] += 1

    def process(self, envelope):
        require(set(envelope) == ENVELOPE_KEYS, "Unsupported record envelope fields")
        require(type(envelope.get("SchemaVersion")) is int and envelope["SchemaVersion"] == 1,
                "Unsupported record schema")
        sequence = integer(envelope.get("Sequence"), "record Sequence", 1)
        require(sequence == self.counts["records"] + 1, "Record sequences are not contiguous")
        when = timestamp(envelope.get("Utc"))
        kind = envelope.get("Kind")
        require(isinstance(kind, str) and kind in KINDS, "Unsupported record kind")
        payload = object_value(envelope.get("Payload"), "Payload")
        if sequence == 1:
            require(kind == "header", "First record must be header")
            self.identity = public_identity(payload)
            self.capture_scope = public_capture_scope(payload)
            self.first_time = when
        else:
            require(kind != "header", "Duplicate header")
        self.last_time = when
        self.counts["records"] += 1
        self.kinds[kind] += 1
        if kind == "frame":
            frame_sequence = integer(payload.get("Sequence"), "frame Sequence", 1)
            require(frame_sequence > self.last_frame_sequence, "Frame sequences must increase")
            if self.last_frame_sequence and frame_sequence != self.last_frame_sequence + 1:
                self.frame_sequence_gap_count += 1
            self.last_frame_sequence = frame_sequence
            frame_time = timestamp(payload.get("Utc"))
            if self.first_frame_time is None:
                self.first_frame_time = frame_time
            if self.last_frame_time is not None:
                interval = (frame_time - self.last_frame_time).total_seconds()
                if interval < 0:
                    self.clock_regressions += 1
                else:
                    self.min_frame_interval = interval if self.min_frame_interval is None else min(self.min_frame_interval, interval)
                    self.max_frame_interval = interval if self.max_frame_interval is None else max(self.max_frame_interval, interval)
                    self.frame_gap_count += interval > 1.0
            self.last_frame_time = frame_time
            self.counts["frames"] += 1
            require(self.counts["frames"] <= MAX_FRAMES, "Session exceeds 7200 frame limit")
            frame_areas = set()
            for addon in array(payload.get("Addons"), "Addons", 16):
                addon = object_value(addon, "addon")
                require(isinstance(addon.get("Name"), str) and ADDON_NAME.fullmatch(addon["Name"]), "Unrecognized addon")
                for field in ("Present", "Visible", "Ready"):
                    self.counts["addon_" + field.lower()] += boolean(addon.get(field), "addon status")
                if addon["Name"] == "Emj" and addon["Present"] and "AtkValueCount" in addon:
                    count = integer(addon["AtkValueCount"], "AtkValueCount")
                    require(count <= 65535, "AtkValueCount outside uint16")
                    self.atk_value_counts[count] += 1
                failure = code(addon.get("Error"))
                if failure:
                    self.errors[failure] += 1
                self.counts["ui_node_samples"] += len(array(addon.get("VisibleNodes"), "VisibleNodes", 2048))
                for face in array(addon.get("LowerHandFaces") or [], "LowerHandFaces", 64):
                    self.face(face)
                reading = addon.get("LowerHandReading")
                if reading is not None:
                    reading = object_value(reading, "LowerHandReading")
                    require(isinstance(reading.get("Code"), str), "Missing reading code")
                    self.readings[code(reading["Code"], "UNCLASSIFIED_STATUS")] += 1
                    self.counts["stable_reading_frames"] += boolean(reading.get("Stable"), "reading Stable")
                for layout in array(addon.get("PublicLayouts") or [], "PublicLayouts", 512):
                    layout = object_value(layout, "public layout")
                    require(layout.get("Area") in PUBLIC_AREAS, "Unrecognized public layout area")
                    self.layouts[layout["Area"]] += 1
                    frame_areas.add(layout["Area"])
                    require(isinstance(layout.get("Status"), str), "Missing public layout status")
                    self.layout_statuses[code(layout["Status"], "UNCLASSIFIED_STATUS")] += 1
                    self.counts["public_transform_samples"] += len(array(layout.get("NodeAndParents"), "NodeAndParents", 16))
                    self.counts["selected_shell_part_samples"] += layout.get("ShellPart") is not None
            self.layout_frames.update(frame_areas)
            require(isinstance(payload.get("Recognition"), str), "Missing frame recognition")
            recognition = code(payload["Recognition"], "NO_MACHINE_ERROR_CODE")
            if recognition != "NO_MACHINE_ERROR_CODE":
                self.recognition_codes[recognition] += 1
        elif kind == "review":
            integer(payload.get("Sequence"), "review Sequence", 1)
            self.reviews["match" if boolean(payload.get("UserConfirmedMatch"), "review match") else "mismatch"] += 1
        elif kind == "lifecycle":
            require(isinstance(payload.get("Addon"), str) and ADDON_NAME.fullmatch(payload["Addon"]), "Unrecognized lifecycle addon")
            self.counts["lifecycle_records"] += 1
            event = payload.get("Event")
            self.lifecycle_events[event if event in ("PostSetup", "PreFinalize") else "OTHER"] += 1
        elif kind == "status":
            # Status text is deliberately not exported, even if a writer adds fields.
            for field in ("Error", "Fault"):
                if payload.get(field) is not None:
                    self.errors[code(payload[field])] += 1
            if payload.get("Code") is not None:
                self.status_codes[code(payload["Code"], "UNCLASSIFIED_STATUS")] += 1

    def summary(self):
        require(self.identity is not None, "Missing committed header")
        return {
            "summary_schema": 1, "source_schema": 1, "identity": self.identity,
            "capture_scope": self.capture_scope,
            "counts": dict(sorted(self.counts.items())), "record_kinds": dict(sorted(self.kinds.items())),
            "error_codes": dict(sorted(self.errors.items())), "candidate_statuses": dict(sorted(self.face_statuses.items())),
            "frame_recognition_codes": dict(sorted(self.recognition_codes.items())),
            "recorder_status_codes": dict(sorted(self.status_codes.items())),
            "catalog_game_version_matches": self.identity["GameVersion"] == self.catalog_version,
            "catalog_match_counts": dict(sorted(self.catalog_counts.items())),
            "lower_reading_codes": dict(sorted(self.readings.items())), "user_review_counts": dict(sorted(self.reviews.items())),
            "public_layout_area_counts": dict(sorted(self.layouts.items())),
            "public_layout_frame_counts": dict(sorted(self.layout_frames.items())),
            "public_layout_statuses": dict(sorted(self.layout_statuses.items())),
            "lifecycle_event_counts": dict(sorted(self.lifecycle_events.items())),
            "emj_atk_value_count_histogram": {str(k): v for k, v in sorted(self.atk_value_counts.items())},
            "timing": {
                "session_wall_seconds": round((self.last_time - self.first_time).total_seconds(), 6),
                "frame_span_seconds": round((self.last_frame_time - self.first_frame_time).total_seconds(), 6)
                    if self.first_frame_time is not None else None,
                "minimum_frame_interval_seconds": self.min_frame_interval,
                "maximum_frame_interval_seconds": self.max_frame_interval,
                "frame_intervals_over_one_second": self.frame_gap_count,
                "frame_clock_regressions": self.clock_regressions,
                "frame_sequence_gap_events": self.frame_sequence_gap_count,
                "interpretation": "Wall-clock intervals, not monotonic performance measurements. Frame sequence gaps may be lifecycle events, not lost records. AtkValue counts have no assigned game-phase semantics.",
            },
            "interpretation": "Repeated visible diagnostic samples, not distinct tiles or verified full game state. Automation fields report header configuration only; they do not prove actions occurred, remained enabled, or were correct. At-start mode fields do not describe subsequent mode changes. User reviews are statements, not automated correctness checks. No hand sequence, text, coordinates or source paths are exported.",
        }


def read_segment(source, name, last_segment, recoverable, stats):
    digest = hashlib.sha256()
    length = records = tail_bytes = 0
    with source.open(name) as handle:
        def read(count, line=False):
            nonlocal length
            raw = handle.readline(count) if line else handle.read(count)
            source.consume(len(raw)); length += len(raw); digest.update(raw)
            return raw

        if not source.compressed:
            while raw := read(MAX_LINE_BYTES + 1, line=True):
                source.expand(len(raw))
                require(len(raw) <= MAX_LINE_BYTES, "JSONL line exceeds 4 MiB limit")
                if not raw.endswith(b"\n"):
                    require(last_segment and recoverable, "Uncommitted tail is only recoverable in final incomplete segment")
                    tail_bytes = len(raw)
                    break
                stats.process(json_object(raw, "committed record")); records += 1
                stats.counts["committed_record_bytes"] += len(raw)
        else:
            decoder = zlib.decompressobj(16 + zlib.MAX_WBITS)
            pending = b""
            member = bytearray()
            member_bytes = 0
            while True:
                if not pending:
                    pending = read(64 * 1024)
                    if not pending:
                        if member_bytes or member:
                            require(last_segment and recoverable, "Incomplete gzip member in finalized or non-final segment")
                            tail_bytes = member_bytes
                        break
                try:
                    block = decoder.decompress(pending, MAX_LINE_BYTES - len(member) + 1)
                except zlib.error:
                    raise SessionError("Invalid gzip member or CRC; corrupt members are not recovered") from None
                source.expand(len(block)); member.extend(block)
                require(len(member) <= MAX_LINE_BYTES, "Expanded gzip record exceeds 4 MiB limit")
                remainder = decoder.unused_data if decoder.eof else decoder.unconsumed_tail
                member_bytes += len(pending) - len(remainder)
                pending = remainder
                if decoder.eof:
                    require(member.endswith(b"\n") and member.count(b"\n") == 1,
                            "Each gzip member must contain exactly one committed JSONL record")
                    stats.process(json_object(bytes(member), "committed gzip record")); records += 1
                    stats.counts["committed_record_bytes"] += len(member)
                    decoder = zlib.decompressobj(16 + zlib.MAX_WBITS)
                    member = bytearray(); member_bytes = 0
    return {"FileName": name, "Bytes": length, "Records": records, "Sha256": digest.hexdigest().upper()}, tail_bytes


def analyze(input_path, catalog_path):
    source = SessionSource(input_path)
    try:
        marker = source.metadata("session.json")
        require(type(marker.get("SchemaVersion")) is int and marker["SchemaVersion"] == 1,
                "Unsupported session marker schema")
        require(marker.get("Format") == "mjcn-local-capture", "Unsupported session format")
        require(isinstance(marker.get("SessionId"), str) and re.fullmatch(r"[0-9a-fA-F]{32}", marker["SessionId"]),
                "Invalid session identifier")
        compression = marker.get("Compression")
        require(compression == "gzip-record-members" if source.compressed else compression in (None, "none"),
                "Unsupported or mismatched segment compression")
        manifest = source.metadata("manifest.json") if "manifest.json" in source.sizes else None
        if manifest is not None:
            require(type(manifest.get("SchemaVersion")) is int and manifest["SchemaVersion"] == 1,
                    "Unsupported manifest schema")
            require(manifest.get("Format") == "mjcn-local-capture", "Unsupported manifest format")
            require(manifest.get("State") in {"complete", "faulted", "recovered"}, "Unsupported manifest state")
            require(manifest.get("SessionId") == marker.get("SessionId"), "Session identifiers differ")
            require(manifest.get("Compression") == compression, "Manifest compression differs")
        stats = Statistics(load_catalog(catalog_path))
        segment_results = []
        tail_bytes = 0
        for index, name in enumerate(source.segments):
            result, ignored = read_segment(source, name, index == len(source.segments) - 1,
                                           manifest is None or manifest["State"] != "complete", stats)
            segment_results.append(result); tail_bytes += ignored
        require(stats.identity is not None, "Missing committed header")
        require(marker.get("PluginVersion") == stats.identity["PluginVersion"], "Marker plugin version differs")
        if manifest is not None:
            expected = array(manifest.get("Segments"), "manifest Segments", 4096)
            require(len(expected) == len(segment_results), "Manifest segment count differs")
            for actual, claimed in zip(segment_results, expected):
                claimed = object_value(claimed, "manifest segment")
                require(claimed.get("FileName") == actual["FileName"], "Manifest segment order differs")
                for key in ("Bytes", "Records"):
                    require(integer(claimed.get(key), "manifest segment count") == actual[key], "Manifest segment counts differ")
                require(isinstance(claimed.get("Sha256"), str) and claimed["Sha256"].upper() == actual["Sha256"],
                        "Manifest segment hash differs")
            require(integer(manifest.get("RecordCount"), "RecordCount") == stats.counts["records"], "Manifest record count differs")
            require(integer(manifest.get("JsonlBytes"), "JsonlBytes") == sum(s["Bytes"] for s in segment_results), "Manifest byte count differs")
            require(integer(manifest.get("UncompressedJsonlBytes"), "UncompressedJsonlBytes") == stats.counts["committed_record_bytes"],
                    "Manifest expanded byte count differs")
            accepted = integer(manifest.get("AcceptedRecordCount"), "AcceptedRecordCount")
            unwritten = integer(manifest.get("UnwrittenRecords"), "UnwrittenRecords")
            require(accepted - stats.counts["records"] == unwritten, "Manifest unwritten-record accounting differs")
            if manifest.get("Fault") is not None:
                stats.errors[code(manifest["Fault"])] += 1
        summary = stats.summary()
        summary["session"] = {"state": manifest["State"] if manifest else "in_progress_or_interrupted",
                              "segments": len(segment_results), "jsonl_bytes": sum(s["Bytes"] for s in segment_results),
                              "ignored_uncommitted_tail_bytes": tail_bytes,
                              "expanded_bytes": source.expanded_bytes,
                              "compression": compression or "none",
                              "unwritten_accepted_records": manifest["UnwrittenRecords"] if manifest else None,
                              "ignored_archive_copies": source.ignored_archives,
                              "manifest_verified": manifest is not None}
        return summary
    finally:
        source.close()


def write_summary(path, summary, input_path):
    destination = Path(path)
    original = Path(input_path).resolve()
    resolved = destination.resolve()
    require(resolved != original and not (original.is_dir() and resolved.is_relative_to(original)),
            "Output must not replace or be written inside the input")
    require(not destination.exists() and not destination.is_symlink(), "Output already exists; refusing overwrite")
    with destination.open("x", encoding="utf-8", newline="\n") as handle:
        json.dump(summary, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="One local session directory or capture ZIP")
    parser.add_argument("--catalog", type=Path, default=Path(__file__).resolve().parent.parent / "docs/cn/evidence/tile-resource-catalog.json")
    parser.add_argument("--output", type=Path, help="New summary file outside input; never overwrites")
    args = parser.parse_args()
    try:
        summary = analyze(args.input, args.catalog)
        if args.output:
            write_summary(args.output, summary, args.input)
        else:
            print(json.dumps(summary, ensure_ascii=False, indent=2))
    except (SessionError, OSError, zipfile.BadZipFile, RuntimeError, zlib.error, EOFError) as error:
        # OS/ZIP messages can contain private paths; only our redacted errors may be shown.
        message = str(error) if isinstance(error, SessionError) else type(error).__name__
        print("Session analysis failed: " + message, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
