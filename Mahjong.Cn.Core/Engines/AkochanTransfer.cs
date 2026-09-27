using System.IO.Compression;

namespace Mahjong.Cn.Engines;

/// <summary>Local personal-machine transfer only. Never downloads or executes a binary.</summary>
public static class AkochanTransfer
{
    public static string Locate(string configRoot)
    {
        foreach (string name in new[] { "akochan-global-v5", "akochan-global-v4", "akochan-global-v3", "akochan-global-v2", "akochan-global" })
        {
            string path = Path.Combine(configRoot, "engines", name);
            if (File.Exists(Path.Combine(path, "akochan-installation.json"))) return path;
        }
        return Path.Combine(configRoot, "engines", "akochan-global-v5");
    }

    public static async Task<AkochanInstallation> CheckAsync(string directory, CancellationToken token = default)
    {
        var install = await AkochanInstallation.LoadAsync(directory, token).ConfigureAwait(false);
        if (!install.HasObservedFuritenBridge) throw new AkochanException("AKOCHAN_V5_REQUIRED");
        foreach (string file in new[] { "ai.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll" })
            if (!File.Exists(Path.Combine(directory, file))) throw new AkochanException("AKOCHAN_RUNTIME_FILE_MISSING:" + file);
        return install;
    }

    public static async Task<string> ExportAsync(string directory, string archivePath, CancellationToken token = default)
    {
        await CheckAsync(directory, token).ConfigureAwait(false);
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string output = Path.GetFullPath(archivePath);
        if (output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new AkochanException("AKOCHAN_EXPORT_INSIDE_ENGINE");
        using var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order())
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new AkochanException("AKOCHAN_PATH_REPARSE_POINT");
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var target = archive.CreateEntry(Path.GetRelativePath(directory, file).Replace('\\', '/'), CompressionLevel.Optimal).Open();
            await input.CopyToAsync(target, token).ConfigureAwait(false);
        }
        return output;
    }

    public static async Task<AkochanInstallation> ImportAsync(string archivePath, string enginesRoot, CancellationToken token = default)
    {
        string stage = await ExtractAsync(archivePath, enginesRoot, 2048, 1024L * 1024 * 1024, token).ConfigureAwait(false);
        return await CheckAsync(stage, token).ConfigureAwait(false);
    }

    internal static async Task<string> ExtractAsync(string archivePath, string enginesRoot, int maxEntries, long maxBytes, CancellationToken token)
    {
        enginesRoot = Path.GetFullPath(enginesRoot);
        EnsureNoLinks(enginesRoot);
        Directory.CreateDirectory(enginesRoot);
        // Unique destination: existing installations are never deleted or overwritten.
        string stage = Path.Combine(enginesRoot, "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        using var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maxBytes) throw new AkochanException("AKOCHAN_ARCHIVE_TOO_LARGE");
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count == 0 || archive.Entries.Count > maxEntries) throw new AkochanException("AKOCHAN_ARCHIVE_INVENTORY_INVALID");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            string name = entry.FullName;
            if (name.Contains('\\') || name.Contains(':') || name.Split('/').Any(p =>
                p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || IsDeviceName(p) || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) ||
                !names.Add(name) || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000 ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new AkochanException("AKOCHAN_ARCHIVE_PATH_INVALID");
            total = checked(total + entry.Length);
            if (entry.Length > 512L * 1024 * 1024 || total > maxBytes)
                throw new AkochanException("AKOCHAN_ARCHIVE_TOO_LARGE");
        }
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.GetFullPath(Path.Combine(stage, entry.FullName));
            if (!path.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new AkochanException("AKOCHAN_ARCHIVE_PATH_INVALID");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            EnsureNoLinks(path);
            using var source = entry.Open();
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[65536];
            long written = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                written += count;
                if (written > entry.Length) throw new AkochanException("AKOCHAN_ARCHIVE_LENGTH_INVALID");
                await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (written != entry.Length) throw new AkochanException("AKOCHAN_ARCHIVE_LENGTH_INVALID");
        }
        // Failed imports remain unselected in their unique directory for diagnostics.
        return stage;
    }

    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new AkochanException("AKOCHAN_PATH_REPARSE_POINT");
    }

    private static bool IsDeviceName(string segment)
    {
        string name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name.StartsWith("COM") || name.StartsWith("LPT")) && char.IsDigit(name[3]);
    }
}
