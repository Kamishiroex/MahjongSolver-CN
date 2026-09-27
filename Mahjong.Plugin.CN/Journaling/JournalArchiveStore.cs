using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Mahjong.Plugin.CN.Journaling;

/// <summary>Off-thread, lossless archival of inactive old sessions. Never prunes an active stream.</summary>
internal static class JournalArchiveStore
{
    private static readonly object Gate = new();
    private static readonly Regex SessionName = new(@"\Amjcn-game-[0-9]{8}-[0-9]{6}-[0-9a-f]{32}\z",RegexOptions.CultureInvariant);
    internal static long Prepare(string root, string current, long budget, int keepRecent = 2)
    {
        lock (Gate)
        {
            root=Path.GetFullPath(root); CheckPath(root); Directory.CreateDirectory(root);
            var directories=Directory.EnumerateDirectories(root).Where(p=>SessionName.IsMatch(Path.GetFileName(p)))
                .OrderByDescending(p=>Path.GetFileName(p),StringComparer.Ordinal).ToArray();
            long used=directories.Sum(Size);
            int remainingCount=directories.Length;
            if(used<budget*3/4 && directories.Length<240)return Math.Max(0,budget-used);
            foreach(var directory in directories.Where(p=>!string.Equals(p,current,StringComparison.OrdinalIgnoreCase)).Skip(keepRecent).Reverse())
            {
                // Recent writers of older plugin versions have no lease. Preserve the
                // newest sessions plus a quiet interval rather than assuming they ended.
                if(Directory.EnumerateFiles(directory).Any(p=>File.GetLastWriteTimeUtc(p)>DateTime.UtcNow.AddMinutes(-1)))continue;
                long bytes=Size(directory);
                if(TryArchive(root,directory)){used-=bytes;remainingCount--;}
                if(used<budget/2 && remainingCount<128)break;
            }
            return Math.Max(0,budget-used);
        }
    }

    internal static bool TryArchive(string root,string source)
    {
        root=Path.GetFullPath(root);source=Path.GetFullPath(source);
        if(!string.Equals(Path.GetDirectoryName(source),root,StringComparison.OrdinalIgnoreCase) ||
            !SessionName.IsMatch(Path.GetFileName(source)))throw new IOException("JOURNAL_ARCHIVE_PATH_INVALID");
        CheckPath(source);
        if(Directory.EnumerateDirectories(source).Any())return false;
        string archiveRoot=Path.Combine(Path.GetDirectoryName(root)!,Path.GetFileName(root)+"-archive");
        CheckPath(archiveRoot);Directory.CreateDirectory(archiveRoot);
        string final=Path.Combine(archiveRoot,Path.GetFileName(source)+".zip");CheckPath(final);
        if(File.Exists(final))return false; // Preserve both copies after an interrupted previous cleanup.
        string temporary=final+"."+Guid.NewGuid().ToString("N")+".tmp";
        var held=new List<FileStream>();
        var hashes=new Dictionary<string,(long Length,byte[] Hash)>();
        try
        {
            // The current plugin holds this file until CompleteAsync finishes. Old
            // versions are additionally protected by Prepare's recent-session rules.
            string lease=Path.Combine(source,".active");CheckPath(lease);
            held.Add(new FileStream(lease,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None));
            var paths=Directory.EnumerateFiles(source).Where(p=>Path.GetFileName(p)!=".active").Order().ToArray();
            if(paths.Length is 0 or >1024)return false;
            foreach(var path in paths){CheckPath(path);held.Add(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None));}
            using(var zip=ZipFile.Open(temporary,ZipArchiveMode.Create))
            foreach(var file in held.Skip(1))
            {
                string name=Path.GetFileName(file.Name);if(file.Length>64L*1024*1024)throw new IOException("JOURNAL_ARCHIVE_FILE_LIMIT");
                hashes.Add(name,(file.Length,SHA256.HashData(file)));file.Position=0;
                using var output=zip.CreateEntry(name,CompressionLevel.Optimal).Open();file.CopyTo(output);
            }
            using(var zip=ZipFile.OpenRead(temporary))
            {
                if(zip.Entries.Count!=hashes.Count)throw new IOException("JOURNAL_ARCHIVE_COUNT_MISMATCH");
                foreach(var entry in zip.Entries)
                {
                    using var data=entry.Open();var expected=hashes[entry.FullName];
                    if(entry.Length!=expected.Length || !SHA256.HashData(data).SequenceEqual(expected.Hash))
                        throw new IOException("JOURNAL_ARCHIVE_HASH_MISMATCH");
                }
            }
            File.Move(temporary,final); // Verified full contents exist before originals are removed.
            foreach(var stream in held.Skip(1))stream.Dispose();
            foreach(var path in paths){CheckPath(path);File.Delete(path);}
            held[0].Dispose();File.Delete(lease);
            CheckPath(source);Directory.Delete(source,false); // Never recursively remove unexpected contents.
            return true;
        }
        catch(IOException){return false;}
        catch(UnauthorizedAccessException){return false;}
        finally
        {
            foreach(var stream in held)stream.Dispose();
            if(File.Exists(temporary))File.Delete(temporary);
        }
    }

    private static long Size(string directory)
    {
        CheckPath(directory);long total=0;
        foreach(string path in Directory.EnumerateFiles(directory))
        {
            CheckPath(path);
            string name=Path.GetFileName(path);
            if(name=="events.jsonl" || name=="errors.jsonl" || Regex.IsMatch(name,@"\A(events|errors)\.[0-9]{4}\.jsonl\z"))
                total=checked(total+new FileInfo(path).Length);
        }
        return total;
    }
    private static void CheckPath(string path)
    {
        for(string? p=Path.GetFullPath(path);p is not null;p=Path.GetDirectoryName(p))
            if((File.Exists(p)||Directory.Exists(p)) && (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)
                throw new IOException("JOURNAL_ARCHIVE_REPARSE_POINT");
    }
}
