using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mahjong.Cn.Engines;

public sealed class AkochanException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>A locally built, pinned executable. No downloading or installation occurs in the plugin.</summary>
public sealed record AkochanInstallation
{
    public string Directory { get; }
    public string Executable { get; }
    public string Tactics { get; }
    public string SourceCommit { get; }
    public string ManifestSha256 { get; }
    public bool HasKanDoraCounterFix { get; }
    public bool HasGlobalSnapshotBridge { get; }
    public bool HasWinContextBridge { get; }
    public bool HasPartialHistoryFuritenFix { get; }
    public bool HasDoubleRiichiBridge { get; }
    public bool HasObservedFuritenBridge { get; }
    private AkochanInstallation(string directory, string executable, string tactics, string sourceCommit, string manifestSha256, bool kanDoraCounterFix, bool globalSnapshotBridge, bool winContextBridge, bool partialHistoryFuritenFix, bool doubleRiichiBridge, bool observedFuritenBridge)
    { Directory = directory; Executable = executable; Tactics = tactics; SourceCommit = sourceCommit; ManifestSha256 = manifestSha256;
      HasKanDoraCounterFix = kanDoraCounterFix; HasGlobalSnapshotBridge = globalSnapshotBridge; HasWinContextBridge = winContextBridge;
      HasPartialHistoryFuritenFix = partialHistoryFuritenFix; HasDoubleRiichiBridge = doubleRiichiBridge; HasObservedFuritenBridge = observedFuritenBridge; }
    public const string ExpectedCommit = "53188a0b926fbab38177f88c3cd87d554cf412af";
    public const string KanDoraPatchId = "mjcn-kan-dora-rinshan-v1";
    public const string KanDoraOriginalSha256 = "a0bac9e40b13e99b9b9e30dd5fd43b71de6225505a118a538cf0915b3a243076";
    public const string KanDoraPatchedSha256 = "dd77887b85aa7409a3141011e43012b2c1b85ac7d9b5fc70062e1c7089ac05f4";
    public const string Repository = "https://github.com/critter-mj/akochan";
    public const string GlobalPatchId = "mjcn-public-snapshot-v1";
    public const string GlobalTypesSha256 = "a4bbc79bdf37c7108f4af71c6c39c691d95af6ef0b1913ee662a42cdafb3f1bb";
    public const string GlobalMjutilSha256 = "c6132071160a95ffe1961267c228a6d3193586d620defa8fcd0d5c7f93c64c2a";
    public const string GlobalBridgeSha256 = "15fea4c64ac558285405d830eebc4fdb1ed2ac3fcfaf10ba8bf5b9983741db6b";
    public const string WinContextPatchId = "mjcn-public-snapshot-v2";
    public const string WinContextBridgeSha256 = "ec0168f32542046d5033ddd900b2f3b227e8f54e80d5f6e6150c988779386d26";
    public const string WinContextSelectorSha256 = "cf8e37bacf6dcf403ad50f0aa8ee23918e7e9a3134a088f628f3a43920fb1e89";
    public const string PartialHistoryPatchId = "mjcn-public-snapshot-v3";
    public const string PartialHistoryTypesSha256 = "568ff21dc05765d6c927414ef0aed15eeb3b9ff8347260c463c6b189c84f042d";
    public const string PartialHistoryBridgeSha256 = "d43c9d2fa8b592d30c910db346212578ad16a39ec21ca9f41eb6a9b2250ac79d";
    public const string DoubleRiichiPatchId = "mjcn-public-snapshot-v4";
    public const string DoubleRiichiBridgeSha256 = "2ecb9f1f4e7d258836acc1aa9f09090839c68bdc48ba9dc0b82beea4a96d0ce9";
    public const string DoubleRiichiSelectorSha256 = "24ffcd35083e7b26be119037b905b05448e43be054c2e9fce8b4e9a85ed1c7e5";
    public const string ObservedFuritenPatchId = "mjcn-public-snapshot-v5";
    public const string ObservedFuritenTypesSha256 = "2e0ff3f5eea5daa36442ae94bc8138dfa80e39a10c3977271ed721ba54da8124";
    public const string ObservedFuritenBridgeSha256 = "9ae42006991a5e2c4f23bd3ea7dd4157e77e7882a6bb9e6b9fab4bb55a0bcb9b";
    public static ImmutableArray<string> ExpectedParameterFiles { get; } = ReadParameterFiles();

    public static async Task<AkochanInstallation> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        try { return await LoadCoreAsync(directory, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new AkochanException("AKOCHAN_MANIFEST_INVALID"); }
    }

    private static async Task<AkochanInstallation> LoadCoreAsync(string directory, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new AkochanException("AKOCHAN_PATH_REQUIRED");
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string manifestPath = SafePath(root, "akochan-installation.json");
        if (!File.Exists(manifestPath)) throw new AkochanException("AKOCHAN_NOT_INSTALLED");
        byte[] bytes;
        await using (var manifest = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
        {
            if (manifest.Length > 512 * 1024) throw new AkochanException("AKOCHAN_MANIFEST_TOO_LARGE");
            bytes = new byte[checked((int)manifest.Length)];
            await manifest.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var value = json.RootElement;
        var rootKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!rootKeys.Add(property.Name)) throw new AkochanException("AKOCHAN_MANIFEST_INVALID");
        if (value.GetProperty("schema").GetInt32() != 1 || value.GetProperty("threads").GetInt32() != 2 ||
            value.GetProperty("sourceCommit").GetString() != ExpectedCommit ||
            value.GetProperty("sourceRepository").GetString() != Repository)
            throw new AkochanException("AKOCHAN_IDENTITY_MISMATCH");
        bool kanDoraFix = ReadKanDoraPatch(value);
        bool globalSnapshot = ReadGlobalPatch(value, out bool winContext, out bool partialHistory, out bool doubleRiichi, out bool observedFuriten);
        if (globalSnapshot && !kanDoraFix) throw new AkochanException("AKOCHAN_GLOBAL_PATCH_IDENTITY_MISMATCH");
        string executableName = value.GetProperty("executable").GetString() ?? "";
        string tacticsName = value.GetProperty("tactics").GetString() ?? "";
        var files = value.GetProperty("files");
        if (files.ValueKind != JsonValueKind.Object) throw new AkochanException("AKOCHAN_MANIFEST_INVALID");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in files.EnumerateObject())
        {
            if (!paths.Add(file.Name) || paths.Count > 2048) throw new AkochanException("AKOCHAN_MANIFEST_INVALID");
            string path = SafePath(root, file.Name);
            if (!File.Exists(path)) throw new AkochanException("AKOCHAN_FILE_MISSING");
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            long length = stream.Length;
            totalBytes = checked(totalBytes + length);
            if (length > 512L * 1024 * 1024 || totalBytes > 1024L * 1024 * 1024)
                throw new AkochanException("AKOCHAN_FILES_TOO_LARGE");
            string expected = file.Value.GetString() ?? "";
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new AkochanException("AKOCHAN_HASH_INVALID");
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!hash.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new AkochanException("AKOCHAN_FILE_HASH_MISMATCH");
        }
        if (!paths.Contains(executableName) || !paths.Contains(tacticsName) || !paths.Contains("LICENSE") ||
            ExpectedParameterFiles.Any(x => !paths.Contains(x)))
            throw new AkochanException("AKOCHAN_MANIFEST_INCOMPLETE");
        // Do not let an unlisted native parameter or DLL evade the checksum inventory.
        var directories = new Stack<string>();
        directories.Push(root);
        int entryCount = 0;
        while (directories.TryPop(out var currentDirectory))
            foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(currentDirectory))
            {
                if (++entryCount > 4096 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new AkochanException("AKOCHAN_INVENTORY_INVALID");
                if (System.IO.Directory.Exists(path)) directories.Push(path);
                else
                {
                    string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (relative != "akochan-installation.json" && !paths.Contains(relative))
                        throw new AkochanException("AKOCHAN_UNLISTED_FILE");
                }
            }
        return new(root, SafePath(root, executableName), SafePath(root, tacticsName), ExpectedCommit,
            Convert.ToHexString(SHA256.HashData(bytes)), kanDoraFix, globalSnapshot, winContext, partialHistory, doubleRiichi, observedFuriten);
    }

    private static bool ReadGlobalPatch(JsonElement manifest, out bool winContext, out bool partialHistory, out bool doubleRiichi, out bool observedFuriten)
    {
        winContext = partialHistory = doubleRiichi = observedFuriten = false;
        if (!manifest.TryGetProperty("globalSnapshot", out var patch)) return false;
        if (patch.ValueKind != JsonValueKind.Object) throw new AkochanException("AKOCHAN_GLOBAL_PATCH_IDENTITY_MISMATCH");
        observedFuriten = patch.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == ObservedFuritenPatchId;
        doubleRiichi = observedFuriten || id.ValueKind == JsonValueKind.String && id.GetString() == DoubleRiichiPatchId;
        partialHistory = doubleRiichi || id.ValueKind == JsonValueKind.String && id.GetString() == PartialHistoryPatchId;
        winContext = partialHistory || id.ValueKind == JsonValueKind.String && id.GetString() == WinContextPatchId;
        string[] keys = ["id", "typesSha256", "mjutilSha256", "bridgeSha256", "patchScriptSha256"];
        if (winContext) keys = [.. keys, "selectorSha256"];
        if (patch.ValueKind != JsonValueKind.Object || patch.EnumerateObject().Count() != keys.Length ||
            patch.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != keys.Length ||
            patch.EnumerateObject().Any(p => !keys.Contains(p.Name) || p.Value.ValueKind != JsonValueKind.String) ||
            patch.GetProperty("id").GetString() != (observedFuriten ? ObservedFuritenPatchId : doubleRiichi ? DoubleRiichiPatchId : partialHistory ? PartialHistoryPatchId : winContext ? WinContextPatchId : GlobalPatchId) ||
            patch.GetProperty("typesSha256").GetString() != (observedFuriten ? ObservedFuritenTypesSha256 : partialHistory ? PartialHistoryTypesSha256 : GlobalTypesSha256) ||
            patch.GetProperty("mjutilSha256").GetString() != GlobalMjutilSha256 ||
            patch.GetProperty("bridgeSha256").GetString() != (observedFuriten ? ObservedFuritenBridgeSha256 : doubleRiichi ? DoubleRiichiBridgeSha256 : partialHistory ? PartialHistoryBridgeSha256 : winContext ? WinContextBridgeSha256 : GlobalBridgeSha256) ||
            winContext && patch.GetProperty("selectorSha256").GetString() != (doubleRiichi ? DoubleRiichiSelectorSha256 : WinContextSelectorSha256) ||
            patch.GetProperty("patchScriptSha256").GetString() is not { Length: 64 } scriptHash || !scriptHash.All(Uri.IsHexDigit))
            throw new AkochanException("AKOCHAN_GLOBAL_PATCH_IDENTITY_MISMATCH");
        return true;
    }

    private static bool ReadKanDoraPatch(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("localPatches", out var patches)) return false;
        if (patches.ValueKind != JsonValueKind.Array || patches.GetArrayLength() > 1)
            throw new AkochanException("AKOCHAN_PATCH_IDENTITY_MISMATCH");
        if (patches.GetArrayLength() == 0) return false;
        var patch = patches[0];
        string[] keys = ["id", "sourceFile", "sourceCommit", "originalLfSha256", "patchedSha256", "patchScriptSha256"];
        if (patch.ValueKind != JsonValueKind.Object || patch.EnumerateObject().Count() != keys.Length ||
            patch.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != keys.Length ||
            patch.EnumerateObject().Any(p => !keys.Contains(p.Name) || p.Value.ValueKind != JsonValueKind.String) ||
            patch.GetProperty("id").GetString() != KanDoraPatchId ||
            patch.GetProperty("sourceFile").GetString() != "share/types.cpp" ||
            patch.GetProperty("sourceCommit").GetString() != ExpectedCommit ||
            patch.GetProperty("originalLfSha256").GetString() != KanDoraOriginalSha256 ||
            patch.GetProperty("patchedSha256").GetString() != KanDoraPatchedSha256 ||
            patch.GetProperty("patchScriptSha256").GetString() is not { Length: 64 } scriptHash || !scriptHash.All(Uri.IsHexDigit))
            throw new AkochanException("AKOCHAN_PATCH_IDENTITY_MISMATCH");
        return true;
    }

    private static ImmutableArray<string> ReadParameterFiles()
    {
        using var stream = typeof(AkochanInstallation).Assembly.GetManifestResourceStream("akochan-parameter-files.json")!;
        return JsonSerializer.Deserialize<ImmutableArray<string>>(stream);
    }

    private static string SafePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') ||
            relative.Split('/', '\\').Any(p => p is ".." or "." or ""))
            throw new AkochanException("AKOCHAN_PATH_INVALID");
        string full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new AkochanException("AKOCHAN_PATH_INVALID");
        string? current = full;
        while (current is not null)
        {
            if ((File.Exists(current) || System.IO.Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new AkochanException("AKOCHAN_PATH_REPARSE_POINT");
            if (current.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
        return full;
    }
}
