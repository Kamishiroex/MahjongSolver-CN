using System.Reflection;
using System.Runtime.CompilerServices;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Journaling;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Managed synthetic images; does not assert live client decoding or AI quality.</summary>
public sealed class JournalObservationDeduplicationTests
{
    [Fact]
    public async Task Pulse_only_polling_keeps_current_reader_fresh_with_small_heartbeat_and_logs_real_changes()
    {
        string directory = Path.Combine(Path.GetTempPath(), "mjcn-journal-pulse-" + Guid.NewGuid().ToString("N"));
        var journal = new GameJournal(directory);
        try
        {
            var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            void Set(string name, object value) => typeof(Plugin).GetField(name, flags)!.SetValue(plugin, value);
            Set("journal", journal);
            Set("journalActive", true);
            Set("journalLowerTracker", new LowerHandTracker());
            Set("journalTableTracker", new PublicTableTracker());
            Set("journalRiverBaseline", new Dictionary<string, PublicTableTile[]>(StringComparer.Ordinal));
            var monitor = new PublicMonitorSession();
            monitor.Start();
            Set("journalPublicMonitor", monitor);
            var record = typeof(Plugin).GetMethod("RecordPublicTable", flags)!;
            DateTimeOffset start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
            void Sample(int sequence, int kind, short pulse)
            {
                uint icon = 76001u + (uint)kind;
                var face = new PublicTableFaceCandidate("river-bottom", "bottom", "Emj/118/4", "Emj/118", 1021,
                    100, 200, 40, 56, 0, false, icon,
                    LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex"),
                    PublicTableImageReader.VerifiedResourceCode,
                    VisualMark: new(pulse, pulse, pulse, 100, 100, 100, pulse == 0 ? "normal" : "unclassified"));
                var addon = new AddonProbe("Emj", true, true, true, 109, [], null, PublicTableFaces: [face],
                    PublicActionMenu: new("ACTION_MENU_VISIBLE_CANDIDATE", true, true,
                        [new("Emj/104/3/2", 100, "Chi", true, "ACTION_MENU_LABEL_CANDIDATE", 0),
                         new("Emj/104/3/21001", 140, "Pass", true, "ACTION_MENU_LABEL_CANDIDATE", 1)],
                        new(2, 2, 0, 5, false, false, true)));
                record.Invoke(plugin, [new DiagnosticFrame(sequence, start.AddMilliseconds(sequence * 100),
                    "synthetic pulse", "synthetic", [addon])]);
            }
            for (int sequence = 1; sequence <= 600; sequence++)
                Sample(sequence, 0, (short)((sequence % 4) switch { 0 => 0, 1 => 0, 2 => 23, _ => 45 }));
            // Dedup applies only to persistence. Every observation and AI projector update
            // still runs, so frame freshness and missing/changed data are never concealed.
            Assert.Equal(600, plugin.CurrentJournalPublicSnapshot!.Observation!.Sequence);
            Assert.Equal(0, Assert.Single(plugin.CurrentPublicTable!.Tiles).Kind34);
            Sample(601, 1, 2);
            Assert.Equal(601, plugin.CurrentJournalPublicSnapshot!.Observation!.Sequence);
            Assert.Equal(1, Assert.Single(plugin.CurrentPublicTable!.Tiles).Kind34);
            await journal.CompleteAsync();
            Assert.Null(journal.Fault);
            string path = Path.Combine(journal.DirectoryPath, "events.jsonl");
            var replay = await GameJournal.ReadAsync(path);
            Assert.True(replay.IntegrityPassed);
            var tables = replay.Lines.Where(x => x.Entry.Kind == "public_table_changed").ToArray();
            Assert.Equal(4, tables.Length); // initial, highlight confirmed, stable, real tile change
            Assert.Equal(new long[] { 1, 2, 3, 601 }, tables.Select(x => x.Entry.Data.GetProperty("Sequence").GetInt64()));
            Assert.Equal(601, tables[^1].Entry.Data.GetProperty("Sequence").GetInt64());
            Assert.Equal("bounded-export-ring", tables[^1].Entry.Data.GetProperty("DiagnosticDetail").GetString());
            string archive = Path.Combine(directory, "export.zip");
            await journal.ExportAsync(archive);
            using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
            using (var detail = new StreamReader(zip.GetEntry("diagnostic-ring.jsonl")!.Open()))
            {
                string raw = await detail.ReadToEndAsync();
                Assert.Equal(4, raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
                Assert.Contains("SnapshotHashKind", raw);
            }
            Assert.Single(replay.Lines.Where(x => x.Entry.Kind == "public_table_heartbeat"));
            var snapshots = replay.Lines.Where(x => x.Entry.Kind == "public_event" &&
                x.Entry.Data.GetProperty("Snapshot").ValueKind != System.Text.Json.JsonValueKind.Null)
                .Select(x => x.Entry.Data.GetProperty("Snapshot")).ToArray();
            Assert.Equal(new long[] { 1, 2, 3, 601 }, snapshots.Select(x => x.GetProperty("Observation").GetProperty("Sequence").GetInt64()));
            Assert.All(snapshots, x => Assert.Equal(1, x.GetProperty("Players")[0].GetProperty("RiverImages").GetProperty("Availability").GetInt32()));
            Assert.True(new FileInfo(path).Length < 150_000, "One minute of identical public state must remain bounded.");
        }
        finally
        {
            await journal.CompleteAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
