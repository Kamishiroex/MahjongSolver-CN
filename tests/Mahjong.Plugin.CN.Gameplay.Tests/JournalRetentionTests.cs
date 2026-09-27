using System.IO.Compression;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class JournalRetentionTests : IDisposable
{
    private readonly string home = Path.Combine(Path.GetTempPath(), "mjcn-retention-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(home, "logs");
    private string Session(int n)
    {
        string path = Path.Combine(Root, $"mjcn-game-20260925-{n:D6}-00000000000000000000000000000000");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "events.jsonl"), new string('x', 2048));
        File.WriteAllText(Path.Combine(path, "record-closed.json"), "{}");
        return path;
    }

    [Theory]
    [InlineData(20)] [InlineData(50)] [InlineData(100)]
    public void Count_policy_keeps_newest_matches_and_does_not_touch_exports(int keep)
    {
        var paths = Enumerable.Range(0, keep + 7).Select(Session).ToArray();
        foreach(string path in paths)
        {
            File.WriteAllText(Path.Combine(path,"match-summary.json"),"{}");
            File.WriteAllText(Path.Combine(path,"rating-after.json"),"{}");
            File.WriteAllText(Path.Combine(path,"final-result.json"),"{}");
        }
        string export = Path.Combine(home, "my-export.zip"); File.WriteAllText(export, "keep");
        var result = JournalRetention.Maintain(Root, keep);
        Assert.Equal(7, result.Removed); Assert.Equal(keep, result.Retained);
        Assert.All(paths.Take(7), p => Assert.False(Directory.Exists(p)));
        Assert.All(paths.Skip(7), p => Assert.True(Directory.Exists(p)));
        Assert.Equal("keep", File.ReadAllText(export));
    }

    [Fact]
    public async Task Current_writer_locked_record_unknown_files_and_nested_directories_are_preserved()
    {
        var paths = Enumerable.Range(0, 24).Select(Session).ToArray();
        File.WriteAllText(Path.Combine(paths[0], "personal.txt"), "keep");
        Directory.CreateDirectory(Path.Combine(paths[1], "unknown"));
        var live = new GameJournal(Root); live.Event("draw", new { Tile = 1 }); await live.Completion;
        using (var locked = new FileStream(Path.Combine(paths[2], "events.jsonl"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            JournalRetention.Maintain(Root, 20, budget: 1);
            Assert.True(Directory.Exists(paths[2]));
        }
        Assert.True(Directory.Exists(paths[0])); Assert.True(Directory.Exists(paths[1]));
        Assert.True(Directory.Exists(live.DirectoryPath));
        Assert.True(live.Event("discard", new { Tile = 1 })); await live.CompleteAsync();
        Assert.Null(live.Fault);
        Assert.Equal(2, (await GameJournal.ReadAsync(Path.Combine(live.DirectoryPath, "events.jsonl"))).Lines.Length);
    }

    [Fact]
    public void Legacy_archives_share_retention_count_and_byte_budget_but_newest_recovery_survives()
    {
        string oldest = Session(0), newest = Session(9);
        string archives = Path.Combine(home, "logs-archive"); Directory.CreateDirectory(archives);
        string zip = Path.Combine(archives, "mjcn-game-20260925-000001-00000000000000000000000000000000.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var w = new StreamWriter(z.CreateEntry("events.jsonl").Open())) w.Write("old");
        var result = JournalRetention.Maintain(Root, 50, budget: 1);
        Assert.Equal(2, result.Removed); Assert.False(File.Exists(zip));
        Assert.False(Directory.Exists(oldest)); Assert.True(Directory.Exists(newest));
    }

    [Fact]
    public async Task Old_256_MiB_and_256_directory_limits_do_not_block_a_new_record()
    {
        var paths = Enumerable.Range(0, 260).Select(Session).ToArray();
        // Sparse allocation is enough: the old guard counted FileInfo.Length.
        using (var f = File.OpenWrite(Path.Combine(paths[0], "events.jsonl"))) f.SetLength(257L * 1024 * 1024);
        var live = new GameJournal(Root);
        live.Event("draw", new { Tile = 3 }); await live.CompleteAsync();
        Assert.Null(live.Fault);
        Assert.Single((await GameJournal.ReadAsync(Path.Combine(live.DirectoryPath, "events.jsonl"))).Lines);
        Assert.Equal(16, GameJournal.RecentDirectories(Root).Length);
        JournalRetention.Maintain(Root, 50, live.DirectoryPath);
        Assert.True(Directory.GetDirectories(Root).Length <= 51);
    }

    [Fact]
    public async Task One_uninterrupted_record_can_cross_the_old_256_MiB_budget_and_export_all_segments()
    {
        var live = new GameJournal(Root);
        // Uncompressed event kind deliberately exercises the actual production byte boundary.
        for (int i = 0; i < 2200; i++)
        {
            Assert.True(live.Event("storage_stress", new { Index = i, Payload = new string('x', 123000) }));
            if (i % 16 == 0) await live.Completion;
        }
        await live.CompleteAsync(); Assert.Null(live.Fault);
        var parts = GameJournal.StreamFiles(live.DirectoryPath, "events.jsonl");
        Assert.True(parts.Sum(p => new FileInfo(p).Length) > 256L * 1024 * 1024);
        Assert.All(parts, p => Assert.InRange(new FileInfo(p).Length, 0, GameJournal.MaximumFileBytes));
        string exported = Path.Combine(home, "export.zip"); await live.ExportAsync(exported);
        using var zip = ZipFile.OpenRead(exported);
        int rows = 0;
        foreach (var part in parts)
        {
            using var reader = new StreamReader(zip.GetEntry(Path.GetFileName(part))!.Open());
            while (await reader.ReadLineAsync() is { } row)
            {
                using var json = System.Text.Json.JsonDocument.Parse(row);
                Assert.Equal(rows++, json.RootElement.GetProperty("Entry").GetProperty("Data").GetProperty("Index").GetInt32());
            }
        }
        Assert.Equal(2200, rows);
    }

    public void Dispose() { if (Directory.Exists(home)) Directory.Delete(home, true); }
}
