using System.IO.Compression;
using System.Text.Json;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Journaling;
namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class DiagnosticPrivacyTests
{
    [Fact]
    public void Large_export_strings_remain_private_even_if_processing_limit_is_reached()
    {
        string text = new string('a', 500_000) + " token=synthetic-private-value C:\\Users\\SyntheticPrivateUser\\file";
        var result = DiagnosticPrivacy.Sanitize(JsonSerializer.SerializeToElement(new { Detail = text }));
        Assert.DoesNotContain("synthetic-private-value", result.GetRawText());
        Assert.DoesNotContain("SyntheticPrivateUser", result.GetRawText());
        Assert.Contains("redacted", result.GetProperty("Detail").GetString());
        Assert.Contains("synthetic-private-value", text); // Export-only; source evidence stays intact.
    }
    [Fact]
    public void Export_redacts_private_fields_and_paths_but_preserves_actual_source_and_public_links()
    {
        var value = DiagnosticPrivacy.Sanitize(JsonSerializer.SerializeToElement(new
        {
            Token = "synthetic-secret", TestCode = "synthetic-code", PlayerName = "synthetic-player",
            Error = @"worker C:\Users\SyntheticPrivateUser\runtime\worker.py",
            Backend = "Mortal V4", EngineCommit = "synthetic-commit", InputSha256 = "synthetic-input-hash",
            Source = "https://github.com/Equim-chan/Mortal", Resource = "ui/uld/emj.uld", ObservationSequence = 12,
        }));
        Assert.DoesNotContain("synthetic-secret", value.GetRawText());
        Assert.DoesNotContain("synthetic-code", value.GetRawText());
        Assert.DoesNotContain("synthetic-player", value.GetRawText());
        Assert.DoesNotContain("SyntheticPrivateUser", value.GetRawText());
        Assert.Equal("Mortal V4", value.GetProperty("Backend").GetString());
        Assert.Equal("https://github.com/Equim-chan/Mortal", value.GetProperty("Source").GetString());
        Assert.Equal("ui/uld/emj.uld", value.GetProperty("Resource").GetString());
        Assert.Equal(12, value.GetProperty("ObservationSequence").GetInt32());
    }

    [Fact]
    public async Task Compressed_private_payload_exports_as_readable_derived_chain_without_rewriting_original()
    {
        string root = Path.Combine(Path.GetTempPath(), "mjcn-privacy-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new GameJournal(root, 1024);
            for (int i = 0; i < 5; i++)
            {
                journal.Event("global_ai_status", new { Index = i, Token = new string('x', 100), Backend = "akochan v5", Error = @"C:\Users\SyntheticPrivateUser\worker.py" });
                await journal.Completion;
            }
            await journal.CompleteAsync();
            var originals = GameJournal.StreamFiles(journal.DirectoryPath, "events.jsonl").ToDictionary(x => x, File.ReadAllBytes);
            string zip = await journal.ExportAsync(Path.Combine(root, "export.zip"));
            string unpack = Path.Combine(root, "unpack"); ZipFile.ExtractToDirectory(zip, unpack);
            var replay = await GameJournal.ReadAsync(Path.Combine(unpack, "events.jsonl"));
            Assert.True(replay.IntegrityPassed, replay.Error); Assert.Equal(5, replay.Lines.Length);
            foreach (var line in replay.Lines)
            {
                Assert.Equal("[redacted]", line.Entry.Data.GetProperty("Token").GetString());
                Assert.Equal("akochan v5", line.Entry.Data.GetProperty("Backend").GetString());
                Assert.DoesNotContain("SyntheticPrivateUser", line.Entry.Data.GetRawText());
            }
            foreach (var original in originals) Assert.Equal(original.Value, File.ReadAllBytes(original.Key));
            Assert.Contains("DerivedSanitizedChain", File.ReadAllText(Path.Combine(unpack, "export-privacy.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
