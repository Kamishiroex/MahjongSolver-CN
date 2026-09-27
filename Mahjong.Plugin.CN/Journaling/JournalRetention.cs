using System.Text.RegularExpressions;

namespace Mahjong.Plugin.CN.Journaling;

/// <summary>Only closed, plugin-owned records are retention candidates. Never an input gate.</summary>
internal static class JournalRetention
{
    internal const int DefaultMatches = 50;
    internal const long DefaultHistoryBytes = 1024L * 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly Regex Name = new(@"\Amjcn-game-[0-9]{8}-[0-9]{6}-[0-9a-f]{32}\z");
    internal sealed record Result(int Removed, int Retained, long Bytes, string? Warning);

    internal static Result Maintain(string root, int keep, string? current = null, long budget = DefaultHistoryBytes)
    {
        if (keep is < 20 or > 500 || budget < 0) throw new ArgumentOutOfRangeException(nameof(keep));
        lock (Gate)
        {
            root = Path.GetFullPath(root);
            GameJournal.RejectLinks(root);
            string archives = Path.Combine(Path.GetDirectoryName(root)!, Path.GetFileName(root) + "-archive");
            GameJournal.RejectLinks(archives);
            var entries = new List<(string Path, string Name, long Size, bool Zip)>();
            string? warning = null;
            if (Directory.Exists(root))
                foreach (var path in Directory.EnumerateDirectories(root))
                {
                    if (!Name.IsMatch(Path.GetFileName(path)) || string.Equals(path, current, StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        GameJournal.RejectLinks(path);
                        var files = Directory.GetFiles(path);
                        if (Directory.EnumerateDirectories(path).Any() || files.Any(p => !OwnedFile(Path.GetFileName(p)))) continue;
                        foreach (string file in files) GameJournal.RejectLinks(file);
                        // A complete marker releases the quiet-period guard. Crash/legacy
                        // records need an idle interval as old writers did not hold leases.
                        if (!File.Exists(Path.Combine(path, "record-closed.json")) &&
                            files.Any(p => File.GetLastWriteTimeUtc(p) > DateTime.UtcNow.AddMinutes(-1))) continue;
                        GameJournal.RejectLinks(Path.Combine(path, ".active"));
                        using var lease = new FileStream(Path.Combine(path, ".active"), FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None);
                        entries.Add((path, Path.GetFileName(path), files.Sum(p => new FileInfo(p).Length), false));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { warning = "日志清理暂未完成：" + ex.GetType().Name; }
                }
            if (Directory.Exists(archives))
                foreach (var path in Directory.EnumerateFiles(archives, "*.zip"))
                {
                    if (!Name.IsMatch(Path.GetFileNameWithoutExtension(path))) continue;
                    try { GameJournal.RejectLinks(path); entries.Add((path, Path.GetFileNameWithoutExtension(path), new FileInfo(path).Length, true)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warning = "旧归档无法检查：" + ex.GetType().Name; }
                }
            var groups = entries.GroupBy(x => x.Name).OrderByDescending(g => g.Key, StringComparer.Ordinal).ToArray();
            long total = entries.Sum(x => x.Size);
            int retained = groups.Length, removed = 0;
            // Preserve the newest closed record as a recovery fallback even when it
            // exceeds the historical budget. Active records are excluded altogether.
            for (int i = groups.Length - 1; i >= 1; i--)
            {
                if (retained <= keep && total <= budget) break;
                bool allRemoved = true;
                foreach (var entry in groups[i])
                {
                    try
                    {
                        DeleteOwned(root, archives, entry.Path, entry.Zip);
                        total -= entry.Size;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { allRemoved = false; warning = "旧日志清理将在稍后重试：" + ex.GetType().Name; }
                }
                if (allRemoved) { retained--; removed++; }
            }
            return new(removed, retained, total, warning);
        }
    }

    private static bool OwnedFile(string name) => GameJournal.IsJournalFile(name) || name is
        ".active" or "journal-index.json" or "journal-index.tmp" or "recovery-latest.jsonl" or
        "recovery-latest.jsonl.tmp" or "diagnostic-fault.jsonl" or "diagnostic-fault.jsonl.tmp" or "record-closed.json";

    private static void DeleteOwned(string root, string archives, string path, bool zip)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(path), zip ? archives : root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("JOURNAL_RETENTION_PATH_INVALID");
        GameJournal.RejectLinks(path);
        if (zip) { File.Delete(path); return; }
        if (Directory.EnumerateDirectories(path).Any()) throw new IOException("JOURNAL_RETENTION_UNKNOWN_CONTENT");
        var files = Directory.GetFiles(path);
        if (files.Any(p => !OwnedFile(Path.GetFileName(p)))) throw new IOException("JOURNAL_RETENTION_UNKNOWN_CONTENT");
        // Lock every file before deleting any: locked/exporting records remain intact.
        var held = new List<FileStream>();
        try
        {
            foreach (string file in files.OrderBy(p => Path.GetFileName(p) == ".active" ? 0 : 1))
            {
                GameJournal.RejectLinks(file);
                held.Add(new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete));
            }
            foreach (string file in files) { GameJournal.RejectLinks(file); File.Delete(file); }
        }
        finally { foreach (var stream in held) stream.Dispose(); }
        Directory.Delete(path, false);
    }
}
