using System.IO.Compression;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class JournalArchiveStoreTests : IDisposable
{
    private readonly string home=Path.Combine(Path.GetTempPath(),"mjcn-archive-test-"+Guid.NewGuid().ToString("N"));
    private string Root=>Path.Combine(home,"logs");
    public JournalArchiveStoreTests()=>Directory.CreateDirectory(Root);
    public void Dispose()=>Directory.Delete(home,true);
    private string Session(int n)
    {
        string dir=Path.Combine(Root,$"mjcn-game-20260925-{n:D6}-00000000000000000000000000000000");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir,"events.jsonl"),new string((char)('a'+n),2048));
        File.WriteAllText(Path.Combine(dir,"recovery-latest.jsonl"),"recovery "+n);
        foreach(var file in Directory.EnumerateFiles(dir))File.SetLastWriteTimeUtc(file,DateTime.UtcNow.AddHours(-1));
        return dir;
    }

    [Fact]
    public void Budget_archives_oldest_but_preserves_current_and_two_recent_sessions()
    {
        var dirs=Enumerable.Range(0,5).Select(Session).ToArray();
        long remaining=JournalArchiveStore.Prepare(Root,dirs[4],12*1024);
        Assert.True(remaining>5*1024);
        Assert.False(Directory.Exists(dirs[0]));Assert.False(Directory.Exists(dirs[1]));
        Assert.All(dirs.Skip(2),d=>Assert.True(Directory.Exists(d)));
        using var zip=ZipFile.OpenRead(Path.Combine(home,"logs-archive",Path.GetFileName(dirs[0])+".zip"));
        using var reader=new StreamReader(zip.GetEntry("events.jsonl")!.Open());
        Assert.Equal(new string('a',2048),reader.ReadToEnd());Assert.NotNull(zip.GetEntry("recovery-latest.jsonl"));
    }

    [Fact]
    public async Task Live_writer_lease_prevents_archival_and_completed_session_replays_from_zip()
    {
        var journal=new GameJournal(Root);journal.Event("discard",new {Tile=9});await journal.Completion;
        Assert.False(JournalArchiveStore.TryArchive(Root,journal.DirectoryPath));Assert.Null(journal.Fault);
        journal.Event("draw",new {Tile=22});await journal.CompleteAsync();
        Assert.True(JournalArchiveStore.TryArchive(Root,journal.DirectoryPath));
        string unpack=Path.Combine(home,"unpacked");
        ZipFile.ExtractToDirectory(Path.Combine(home,"logs-archive",Path.GetFileName(journal.DirectoryPath)+".zip"),unpack);
        var replay=await GameJournal.ReadAsync(Path.Combine(unpack,"events.jsonl"));
        Assert.True(replay.IntegrityPassed,replay.Error);Assert.Equal(2,replay.Lines.Length);
    }

    [Fact]
    public void Locked_file_or_existing_archive_cannot_destroy_originals()
    {
        string dir=Session(0);using(var held=new FileStream(Path.Combine(dir,"events.jsonl"),FileMode.Open,FileAccess.Read,FileShare.None))
            Assert.False(JournalArchiveStore.TryArchive(Root,dir));
        Assert.True(File.Exists(Path.Combine(dir,"events.jsonl")));
        Directory.CreateDirectory(Path.Combine(home,"logs-archive"));
        string zip=Path.Combine(home,"logs-archive",Path.GetFileName(dir)+".zip");File.WriteAllText(zip,"preserve");
        Assert.False(JournalArchiveStore.TryArchive(Root,dir));
        Assert.Equal("preserve",File.ReadAllText(zip));Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public void Unknown_nested_contents_and_outside_paths_are_not_removed()
    {
        string dir=Session(0);Directory.CreateDirectory(Path.Combine(dir,"unexpected"));
        Assert.False(JournalArchiveStore.TryArchive(Root,dir));Assert.True(Directory.Exists(dir));
        Assert.Throws<IOException>(()=>JournalArchiveStore.TryArchive(Root,home));
    }

    [Fact]
    public void Many_small_sessions_are_archived_before_the_directory_count_limit()
    {
        var dirs=Enumerable.Range(0,241).Select(Session).ToArray();
        JournalArchiveStore.Prepare(Root,dirs[^1],10*1024*1024);
        Assert.True(Directory.GetDirectories(Root).Length<128);
        Assert.All(dirs.TakeLast(3),d=>Assert.True(Directory.Exists(d)));
    }
}
