using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>Entirely synthetic managed DTOs and temporary files; no client/process/account data.</summary>
public sealed class LocalCaptureRecorderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "mjcn-recorder-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CompletedArchiveContainsOnlyGeneratedFilesAndVerifiableContinuousRecords()
    {
        var recorder = Start();
        Assert.True(recorder.TryAppend("frame", new { Sequence = 12, Nodes = new[] { new { Path = "Emj/28", X = 120 } } }));
        Assert.True(recorder.TryAppend("review", new { FrameSequence = 12, MatchesVisibleHand = true }));
        Assert.True(recorder.TryAppend("lifecycle", new { Addon = "Emj", Event = "PostSetup" }));
        Assert.True(recorder.TryAppend("status", new { Code = "STOP_REQUESTED" }));
        await WaitForCommittedHeader(recorder);
        File.WriteAllText(Path.Combine(recorder.SessionDirectory, "unrelated-private.txt"), "not-a-record");
        var firstCompletion = recorder.CompleteAsync("USER_STOPPED");
        Assert.Same(firstCompletion, recorder.CompleteAsync("IGNORED_SECOND_REASON"));
        var zip = await firstCompletion;
        Assert.NotNull(zip);
        Assert.Equal(zip, recorder.ZipPath);
        Assert.Null(recorder.Fault);
        Assert.False(recorder.TryAppend("frame", new { Sequence = 999 }));
        Assert.Equal("NOT_RECORDING", recorder.LastAppendError);
        Assert.Null(recorder.Fault);

        using var archive = ZipFile.OpenRead(zip);
        var expected = new[] { "manifest.json", "segment-000001.jsonl.gz", "session.json" };
        Assert.Equal(expected, archive.Entries.Select(e => e.FullName).Order().ToArray());
        using var marker = JsonDocument.Parse(ReadEntry(archive, "session.json"));
        Assert.Equal("gzip-record-members", marker.RootElement.GetProperty("Compression").GetString());
        Assert.Equal(64, marker.RootElement.GetProperty("Limits").GetProperty("ChannelCapacity").GetInt32());
        Assert.Equal(2L * 1024 * 1024 * 1024, marker.RootElement.GetProperty("Limits").GetProperty("ExpandedSessionBytes").GetInt64());
        using var manifest = JsonDocument.Parse(ReadEntry(archive, "manifest.json"));
        Assert.Equal("complete", manifest.RootElement.GetProperty("State").GetString());
        Assert.Equal("USER_STOPPED", manifest.RootElement.GetProperty("StopReason").GetString());
        Assert.Equal(5, manifest.RootElement.GetProperty("RecordCount").GetInt64());
        Assert.Equal(0, manifest.RootElement.GetProperty("UnwrittenRecords").GetInt64());
        var records = ReadRecords(recorder);
        Assert.Equal(new[] { "header", "frame", "review", "lifecycle", "status" }, records.Select(r => r.GetProperty("Kind").GetString()));
        Assert.Equal(Enumerable.Range(1, 5).Select(i => (long)i), records.Select(r => r.GetProperty("Sequence").GetInt64()));
        Assert.All(records, r =>
        {
            Assert.Equal(1, r.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(JsonValueKind.Object, r.GetProperty("Payload").ValueKind);
            Assert.NotEqual(default, r.GetProperty("Utc").GetDateTimeOffset());
        });
        AssertManifestMatchesDisk(recorder);
    }

    [Fact]
    public async Task SegmentRolloverPreservesIndependentCompressedRecordsAndHashes()
    {
        var recorder = Start(new LocalCaptureLimits(SegmentBytes: 1024));
        for (var i = 0; i < 20; i++)
            Assert.True(recorder.TryAppend("frame", new { Id = i, Data = Noise(420, i) }));
        Assert.NotNull(await recorder.CompleteAsync("TEST"));
        Assert.Null(recorder.Fault);
        var paths = SegmentFiles(recorder);
        Assert.True(paths.Length >= 10);
        Assert.All(paths, p => Assert.InRange(new FileInfo(p).Length, 1, 1024));
        Assert.Equal(Enumerable.Range(1, paths.Length).Select(i => $"segment-{i:D6}.jsonl.gz"), paths.Select(Path.GetFileName));
        var records = ReadRecords(recorder);
        Assert.Equal(21, records.Count);
        Assert.Equal(Enumerable.Range(1, 21).Select(i => (long)i), records.Select(r => r.GetProperty("Sequence").GetInt64()));
        AssertManifestMatchesDisk(recorder);
    }

    [Fact]
    public async Task QueueHas64SlotsAndOverflowIsExplicitWithoutDroppingAcceptedRecords()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var recorder = LocalCaptureRecorder.Start(root, new BlockingPayload(entered, release));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            for (var i = 0; i < 64; i++)
                Assert.True(recorder.TryAppend("frame", new { Id = i }));
            Assert.False(recorder.TryAppend("frame", new { Id = 64 }));
            Assert.Equal("QUEUE_FULL", recorder.Fault);
            Assert.Equal("QUEUE_FULL", recorder.LastAppendError);
        }
        finally { release.Set(); }
        Assert.NotNull(await recorder.CompleteAsync("FAULT"));
        Assert.Equal(65, ReadRecords(recorder).Count);
        using var manifest = ReadManifest(recorder);
        Assert.Equal("faulted", manifest.RootElement.GetProperty("State").GetString());
        Assert.Equal(65, manifest.RootElement.GetProperty("AcceptedRecordCount").GetInt64());
        Assert.Equal(0, manifest.RootElement.GetProperty("UnwrittenRecords").GetInt64());
    }

    [Fact]
    public async Task OversizedUncompressedRecordStopsAtValidPrefixAndCountsUnwrittenRecords()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var recorder = LocalCaptureRecorder.Start(root, new BlockingPayload(entered, release));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(recorder.TryAppend("frame", new { Data = new string('a', LocalCaptureRecorder.MaximumRecordBytes) }));
            Assert.True(recorder.TryAppend("frame", new { MustNotAppearAfterGap = true }));
        }
        finally { release.Set(); }
        Assert.NotNull(await recorder.CompleteAsync("FAULT"));
        Assert.Equal("RECORD_TOO_LARGE", recorder.Fault);
        Assert.Single(ReadRecords(recorder));
        using var manifest = ReadManifest(recorder);
        Assert.Equal(3, manifest.RootElement.GetProperty("AcceptedRecordCount").GetInt64());
        Assert.Equal(2, manifest.RootElement.GetProperty("UnwrittenRecords").GetInt64());
        AssertManifestMatchesDisk(recorder);
    }

    [Fact]
    public async Task CompressedDiskQuotaIncludesRawMetadataAndStoredZip()
    {
        var limits = new LocalCaptureLimits(SegmentBytes: 4096, SessionBytes: 128 * 1024);
        var recorder = Start(limits);
        for (var i = 0; i < 30; i++)
            if (!recorder.TryAppend("frame", new { Id = i, Data = Noise(1500, i) })) break;
        Assert.NotNull(await recorder.CompleteAsync("LIMIT"));
        Assert.Equal("SESSION_LIMIT", recorder.Fault);
        Assert.InRange(Directory.GetFiles(recorder.SessionDirectory).Sum(p => new FileInfo(p).Length), 1, limits.SessionBytes);
        using var manifest = ReadManifest(recorder);
        Assert.True(manifest.RootElement.GetProperty("UnwrittenRecords").GetInt64() > 0);
        AssertManifestMatchesDisk(recorder);
    }

    [Fact]
    public async Task ExpandedQuotaStopsHighlyCompressibleRecordsAtContinuousPrefix()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var limits = new LocalCaptureLimits(ExpandedSessionBytes: 4096);
        var recorder = LocalCaptureRecorder.Start(root, new BlockingPayload(entered, release), limits);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(recorder.TryAppend("frame", new { Data = new string('a', 2000) }));
            Assert.True(recorder.TryAppend("frame", new { Data = new string('b', 2000) }));
            Assert.True(recorder.TryAppend("status", new { MustNotAppearAfterGap = true }));
        }
        finally { release.Set(); }

        Assert.NotNull(await recorder.CompleteAsync("LIMIT"));
        Assert.Equal("EXPANDED_LIMIT", recorder.Fault);
        var records = ReadRecords(recorder);
        Assert.Equal(new long[] { 1, 2 }, records.Select(r => r.GetProperty("Sequence").GetInt64()));
        Assert.Equal(new string('a', 2000), records[1].GetProperty("Payload").GetProperty("Data").GetString());
        using var manifest = ReadManifest(recorder);
        Assert.Equal("EXPANDED_LIMIT", manifest.RootElement.GetProperty("Fault").GetString());
        Assert.Equal(4, manifest.RootElement.GetProperty("AcceptedRecordCount").GetInt64());
        Assert.Equal(2, manifest.RootElement.GetProperty("UnwrittenRecords").GetInt64());
        Assert.InRange(manifest.RootElement.GetProperty("UncompressedJsonlBytes").GetInt64(), 2001, limits.ExpandedSessionBytes);
        Assert.True(manifest.RootElement.GetProperty("JsonlBytes").GetInt64() < 1024);
        Assert.Equal(limits.ExpandedSessionBytes, manifest.RootElement.GetProperty("Limits").GetProperty("ExpandedSessionBytes").GetInt64());
        AssertManifestMatchesDisk(recorder);
    }

    [Fact]
    public async Task AnIncompressibleMemberCannotCrossSegmentLimit()
    {
        var recorder = Start(new LocalCaptureLimits(SegmentBytes: 1024));
        Assert.True(recorder.TryAppend("frame", new { Data = Noise(3000, 3) }));
        Assert.NotNull(await recorder.CompleteAsync("TEST"));
        Assert.Equal("COMPRESSED_RECORD_TOO_LARGE", recorder.Fault);
        Assert.Single(ReadRecords(recorder));
        Assert.All(SegmentFiles(recorder), p => Assert.InRange(new FileInfo(p).Length, 1, 1024));
    }

    [Fact]
    public async Task FinishedMembersAreReadableDuringRecordingWithoutCompletion()
    {
        var recorder = Start();
        try
        {
            await WaitForCommittedHeader(recorder);
            Assert.False(File.Exists(Path.Combine(recorder.SessionDirectory, "manifest.json")));
            Assert.False(File.Exists(Path.Combine(recorder.SessionDirectory, "capture.zip")));
            Assert.Equal("header", Assert.Single(ReadRecords(recorder)).GetProperty("Kind").GetString());
            Assert.True(recorder.TryAppend("status", new { Code = "STILL_RECORDING" }));
        }
        finally { await recorder.CompleteAsync("TEST"); }
        Assert.Equal(2, ReadRecords(recorder).Count);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("../../other")]
    [InlineData("FRAME")]
    [InlineData("unknown")]
    public async Task UnsupportedKindsCannotWriteFilesOrInventRecordSchemas(string kind)
    {
        var recorder = Start();
        Assert.False(recorder.TryAppend(kind, new { Data = 1 }));
        Assert.Equal("INVALID_KIND", recorder.Fault);
        Assert.NotNull(await recorder.CompleteAsync("TEST"));
        Assert.Single(ReadRecords(recorder));
    }

    [Fact]
    public async Task InvalidPayloadStopsWithoutLosingCommittedHeader()
    {
        var recorder = Start();
        Assert.True(recorder.TryAppend("frame", 42));
        Assert.NotNull(await recorder.CompleteAsync("TEST"));
        Assert.Equal("SERIALIZE_FAILED:JsonException", recorder.Fault);
        Assert.Single(ReadRecords(recorder));
        using var manifest = ReadManifest(recorder);
        Assert.Equal(1, manifest.RootElement.GetProperty("UnwrittenRecords").GetInt64());
    }

    [Fact]
    public async Task FileSystemFailureIsReportedWithoutThrowingThroughCaller()
    {
        Directory.CreateDirectory(root);
        var fileInsteadOfDirectory = Path.Combine(root, "not-a-directory");
        File.WriteAllText(fileInsteadOfDirectory, "test");
        var recorder = LocalCaptureRecorder.Start(fileInsteadOfDirectory, new { Synthetic = true });
        Assert.Null(await recorder.CompleteAsync("TEST"));
        Assert.StartsWith("WRITE_FAILED:", recorder.Fault);
        Assert.False(recorder.TryAppend("frame", new { Data = 1 }));
        Assert.Equal(recorder.Fault, recorder.LastAppendError);
        Assert.Equal("test", File.ReadAllText(fileInsteadOfDirectory));
    }

    [Fact]
    public async Task ArchiveFailureRetainsCommittedRecordsAndUpdatesManifestFault()
    {
        var recorder = Start();
        await WaitForCommittedHeader(recorder);
        var blocker = Path.Combine(recorder.SessionDirectory, "capture.partial.zip");
        File.WriteAllText(blocker, "existing-file-must-not-be-overwritten");
        Assert.Null(await recorder.CompleteAsync("TEST"));
        Assert.StartsWith("ARCHIVE_FAILED:", recorder.Fault);
        Assert.Equal("existing-file-must-not-be-overwritten", File.ReadAllText(blocker));
        Assert.Single(ReadRecords(recorder));
        using var manifest = ReadManifest(recorder);
        Assert.Equal("faulted", manifest.RootElement.GetProperty("State").GetString());
        Assert.Equal(recorder.Fault, manifest.RootElement.GetProperty("Fault").GetString());
        AssertManifestMatchesDisk(recorder);
    }

    [Fact]
    public async Task NewSessionDoesNotReadOrRewritePreviousSession()
    {
        var first = Start();
        var firstZip = await first.CompleteAsync("FIRST");
        var original = File.ReadAllBytes(firstZip!);
        var second = Start();
        Assert.NotEqual(first.SessionDirectory, second.SessionDirectory);
        Assert.True(second.TryAppend("frame", new { Second = true }));
        Assert.NotNull(await second.CompleteAsync("SECOND"));
        Assert.Equal(original, File.ReadAllBytes(firstZip!));
        Assert.Single(ReadRecords(first));
        Assert.Equal(2, ReadRecords(second).Count);
    }

    [Fact]
    public async Task Representative250KiBLayoutUsesCompressedBudgetRatherThanExpandedSize()
    {
        // Synthetic repeated paths/geometry, not a claimed real capture or proof of 60-minute production capacity.
        var nodes = Enumerable.Range(0, 2200).Select(i => new
        {
            Path = $"Emj/{118 + i % 4}/5", Area = "river-bottom", Status = "SHELL_METADATA_ONLY",
            X = i % 24 * 34, Y = i / 24 * 45, Width = 34, Height = 45, ScaleX = 1.0, ScaleY = 1.0,
        }).ToArray();
        var frame = new { Sequence = 1, Nodes = nodes };
        var expandedBytes = JsonSerializer.SerializeToUtf8Bytes(frame).Length;
        Assert.True(expandedBytes > 250 * 1024);
        var recorder = Start(new LocalCaptureLimits(SessionBytes: 128 * 1024));
        Assert.True(recorder.TryAppend("frame", frame));
        Assert.NotNull(await recorder.CompleteAsync("TEST"));
        Assert.Null(recorder.Fault);
        using var manifest = ReadManifest(recorder);
        Assert.True(manifest.RootElement.GetProperty("UncompressedJsonlBytes").GetInt64() > expandedBytes);
        Assert.True(manifest.RootElement.GetProperty("JsonlBytes").GetInt64() < expandedBytes / 10);
    }

    [Theory]
    [InlineData(65, 4096, 268435456)]
    [InlineData(64, 1023, 268435456)]
    [InlineData(64, 4194305, 268435456)]
    [InlineData(64, 4096, 131071)]
    [InlineData(64, 4096, 268435457)]
    public void InvalidLimitsAreRejectedBeforeAnyFileCreation(int capacity, int segmentBytes, long sessionBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Start(new LocalCaptureLimits(capacity, segmentBytes, sessionBytes)));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2147483649L)]
    public void ExpandedLimitMustRemainWithinOfflineParserBound(long expandedBytes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Start(new LocalCaptureLimits(ExpandedSessionBytes: expandedBytes)));
        Assert.False(Directory.Exists(root));
    }

    private LocalCaptureRecorder Start(LocalCaptureLimits? limits = null) =>
        LocalCaptureRecorder.Start(root, new { Synthetic = true, ClientVersion = "test-only", Scope = "managed-dto" }, limits);

    private static string Noise(int bytes, int seed)
    {
        var data = new byte[bytes];
        new Random(seed).NextBytes(data);
        return Convert.ToBase64String(data);
    }

    private static string[] SegmentFiles(LocalCaptureRecorder recorder) =>
        Directory.GetFiles(recorder.SessionDirectory, "segment-*.jsonl.gz").Order().ToArray();

    private static List<JsonElement> ReadRecords(LocalCaptureRecorder recorder)
    {
        var result = new List<JsonElement>();
        foreach (var path in SegmentFiles(recorder))
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            var bytes = output.ToArray();
            Assert.NotEmpty(bytes);
            Assert.Equal((byte)'\n', bytes[^1]);
            foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                using var parsed = JsonDocument.Parse(line);
                result.Add(parsed.RootElement.Clone());
            }
        }
        return result;
    }

    private static JsonDocument ReadManifest(LocalCaptureRecorder recorder) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(recorder.SessionDirectory, "manifest.json")));

    private static void AssertManifestMatchesDisk(LocalCaptureRecorder recorder)
    {
        using var manifest = ReadManifest(recorder);
        long total = 0;
        foreach (var segment in manifest.RootElement.GetProperty("Segments").EnumerateArray())
        {
            var bytes = File.ReadAllBytes(Path.Combine(recorder.SessionDirectory, segment.GetProperty("FileName").GetString()!));
            Assert.Equal(bytes.Length, segment.GetProperty("Bytes").GetInt64());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), segment.GetProperty("Sha256").GetString());
            total += bytes.Length;
        }
        Assert.Equal(total, manifest.RootElement.GetProperty("JsonlBytes").GetInt64());
    }

    private static byte[] ReadEntry(ZipArchive archive, string name)
    {
        using var input = archive.GetEntry(name)!.Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static async Task WaitForCommittedHeader(LocalCaptureRecorder recorder)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var path = Path.Combine(recorder.SessionDirectory, "segment-000001.jsonl.gz");
            if (File.Exists(path) && new FileInfo(path).Length > 0) return;
            Assert.Null(recorder.Fault);
            await Task.Delay(10);
        }
        Assert.Fail("Timed out waiting for worker to commit header.");
    }

    public void Dispose()
    {
        // Only the generated absolute test directory is ever recursively deleted.
        var absolute = Path.GetFullPath(root);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), absolute, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("mjcn-recorder-tests-", Path.GetFileName(absolute), StringComparison.Ordinal);
        if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
    }

    [JsonConverter(typeof(BlockingPayloadConverter))]
    public sealed record BlockingPayload(ManualResetEventSlim Entered, ManualResetEventSlim Release);

    public sealed class BlockingPayloadConverter : JsonConverter<BlockingPayload>
    {
        public override BlockingPayload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, BlockingPayload value, JsonSerializerOptions options)
        {
            value.Entered.Set();
            if (!value.Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            writer.WriteStartObject();
            writer.WriteBoolean("Synthetic", true);
            writer.WriteEndObject();
        }
    }
}
