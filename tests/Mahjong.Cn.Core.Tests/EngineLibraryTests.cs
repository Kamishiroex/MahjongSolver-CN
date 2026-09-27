using System.Text.Json;
using Mahjong.Cn.Engines;
using Xunit;

namespace Mahjong.Cn.Core.Tests;

public sealed class EngineLibraryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mjcn-library-" + Guid.NewGuid().ToString("N"));
    public EngineLibraryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void Multiple_models_survive_save_reload_and_selection_without_losing_previous_imports()
    {
        var profiles = Enumerable.Range(0, 4).Select(i => new EngineProfile("model-" + i, "Model " + i,
            i % 2 == 0 ? "Mortal" : "Akochan", Path.Combine(root, "engine-" + i))).ToArray();
        foreach (var selected in profiles)
        {
            EngineLibrary.Save(root, new(selected.Id, profiles));
            var loaded = EngineLibrary.Load(root);
            Assert.Equal(selected.Id, loaded.SelectedId); Assert.Equal(profiles, loaded.Profiles);
        }
        var renamed = profiles[0] with { Name = "Renamed model" };
        Assert.Equal(4, EngineLibrary.Merge(profiles, renamed).Length);
        Assert.Equal("Renamed model", EngineLibrary.Merge(profiles, renamed).Single(p => p.Id == renamed.Id).Name);
    }

    [Theory]
    [InlineData("../outside.zip", "Mortal")]
    [InlineData("C:outside.zip", "Mortal")]
    [InlineData("engine.zip", "Unknown")]
    public async Task Bundle_rejects_unsafe_paths_and_unknown_backend_before_import(string archive, string backend)
    {
        File.WriteAllText(Path.Combine(root, "bundled-engines.json"), JsonSerializer.Serialize(new[]
        { new BundledEngine("model", "Model", backend, archive, new string('0', 64)) }));
        var ex = await Assert.ThrowsAsync<AkochanException>(() => EngineLibrary.PrepareBundledAsync(root, Path.Combine(root, "config"), []));
        Assert.Equal("ENGINE_BUNDLE_INVALID", ex.Code);
    }

    [Fact]
    public async Task Bundle_hash_mismatch_leaves_saved_selection_unchanged()
    {
        var profile = new EngineProfile("existing", "Existing", "Mortal", Path.Combine(root, "existing"));
        EngineLibrary.Save(root, new(profile.Id, [profile]));
        File.WriteAllText(Path.Combine(root, "engine.zip"), "corrupt");
        File.WriteAllText(Path.Combine(root, "bundled-engines.json"), JsonSerializer.Serialize(new[]
        { new BundledEngine("new", "New", "Mortal", "engine.zip", new string('0', 64)) }));
        var ex = await Assert.ThrowsAsync<AkochanException>(() => EngineLibrary.PrepareBundledAsync(root, root, [profile]));
        Assert.Equal("ENGINE_BUNDLE_HASH", ex.Code);
        Assert.Equal(profile.Id, EngineLibrary.Load(root).SelectedId);
        Assert.Single(EngineLibrary.Load(root).Profiles);
    }
    public void Dispose() => Directory.Delete(root, true);
}
