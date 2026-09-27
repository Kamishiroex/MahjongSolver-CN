#!/usr/bin/env python3
"""Summarize schema-1/2 CN UI diagnostics without exporting node content or positions.

Counts identify visible UI components, never verified Mahjong tile counts.
Uses only the Python standard library and never extracts ZIP entries to disk.
"""

from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import sys
from typing import Any
import zipfile


MAX_REPORT_BYTES = 64 * 1024 * 1024
FAMILY_PARENT = re.compile(r"Emj/134(?:00[0-9]{2})?\Z")
FAMILY_CONTENT = re.compile(r"Emj/134(?:00[0-9]{2})?/9\Z")
ADDON_NAME = re.compile(r"Emj[A-Za-z0-9_]{0,24}\Z")
PUBLIC_IDENTITY = re.compile(r"[A-Za-z0-9.+_-]{1,160}\Z")
VERIFIED_ICON_STATUS = "RESOURCE_VERIFIED"


class DiagnosticError(ValueError):
    """Invalid or unsupported diagnostic input; messages exclude input contents."""


def object_value(value: Any, location: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise DiagnosticError(f"Expected object: {location}")
    return value


def array_value(value: Any, location: str) -> list[Any]:
    if not isinstance(value, list):
        raise DiagnosticError(f"Expected array: {location}")
    return value


def integer(value: Any, location: str, minimum: int = 0) -> int:
    if type(value) is not int or value < minimum:
        raise DiagnosticError(f"Expected integer >= {minimum}: {location}")
    return value


def boolean(value: Any, location: str) -> bool:
    if type(value) is not bool:
        raise DiagnosticError(f"Expected boolean: {location}")
    return value


def timestamp(value: Any) -> datetime:
    if not isinstance(value, str):
        raise DiagnosticError("Expected timestamp")
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise DiagnosticError("Invalid timestamp") from error
    if result.tzinfo is None:
        raise DiagnosticError("Timestamp must include a timezone")
    return result.astimezone(timezone.utc)


def identity_token(value: Any) -> str | None:
    # Preserve public version/build tokens; refuse arbitrary strings or paths.
    if value is None:
        return None
    if not isinstance(value, str) or not PUBLIC_IDENTITY.fullmatch(value):
        raise DiagnosticError("Invalid public identity token")
    return value


def error_code(value: Any) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise DiagnosticError("Expected nullable error string")
    match = re.match(r"([A-Z][A-Z0-9_]{0,79})(?:[:\uff1a]|\Z)", value)
    return match.group(1) if match else "UNCLASSIFIED_ERROR"


def supported_schema(report: dict[str, Any]) -> int:
    value = report.get("SchemaVersion")
    if type(value) is not int or value not in (1, 2):
        raise DiagnosticError("Unsupported SchemaVersion; only schemas 1 and 2 are accepted")
    return value


def candidate_status(value: Any) -> str:
    if not isinstance(value, str):
        raise DiagnosticError("Expected lower-hand DiagnosticStatus string")
    match = re.match(r"([A-Z][A-Z0-9_]{0,79})(?:[:\uff1a]|\Z)", value)
    return match.group(1) if match else "UNCLASSIFIED_STATUS"


def schema2_metadata(report: dict[str, Any], candidate_counts: Counter[str],
                     statuses: Counter[str], retained_sequences: set[int]) -> dict[str, Any]:
    capture_timeline = array_value(report.get("Timeline"), "Timeline")
    previous_sequence: int | None = None
    previous_time: datetime | None = None
    first_time: datetime | None = None
    first_sequence: int | None = None
    for raw_entry in capture_timeline:
        entry = object_value(raw_entry, "Timeline entry")
        sequence = integer(entry.get("Sequence"), "Timeline.Sequence")
        when = timestamp(entry.get("Utc"))
        if previous_sequence is not None and sequence <= previous_sequence:
            raise DiagnosticError("Timeline sequences must strictly increase")
        if previous_time is not None and when < previous_time:
            raise DiagnosticError("Timeline timestamps must not decrease")
        if first_time is None:
            first_time, first_sequence = when, sequence
        previous_sequence, previous_time = sequence, when

    first_table = report.get("FirstTableFrameSequence")
    if first_table is not None:
        first_table = integer(first_table, "FirstTableFrameSequence")
    enabled = boolean(report.get("LowerHandCaptureEnabled"), "LowerHandCaptureEnabled")
    uld_hash = report.get("LowerHandUldHash")
    if uld_hash is not None and (not isinstance(uld_hash, str) or not re.fullmatch(r"[0-9A-Fa-f]{64}", uld_hash)):
        raise DiagnosticError("Expected nullable 64-character LowerHandUldHash")
    reviews = array_value(report.get("LowerHandReviews"), "LowerHandReviews")
    reviewed_sequences: set[int] = set()
    confirmed = Counter({"true": 0, "false": 0})
    for raw_review in reviews:
        review = object_value(raw_review, "LowerHandReview")
        matches = boolean(review.get("UserConfirmedMatch"), "UserConfirmedMatch")
        confirmed[str(matches).lower()] += 1
        reviewed_sequences.add(integer(review.get("Sequence"), "LowerHandReview.Sequence"))
    return {
        "recorded_timeline": {
            "entries": len(capture_timeline),
            "first_sequence": first_sequence,
            "last_sequence": previous_sequence,
            "duration_seconds": round((previous_time - first_time).total_seconds(), 6) if first_time else 0,
        },
        "first_table_frame_sequence": first_table,
        "first_table_frame_retained": first_table in retained_sequences if first_table is not None else None,
        "lower_hand_diagnostics": {
            "capture_enabled_reported": enabled,
            "uld_sha256": uld_hash,
            "candidate_samples": candidate_counts["total"],
            "resource_verified_icon_samples": candidate_counts["resource_verified"],
            "unknown_icon_samples": candidate_counts["unknown"],
            "unverified_icon_samples": candidate_counts["unverified"],
            "status_distribution": dict(sorted(statuses.items())),
            "face_samples_present_while_capture_disabled": bool(candidate_counts["total"] and not enabled),
            "user_confirmed_match_counts": dict(confirmed),
            "review_records": len(reviews),
            "distinct_reviewed_sequences": len(reviewed_sequences),
            "interpretation": "Repeated candidate samples, not distinct tiles. RESOURCE_VERIFIED is the capture's resource status, not independently verified tile identity. Review counts are user statements, not automatic correctness checks.",
        },
    }


def sha256_file(path: Path) -> str:
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def read_report(path: Path) -> dict[str, Any]:
    with zipfile.ZipFile(path) as archive:
        entries = [entry for entry in archive.infolist() if entry.filename == "report.json"]
        if len(entries) != 1:
            raise DiagnosticError("ZIP must contain exactly one root report.json")
        entry = entries[0]
        if entry.file_size > MAX_REPORT_BYTES:
            raise DiagnosticError("report.json exceeds the 64 MiB decompression limit")
        with archive.open(entry) as stream:
            data = stream.read(MAX_REPORT_BYTES + 1)
        if len(data) > MAX_REPORT_BYTES:
            raise DiagnosticError("report.json exceeds the 64 MiB decompression limit")
    try:
        report = object_value(json.loads(data.decode("utf-8-sig")), "report")
    except (UnicodeError, ValueError, RecursionError) as error:
        raise DiagnosticError("report.json is not a supported UTF-8 JSON document") from error
    supported_schema(report)
    return report


def summarize(report: dict[str, Any], source_sha256: str) -> dict[str, Any]:
    schema = supported_schema(report)
    frames = array_value(report.get("Frames"), "Frames")
    identity = object_value(report.get("Identity"), "Identity")
    lifecycle = array_value(report.get("Lifecycle"), "Lifecycle")
    first_time = timestamp(object_value(frames[0], "frame").get("Utc")) if frames else None
    timeline: list[dict[str, Any]] = []
    addon_counts: dict[str, Counter[str]] = {}
    node_lengths: Counter[str] = Counter()
    errors: Counter[str] = Counter()
    frame_errors: Counter[str] = Counter()
    lower_hand_counts: Counter[str] = Counter()
    lower_hand_statuses: Counter[str] = Counter()
    distinct_paths: set[str] = set()
    previous_topology: tuple[str, ...] | None = None
    topology_changes = 0
    previous_sequence: int | None = None
    previous_time: datetime | None = None
    visible_emj_counts: list[int] = []

    for raw_frame in frames:
        frame = object_value(raw_frame, "frame")
        seq = integer(frame.get("Sequence"), "Sequence")
        now = timestamp(frame.get("Utc"))
        if previous_sequence is not None and seq <= previous_sequence:
            raise DiagnosticError("Frame sequences must strictly increase")
        if previous_time is not None and now < previous_time:
            raise DiagnosticError("Frame timestamps must not decrease")
        previous_sequence, previous_time = seq, now
        recognition = frame.get("Recognition")
        if not isinstance(recognition, str):
            raise DiagnosticError("Expected frame Recognition string")
        recognition_code = error_code(recognition)
        if recognition_code in ("READ_ERROR", "UPDATE_ERROR", "BUDGET_EXCEEDED"):
            frame_errors[recognition_code] += 1
        row: dict[str, Any] = {
            "sequence": seq,
            "seconds_from_first_retained_frame": round((now - first_time).total_seconds(), 6),
            "emj_present": False,
            "emj_visible": False,
            "emj_ready": False,
            "emj_atk_value_count": 0,
            "emj_visible_node_count": 0,
            "family134_parent_components": 0,
            "family134_visible_content9": 0,
            "separate135_parent_components": 0,
            "separate135_visible_content9": 0,
            "emj_duplicate_path_count": 0,
        }
        emj_paths: list[str] = []
        names_seen: set[str] = set()
        for raw_addon in array_value(frame.get("Addons"), "Addons"):
            addon = object_value(raw_addon, "addon")
            name = addon.get("Name")
            if not isinstance(name, str) or not ADDON_NAME.fullmatch(name):
                raise DiagnosticError("Unexpected addon name for CN diagnostics")
            if name in names_seen:
                raise DiagnosticError("Duplicate addon in one frame")
            names_seen.add(name)
            counter = addon_counts.setdefault(name, Counter())
            counter["probed_frames"] += 1
            present = boolean(addon.get("Present"), "Present")
            visible = boolean(addon.get("Visible"), "Visible")
            ready = boolean(addon.get("Ready"), "Ready")
            counter["present_frames"] += int(present)
            counter["visible_frames"] += int(visible)
            counter["ready_frames"] += int(ready)
            code = error_code(addon.get("Error"))
            if code:
                errors[code] += 1
                counter["error_frames"] += 1
            nodes = array_value(addon.get("VisibleNodes"), "VisibleNodes")
            counter["retained_nodes"] += len(nodes)
            if schema == 2 and addon.get("LowerHandFaces") is not None:
                for raw_face in array_value(addon["LowerHandFaces"], "LowerHandFaces"):
                    face = object_value(raw_face, "LowerHandFace")
                    status = candidate_status(face.get("DiagnosticStatus"))
                    lower_hand_statuses[status] += 1
                    lower_hand_counts["total"] += 1
                    icon = face.get("IconId")
                    if icon is not None and integer(icon, "IconId") > 0xFFFFFFFF:
                        raise DiagnosticError("IconId is outside the unsigned 32-bit range")
                    if icon in (None, 0):
                        lower_hand_counts["unknown"] += 1
                    elif face["DiagnosticStatus"] == VERIFIED_ICON_STATUS:
                        lower_hand_counts["resource_verified"] += 1
                    else:
                        lower_hand_counts["unverified"] += 1
            for raw_node in nodes:
                node = object_value(raw_node, "node")
                node_path = node.get("Path")
                if not isinstance(node_path, str) or not re.fullmatch(r"Emj[A-Za-z0-9_]{0,24}(?:/[0-9]+)+", node_path):
                    raise DiagnosticError("Invalid structural node path")
                distinct_paths.add(node_path)
                if integer(node.get("Type"), "Type") == 3:
                    length = node.get("TextByteLength")
                    node_lengths["null" if length is None else str(integer(length, "TextByteLength"))] += 1
                if name == "Emj":
                    emj_paths.append(node_path)
            if name == "Emj":
                row.update(
                    emj_present=present,
                    emj_visible=visible,
                    emj_ready=ready,
                    emj_atk_value_count=integer(addon.get("AtkValueCount"), "AtkValueCount"),
                    emj_visible_node_count=len(nodes),
                    family134_parent_components=sum(bool(FAMILY_PARENT.fullmatch(path)) for path in emj_paths),
                    family134_visible_content9=sum(bool(FAMILY_CONTENT.fullmatch(path)) for path in emj_paths),
                    separate135_parent_components=emj_paths.count("Emj/135"),
                    separate135_visible_content9=emj_paths.count("Emj/135/9"),
                    emj_duplicate_path_count=len(emj_paths) - len(set(emj_paths)),
                )
                if present:
                    visible_emj_counts.append(len(nodes))
        topology = tuple(sorted(emj_paths))
        if previous_topology is not None and topology != previous_topology:
            topology_changes += 1
        previous_topology = topology
        timeline.append(row)

    lifecycle_summary = []
    for raw_event in lifecycle:
        event = object_value(raw_event, "lifecycle event")
        addon = event.get("Addon")
        kind = event.get("Event")
        if not isinstance(addon, str) or not ADDON_NAME.fullmatch(addon):
            raise DiagnosticError("Unexpected lifecycle addon name")
        if kind not in ("PostSetup", "PreFinalize"):
            raise DiagnosticError("Unexpected lifecycle event")
        when = timestamp(event.get("Utc"))
        lifecycle_summary.append({
            "addon": addon,
            "event": kind,
            "seconds_from_first_retained_frame": round((when - first_time).total_seconds(), 6) if first_time else None,
        })

    session = object_value(report.get("SessionStatus"), "SessionStatus")
    if schema == 2 and not isinstance(report.get("TextLengthContract"), str):
        raise DiagnosticError("Schema 2 requires a TextLengthContract string")
    result = {
        "summary_schema": 2,
        "source_schema": schema,
        "source_zip_sha256": source_sha256,
        "interpretation": "Structural UI component counts only; not verified hand or tile counts. No text, image IDs, screen coordinates, memory addresses, usernames or original paths are exported.",
        "plugin_version": identity_token(report.get("PluginVersion")),
        "upstream_commit": identity_token(report.get("UpstreamCommit")),
        "identity": {**{key: identity_token(identity.get(key)) for key in (
            "GameVersion", "DalamudVersion", "DalamudCommit", "ClientStructsVersion", "Language", "Runtime")},
            "Api": integer(identity.get("Api"), "Api"), "error_code": error_code(identity.get("Error"))},
        "final_session": {
            "code": identity_token(session.get("Code")),
            "ready": boolean(session.get("Ready"), "SessionStatus.Ready"),
        },
        "totals": {
            "retained_frames": len(frames),
            "dropped_old_frames": integer(report.get("DroppedOldFrames"), "DroppedOldFrames"),
            "retained_nodes": sum(counter["retained_nodes"] for counter in addon_counts.values()),
            "distinct_node_paths": len(distinct_paths),
            "consecutive_emj_topology_changes": topology_changes,
            "present_emj_node_count_min": min(visible_emj_counts, default=0),
            "present_emj_node_count_max": max(visible_emj_counts, default=0),
            "probe_errors": sum(errors.values()),
            "error_frames": sum(frame_errors.values()),
            "lifecycle_events": len(lifecycle),
            "text_node_samples": sum(node_lengths.values()),
        },
        "addons": {name: dict(sorted(counter.items())) for name, counter in sorted(addon_counts.items())},
        "probe_error_codes": dict(sorted(errors.items())),
        "frame_error_codes": dict(sorted(frame_errors.items())),
        "text_byte_length_distribution": dict(sorted(node_lengths.items())),
        "text_length_contract": {
            "source_schema": schema,
            "basis": "NodeText.StringLength (legacy)" if schema == 1 else "NodeText.BufUsed - 1; invalid buffer accounting stays null",
            "declared_bufused_minus_one": report.get("TextLengthContract", "").startswith("NodeText.BufUsed - 1") if schema == 2 else None,
            "interpretation": "Schema 1 zero readings do not prove displayed text was empty." if schema == 1 else "Schema 2 zero is a recorded zero-byte buffer; null is unknown. Neither establishes displayed text semantics. Do not apply schema-1 StringLength conclusions to these readings.",
        },
        "lifecycle": lifecycle_summary,
        "timeline_scope": "retained detailed Frames; schema 2 has a separate recorded_timeline summary",
        "timeline": timeline,
    }
    if schema == 2:
        result.update(schema2_metadata(report, lower_hand_counts, lower_hand_statuses,
                                       {row["sequence"] for row in timeline}))
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("zip_path", type=Path, help="Local mjcn-diagnostic-*.zip; never uploaded")
    parser.add_argument("--output", type=Path, help="Write summary JSON to this file instead of stdout")
    args = parser.parse_args()
    try:
        if args.output and args.output.resolve() == args.zip_path.resolve():
            raise DiagnosticError("Output must not overwrite the source ZIP")
        summary = summarize(read_report(args.zip_path), sha256_file(args.zip_path))
        output = json.dumps(summary, ensure_ascii=False, indent=2, allow_nan=False) + "\n"
        if args.output:
            args.output.write_text(output, encoding="utf-8")
        else:
            print(output, end="")
    except (DiagnosticError, OSError, zipfile.BadZipFile, RuntimeError, NotImplementedError) as error:
        # Unexpected OS/archive messages can contain the private input path.
        message = str(error) if isinstance(error, DiagnosticError) else type(error).__name__
        print(f"Diagnostic analysis failed: {message}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
