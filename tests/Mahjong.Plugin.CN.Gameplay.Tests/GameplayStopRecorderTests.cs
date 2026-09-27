using System.Text.Json;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class GameplayStopRecorderTests
{
    [Fact]
    public async Task Retains_latest_twenty_readable_reports_and_preserves_foreign_files()
    {
        using var folder = new TemporaryFolder();
        string output = Path.Combine(folder.Path, "gameplay-stops");
        Directory.CreateDirectory(output);
        string foreign = Path.Combine(output, "mjcn-stop-user-notes.json");
        await File.WriteAllTextAsync(foreign, "keep me");
        string unrelated = Path.Combine(output, "settings.json");
        await File.WriteAllTextAsync(unrelated, "also keep me");
        string child = Path.Combine(output, "nested");
        Directory.CreateDirectory(child);
        string nested = Path.Combine(child, "mjcn-stop-20000101T0000000000000Z-00000000000000000000000000000000.json");
        await File.WriteAllTextAsync(nested, "nested untouched");

        var recorder = new GameplayStopRecorder(folder.Path);
        for (int sequence = 0; sequence < 25; sequence++)
        {
            Assert.True(recorder.TryRecord(JsonSerializer.Serialize(new { sequence, reason = "测试停机" })));
            await recorder.Completion;
        }
        await recorder.CompleteAsync();

        var actual = new List<int>();
        foreach (string path in Directory.GetFiles(output, "mjcn-stop-*.json"))
        {
            if (path == foreign) continue;
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            actual.Add(document.RootElement.GetProperty("sequence").GetInt32());
        }
        Assert.Equal(Enumerable.Range(5, 20), actual.Order());
        Assert.Equal("keep me", await File.ReadAllTextAsync(foreign));
        Assert.Equal("also keep me", await File.ReadAllTextAsync(unrelated));
        Assert.Equal("nested untouched", await File.ReadAllTextAsync(nested));
        Assert.Null(recorder.LastError);
        Assert.NotNull(recorder.LastPath);
        using var last = JsonDocument.Parse(await File.ReadAllTextAsync(recorder.LastPath));
        Assert.Equal(24, last.RootElement.GetProperty("sequence").GetInt32());
    }

    [Fact]
    public async Task Inaccessible_destination_is_reported_asynchronously_and_does_not_poison_future_writes()
    {
        using var folder = new TemporaryFolder();
        string output = Path.Combine(folder.Path, "gameplay-stops");
        await File.WriteAllTextAsync(output, "a file blocks directory creation");
        var recorder = new GameplayStopRecorder(folder.Path);

        Assert.True(recorder.TryRecord("{\"reason\":\"first\"}"));
        await recorder.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("STOP_RECORD_WRITE_FAILED:", recorder.LastError);
        Assert.Null(recorder.LastPath);
        Assert.Equal("a file blocks directory creation", await File.ReadAllTextAsync(output));

        File.Delete(output);
        Assert.True(recorder.TryRecord("{\"reason\":\"recovered\"}"));
        await recorder.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(recorder.LastError);
        Assert.Equal("{\"reason\":\"recovered\"}", await File.ReadAllTextAsync(recorder.LastPath!));
    }

    [Fact]
    public async Task Oversized_invalid_and_closed_input_cannot_produce_an_unbounded_or_bad_report()
    {
        using var folder = new TemporaryFolder();
        var recorder = new GameplayStopRecorder(folder.Path);
        Assert.False(recorder.TryRecord(new string('x', GameplayStopRecorder.MaximumCharacters + 1)));
        Assert.Equal("STOP_RECORD_INVALID_SIZE", recorder.LastError);
        Assert.False(Directory.Exists(Path.Combine(folder.Path, "gameplay-stops")));
        Assert.True(recorder.TryRecord("not-json"));
        await recorder.Completion;
        Assert.StartsWith("STOP_RECORD_WRITE_FAILED:", recorder.LastError);
        Assert.Null(recorder.LastPath);
        await recorder.CompleteAsync();
        Assert.False(recorder.TryRecord("{}"));
        Assert.Equal("STOP_RECORDER_CLOSED", recorder.LastError);
    }

    private sealed class TemporaryFolder : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mjcn-stop-test-" + Guid.NewGuid().ToString("N"));
        internal TemporaryFolder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
