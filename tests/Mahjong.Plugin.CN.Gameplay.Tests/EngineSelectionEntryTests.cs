using System.Reflection;
using System.Runtime.CompilerServices;
using Mahjong.Cn.Engines;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class EngineSelectionEntryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mjcn-engine-entry-" + Guid.NewGuid().ToString("N"));
    public EngineSelectionEntryTests() => Directory.CreateDirectory(root);

    private Plugin Entry(bool mortal)
    {
        var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
        Set(plugin, "selectedEngineDirectory", root);
        Set(plugin, "<MortalSelected>k__BackingField", mortal);
        Set(plugin, "experimentalHandAiEnabled", 1);
        var access = new Mahjong.Plugin.CN.Access.TestCodeAccess(Path.Combine(root, "synthetic-access.json"), null,
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("synthetic-engine-test")));
        Assert.True(access.TryUnlock("synthetic-engine-test")); Set(plugin, "testAccess", access);
        return plugin;
    }
    private static void Set(object target, string field, object value) =>
        target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    [Theory]
    [InlineData(true, "mortal-installation.json")]
    [InlineData(false, "akochan-installation.json")]
    public void Actual_activation_gate_accepts_selected_backend_without_requiring_other_backend(bool mortal, string manifest)
    {
        File.WriteAllText(Path.Combine(root, manifest), "{}");
        var plugin = Entry(mortal);
        Assert.Null(plugin.EngineActivationError);
        // The actual policy factory must route to the corresponding source, too.
        using var policy = (IDisposable)plugin.CreateDecisionPolicy();
        string commit = (string)policy.GetType().GetField("expectedCommit", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(policy)!;
        Assert.Equal(mortal ? MortalInstallation.Commit : AkochanInstallation.ExpectedCommit, commit);
    }

    [Theory]
    [InlineData(true, "akochan-installation.json")]
    [InlineData(false, "mortal-installation.json")]
    public void Manifest_of_other_engine_does_not_unlock_selected_engine(bool mortal, string wrongManifest)
    {
        File.WriteAllText(Path.Combine(root, wrongManifest), "{}");
        Assert.NotNull(Entry(mortal).EngineActivationError);
    }

    [Fact]
    public void Selecting_current_model_is_a_noop_and_does_not_stop_or_rearm_play()
    {
        var plugin = Entry(true);
        Set(plugin, "<SelectedEngineId>k__BackingField", "mortal");
        Set(plugin, "gameplayAllowed", true);
        plugin.SelectInstalledEngine("mortal"); // No services: would fail if it tried Stop/check/init.
        Assert.True((bool)typeof(Plugin).GetField("gameplayAllowed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(plugin)!);
    }

    public void Dispose() => Directory.Delete(root, true);
}
