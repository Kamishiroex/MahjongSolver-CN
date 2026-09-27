"""Extract public-only evidence for the 0.6.0.1 missed-opening Chi regression.

Usage: python scripts/extract-chi-response-fixture.py events.jsonl new-fixture.json [prefix-bytes]
An active log is hashed by the exact complete-line prefix that was read.
"""
import hashlib
import json
import pathlib
import sys


def main():
    source, destination = map(pathlib.Path, sys.argv[1:3])
    if destination.exists():
        raise FileExistsError(destination)
    with source.open("rb") as stream:
        raw = stream.read(int(sys.argv[3]) if len(sys.argv) > 3 else -1)
    raw = raw[:raw.rfind(b"\n") + 1]
    rows = [json.loads(line)["Entry"] for line in raw.decode("utf-8-sig").splitlines()]
    tables = {row["Data"]["Sequence"]: (line, row) for line, row in enumerate(rows, 1)
              if row["Kind"] == "public_table_observation"}
    snapshots = {row["Data"]["Snapshot"]["Observation"]["Sequence"]: row["Data"]["Snapshot"]
                 for row in rows if row["Kind"] == "public_event" and row["Data"].get("Snapshot")}
    required = [1, 228, 235, 236, 237, 238, 239, 245, 246, 247, 248, 249]
    expected_hand = [(tile["Path"], tile["Kind34"], tile["RedFive"]) for tile in tables[249][1]["Data"]["Lower"]["Tiles"]]

    def same_window(data):
        hand = [(tile["Path"], tile["Kind34"], tile["RedFive"]) for tile in data["Lower"]["Tiles"]]
        enabled = {row["Action"] for row in (data.get("ActionMenu") or {}).get("Rows", []) if row.get("Enabled")}
        return hand == expected_hand and {"Chi", "Pass"} <= enabled and any(
            tile["Area"] == "river-left" and tile["SlotPath"] == "Emj/1270004/4" and tile["Kind34"] == 5 and tile["Stable"]
            for tile in data["Table"]["Tiles"])

    last = max(sample for sample, (_, row) in tables.items() if sample in snapshots and sample > 249 and same_window(row["Data"]))
    sections, frames = [], []
    keys = "Lower Table Status Dora RoundTitle ActionMenu OpponentHands HandInteraction Riichi ProbeError".split()
    for sample in required + [last]:
        line, row = tables[sample]
        data = row["Data"]
        refs = {}
        for key in keys:
            value = data.get(key)
            if value not in sections:
                sections.append(value)
            refs[key] = sections.index(value)
        frames.append({"Sample": sample, "Utc": data["Utc"], "SourceLine": line,
                       "JournalSequence": row["Sequence"], "Sections": refs,
                       "RecordedRoundId": snapshots[sample]["RoundId"]})
    session = next(row["Data"] for row in rows if row["Kind"] == "session_started")
    fixture = {
        "Schema": 1, "Purpose": "Offline public read failure / missed opening / newly observed Chi response regression.",
        "Source": {"SessionId": rows[0]["SessionId"], "LogName": "events.jsonl", "SourceWasActive": True,
                   "PrefixBytes": len(raw), "PrefixSha256": hashlib.sha256(raw).hexdigest(),
                   "LastJournalSequenceAtRead": rows[-1]["Sequence"],
                   "RecordedPluginVersion": session["PluginVersion"], "RuntimeIdentity": session["Identity"]},
        "Sections": sections, "Frames": frames, "LateReadOnlySample": last,
        "LateRecordedPublicSnapshot": snapshots[last],
        "ManagedHoldCondition": {"AfterRecordedSample": 249, "SimulatedSequence": 250,
                                 "ReusedRecordedSample": 249, "ElapsedMilliseconds": 100,
                                 "IsOriginalObservation": False,
                                 "Reason": "The next original semantic observation is sample384 about15s later. New response-highlight lifecycle needs one more unchanged confirmation after sample249; this is an explicit offline persistence condition."},
        "ReconstructionConditions": [
            "Public lower hand resources, identities, ordering, menus, status, table geometry and visual styles are copied from indicated source records. Repeated JSON sections are deduplicated only.",
            "Recorded table fields contain verified decoded Kind34/Red rather than raw resource IDs. The probe reconstructs their pinned canonical public icon/hash; this exercises managed tracking, not original native pointer/resource ownership reads.",
            "Recorded hand interaction supplies actual X positions. Unlogged lower Y/width/height are reconstructed as non-overlapping rectangles for the managed layout gate, not claimed live coordinates.",
            "Addon presence/visibility/readiness and AtkValueCount109 are managed replay shell conditions. The original public ProbeError is retained, including the initial read budget failure.",
            "The source's short segment is replayed through PublicMonitorSession and the real river tracker. One explicitly artificial held observation follows recorded sample249 to confirm the new highlight lifecycle; it is not claimed as a captured client frame. No typed discard events or confirmed opening are injected; RoundId must remain unknown and HistoryGap must remain set.",
            "The late source frame is a separate read-only cold-start restoration check. No interpolated observations or invented actions bridge the omitted time.",
            "Runtime Chi/Pass permission is reconstructed from the current recorded enabled public menu; persisted recovery records intentionally remove LegalActions. Runtime addon state is an explicit test shell value, not a new native read.",
            "No game callback is sent. Native candidates and legal mapping establish offline integration only.",
        ],
    }
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(fixture, ensure_ascii=False, indent=2) + "\n", encoding="utf8")
    print(json.dumps({"Source": fixture["Source"], "Samples": required + [last], "Bytes": destination.stat().st_size}))


if __name__ == "__main__":
    main()
