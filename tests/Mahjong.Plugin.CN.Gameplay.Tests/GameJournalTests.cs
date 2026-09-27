using System.Text;
using System.Text.Json;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class GameJournalTests
{
    [Fact]
    public async Task Actual_AI_error_trace_keeps_both_streams_writable()
    {
        using var folder = new Folder();
        var journal = new GameJournal(folder.Path);
        var plugin = (Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(Plugin).GetField("journal", flags)!.SetValue(plugin, journal);
        typeof(Plugin).GetField("journalActive", flags)!.SetValue(plugin, true);
        const string code = "GLOBAL_AI_PROTOCOL_MOVE_BATCH_INCOMPLETE@-1:response";
        var trace = new Experimental.GlobalAiTrace("error", "test", null, null, null, code);
        typeof(Plugin).GetMethod("RecordGlobalAiTrace", flags)!.Invoke(plugin, [trace]);
        Assert.True(journal.Event("play_paused", new { Reason = code }));
        await journal.CompleteAsync();
        Assert.Null(journal.Fault);
        var events = await GameJournal.ReadAsync(System.IO.Path.Combine(journal.DirectoryPath, "events.jsonl"));
        var errors = await GameJournal.ReadAsync(System.IO.Path.Combine(journal.DirectoryPath, "errors.jsonl"));
        Assert.True(events.IntegrityPassed);
        Assert.True(errors.IntegrityPassed);
        Assert.Equal(code, events.Lines[0].Entry.Data.GetProperty("Error").GetString());
        Assert.Equal(code, Assert.Single(errors.Lines).Entry.Data.GetProperty("Code").GetString());
        Assert.Same(trace, plugin.LastGlobalAiTrace);
    }

    [Theory]
    [InlineData("GLOBAL_AI_PROTOCOL_MOVE_BATCH_INCOMPLETE@-1:response")]
    [InlineData("MORTAL_INPUT:invalid state")]
    [InlineData("读取异常：测试位置")]
    public async Task Detailed_error_codes_never_poison_writer_and_preserve_original_detail(string code)
    {
        using var folder = new Folder();
        var journal = new GameJournal(folder.Path);
        Assert.True(journal.Error(code, new { Stage = "global-ai" }));
        Assert.True(journal.Error(code, new { Stage = "global-ai" }));
        journal.ErrorsRecovered();
        Assert.True(journal.Event("global_ai_request", new { Request = "after-error" }));
        await journal.CompleteAsync();
        Assert.Null(journal.Fault);
        var errors = await GameJournal.ReadAsync(System.IO.Path.Combine(journal.DirectoryPath, "errors.jsonl"));
        Assert.True(errors.IntegrityPassed);
        Assert.Equal("GAMEPLAY_DIAGNOSTIC_ERROR", errors.Lines[0].Entry.Kind);
        Assert.Equal(code, errors.Lines[0].Entry.Data.GetProperty("Code").GetString());
        Assert.Equal("global-ai", errors.Lines[0].Entry.Data.GetProperty("Detail").GetProperty("Stage").GetString());
        Assert.Equal(new[] { "GAMEPLAY_DIAGNOSTIC_ERROR", "REPEATED_ERROR_SUMMARY", "READ_RECOVERED" }, errors.Lines.Select(x => x.Entry.Kind));
        var events = await GameJournal.ReadAsync(System.IO.Path.Combine(journal.DirectoryPath, "events.jsonl"));
        Assert.True(events.IntegrityPassed);
        Assert.Equal("global_ai_request", Assert.Single(events.Lines).Entry.Kind);
    }

    [Theory]
    [InlineData('牌')]
    [InlineData('~')]
    public async Task Large_global_AI_trace_is_losslessly_chunked_without_faulting_event_journal(char character)
    {
        using var folder=new Folder(); var journal=new GameJournal(folder.Path);
        var plugin=(Plugin)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        typeof(Plugin).GetField("journal",flags)!.SetValue(plugin,journal);
        typeof(Plugin).GetField("journalActive",flags)!.SetValue(plugin,true);
        string reasoning=new string(character,50_000);
        var trace=new Experimental.GlobalAiTrace("decision","test",null,null,
            Mahjong.Policy.Abstractions.ActionChoice.Pass(reasoning),null);
        typeof(Plugin).GetMethod("RecordGlobalAiTrace",flags)!.Invoke(plugin,[trace]);
        await journal.CompleteAsync(); Assert.Null(journal.Fault);
        var read=await GameJournal.ReadAsync(System.IO.Path.Combine(journal.DirectoryPath,"events.jsonl"));
        Assert.True(read.IntegrityPassed); Assert.True(read.Lines.Length>1);
        using var bytes=new MemoryStream();
        foreach(var row in read.Lines.OrderBy(x=>x.Entry.Data.GetProperty("Index").GetInt32()))
        {
            Assert.Equal("global_ai_trace_chunk",row.Entry.Kind);
            bytes.Write(Convert.FromBase64String(row.Entry.Data.GetProperty("Data").GetString()!));
        }
        using var decoded=JsonDocument.Parse(bytes.ToArray());
        Assert.Equal(reasoning,decoded.RootElement.GetProperty("Choice").GetProperty("Reasoning").GetString());
        Assert.Same(trace,plugin.LastGlobalAiTrace);
    }

    [Fact]
    public async Task Events_and_errors_are_separate_ordered_and_reloadable()
    {
        using var folder = new Folder();
        var journal = new GameJournal(folder.Path);
        Assert.True(journal.Event("session_started", new { source = "synthetic", version = "test" }));
        Assert.True(journal.Event("discard_observed", new { tile = 13, red = true }));
        Assert.True(journal.Error("READ_CONFLICT", new { eventSequence = 2, position = "river-bottom" }));
        await journal.CompleteAsync();
        Assert.Null(journal.Fault);
        Assert.False(journal.Event("closed", new { source = "synthetic" }));
        var events = await GameJournal.ReadAsync(System.IO.Path.Combine(journal.DirectoryPath, "events.jsonl"));
        var errors = await GameJournal.ReadAsync(System.IO.Path.Combine(journal.DirectoryPath, "errors.jsonl"));
        Assert.True(events.IntegrityPassed);
        Assert.True(errors.IntegrityPassed);
        Assert.False(events.IncompleteTail);
        Assert.Equal(new[] { "session_started", "discard_observed" }, events.Lines.Select(x => x.Entry.Kind));
        Assert.Equal(new long[] { 1, 2 }, events.Lines.Select(x => x.Entry.Sequence));
        Assert.Equal("READ_CONFLICT", Assert.Single(errors.Lines).Entry.Kind);
        Assert.Equal(events.Lines[0].Entry.SessionId, errors.Lines[0].Entry.SessionId);
        Assert.Equal(new[] { journal.DirectoryPath }, GameJournal.RecentDirectories(folder.Path));
    }

    [Fact]
    public async Task Crash_tail_is_reported_but_prior_committed_checkpoint_survives()
    {
        using var folder = new Folder();
        var journal = new GameJournal(folder.Path);
        journal.Event("recovery_checkpoint", new { source = "synthetic", stateRevision = 7 });
        await journal.CompleteAsync();
        string file = System.IO.Path.Combine(journal.DirectoryPath, "events.jsonl");
        await File.AppendAllTextAsync(file, "{\"Entry\":{\"Schema\":1,\"Da");
        var read = await GameJournal.ReadAsync(file);
        Assert.True(read.IntegrityPassed);
        Assert.True(read.IncompleteTail);
        Assert.Equal(7, Assert.Single(read.Lines).Entry.Data.GetProperty("stateRevision").GetInt32());
    }

    [Fact]
    public async Task Editing_or_reordering_complete_rows_is_rejected()
    {
        using var folder = new Folder();
        var journal = new GameJournal(folder.Path);
        journal.Event("observed", new { amount = 13 });
        journal.Event("observed", new { amount = 14 });
        await journal.CompleteAsync();
        string file = System.IO.Path.Combine(journal.DirectoryPath, "events.jsonl");
        string original = await File.ReadAllTextAsync(file);
        await File.WriteAllTextAsync(file, original.Replace("\"amount\":13", "\"amount\":12", StringComparison.Ordinal));
        Assert.Equal("JOURNAL_HASH_MISMATCH", (await GameJournal.ReadAsync(file)).Error);
        string[] lines = original.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        await File.WriteAllTextAsync(file, lines[1] + "\n" + lines[0] + "\n");
        Assert.Equal("JOURNAL_CHAIN_INVALID", (await GameJournal.ReadAsync(file)).Error);
    }

    [Fact]
    public async Task Malformed_complete_row_is_not_silently_treated_as_a_crash_tail()
    {
        using var folder = new Folder();
        string file = System.IO.Path.Combine(folder.Path, "bad.jsonl");
        await File.WriteAllTextAsync(file, "{\"broken\":\n");
        Assert.False((await GameJournal.ReadAsync(file)).IntegrityPassed);
        await File.WriteAllBytesAsync(file, [0xff, (byte)'\n']);
        Assert.False((await GameJournal.ReadAsync(file)).IntegrityPassed);
    }

    [Fact]
    public async Task Oversize_row_is_rejected_before_unbounded_parse()
    {
        using var folder = new Folder();
        string file = System.IO.Path.Combine(folder.Path, "long.jsonl");
        await File.WriteAllTextAsync(file, new string('x', GameJournal.MaximumLineBytes + 1));
        Assert.Equal("JOURNAL_LINE_LIMIT", (await GameJournal.ReadAsync(file)).Error);
        var journal = new GameJournal(folder.Path);
        Assert.False(journal.Event("too_large", new { data = new string('x', GameJournal.MaximumLineBytes) }));
        Assert.Equal("JOURNAL_ENTRY_TOO_LARGE", journal.Fault);
        await journal.CompleteAsync();
    }

    [Fact]
    public async Task Duplicate_JSON_properties_cannot_bypass_integrity_checks()
    {
        using var folder = new Folder();
        var journal = new GameJournal(folder.Path);
        journal.Event("observed", new { source = "synthetic" });
        await journal.CompleteAsync();
        string file = System.IO.Path.Combine(journal.DirectoryPath, "events.jsonl");
        string raw = await File.ReadAllTextAsync(file);
        await File.WriteAllTextAsync(file, raw.Replace("\"Schema\":2", "\"Schema\":2,\"Schema\":2", StringComparison.Ordinal));
        Assert.Equal("JOURNAL_DUPLICATE_PROPERTY", (await GameJournal.ReadAsync(file)).Error);
    }

    [Fact]
    public async Task Storage_failure_is_reported_off_thread_and_no_foreign_file_is_overwritten()
    {
        using var folder = new Folder();
        string root = System.IO.Path.Combine(folder.Path, "blocked");
        await File.WriteAllTextAsync(root, "preserve");
        var journal = new GameJournal(root);
        Assert.True(journal.Event("observed", new { source = "synthetic" }));
        await journal.CompleteAsync();
        Assert.StartsWith("JOURNAL_WRITE_FAILED", journal.Fault);
        Assert.Equal("preserve", await File.ReadAllTextAsync(root));
    }

    private sealed class Folder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mjcn-journal-test-" + Guid.NewGuid().ToString("N"));
        internal Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
