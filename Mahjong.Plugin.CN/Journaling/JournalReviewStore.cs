using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Mahjong.Plugin.CN.Journaling;

/// <summary>Reuse existing journals/archives. Bounded temporary extraction of event streams only.</summary>
internal static class JournalReviewStore
{
    internal static string[] Recent(string root,int limit)
    {
        root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string archives=root+"-archive"; GameJournal.RejectLinks(archives);
        return GameJournal.RecentDirectories(root,limit).Concat(Directory.Exists(archives)?Directory.EnumerateFiles(archives,"mjcn-game-*.zip"):[])
            .Where(p=>Regex.IsMatch(Path.GetFileName(p),@"\Amjcn-game-\d{8}-\d{6}-[a-f0-9]{32}(\.zip)?\z"))
            .OrderByDescending(Path.GetFileName,StringComparer.Ordinal).DistinctBy(p=>Path.GetFileName(p).Replace(".zip","",StringComparison.Ordinal))
            .Take(limit).ToArray();
    }
    internal static async Task<ImmutableArray<MatchSummary>> SummariesAsync(string path,CancellationToken token=default) =>
        (await WithDirectoryAsync(path,p=>MatchSummaryBuilder.LoadAsync(p,token),token)).Select(r=>r with {JournalPath=path}).ToImmutableArray();
    internal static Task<JournalReadResult> EventsAsync(string path,CancellationToken token=default) =>
        WithDirectoryAsync(path,p=>GameJournal.ReadAsync(Path.Combine(p,"events.jsonl"),token),token);
    private static async Task<T> WithDirectoryAsync<T>(string path,Func<string,Task<T>> read,CancellationToken token)
    {
        GameJournal.RejectLinks(path);
        if(Directory.Exists(path)) return await read(path);
        if(new FileInfo(path).Length>256L*1024*1024)throw new IOException("REVIEW_ARCHIVE_SIZE_LIMIT");
        string temp=Path.Combine(Path.GetTempPath(),"mjcn-review-"+Guid.NewGuid().ToString("N"));
        GameJournal.RejectLinks(temp); Directory.CreateDirectory(temp);
        try
        {
            using var archive=ZipFile.OpenRead(path);
            if(archive.Entries.Count>1024)throw new IOException("REVIEW_ARCHIVE_ENTRY_LIMIT");
            long total=0; var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                if(!Regex.IsMatch(entry.FullName,@"\Aevents(\.[0-9]{4})?\.jsonl\z") && entry.FullName is not ("rating-after.json" or "final-result.json"))continue;
                if(!names.Add(entry.FullName) || entry.Length>GameJournal.MaximumFileBytes || (total+=entry.Length)>256L*1024*1024)
                    throw new IOException("REVIEW_ARCHIVE_EXPANSION_LIMIT");
                string file=Path.Combine(temp,entry.FullName);GameJournal.RejectLinks(file);
                using var input=entry.Open();using var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                byte[] buffer=new byte[65536];long copied=0;int n;
                while((n=await input.ReadAsync(buffer,token))>0)
                { if((copied+=n)>entry.Length)throw new IOException("REVIEW_ARCHIVE_LENGTH_MISMATCH");await output.WriteAsync(buffer.AsMemory(0,n),token); }
                if(copied!=entry.Length)throw new IOException("REVIEW_ARCHIVE_LENGTH_MISMATCH");
            }
            if(!names.Contains("events.jsonl"))throw new IOException("REVIEW_ARCHIVE_EVENTS_MISSING");
            return await read(temp);
        }
        finally
        {
            // Only this exact owned temporary directory; no recursive traversal/deletion.
            GameJournal.RejectLinks(temp);
            foreach(string file in Directory.EnumerateFiles(temp)){GameJournal.RejectLinks(file);File.Delete(file);}
            Directory.Delete(temp,false);
        }
    }
}
