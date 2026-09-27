using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mahjong.Plugin.CN.Access;

/// <summary>A local seven-day test lease. This is not an online or tamper-resistant license.</summary>
internal sealed class TestCodeAccess
{
    private const string ExpectedDigestHex = "F1AD49099105CBAB3D1D93DFC92DF20759EC71744F81728359943822B777E8D7";
    private const int MaxRecordBytes = 4096;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions RecordJson = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly object gate = new();
    private readonly string recordPath;
    private readonly Func<DateTimeOffset> clock;
    private readonly byte[] expectedDigest;
    private LeaseRecord? lease;
    private string statusMessage = "尚未验证测试码。";
    private string? lastVerificationError;

    internal TestCodeAccess(string recordPath, Func<DateTimeOffset>? clock = null)
        : this(recordPath, clock, Convert.FromHexString(ExpectedDigestHex)) { }

    /// <summary>Inject a synthetic digest for tests; the supplied digest is defensively copied.</summary>
    internal TestCodeAccess(string recordPath, Func<DateTimeOffset>? clock, byte[] expectedDigest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recordPath);
        ArgumentNullException.ThrowIfNull(expectedDigest);
        if (expectedDigest.Length != SHA256.HashSizeInBytes)
            throw new ArgumentException("测试摘要长度无效。", nameof(expectedDigest));
        this.recordPath = recordPath;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.expectedDigest = (byte[])expectedDigest.Clone();
        LoadRecord();
    }

    internal bool IsUnlocked
    {
        get { lock (gate) return TryGetNow(out var now) && IsValidAt(now); }
    }

    internal DateTimeOffset? ExpiresAtUtc
    {
        get { lock (gate) return lease?.ExpiresAtUtc; }
    }

    internal bool HasExpired
    {
        get { lock (gate) return lease is not null && TryGetNow(out var now) && now >= lease.ExpiresAtUtc; }
    }

    internal string StatusMessage
    {
        get
        {
            lock (gate)
            {
                if (TryGetNow(out var now) && lease is not null) IsValidAt(now);
                return lastVerificationError ?? statusMessage;
            }
        }
    }

    internal bool TryUnlock(string? input)
    {
        lock (gate)
        {
            lastVerificationError = null;
            if (!TryGetNow(out var now)) return false;
            bool alreadyValid = IsValidAt(now);
            // Bound temporary encoding allocations. Neither input nor its digest is persisted.
            if (input is null || input.Length > 1024 ||
                !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(input.Trim())), expectedDigest))
            {
                lastVerificationError = statusMessage = alreadyValid ? "测试码不正确；现有测试授权仍有效。" : "测试码不正确。";
                return false;
            }

            if (alreadyValid)
            {
                statusMessage = "测试授权仍有效，原到期时间不变。";
                return true;
            }

            try
            {
                var candidate = new LeaseRecord(1, now, now.Add(LeaseDuration));
                SaveAtomically(candidate);
                // Grant access only after the complete record was committed successfully.
                lease = candidate;
                statusMessage = "测试授权已生效，有效期为七天。";
                return true;
            }
            catch (Exception ex)
            {
                lastVerificationError = statusMessage = $"无法保存测试授权（{ex.GetType().Name}），尚未授权。";
                return false;
            }
        }
    }

    private bool TryGetNow(out DateTimeOffset now)
    {
        try
        {
            now = clock().ToUniversalTime();
            return true;
        }
        catch (Exception ex)
        {
            now = default;
            statusMessage = $"无法读取本地时间（{ex.GetType().Name}），测试授权已暂停。";
            return false;
        }
    }

    private bool IsValidAt(DateTimeOffset now)
    {
        if (lease is null) return false;
        if (now < lease.IssuedAtUtc)
        {
            statusMessage = "本地时间早于授权时间，测试授权已暂停。";
            return false;
        }
        if (now >= lease.ExpiresAtUtc)
        {
            statusMessage = "测试授权已到期，请重新验证测试码。";
            return false;
        }
        return true;
    }

    private void LoadRecord()
    {
        try
        {
            using var stream = new FileStream(recordPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaxRecordBytes)
            {
                statusMessage = "测试授权记录大小无效，请重新验证测试码。";
                return;
            }
            byte[] bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var loaded = JsonSerializer.Deserialize<LeaseRecord>(bytes, RecordJson);
            if (loaded is null || loaded.SchemaVersion != 1 ||
                loaded.IssuedAtUtc.Offset != TimeSpan.Zero || loaded.ExpiresAtUtc.Offset != TimeSpan.Zero ||
                loaded.ExpiresAtUtc - loaded.IssuedAtUtc != LeaseDuration)
            {
                statusMessage = "测试授权记录无效，请重新验证测试码。";
                return;
            }
            if (!TryGetNow(out var now)) return;
            if (loaded.IssuedAtUtc > now)
            {
                statusMessage = "测试授权记录时间异常，请重新验证测试码。";
                return;
            }
            lease = loaded;
            if (IsValidAt(now)) statusMessage = "已恢复本地测试授权。";
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex)
        {
            statusMessage = $"无法读取测试授权记录（{ex.GetType().Name}），请重新验证测试码。";
        }
    }

    private void SaveAtomically(LeaseRecord candidate)
    {
        string fullPath = Path.GetFullPath(recordPath);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(candidate, RecordJson);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Same-directory rename commits the whole record; never overwrite the destination in place.
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch { /* Best effort cleanup; never replace the original save failure. */ }
        }
    }

    private sealed record LeaseRecord(int SchemaVersion, DateTimeOffset IssuedAtUtc, DateTimeOffset ExpiresAtUtc);
}
