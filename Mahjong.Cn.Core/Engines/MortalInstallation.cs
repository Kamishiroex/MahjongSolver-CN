using System.Security.Cryptography;
using System.Text.Json;

namespace Mahjong.Cn.Engines;

public sealed record MortalInstallation(string Directory)
{
    public const string Commit = "0cff2b52982be5b1163aa9a62fb01f03ce91e0d2";
    public const string Bridge = "mjcn-mortal-public-v1";
    public const string FeatureBridge = "mjcn-mortal-public-v2";
    public bool HasRuleAwareFeatures { get; init; }
    public const string ModelHash = "738e0d6e3c0ce9671629554ad39abd147d2ffbac676e80b194c83f2acc0fea20";
    public static async Task<MortalInstallation> LoadAsync(string directory, CancellationToken token = default)
    {
        string root = Path.GetFullPath(directory);
        string file = Path.Combine(root, "mortal-installation.json");
        if (!File.Exists(file) || new FileInfo(file).Length > 8 * 1024 * 1024)
            throw new AkochanException("MORTAL_INSTALLATION_MISSING");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(file, token).ConfigureAwait(false));
        var m = manifest.RootElement;
        string? bridge = m.GetProperty("bridge").GetString();
        if (bridge is not (Bridge or FeatureBridge) || m.GetProperty("source_commit").GetString() != Commit ||
            m.GetProperty("model_sha256").GetString() != ModelHash)
            throw new AkochanException("MORTAL_INSTALLATION_VERSION");
        var files = m.GetProperty("files").EnumerateObject().ToArray();
        if (files.Length is 0 or > 20000) throw new AkochanException("MORTAL_INVENTORY_INVALID");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files)
        {
            token.ThrowIfCancellationRequested();
            string name = item.Name;
            if (!names.Add(name) || name.Contains('\\') || name.Contains(':') || name.Split('/').Any(s => s is "" or "." or ".."))
                throw new AkochanException("MORTAL_PATH_INVALID");
            string path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new AkochanException("MORTAL_FILE_MISSING:" + name);
            for (string? p = path; p is not null; p = Path.GetDirectoryName(p))
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new AkochanException("MORTAL_PATH_REPARSE_POINT");
            await using var stream = File.OpenRead(path);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
            if (!hash.Equals(item.Value.GetString(), StringComparison.OrdinalIgnoreCase))
                throw new AkochanException("MORTAL_FILE_HASH:" + name);
        }
        foreach (string name in new[] { "python/python.exe", "python/python313.dll", "runtime/libriichi.pyd", "snapshot.py", "runtime.py", "runtime.json", "mortal_582500.pth" })
            if (!names.Contains(name)) throw new AkochanException("MORTAL_REQUIRED_FILE:" + name);
        if (!m.GetProperty("files").GetProperty("mortal_582500.pth").GetString()!.Equals(ModelHash, StringComparison.OrdinalIgnoreCase))
            throw new AkochanException("MORTAL_MODEL_HASH");
        foreach (string path in System.IO.Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string name = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (name != "mortal-installation.json" && !names.Contains(name)) throw new AkochanException("MORTAL_UNEXPECTED_FILE:" + name);
        }
        if (bridge == FeatureBridge && !names.Contains("rules.py"))
            throw new AkochanException("MORTAL_REQUIRED_FILE:rules.py");
        return new(root) { HasRuleAwareFeatures = bridge == FeatureBridge };
    }

    public static async Task<MortalInstallation> ImportAsync(string archive, string enginesRoot, CancellationToken token = default)
    {
        string stage = await AkochanTransfer.ExtractAsync(archive, enginesRoot, 20000, 2L * 1024 * 1024 * 1024, token).ConfigureAwait(false);
        return await LoadAsync(stage, token).ConfigureAwait(false);
    }
}
