using Mahjong.Plugin.CN.Access;
using System.Text.Json;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class SolverPreferenceStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mjcn-preference-test-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "solver-preference.json");

    [Fact]
    public void Missing_preference_uses_standard_and_does_not_write_a_file()
    {
        var store = new SolverPreferenceStore(FilePath);
        Assert.False(store.PreferBeta);
        Assert.Null(store.Error);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Explicit_selection_round_trips_without_execution_authority_or_lease()
    {
        var store = new SolverPreferenceStore(FilePath);
        Assert.True(store.Save(true));
        Assert.True(new SolverPreferenceStore(FilePath).PreferBeta);
        using var data = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal(new[] { "PreferBeta", "SchemaVersion" }, data.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.True(store.Save(false));
        Assert.False(new SolverPreferenceStore(FilePath).PreferBeta);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("null")]
    [InlineData("{\"SchemaVersion\":2,\"PreferBeta\":true}")]
    [InlineData("{\"SchemaVersion\":1,\"PreferBeta\":true,\"AutoStart\":true}")]
    public void Unknown_or_corrupt_preference_is_preserved(string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(FilePath, content);
        var store = new SolverPreferenceStore(FilePath);
        Assert.False(store.PreferBeta);
        Assert.NotNull(store.Error);
        Assert.False(store.Save(true));
        Assert.Equal(content, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Oversized_preference_is_preserved()
    {
        Directory.CreateDirectory(directory);
        string content = new(' ', 4097);
        File.WriteAllText(FilePath, content);
        var store = new SolverPreferenceStore(FilePath);
        Assert.NotNull(store.Error);
        Assert.False(store.Save(false));
        Assert.Equal(content, File.ReadAllText(FilePath));
    }

    [Fact]
    public void Write_failure_does_not_claim_a_persisted_selection()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "blocker"), "synthetic");
        var store = new SolverPreferenceStore(Path.Combine(directory, "blocker", "preference.json"));
        Assert.False(store.Save(true));
        Assert.False(store.PreferBeta);
        Assert.NotNull(store.Error);
        Assert.DoesNotContain(directory, store.Error);
    }

    public void Dispose()
    { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
