"""Synthetic-only standard-library tests; no captured game data is stored here."""
import gzip
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import warnings
import zipfile


MODULE_PATH = Path(__file__).resolve().parents[1] / "scripts" / "analyze-cn-session.py"
SPEC = importlib.util.spec_from_file_location("analyze_cn_session", MODULE_PATH)
analyzer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(analyzer)


def envelope(sequence, kind, payload, schema=1):
    return {"SchemaVersion": schema, "Sequence": sequence, "Utc": "2026-09-23T00:00:00+00:00",
            "Kind": kind, "Payload": payload}


def encoded(record):
    return json.dumps(record, ensure_ascii=False, separators=(",", ":")).encode("utf-8") + b"\n"


def header():
    return envelope(1, "header", {
        "PluginVersion": "0.1.2.0", "UpstreamCommit": "a" * 40,
        "Identity": {"GameVersion": "2026.09.15.0000.0000", "Api": 15,
                     "DalamudVersion": "15.0.3.5", "DalamudCommit": "b" * 40,
                     "ClientStructsVersion": "1.0.0+" + "c" * 40,
                     "Language": "ChineseSimplified", "Runtime": "10.0.1", "Error": None},
        "LowerHandCaptureEnabled": True, "PublicLayoutCaptureEnabled": True,
        "FullGameStateVerified": False, "AutomationEnabled": False,
    })


def frame():
    return envelope(2, "frame", {
        "Sequence": 1, "Utc": "2026-09-23T00:00:00Z", "Marker": "SECRET_MARKER",
        "Recognition": "只输出统计，不输出这段原文",
        "Addons": [{"Name": "Emj", "Present": True, "Visible": True, "Ready": True,
                    "Error": "SYNTHETIC_ERROR: SECRET_ERROR_PAYLOAD", "VisibleNodes": [{"Path": "SECRET_NODE_PATH", "X": 98765}],
                    "LowerHandFaces": [{"Path": "SECRET_FACE_PATH", "X": 12345, "IconId": 76001,
                                        "FacePathHash": 0x12345678, "DiagnosticStatus": "RESOURCE_VERIFIED"}],
                    "LowerHandReading": {"Code": "LOWER_IMAGES_STABLE", "Stable": True,
                                         "Tiles": [{"ChineseName": "SECRET_TILE_SEQUENCE"}]},
                    "PublicLayouts": [{"Path": "SECRET_LAYOUT_PATH", "Area": "lower-meld-candidate",
                                       "Status": "PUBLIC_LAYOUT_METADATA", "NodeAndParents": [{}], "ShellPart": {}}]}],
    })


class SessionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.session = self.root / "session"
        self.session.mkdir()
        self.marker = {"SchemaVersion": 1, "Format": "mjcn-local-capture", "PluginVersion": "0.1.2.0",
                       "SessionId": "d" * 32, "StartedUtc": "2026-09-23T00:00:00Z",
                       "Compression": "gzip-record-members", "Limits": {}}
        (self.session / "session.json").write_text(json.dumps(self.marker), encoding="utf-8")
        self.catalog = self.root / "catalog.json"
        self.catalog.write_text(json.dumps({"SchemaVersion": 1, "GameVersion": "2026.09.15.0000.0000",
            "Resources": [{"IconId": 76001, "StandardCrc32": "12345678", "MahjongSemanticVisuallyConfirmed": True}]}), encoding="utf-8")

    def tearDown(self):
        self.temp.cleanup()

    def write_records(self, records, tail=b"", complete=False):
        lines = [encoded(r) for r in records]
        contents = b"".join(gzip.compress(line, mtime=0) for line in lines) + tail
        name = "segment-000001.jsonl.gz"
        (self.session / name).write_bytes(contents)
        if complete:
            manifest = {**self.marker, "State": "complete", "Fault": None,
                "RecordCount": len(records), "AcceptedRecordCount": len(records), "UnwrittenRecords": 0,
                "JsonlBytes": len(contents), "UncompressedJsonlBytes": sum(map(len, lines)),
                "Segments": [{"FileName": name, "Bytes": len(contents), "Records": len(records),
                              "Sha256": hashlib.sha256(contents).hexdigest().upper()}]}
            (self.session / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        return contents

    def analyze(self, path=None):
        return analyzer.analyze(path or self.session, self.catalog)

    def archive(self):
        path = self.root / "capture.zip"
        with zipfile.ZipFile(path, "w") as archive:
            for file in self.session.iterdir():
                archive.write(file, file.name)
        return path

    def test_complete_directory_and_zip_agree_and_summary_is_redacted(self):
        self.write_records([header(), frame()], complete=True)
        summary = self.analyze()
        self.assertTrue(summary["session"]["manifest_verified"])
        self.assertEqual(1, summary["counts"]["frames"])
        self.assertEqual({"exact_pair_known_semantics": 1}, summary["catalog_match_counts"])
        self.assertEqual({"SYNTHETIC_ERROR": 1}, summary["error_codes"])
        self.assertEqual(summary, self.analyze(self.archive()))
        rendered = json.dumps(summary)
        for secret in ("SECRET_", "98765", "12345", str(self.root), "ChineseName", "76001"):
            self.assertNotIn(secret, rendered)

    def test_truncated_last_gzip_member_does_not_commit_unverified_record(self):
        pending = gzip.compress(encoded(envelope(3, "status", {"Code": "CAPTURE_STOPPED"})), mtime=0)
        self.write_records([header(), frame()], tail=pending[:-5])
        summary = self.analyze()
        self.assertEqual(2, summary["counts"]["records"])
        self.assertEqual(len(pending) - 5, summary["session"]["ignored_uncommitted_tail_bytes"])
        self.assertNotIn("CAPTURE_STOPPED", summary["error_codes"])

    def test_at_start_modes_are_configuration_not_execution_verification(self):
        self.marker["PluginVersion"] = "0.2.0.0"
        (self.session / "session.json").write_text(json.dumps(self.marker), encoding="utf-8")
        for mode in ("Off", "Manual", "Automatic"):
            with self.subTest(mode=mode):
                record = header()
                payload = record["Payload"]
                payload.pop("AutomationEnabled")
                payload.update(PluginVersion="0.2.0.0", GameplayModeAtCaptureStart=mode,
                               AutomationEnabledAtCaptureStart=mode == "Automatic")
                self.write_records([record, frame()], complete=True)
                summary = self.analyze()
                self.assertTrue(summary["session"]["manifest_verified"])
                self.assertEqual(mode, summary["capture_scope"]["GameplayModeAtCaptureStart"])
                self.assertEqual(mode == "Automatic", summary["capture_scope"]["AutomationEnabledAtCaptureStart"])
                self.assertNotIn("AutomationEnabled", summary["capture_scope"])
                self.assertFalse(summary["capture_scope"]["FullGameStateVerified"])
                self.assertIn("do not prove actions occurred", summary["interpretation"])

    def test_legacy_automation_boolean_is_preserved_without_inventing_a_mode(self):
        for enabled in (False, True):
            record = header()
            record["Payload"]["AutomationEnabled"] = enabled
            self.write_records([record])
            scope = self.analyze()["capture_scope"]
            self.assertEqual(enabled, scope["AutomationEnabled"])
            self.assertNotIn("GameplayModeAtCaptureStart", scope)
            self.assertFalse(scope["FullGameStateVerified"])

    def test_conflicting_or_incomplete_mode_metadata_is_rejected(self):
        cases = [
            {"GameplayModeAtCaptureStart": "Automatic", "AutomationEnabledAtCaptureStart": False},
            {"GameplayModeAtCaptureStart": "Manual", "AutomationEnabledAtCaptureStart": True},
            {"GameplayModeAtCaptureStart": "Off", "AutomationEnabledAtCaptureStart": True},
            {"GameplayModeAtCaptureStart": "Automatic"},
            {"AutomationEnabledAtCaptureStart": True},
            {},
            {"AutomationEnabled": False, "GameplayModeAtCaptureStart": "Automatic", "AutomationEnabledAtCaptureStart": True},
            {"AutomationEnabled": True, "GameplayModeAtCaptureStart": "Manual", "AutomationEnabledAtCaptureStart": False},
        ]
        for fields in cases:
            with self.subTest(fields=fields):
                record = header()
                record["Payload"].pop("AutomationEnabled")
                record["Payload"].update(fields)
                self.write_records([record])
                with self.assertRaises(analyzer.SessionError):
                    self.analyze()

    def test_matching_legacy_and_at_start_fields_are_accepted(self):
        record = header()
        record["Payload"].update(AutomationEnabled=True, GameplayModeAtCaptureStart="Automatic",
                                 AutomationEnabledAtCaptureStart=True)
        self.write_records([record])
        scope = self.analyze()["capture_scope"]
        self.assertTrue(scope["AutomationEnabled"])
        self.assertTrue(scope["AutomationEnabledAtCaptureStart"])

    def test_automation_metadata_rejects_non_boolean_and_non_enum_values(self):
        for field, value in [("AutomationEnabled", v) for v in (0, 1, "false", None, [])] + [
                ("AutomationEnabledAtCaptureStart", v) for v in (0, 1, "true", None, {})] + [
                ("GameplayModeAtCaptureStart", v) for v in (0, True, None, [], "automatic", "SECRET_MODE")]:
            with self.subTest(field=field, value=value):
                record = header()
                record["Payload"].update(GameplayModeAtCaptureStart="Off", AutomationEnabledAtCaptureStart=False)
                record["Payload"][field] = value
                self.write_records([record])
                with self.assertRaises(analyzer.SessionError) as raised:
                    self.analyze()
                self.assertNotIn("SECRET_MODE", str(raised.exception))

    def test_automation_enabled_does_not_accept_a_full_state_verification_claim(self):
        record = header()
        record["Payload"].update(FullGameStateVerified=True, GameplayModeAtCaptureStart="Automatic",
                                 AutomationEnabledAtCaptureStart=True, AutomationEnabled=True)
        self.write_records([record])
        with self.assertRaisesRegex(analyzer.SessionError, "unsupported verified game-state"):
            self.analyze()

    def test_crc_damage_is_not_treated_as_recoverable_truncation(self):
        member = bytearray(gzip.compress(encoded(frame()), mtime=0))
        member[-8] ^= 0xFF
        self.write_records([header()], tail=bytes(member))
        with self.assertRaisesRegex(analyzer.SessionError, "CRC"):
            self.analyze()

    def test_truncated_nonfinal_segment_is_rejected(self):
        self.write_records([header()], tail=gzip.compress(encoded(frame()), mtime=0)[:-4])
        (self.session / "segment-000002.jsonl.gz").write_bytes(gzip.compress(encoded(frame()), mtime=0))
        with self.assertRaisesRegex(analyzer.SessionError, "non-final"):
            self.analyze()

    def test_unsupported_record_schema_and_kind_are_rejected(self):
        for bad in (envelope(2, "frame", {}, schema=99), envelope(2, "complete", {})):
            with self.subTest(bad=bad["Kind"]):
                self.write_records([header(), bad])
                with self.assertRaisesRegex(analyzer.SessionError, "Unsupported record"):
                    self.analyze()

    def test_normal_stop_code_is_not_counted_as_an_error(self):
        self.write_records([header(), envelope(2, "status", {"Code": "CAPTURE_STOPPED", "Reason": "SECRET_REASON"})])
        summary = self.analyze()
        self.assertEqual({}, summary["error_codes"])
        self.assertEqual({"CAPTURE_STOPPED": 1}, summary["recorder_status_codes"])
        self.assertNotIn("SECRET_REASON", json.dumps(summary))

    def test_schema_marker_mismatch_is_rejected(self):
        self.write_records([header()])
        self.marker["SchemaVersion"] = 2
        (self.session / "session.json").write_text(json.dumps(self.marker), encoding="utf-8")
        with self.assertRaisesRegex(analyzer.SessionError, "marker schema"):
            self.analyze()

    def test_sequence_gap_and_repeated_header_are_rejected(self):
        for bad in (envelope(3, "status", {}), envelope(2, "header", header()["Payload"])):
            self.write_records([header(), bad])
            with self.assertRaises(analyzer.SessionError):
                self.analyze()

    def test_coverage_and_timing_do_not_infer_game_events_or_export_text(self):
        first = frame()
        first["Payload"]["Addons"][0]["AtkValueCount"] = 50
        # Repeated metadata in one frame counts once toward frame coverage.
        first["Payload"]["Addons"][0]["PublicLayouts"] *= 2
        second = frame()
        second.update(Sequence=4, Utc="2026-09-23T00:00:02+00:00")
        second["Payload"].update(Sequence=3, Utc="2026-09-23T00:00:02+00:00")
        second["Payload"]["Addons"][0]["AtkValueCount"] = 73
        lifecycle = envelope(3, "lifecycle", {"Addon": "Emj", "Event": "PreFinalize"})
        unknown = envelope(5, "lifecycle", {"Addon": "Emj", "Event": "SECRET_EVENT"})
        unknown["Utc"] = "2026-09-23T00:00:03+00:00"
        self.write_records([header(), first, lifecycle, second, unknown])
        summary = self.analyze()
        self.assertEqual({"PreFinalize": 1, "OTHER": 1}, summary["lifecycle_event_counts"])
        self.assertEqual({"50": 1, "73": 1}, summary["emj_atk_value_count_histogram"])
        self.assertEqual({"lower-meld-candidate": 2}, summary["public_layout_frame_counts"])
        self.assertEqual({"lower-meld-candidate": 3}, summary["public_layout_area_counts"])
        self.assertEqual(3, summary["timing"]["session_wall_seconds"])
        self.assertEqual(2, summary["timing"]["frame_span_seconds"])
        self.assertEqual(1, summary["timing"]["frame_sequence_gap_events"])
        self.assertEqual(1, summary["timing"]["frame_intervals_over_one_second"])
        self.assertFalse(summary["capture_scope"]["FullGameStateVerified"])
        self.assertNotIn("SECRET_EVENT", json.dumps(summary))

    def test_clock_regression_is_reported_as_a_wall_clock_delta(self):
        first, second = frame(), frame()
        first["Payload"]["Utc"] = "2026-09-23T00:00:01+00:00"
        second["Sequence"] = 3
        second["Payload"]["Sequence"] = 2
        self.write_records([header(), first, second])
        timing = self.analyze()["timing"]
        self.assertEqual(1, timing["frame_clock_regressions"])
        self.assertIsNone(timing["minimum_frame_interval_seconds"])
        self.assertIsNone(timing["maximum_frame_interval_seconds"])
        self.assertEqual(-1, timing["frame_span_seconds"])

    def test_lower_row_geometry_is_aggregated_without_exporting_transforms_or_coordinates(self):
        record = frame()
        addon = record["Payload"]["Addons"][0]
        # New screen dimensions may be fractional; old integer dimensions remain valid.
        addon["LowerHandFaces"][0].update(Width=56.0, Height=72.8)
        addon["PublicLayouts"] = [
            {"Path": "SECRET_FACE_PATH", "Area": "lower-row-geometry",
             "Status": "PUBLIC_LAYOUT_METADATA_ONLY",
             "NodeAndParents": [{"NodeId": 4, "ScaleX": 1.4, "LocalX": 98231.25},
                                {"NodeId": 1, "ScaleX": 1.4, "OriginY": 91234.5}]},
            {"Path": "SECRET_SHELL_PATH", "Area": "lower-row-geometry",
             "Status": "LOWER_GEOMETRY_MATRIX_MISMATCH",
             "NodeAndParents": [{"NodeId": 5, "M11": 123.456, "LocalY": 98765.25}]},
        ]
        self.write_records([header(), record], complete=True)
        summary = self.analyze()
        self.assertEqual({"lower-row-geometry": 2}, summary["public_layout_area_counts"])
        self.assertEqual({"lower-row-geometry": 1}, summary["public_layout_frame_counts"])
        self.assertEqual({"LOWER_GEOMETRY_MATRIX_MISMATCH": 1, "PUBLIC_LAYOUT_METADATA_ONLY": 1},
                         summary["public_layout_statuses"])
        self.assertEqual(3, summary["counts"]["public_transform_samples"])
        self.assertEqual(summary, self.analyze(self.archive()))
        rendered = json.dumps(summary)
        for secret in ("SECRET_", "NodeAndParents", "NodeId", "ScaleX", "LocalX", "LocalY", "OriginY",
                       "98231.25", "91234.5", "98765.25", "123.456", "72.8"):
            self.assertNotIn(secret, rendered)

    def test_new_geometry_area_keeps_unknown_area_and_parent_depth_rejection(self):
        for area, count in (("unverified-new-area", 1), ("lower-row-geometry", 17)):
            with self.subTest(area=area, parent_count=count):
                record = frame()
                record["Payload"]["Addons"][0]["PublicLayouts"] = [
                    {"Path": "SECRET_PATH", "Area": area, "Status": "PUBLIC_LAYOUT_METADATA_ONLY",
                     "NodeAndParents": [{}] * count}]
                self.write_records([header(), record])
                with self.assertRaises(analyzer.SessionError):
                    self.analyze()

    def test_committed_malformed_json_and_duplicate_keys_are_rejected(self):
        for raw in (b'{"SchemaVersion": 1\n', b'{"SchemaVersion":1,"SchemaVersion":1}\n'):
            self.write_records([header()], tail=gzip.compress(raw, mtime=0))
            with self.assertRaises(analyzer.SessionError):
                self.analyze()

    def test_each_gzip_member_must_have_exactly_one_committed_line(self):
        for raw in (encoded(frame()).rstrip(b"\n"), encoded(frame()) + encoded(frame())):
            self.write_records([header()], tail=gzip.compress(raw, mtime=0))
            with self.assertRaisesRegex(analyzer.SessionError, "exactly one"):
                self.analyze()

    def test_manifest_hash_mismatch_is_rejected(self):
        self.write_records([header()], complete=True)
        file = self.session / "manifest.json"
        manifest = json.loads(file.read_text(encoding="utf-8"))
        manifest["Segments"][0]["Sha256"] = "0" * 64
        file.write_text(json.dumps(manifest), encoding="utf-8")
        with self.assertRaisesRegex(analyzer.SessionError, "hash differs"):
            self.analyze()

    def test_zip_duplicate_and_unsafe_names_are_rejected(self):
        self.write_records([header()])
        for entry in ("../escape.json", "session.json", "folder/segment-000001.jsonl.gz"):
            archive_path = self.archive()
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                with zipfile.ZipFile(archive_path, "a") as archive:
                    archive.writestr(entry, b"{}")
            with self.assertRaises(analyzer.SessionError):
                self.analyze(archive_path)

    def test_generated_archive_in_directory_is_not_double_counted(self):
        self.write_records([header()])
        (self.session / "capture.zip").write_bytes(b"ignored archive copy")
        (self.session / "capture.partial.zip").write_bytes(b"ignored unfinished archive")
        summary = self.analyze()
        self.assertEqual(1, summary["counts"]["records"])
        self.assertEqual(2, summary["session"]["ignored_archive_copies"])

    def test_record_and_total_expansion_limits_are_enforced(self):
        self.write_records([header(), frame()])
        with mock.patch.object(analyzer, "MAX_LINE_BYTES", 512):
            with self.assertRaisesRegex(analyzer.SessionError, "4 MiB"):
                self.analyze()
        with mock.patch.object(analyzer, "MAX_EXPANDED_BYTES", 900):
            with self.assertRaisesRegex(analyzer.SessionError, "2 GiB"):
                self.analyze()
        with mock.patch.object(analyzer, "MAX_TOTAL_BYTES", 128):
            with self.assertRaises(analyzer.SessionError):
                self.analyze()

    def test_frame_limit_is_independent_of_compression_ratio(self):
        self.write_records([header(), frame()])
        with mock.patch.object(analyzer, "MAX_FRAMES", 0):
            with self.assertRaisesRegex(analyzer.SessionError, "7200"):
                self.analyze()

    def test_output_cannot_overwrite_or_be_written_inside_source(self):
        self.write_records([header()])
        summary = self.analyze()
        for destination in (self.session, self.session / "new-summary.json", self.catalog):
            with self.assertRaises(analyzer.SessionError):
                analyzer.write_summary(destination, summary, self.session)
        output = self.root / "summary.json"
        analyzer.write_summary(output, summary, self.session)
        self.assertEqual(summary, json.loads(output.read_text(encoding="utf-8")))
        with self.assertRaises(analyzer.SessionError):
            analyzer.write_summary(output, summary, self.session)


if __name__ == "__main__":
    unittest.main()
