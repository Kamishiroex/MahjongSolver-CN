using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Plugin.CN.Access;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class TestCodeAccessTests : IDisposable
{
    private const string SyntheticCode = "synthetic-offline-test-code";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mjcn-access-tests-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
    private string RecordPath => Path.Combine(directory, "test-access.json");
    private static byte[] SyntheticDigest => SHA256.HashData(Encoding.UTF8.GetBytes(SyntheticCode));
    private TestCodeAccess Create() => new(RecordPath, () => now, SyntheticDigest);

    [Fact]
    public void New_access_is_locked_and_empty_or_incorrect_input_creates_no_record()
    {
        var access = Create();
        Assert.False(access.IsUnlocked);
        Assert.Null(access.ExpiresAtUtc);
        Assert.False(access.TryUnlock(null));
        Assert.False(access.TryUnlock("   "));
        Assert.False(access.TryUnlock("not-the-code"));
        Assert.False(File.Exists(RecordPath));
        Assert.DoesNotContain("not-the-code", access.StatusMessage);
    }

    [Fact]
    public void Successful_trimmed_input_survives_restart_and_only_persists_three_lease_fields()
    {
        var access = Create();
        Assert.True(access.TryUnlock(" \t" + SyntheticCode + "\r\n"));
        Assert.True(access.IsUnlocked);
        Assert.Equal(now.AddDays(7), access.ExpiresAtUtc);
        using var json = JsonDocument.Parse(File.ReadAllText(RecordPath));
        Assert.Equal(new[] { "ExpiresAtUtc", "IssuedAtUtc", "SchemaVersion" },
            json.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(name => name));
        string saved = json.RootElement.GetRawText();
        Assert.DoesNotContain(SyntheticCode, saved);
        Assert.DoesNotContain(Convert.ToHexString(SyntheticDigest), saved);
        Assert.DoesNotContain(SyntheticCode, access.StatusMessage);

        now = now.AddDays(2);
        var restarted = Create();
        Assert.True(restarted.IsUnlocked);
        Assert.Equal(access.ExpiresAtUtc, restarted.ExpiresAtUtc);
    }

    [Fact]
    public void Exact_seven_day_expiry_locks_and_valid_code_is_required_for_a_new_period()
    {
        var access = Create();
        Assert.True(access.TryUnlock(SyntheticCode));
        DateTimeOffset expiry = access.ExpiresAtUtc!.Value;
        now = expiry.AddTicks(-1);
        Assert.True(access.IsUnlocked);
        now = expiry;
        Assert.False(access.IsUnlocked);
        Assert.False(Create().IsUnlocked);
        Assert.Contains("到期", access.StatusMessage);
        Assert.False(access.TryUnlock("incorrect"));
        Assert.Contains("不正确", access.StatusMessage);
        Assert.Equal(expiry, access.ExpiresAtUtc);
        Assert.True(access.TryUnlock(SyntheticCode));
        Assert.Equal(expiry.AddDays(7), access.ExpiresAtUtc);
        Assert.True(Create().IsUnlocked);
    }

    [Fact]
    public void Repeat_validation_and_invalid_attempts_do_not_extend_active_authorization()
    {
        var access = Create();
        Assert.True(access.TryUnlock(SyntheticCode));
        string originalRecord = File.ReadAllText(RecordPath);
        DateTimeOffset expiry = access.ExpiresAtUtc!.Value;
        now = now.AddDays(6);
        Assert.True(access.TryUnlock(SyntheticCode));
        Assert.False(access.TryUnlock("incorrect"));
        Assert.True(access.IsUnlocked);
        Assert.Equal(expiry, access.ExpiresAtUtc);
        Assert.Equal(originalRecord, File.ReadAllText(RecordPath));
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("oversized")]
    [InlineData("schema")]
    [InlineData("future")]
    [InlineData("expired")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("extra-field")]
    public void Invalid_persisted_records_fail_closed(string kind)
    {
        Directory.CreateDirectory(directory);
        var issued = now.AddDays(-1);
        var expiry = issued.AddDays(7);
        int schema = 1;
        switch (kind)
        {
            case "schema": schema = 2; break;
            case "future": issued = now.AddMinutes(1); expiry = issued.AddDays(7); break;
            case "expired": issued = now.AddDays(-7); expiry = now; break;
            case "short": expiry = issued.AddDays(6); break;
            case "long": expiry = issued.AddDays(8); break;
        }
        string record = kind switch
        {
            "broken" => "{not-json",
            "oversized" => new string(' ', 4097),
            "extra-field" => JsonSerializer.Serialize(new { SchemaVersion = schema, IssuedAtUtc = issued,
                ExpiresAtUtc = expiry, Unexpected = "private-marker" }),
            _ => JsonSerializer.Serialize(new { SchemaVersion = schema, IssuedAtUtc = issued, ExpiresAtUtc = expiry }),
        };
        File.WriteAllText(RecordPath, record);
        var access = Create();
        Assert.False(access.IsUnlocked);
        Assert.DoesNotContain("private-marker", access.StatusMessage);
    }

    [Fact]
    public void Save_failure_never_grants_access_and_removes_temporary_record()
    {
        Directory.CreateDirectory(RecordPath); // A directory at the destination makes the atomic rename fail.
        var access = Create();
        Assert.False(access.TryUnlock(SyntheticCode));
        Assert.False(access.IsUnlocked);
        Assert.Null(access.ExpiresAtUtc);
        Assert.Contains("无法保存", access.StatusMessage);
        Assert.DoesNotContain(SyntheticCode, access.StatusMessage);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void Failed_renewal_keeps_access_locked_and_preserves_the_save_error()
    {
        var access = Create();
        Assert.True(access.TryUnlock(SyntheticCode));
        now = access.ExpiresAtUtc!.Value;
        File.Delete(RecordPath);
        Directory.CreateDirectory(RecordPath);
        Assert.False(access.TryUnlock(SyntheticCode));
        Assert.False(access.IsUnlocked);
        Assert.Contains("无法保存", access.StatusMessage);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void Moving_the_clock_before_issue_time_temporarily_locks_access()
    {
        var access = Create();
        Assert.True(access.TryUnlock(SyntheticCode));
        now = now.AddTicks(-1);
        Assert.False(access.IsUnlocked);
        Assert.Contains("早于", access.StatusMessage);
        Assert.False(Create().IsUnlocked);
    }

    [Fact]
    public void Injected_digest_is_defensively_copied()
    {
        var digest = SyntheticDigest;
        var access = new TestCodeAccess(RecordPath, () => now, digest);
        Array.Clear(digest);
        Assert.True(access.TryUnlock(SyntheticCode));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
