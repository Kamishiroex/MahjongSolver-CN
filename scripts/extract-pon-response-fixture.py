"""Extract only public Mahjong evidence for the 2026-09-24 stuck-pon regression.

Usage: python scripts/extract-pon-response-fixture.py <events.jsonl> <new-fixture.json>
The source stays private; the output contains no account, character or chat fields.
"""
import hashlib
import json
import pathlib
import sys


def main():
    source, destination = map(pathlib.Path, sys.argv[1:])
    if destination.exists():
        raise FileExistsError(destination)
    source_sha = hashlib.sha256(source.read_bytes()).hexdigest()
    snapshots, tables, rivers = {}, {}, []
    runtime = None
    first_session = None
    for line_number, line in enumerate(source.open(encoding="utf-8-sig"), 1):
        envelope = json.loads(line)
        row = envelope.get("Entry", envelope)
        first_session = first_session or row["SessionId"]
        kind, data = row["Kind"], row["Data"]
        if kind == "public_event" and data.get("Snapshot") is not None:
            sample = data["Snapshot"]["Observation"]["Sequence"]
            snapshots[sample] = (line_number, row)
        elif kind == "public_table_observation":
            tables[data["Sequence"]] = (line_number, row)
        elif kind == "public_river_event":
            rivers.append((line_number, row))
        elif kind == "runtime_observation":
            runtime = (line_number, row)
    trigger = next((line, row) for line, row in rivers
                   if row["Data"]["Change"]["Sample"] == 117
                   and row["Data"]["Change"]["ScreenDirection"] == "top"
                   and row["Data"]["Change"]["Tile"]["Kind34"] == 23)
    samples = [115, 116, 117, max(snapshots.keys() & tables.keys())]
    references = []

    def intern(value):
        if isinstance(value, list):
            return [intern(item) for item in value]
        if not isinstance(value, dict):
            return value
        result = {}
        for key, item in value.items():
            if key == "Observation" and isinstance(item, dict):
                if item not in references:
                    references.append(item)
                result[key] = {"$observation": references.index(item)}
            else:
                result[key] = intern(item)
        return result

    def provenance(line, row):
        return {"SourceLine": line, "JournalSequence": row["Sequence"], "Utc": row["Utc"]}

    frames = []
    snapshot_keys = "SchemaVersion SessionId StateRevision Observation Stability Synchronization SynchronizationReason RoundId DealerPlayerId RoundWind HandNumber Honba RiichiSticks WallRemaining LowerVisibleFaces DoraMode DoraDisplay".split()
    player_keys = "Position PlayerId SeatWind Score RiichiDeclared RiichiEstablished Ippatsu RiverImages MeldImages".split()
    for sample in samples:
        public_line, public = snapshots[sample]
        table_line, table = tables[sample]
        observed = public["Data"]["Snapshot"]
        compact = {key: observed[key] for key in snapshot_keys}
        compact["Players"] = [{key: player[key] for key in player_keys} for player in observed["Players"]]
        frames.append({
            "Sample": sample, "Role": "TrackerWarmup" if sample < 117 else "ResponseProjection",
            "PublicRecord": provenance(public_line, public),
            "TableRecord": provenance(table_line, table), "Snapshot": intern(compact),
            "Table": table["Data"]["Table"], "ActionMenu": table["Data"]["ActionMenu"],
            "Riichi": table["Data"].get("Riichi", []),
        })
    changes = [{"Source": provenance(line, row), "RoundId": row["Data"]["RoundId"], "Change": row["Data"]["Change"]}
               for line, row in rivers if row["Data"]["Change"]["Sample"] <= samples[-1]]
    fixture = {
        "Schema": 1, "Purpose": "Actual recorded public pon window; offline projector/native/mapper regression, not a live dispatch claim.",
        "Source": {"LogFile": "events.jsonl", "LogSha256": source_sha,
                   "LogBytes": source.stat().st_size, "SessionId": first_session,
                   "Trigger": provenance(*trigger)},
        "ObservationReferences": references, "Frames": frames, "RecordedRiverEvents": changes,
        "RecordedRuntime": {"Source": provenance(*runtime),
                            "AddonStateCode": runtime[1]["Data"]["Snapshot"]["AddonStateCode"],
                            "LegalActionsWereIntentionallyRemoved": True},
        "ReconstructionConditions": [
            "PublicSnapshot fields, their mapping status, menu rows and table readings are copied from the indicated source records.",
            "Observation references are deduplicated only; the replay restores their original content.",
            "Recorded samples 115 and 116 warm only the public table response-highlight tracker. They are distinct original observations, do not enter the projector, and do not create discard events.",
            "The replay creates a managed AddonProbe shell and presence-only public slot records from the logged decoded table. No native pointers or hidden faces are synthesized.",
            "Pinned canonical tile resources are reconstructed from logged Kind34/Red identities only for a stability comparison. The second frame's old brightness-sensitive stability bits are replaced by the new tracker result; these recomputed bits are not original live observations.",
            "Runtime action flags and hand are rebuilt from the recorded enabled Pon/Pass menu and exact visible hand; the persisted recovery snapshot intentionally removes LegalActions.",
            "AddonStateCode is copied from the recorded public recovery snapshot; AtkValueCount 109 is an explicit managed replay condition. No other hidden runtime fields are restored.",
            "A legacy candidate for tile 15/fromSeat 1 is an explicit regression perturbation, not a claim that the source runtime supplied that exact candidate.",
            "River history slots are reconstructed from the recorded DiscardObserved events. History remains incomplete; later timestamps do not invent new game actions.",
            "No callback is sent. An accepted mapped Pon or Pass proves only offline integration on this recorded public input.",
        ],
    }
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(fixture, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"SourceSha256": source_sha, "Samples": samples,
                      "RiverEvents": len(changes), "FixtureBytes": destination.stat().st_size}))


if __name__ == "__main__":
    main()
