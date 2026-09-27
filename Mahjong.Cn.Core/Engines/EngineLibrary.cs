using System.Security.Cryptography;
using System.Text.Json;

namespace Mahjong.Cn.Engines;

public sealed record EngineProfile(string Id, string Name, string Backend, string Directory)
{
    public bool IsMortal => Backend == "Mortal";
    public string ManifestName => IsMortal ? "mortal-installation.json" : "akochan-installation.json";
    public bool IsInstalled => Backend is "Mortal" or "Akochan" && File.Exists(Path.Combine(Directory, ManifestName));
    public static EngineProfile FromDirectory(string directory, string? name = null)
    {
        directory = Path.GetFullPath(directory);
        bool mortal = File.Exists(Path.Combine(directory, "mortal-installation.json"));
        string id = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(directory.ToUpperInvariant())))[..20];
        return new(id, string.IsNullOrWhiteSpace(name) ? mortal ? "凡夫 Mortal V4" : "akochan v5" : name.Trim()[..Math.Min(name.Trim().Length, 80)],
            mortal ? "Mortal" : "Akochan", directory);
    }

    public async Task ValidateAsync(CancellationToken token = default)
    {
        if (Backend == "Mortal") await MortalInstallation.LoadAsync(Directory, token).ConfigureAwait(false);
        else if (Backend == "Akochan") await AkochanTransfer.CheckAsync(Directory, token).ConfigureAwait(false);
        else throw new AkochanException("ENGINE_BACKEND_UNSUPPORTED");
    }
}

public sealed record EngineLibraryState(string? SelectedId, EngineProfile[] Profiles);
public sealed record BundledEngine(string Id, string Name, string Backend, string Archive, string Sha256);

/// <summary>Persistent multi-installation catalog. Registration never authorizes game input.</summary>
public static class EngineLibrary
{
    public static async Task<EngineProfile[]> ImportBundleAsync(string archive, string configRoot,
        EngineProfile[] existing, CancellationToken token = default)
    {
        string stage = await AkochanTransfer.ExtractAsync(archive, Path.Combine(configRoot, "engine-bundles"),
            34, 2L * 1024 * 1024 * 1024, token).ConfigureAwait(false);
        if (!File.Exists(Path.Combine(stage, "bundled-engines.json"))) throw new AkochanException("ENGINE_BUNDLE_INVALID");
        return await PrepareBundledAsync(stage, configRoot, existing, token).ConfigureAwait(false);
    }

    public static EngineLibraryState Load(string configRoot)
    {
        string file = Path.Combine(configRoot, "engine-library.json");
        if (!File.Exists(file)) return new(null, []);
        if (new FileInfo(file).Length > 128 * 1024) throw new AkochanException("ENGINE_LIBRARY_TOO_LARGE");
        var state = JsonSerializer.Deserialize<EngineLibraryState>(File.ReadAllText(file)) ?? throw new AkochanException("ENGINE_LIBRARY_INVALID");
        if (state.Profiles is null || state.Profiles.Length > 128 || state.Profiles.Any(p => p is null ||
                string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 80 || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80 ||
                p.Backend is not ("Mortal" or "Akochan") || !Path.IsPathFullyQualified(p.Directory)) ||
            state.Profiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != state.Profiles.Length)
            throw new AkochanException("ENGINE_LIBRARY_INVALID");
        return state;
    }

    public static void Save(string configRoot, EngineLibraryState state)
    {
        System.IO.Directory.CreateDirectory(configRoot);
        string file = Path.Combine(configRoot, "engine-library.json"), temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, file, true);
    }

    public static EngineProfile[] Merge(IEnumerable<EngineProfile> current, params EngineProfile[] added)
    {
        var result = current.ToList();
        foreach (var item in added)
        {
            result.RemoveAll(p => p.Id == item.Id || string.Equals(p.Directory, item.Directory, StringComparison.OrdinalIgnoreCase));
            result.Add(item);
        }
        if (result.Count > 128) throw new AkochanException("ENGINE_LIBRARY_FULL");
        return result.ToArray();
    }

    public static async Task<EngineProfile[]> PrepareBundledAsync(string bundleRoot, string configRoot,
        EngineProfile[] existing, CancellationToken token = default)
    {
        string manifest = Path.Combine(bundleRoot, "bundled-engines.json");
        if (!File.Exists(manifest)) return existing;
        if (new FileInfo(manifest).Length > 32768) throw new AkochanException("ENGINE_BUNDLE_INVALID");
        var packages = JsonSerializer.Deserialize<BundledEngine[]>(await File.ReadAllTextAsync(manifest, token).ConfigureAwait(false));
        if (packages is null || packages.Length is 0 or > 32 || packages.Any(p => p is null ||
                string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 80 || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 80 ||
                p.Backend is not ("Mortal" or "Akochan") || Path.GetFileName(p.Archive) != p.Archive ||
                p.Archive.Contains(':') || !p.Archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                p.Sha256.Length != 64 || !p.Sha256.All(Uri.IsHexDigit)) || packages.Select(p => p.Id).Distinct().Count() != packages.Length)
            throw new AkochanException("ENGINE_BUNDLE_INVALID");
        foreach (var package in packages)
        {
            token.ThrowIfCancellationRequested();
            // Persisted inventory is validated again, including all runtime dependencies.
            var previous = existing.FirstOrDefault(p => p.Id == package.Id && p.Backend == package.Backend);
            if (previous is not null)
            {
                try
                {
                    await previous.ValidateAsync(token).ConfigureAwait(false);
                    existing = Merge(existing, previous);
                    continue;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* Reinstall from the verified bundled copy. */ }
            }
            string archive = Path.Combine(bundleRoot, package.Archive);
            await using (var stream = File.OpenRead(archive))
                if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(package.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new AkochanException("ENGINE_BUNDLE_HASH");
            string engines = Path.Combine(configRoot, "engines");
            string directory = package.Backend == "Mortal"
                ? (await MortalInstallation.ImportAsync(archive, engines, token).ConfigureAwait(false)).Directory
                : (await AkochanTransfer.ImportAsync(archive, engines, token).ConfigureAwait(false)).Directory;
            existing = Merge(existing, new EngineProfile(package.Id, package.Name, package.Backend, directory));
            // Save each completed package so interrupted first-run setup resumes.
            var saved = Load(configRoot);
            Save(configRoot, new(saved.SelectedId, existing));
        }
        return existing;
    }
}
