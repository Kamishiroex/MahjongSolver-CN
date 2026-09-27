using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class JournalSegmentsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mjcn-segment-test-" + Guid.NewGuid().ToString("N"));
    public JournalSegmentsTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task Rotation_preserves_all_actions_sequence_chain_and_deltas_without_fault()
    {
        var journal = new GameJournal(root, 2048);
        var expected = new List<JsonElement>();
        for (int i = 0; i < 70; i++)
        {
            var data = JsonSerializer.SerializeToElement(new { Sample = i, Hand = new[] { 4, 4, 22 },
                Red = i % 2 == 0, Text = new string('牌', 800), State = i % 3 == 0 ? "missing" : "known" });
            expected.Add(data);
            Assert.True(journal.Event("public_event", data));
            if (i % 10 == 0) await journal.Completion;
        }
        await journal.CompleteAsync(); Assert.Null(journal.Fault);
        Assert.True(GameJournal.StreamFiles(journal.DirectoryPath, "events.jsonl").Length > 5);
        var replay = await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "events.jsonl"));
        Assert.True(replay.IntegrityPassed, replay.Error); Assert.Equal(70, replay.Lines.Length);
        for (int i = 0; i < 70; i++)
        {
            Assert.Equal(i + 1, replay.Lines[i].Entry.Sequence);
            Assert.True(JsonElement.DeepEquals(expected[i], replay.Lines[i].Entry.Data));
        }
        var first = File.ReadAllText(Path.Combine(journal.DirectoryPath, "events.jsonl"));
        Assert.Contains("json-delta-gzip-v1", first);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Missing_middle_or_final_volume_is_not_reported_as_complete(bool last)
    {
        var journal = new GameJournal(root, 1024);
        for (int i = 0; i < 8; i++) { journal.Event("discard", new { Index = i, Text = new string('x', 300) }); await journal.Completion; }
        await journal.CompleteAsync();
        string[] parts = GameJournal.StreamFiles(journal.DirectoryPath, "events.jsonl");
        File.Delete(parts[last ? ^1 : 1]);
        Assert.Equal("JOURNAL_SEGMENT_MISSING", (await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "events.jsonl"))).Error);
    }

    [Fact]
    public async Task Export_is_a_consistent_prefix_while_writer_continues_and_ring_is_bounded()
    {
        var journal = new GameJournal(root, 1024);
        for (int i = 0; i < 200; i++) journal.Diagnostic("table", new { Index = i, Text = new string('x', 20000) });
        for (int i = 0; i < 12; i++) { journal.Event("discard", new { Index = i }); await journal.Completion; }
        Task<string> export = journal.ExportAsync(Path.Combine(root, "logs.zip"));
        journal.Event("discard", new { Index = 12 });
        string exported = await export; await journal.CompleteAsync(); Assert.Null(journal.Fault);
        string unpacked = Path.Combine(root, "unpacked"); ZipFile.ExtractToDirectory(exported, unpacked);
        var replay = await GameJournal.ReadAsync(Path.Combine(unpacked, "events.jsonl"));
        Assert.True(replay.IntegrityPassed, replay.Error); Assert.Equal(12, replay.Lines.Length);
        string[] ring = File.ReadAllLines(Path.Combine(unpacked, "diagnostic-ring.jsonl"));
        Assert.InRange(ring.Length, 1, 128); Assert.True(new FileInfo(Path.Combine(unpacked, "diagnostic-ring.jsonl")).Length < 2 * 1024 * 1024 + 128);
        using var last = JsonDocument.Parse(ring[^1]); Assert.Equal(199, last.RootElement.GetProperty("Data").GetProperty("Index").GetInt32());
        Assert.Equal(13, (await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "events.jsonl"))).Lines.Length);
    }

    [Fact]
    public async Task Error_burst_is_summarized_and_stop_survives_journal_fault()
    {
        var journal = new GameJournal(root);
        for (int i = 0; i < 500; i++) journal.Error("PUBLIC_FACE_REJECTED", new { Sequence = i, Slot = "river/1", Code = "TRANSITION" });
        journal.ErrorsRecovered();
        Assert.False(journal.Event("oversize", new { Text = new string('x', GameJournal.MaximumLineBytes) }));
        await journal.SaveStopAsync(new { Reason = "JOURNAL_ENTRY_TOO_LARGE", AutomaticEnabled = false });
        await journal.CompleteAsync();
        using var stop = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "last-stop.json")));
        Assert.Equal("JOURNAL_ENTRY_TOO_LARGE", stop.RootElement.GetProperty("Stop").GetProperty("Reason").GetString());
    }

    [Fact]
    public async Task Error_summary_preserves_count_and_recovery_marker()
    {
        var journal = new GameJournal(root);
        for (int i = 0; i < 500; i++) journal.Error("PUBLIC_FACE_REJECTED", new { Sequence = i, Slot = "river/1" });
        journal.ErrorsRecovered(); await journal.CompleteAsync();
        var replay = await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "errors.jsonl"));
        Assert.True(replay.IntegrityPassed); Assert.Equal(3, replay.Lines.Length);
        Assert.Equal(499, replay.Lines[1].Entry.Data.GetProperty("RepeatedCount").GetInt32());
        Assert.Equal("READ_RECOVERED", replay.Lines[2].Entry.Kind);
    }

    [Fact]
    public async Task Atomic_replacement_tolerates_a_temporary_windows_read_lock()
    {
        string target = Path.Combine(root, "index.json"), temp = Path.Combine(root, "index.tmp");
        await File.WriteAllTextAsync(target, "old"); await File.WriteAllTextAsync(temp, "new");
        var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        Task replacing = Task.Run(() => GameJournal.ReplaceAtomically(temp, target));
        try { await Task.Delay(60); }
        finally { held.Dispose(); }
        await replacing;
        Assert.Equal("new", await File.ReadAllTextAsync(target)); Assert.False(File.Exists(temp));
    }

    [Fact]
    public async Task Failed_export_does_not_fault_or_block_later_events()
    {
        var journal = new GameJournal(root);
        journal.Event("draw", new { Tile = 4 }); await journal.Completion;
        string occupied = Path.Combine(root, "existing.zip"); await File.WriteAllTextAsync(occupied, "preserve");
        Task<string> export = journal.ExportAsync(occupied);
        journal.Event("discard", new { Tile = 5 });
        await Assert.ThrowsAsync<IOException>(async () => await export);
        await journal.CompleteAsync(); Assert.Null(journal.Fault);
        Assert.Equal("preserve", await File.ReadAllTextAsync(occupied));
        Assert.Equal(2, (await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "events.jsonl"))).Lines.Length);
    }

    [Fact]
    public void Decoder_rejects_a_missing_delta_base()
    {
        var encoder = new JournalPayloadCodec();
        var node = JsonSerializer.SerializeToElement(new { Fixed = new string('x', 2000), Tile = 1 });
        encoder.Encode("public_event", node, 1, DateTimeOffset.UnixEpoch);
        var delta = encoder.Encode("public_event", JsonSerializer.SerializeToElement(new { Fixed = new string('x', 2000), Tile = 2 }),
            2, DateTimeOffset.UnixEpoch.AddSeconds(1));
        Assert.Equal(1, delta.GetProperty("BaseSequence").GetInt64());
        Assert.Throws<IOException>(() => new JournalPayloadCodec().Decode("public_event", delta, 2));
    }

    [Fact]
    public void Codec_roundtrip_includes_null_deleted_property_array_resize_and_literal_paths()
    {
        var encoder = new JournalPayloadCodec(); var decoder = new JournalPayloadCodec();
        for (int i = 0; i < 12; i++)
        {
            var node = new JsonObject { ["Fixed"] = new string('牌', 5000), ["x/y~"] = i, ["Null"] = null,
                ["Array"] = new JsonArray(i, i + 1) };
            if (i % 2 == 0) node["Optional"] = true;
            if (i % 3 == 0) node["Array"]!.AsArray().Add(4);
            JsonElement input = JsonSerializer.SerializeToElement(node);
            var encoded = encoder.Encode("public_event", input, i + 1, DateTimeOffset.UnixEpoch.AddSeconds(i));
            var actual = decoder.Decode("public_event", encoded, i + 1);
            Assert.True(JsonElement.DeepEquals(input, actual));
        }
    }
}
