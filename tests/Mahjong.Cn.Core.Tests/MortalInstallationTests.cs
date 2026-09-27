using System.IO.Compression;
using System.Text.Json;
using Mahjong.Cn.Engines;
using Xunit;

namespace Mahjong.Cn.Core.Tests;

public sealed class MortalInstallationTests
{
    [Fact]
    public async Task Wrong_model_or_bridge_is_never_selected()
    {
        string root = Path.Combine(Path.GetTempPath(), "mjcn-mortal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "mortal-installation.json"), JsonSerializer.Serialize(new
                { bridge = "old", source_commit = MortalInstallation.Commit, model_sha256 = MortalInstallation.ModelHash }));
            var ex = await Assert.ThrowsAsync<AkochanException>(() => MortalInstallation.LoadAsync(root));
            Assert.Equal("MORTAL_INSTALLATION_VERSION", ex.Code);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../escape.dll")] [InlineData("python/../escape.dll")]
    [InlineData("python/CON.dll")] [InlineData("python/a:b.dll")]
    public async Task Import_rejects_unsafe_paths_before_writing_files(string name)
    {
        string root = Path.Combine(Path.GetTempPath(), "mjcn-mortal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string zip = Path.Combine(root, "bad.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write("unsafe"); }
            var ex = await Assert.ThrowsAsync<AkochanException>(() => MortalInstallation.ImportAsync(zip, Path.Combine(root, "engines")));
            Assert.Equal("AKOCHAN_ARCHIVE_PATH_INVALID", ex.Code);
            Assert.False(File.Exists(Path.Combine(root, "escape.dll")));
        }
        finally { Directory.Delete(root, true); }
    }
}
