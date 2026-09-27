"""Synthetic GameJournal fixtures only. No actual game log, hand or private path is checked in."""
import contextlib
import hashlib
import importlib.util
import io
import json
import stat
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import warnings
import zipfile
import gzip
import base64


SPEC = importlib.util.spec_from_file_location("analyze_game_logs", Path(__file__).resolve().parents[1] / "scripts/analyze-game-logs.py")
analyzer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(analyzer)
SESSION = "11111111-1111-1111-1111-111111111111"


def body(sequence, kind, data=None, previous="0" * 64, session=SESSION, utc="2026-09-24T02:00:00+00:00"):
    return {"Schema": 1, "SessionId": session, "Sequence": sequence, "Utc": utc, "Kind": kind,
            "Data": data or {}, "PreviousSha256": previous}


def line(entry, *, exact=None, hash_first=False):
    raw = exact if exact is not None else json.dumps(entry, ensure_ascii=True, separators=(",", ":"))
    digest = hashlib.sha256(raw.encode("utf-8")).hexdigest().upper()
    outer = '{"Sha256":"' + digest + '","Entry": ' + raw + '}' if hash_first else '{"Entry":' + raw + ',"Sha256":"' + digest + '"}'
    return outer.encode("utf-8") + b"\n", digest


class GameLogTests(unittest.TestCase):
    def test_compressed_ai_trace_is_decoded_and_closed_record_zip_is_accepted(self):
        original = {"Phase": "request", "InputSha256": "synthetic", "PublicTiles": [1, 2, 3]}
        data = {"Codec": "json-delta-gzip-v1", "BaseSequence": 0,
                "Payload": base64.b64encode(gzip.compress(json.dumps({"Full": original}).encode())).decode()}
        entry = body(1, "global_ai_request", data); entry["Schema"] = 2
        self.events.write_bytes(line(entry)[0])
        self.assertTrue(self.analyze()["integrity_passed"])
        analyzer.decode_payload(entry, {})
        self.assertEqual(original, entry["Data"])
        (self.session / "record-closed.json").write_text("{}")
        archive = self.root / "closed.zip"
        with zipfile.ZipFile(archive, "w") as output:
            for path in self.session.iterdir():
                output.write(path, path.name)
        self.assertTrue(analyzer.analyze(archive)["integrity_passed"])

    def test_large_compressed_observation_roundtrips_above_disk_row_limit(self):
        original = {"SyntheticStorageStress": "x" * (analyzer.MAX_LINE_BYTES + 4096)}
        data = {"Codec": "json-delta-gzip-v1", "BaseSequence": 0,
                "Payload": base64.b64encode(gzip.compress(json.dumps({"Full": original}).encode())).decode()}
        entry = body(1, "public_event", data); entry["Schema"] = 2
        self.events.write_bytes(line(entry)[0])
        self.assertTrue(self.analyze()["integrity_passed"])
        analyzer.decode_payload(entry, {})
        self.assertEqual(original, entry["Data"])

    def test_schema2_delta_crosses_volumes_without_losing_counts(self):
        def packed(value, basis):
            return {"Codec": "json-delta-gzip-v1", "BaseSequence": basis,
                    "Payload": base64.b64encode(gzip.compress(json.dumps(value).encode())).decode()}
        first = body(1, "public_event", packed({"Full": {"Kind": "SnapshotObserved", "Snapshot": {"Hand": [1, 2]}}}, 0))
        first["Schema"] = 2
        a, digest = line(first)
        second = body(2, "public_event", packed({"Changes": [{"Path": ["Snapshot", "Hand", 1], "Value": 3}]}, 1), digest)
        second["Schema"] = 2
        b, _ = line(second)
        self.events.write_bytes(a)
        (self.session / "events.0001.jsonl").write_bytes(b)
        (self.session / "journal-index.json").write_text(json.dumps({"Schema": 2, "Files": {"events.jsonl": len(a), "events.0001.jsonl": len(b)}}))
        result = self.analyze()
        self.assertTrue(result["integrity_passed"])
        self.assertEqual(2, result["sessions"][0]["streams"]["events.jsonl"]["records"])
        archive = self.root / "rotated.zip"
        with zipfile.ZipFile(archive, "w") as zip:
            for p in self.session.iterdir():
                zip.write(p, p.name)
            zip.writestr("diagnostic-ring.jsonl", "")
            zip.writestr("export-privacy.json", '{"SchemaVersion":1,"DerivedSanitizedChain":true,"OriginalFilesChanged":false}')
        self.assertTrue(analyzer.analyze(archive)["integrity_passed"])
        (self.session / "events.0001.jsonl").unlink()
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_SEGMENT_MISSING"):
            self.analyze()

    def test_schema2_missing_base_and_decompression_limit_are_rejected(self):
        for patch, basis, expected in [({"Changes": []}, 7, "JOURNAL_BASE_INVALID"),
                                        ({"Full": {"Text": "x" * (analyzer.MAX_LINE_BYTES * 5)}}, 0, "JOURNAL_DECODE_LIMIT")]:
            data = {"Codec": "json-delta-gzip-v1", "BaseSequence": basis,
                    "Payload": base64.b64encode(gzip.compress(json.dumps(patch).encode())).decode()}
            entry = body(1, "public_event", data); entry["Schema"] = 2
            self.events.write_bytes(line(entry)[0])
            with self.assertRaisesRegex(analyzer.JournalError, expected):
                self.analyze()

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="mjcn-journal-python-test-")
        self.root = Path(self.temp.name)
        self.session = self.root / "session"
        self.session.mkdir()
        self.events = self.session / "events.jsonl"
        self.errors = self.session / "errors.jsonl"
        self.events.write_bytes(b"")
        self.errors.write_bytes(b"")

    def tearDown(self):
        self.temp.cleanup()

    def analyze(self):
        return analyzer.analyze(self.session)

    def seed(self):
        start, digest = line(body(1, "session_started", {
            "PluginVersion": "0.5.0.0", "Identity": {"GameVersion": "2026.09.15.0000.0000", "Api": 15,
            "DalamudVersion": "15.0.3.5", "ClientStructsVersion": "1.0.0+" + "a" * 40, "Runtime": "10.0.1"},
            "Reason": "SECRET_ACCOUNT SECRET_CHAT SECRET_PATH", "FullHistoryVerified": False}))
        gap, digest = line(body(2, "history_gap", {"Reason": "SECRET_FULL_HAND"}, digest))
        recovery, digest = line(body(3, "recovery_applied", {"Code": "RECOVERY_EXPERIMENTAL_MATCH", "SourceLog": "SECRET_LOG_PATH",
            "AutomaticEnabled": False, "Evidence": {"Tiles": [31, 31, 31]}}, digest))
        self.events.write_bytes(start + gap + recovery)
        error, _ = line(body(1, "PUBLIC_FACE_REJECTED", {"Reason": "SECRET_TEXT", "Code": "PUBLIC_SHELL_NOT_FRONT"}))
        self.errors.write_bytes(error)

    def test_valid_two_streams_redact_payloads_and_count_version_gap_recovery_and_codes(self):
        self.seed()
        summary = self.analyze()
        self.assertTrue(summary["integrity_passed"])
        self.assertFalse(summary["partial"])
        self.assertFalse(summary["history_complete"])
        self.assertEqual(["0.5.0.0"], summary["versions"]["PluginVersion"])
        self.assertEqual(1, summary["history_gap_records"])
        self.assertEqual(1, summary["recovery_counts"]["recovery_applied"])
        self.assertEqual(1, summary["error_codes"]["PUBLIC_SHELL_NOT_FRONT"])
        text = json.dumps(summary)
        self.assertNotIn("SECRET", text)
        self.assertNotIn("Tiles", text)
        self.assertNotIn(str(self.root), text)

    def test_hash_uses_exact_original_entry_escapes_whitespace_and_root_order(self):
        entry = body(1, "session_started", {"Reason": "中文<&>\\路径", "PluginVersion": "0.5.0.0"})
        exact = json.dumps(entry, ensure_ascii=True, indent=2).replace("\\u4e2d", "\\u4E2D")
        # JSONL itself must be one physical line. Space and escaped Unicode spellings remain significant.
        exact = exact.replace("\n", " ")
        record, digest = line(entry, exact=exact, hash_first=True)
        self.events.write_bytes(record)
        summary = self.analyze()
        self.assertEqual(digest, summary["sessions"][0]["streams"]["events.jsonl"]["last_verified_sha256"])
        self.assertNotEqual(digest, hashlib.sha256(json.dumps(entry).encode()).hexdigest().upper())

    def test_literal_unicode_is_hashed_as_original_utf8(self):
        entry = body(1, "history_gap", {"Reason": "合成中文，并非实录"})
        record, _ = line(entry, exact=json.dumps(entry, ensure_ascii=False))
        self.events.write_bytes(record)
        self.assertEqual(1, self.analyze()["history_gap_records"])

    def test_truncated_last_row_recovers_only_verified_prefix_and_marks_partial(self):
        good, _ = line(body(1, "play_paused"))
        self.events.write_bytes(good + b'{"Entry":{"Schema":1,"Dat')
        result = self.analyze()
        self.assertEqual("Partial", result["status"])
        self.assertTrue(result["partial"])
        self.assertEqual(1, result["sessions"][0]["streams"]["events.jsonl"]["records"])

    def test_complete_json_without_newline_is_uncommitted_not_interpreted(self):
        raw, _ = line(body(1, "history_gap"))
        self.events.write_bytes(raw[:-1])
        result = self.analyze()
        self.assertTrue(result["partial"])
        self.assertEqual(0, result["history_gap_records"])

    def test_bad_complete_row_is_not_tail_recovery(self):
        good, _ = line(body(1, "play_paused"))
        self.events.write_bytes(good + b'{"broken":\n')
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_JSON_INVALID"):
            self.analyze()

    def test_edited_entry_fails_hash_even_when_valid_json(self):
        raw, _ = line(body(1, "play_paused", {"value": 13}))
        self.events.write_bytes(raw.replace(b'"value":13', b'"value":14'))
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_HASH_MISMATCH"):
            self.analyze()

    def test_reordered_rows_and_wrong_previous_hash_fail(self):
        first, digest = line(body(1, "play_paused"))
        second, _ = line(body(2, "mode_selected", previous=digest))
        self.events.write_bytes(second + first)
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_CHAIN_INVALID"):
            self.analyze()
        second, _ = line(body(2, "mode_selected", previous="F" * 64))
        self.events.write_bytes(first + second)
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_CHAIN_INVALID"):
            self.analyze()

    def test_duplicate_properties_in_data_or_root_are_rejected(self):
        for suffix in ('"Data":{}', '"Data":{"x":1,"x":2}'):
            raw, _ = line(body(1, "play_paused"))
            raw = raw.replace(b'"Data":{}', suffix.encode())
            if suffix == '"Data":{}':
                raw = raw.replace(b'"Entry":', b'"Entry":{},"Entry":', 1)
            self.events.write_bytes(raw)
            with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_DUPLICATE_PROPERTY"):
                self.analyze()

    def test_schema_types_unknown_envelope_keys_and_nonobject_data_are_rejected(self):
        for name, value in (("Schema", True), ("Schema", 3), ("Sequence", True), ("Data", []), ("Kind", "invalid/name")):
            data = body(1, "play_paused")
            data[name] = value
            self.events.write_bytes(line(data)[0])
            with self.assertRaises(analyzer.JournalError):
                self.analyze()
        raw, _ = line(body(1, "play_paused"))
        self.events.write_bytes(raw[:-2] + b',"Extra":1}\n')
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_SCHEMA_INVALID"):
            self.analyze()

    def test_cross_stream_session_mismatch_is_rejected(self):
        self.events.write_bytes(line(body(1, "play_paused"))[0])
        self.errors.write_bytes(line(body(1, "PUBLIC_FACE_REJECTED", session="22222222-2222-2222-2222-222222222222"))[0])
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_CROSS_STREAM_SESSION_MISMATCH"):
            self.analyze()

    def test_structured_public_event_gap_supports_string_and_integer_enum(self):
        a, h = line(body(1, "public_event", {"Kind": "Gap", "Provenance": "Gap"}))
        b, _ = line(body(2, "public_event", {"Kind": 20, "Provenance": 2}, h))
        self.events.write_bytes(a + b)
        summary = self.analyze()
        self.assertEqual(2, summary["history_gap_records"])
        self.assertEqual({"Gap": 2}, summary["public_event_kind_counts"])

    def test_unknown_kinds_and_unstructured_errors_never_echo_arbitrary_text(self):
        self.events.write_bytes(line(body(1, "SECRET_ACCOUNT"))[0])
        self.errors.write_bytes(line(body(1, "SECRET_ACCOUNT", {"Code": "SECRET_CODE"}))[0])
        output = json.dumps(self.analyze())
        self.assertNotIn("SECRET", output)
        self.assertIn("UNCLASSIFIED_CODE", output)

    def test_invalid_utf8_depth_and_line_limits_are_rejected(self):
        self.events.write_bytes(b'\xff\n')
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_JSON_INVALID"):
            self.analyze()
        self.events.write_bytes(b'{' * 49 + b'\n')
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_DEPTH_LIMIT"):
            self.analyze()
        self.events.write_bytes(b'x' * (analyzer.MAX_LINE_BYTES + 1))
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_LINE_LIMIT"):
            self.analyze()

    def test_zip_matches_directory_without_extracting(self):
        self.seed()
        archive = self.root / "logs.zip"
        with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED) as z:
            z.write(self.events, "events.jsonl")
            z.write(self.errors, "errors.jsonl")
        self.assertEqual(self.analyze(), analyzer.analyze(archive))
        self.assertEqual({"session", "logs.zip"}, {p.name for p in self.root.iterdir()})

    def test_zip_traversal_duplicate_and_unexpected_members_rejected(self):
        for names in (("../events.jsonl", "errors.jsonl"), ("events.jsonl", "events.jsonl"), ("events.jsonl", "notes.txt")):
            archive = self.root / "bad.zip"
            with warnings.catch_warnings():
                warnings.simplefilter("ignore", UserWarning)
                with zipfile.ZipFile(archive, "w") as z:
                    for name in names:
                        z.writestr(name, b"")
            with self.assertRaises(analyzer.JournalError):
                analyzer.analyze(archive)

    def test_file_limit_and_missing_stream_fail_before_parsing(self):
        self.seed()
        with mock.patch.object(analyzer, "MAX_FILE_BYTES", 8):
            with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_FILE_LIMIT"):
                self.analyze()
        self.errors.unlink()
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_STREAM_MISSING"):
            self.analyze()

    def test_zip_symlink_and_directory_entry_are_not_regular_journal_files(self):
        for kind in (stat.S_IFLNK, stat.S_IFDIR):
            archive = self.root / "link.zip"
            with zipfile.ZipFile(archive, "w") as z:
                entry = zipfile.ZipInfo("events.jsonl")
                entry.create_system = 3
                entry.external_attr = (kind | 0o600) << 16
                z.writestr(entry, b"")
                z.writestr("errors.jsonl", b"")
            with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_ZIP_MEMBER_TYPE"):
                analyzer.analyze(archive)

    def test_logs_root_only_visits_own_session_names(self):
        self.seed()
        own = self.root / ("mjcn-game-20260924-000000-" + "a" * 32)
        self.session.rename(own)
        (self.root / "unrelated-private.txt").write_text("SECRET_UNRELATED", encoding="utf-8")
        summary = analyzer.analyze(self.root)
        self.assertEqual(1, summary["session_count"])
        self.assertNotIn("SECRET", json.dumps(summary))

    def test_summary_cannot_overwrite_input_existing_file_or_be_inside_source(self):
        self.seed()
        summary = self.analyze()
        for output in (self.events, self.session / "summary.json"):
            with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_OUTPUT_SCOPE"):
                analyzer.write_summary(output, summary, self.session)
        other = self.root / "other"
        other.mkdir()
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_OUTPUT_SCOPE"):
            analyzer.write_summary(other / ".." / "session" / "summary.json", summary, self.session)
        target = self.root / "summary.json"
        analyzer.write_summary(target, summary, self.session)
        original = target.read_bytes()
        with self.assertRaisesRegex(analyzer.JournalError, "JOURNAL_OUTPUT_EXISTS_OR_TYPE"):
            analyzer.write_summary(target, summary, self.session)
        self.assertEqual(original, target.read_bytes())

    def test_cli_partial_returns_two_and_writes_explicit_partial_summary(self):
        self.events.write_bytes(line(body(1, "play_paused"))[0] + b'{"Entry":')
        output = self.root / "summary.json"
        with mock.patch("sys.argv", ["analyze-game-logs.py", str(self.session), "--output", str(output)]), contextlib.redirect_stdout(io.StringIO()) as stdout:
            self.assertEqual(2, analyzer.main())
        self.assertTrue(json.loads(output.read_text(encoding="utf-8"))["partial"])
        self.assertNotIn(str(self.root), stdout.getvalue())

    def test_clock_regression_is_visible_in_summary_without_inventing_event_order(self):
        first, h = line(body(1, "play_paused", utc="2026-09-24T02:00:01Z"))
        second, _ = line(body(2, "mode_selected", previous=h, utc="2026-09-24T02:00:00Z"))
        self.events.write_bytes(first + second)
        self.assertEqual(1, self.analyze()["clock_regressions"])


if __name__ == "__main__":
    unittest.main()
