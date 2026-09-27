using System.Text.Json;
using System.Text.Json.Nodes;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class LongPublicJournalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mjcn-long-journal-" + Guid.NewGuid().ToString("N"));
    public LongPublicJournalTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public async Task Captured_late_round_snapshot_and_larger_envelope_survive_rotation_and_recovery()
    {
        var captured = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "long-public-event-20260925.json")))!;
        // Stress extension, NOT another claimed game observation: preserve every captured field,
        // and exercise growth beyond the old limit using explicitly synthetic diagnostic text.
        var extended = captured.DeepClone();
        extended["SyntheticStorageStress"] = new string('牌', 24_000);
        var expected = new[] { captured, extended, captured.DeepClone() };
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(extended).Length > GameJournal.MaximumLineBytes);
        var journal = new GameJournal(root, 2048);
        foreach (var value in expected)
        {
            Assert.True(journal.Event("public_event", value));
            await journal.Completion;
            Assert.Null(journal.Fault);
        }
        journal.UpdateRecovery(new { SyntheticStorageStress = extended });
        await journal.CompleteAsync();
        Assert.Null(journal.Fault);
        var replay = await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "events.jsonl"));
        Assert.True(replay.IntegrityPassed, replay.Error);
        Assert.Equal(3, replay.Lines.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected[i]), replay.Lines[i].Entry.Data));
        Assert.True(GameJournal.StreamFiles(journal.DirectoryPath, "events.jsonl").Length > 1);
        foreach (var file in GameJournal.StreamFiles(journal.DirectoryPath, "events.jsonl"))
            foreach (var line in File.ReadLines(file))
                Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) < GameJournal.MaximumLineBytes);
        var recovery = await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "recovery-latest.jsonl"));
        Assert.True(recovery.IntegrityPassed, recovery.Error);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(extended),
            Assert.Single(recovery.Lines).Entry.Data.GetProperty("SyntheticStorageStress")));
    }

    [Fact]
    public async Task Oversize_compressed_payload_still_has_a_finite_decoded_limit()
    {
        var journal = new GameJournal(root);
        Assert.False(journal.Event("public_event", new { Text = new string('x', JournalPayloadCodec.MaximumDecodedBytes) }));
        Assert.Equal("JOURNAL_ENTRY_TOO_LARGE", journal.Fault);
        await journal.CompleteAsync();
    }

    [Fact]
    public async Task Incompressible_payload_cannot_write_a_row_that_the_reader_rejects()
    {
        var bytes = new byte[180_000];
        new Random(4729).NextBytes(bytes);
        var journal = new GameJournal(root);
        Assert.True(journal.Event("public_event", new { Data = Convert.ToBase64String(bytes) }));
        await journal.CompleteAsync();
        Assert.Equal("JOURNAL_ENTRY_TOO_LARGE", journal.Fault);
        string events = Path.Combine(journal.DirectoryPath, "events.jsonl");
        Assert.True(!File.Exists(events) || new FileInfo(events).Length == 0);
    }
}
