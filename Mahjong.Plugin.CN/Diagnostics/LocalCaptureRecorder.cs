using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>The session limit includes retained compressed records, metadata and the ZIP temporary copy.</summary>
internal sealed record LocalCaptureLimits(int ChannelCapacity = 64, int SegmentBytes = 4 * 1024 * 1024,
    long SessionBytes = 256L * 1024 * 1024, long ExpandedSessionBytes = 2L * 1024 * 1024 * 1024);

/// <summary>
/// Records only caller-supplied managed DTOs. The caller owns making immutable copies before TryAppend;
/// serialization and all file I/O happen on the worker. There are no game, network or directory-scanning APIs.
/// Each LF-terminated JSONL record is a separate complete GZip member, flushed to disk before the count advances.
/// Recovery accepts only members whose CRC/footer is complete; it may discard the final interrupted member.
/// </summary>
internal sealed class LocalCaptureRecorder
{
    internal const string Format = "mjcn-local-capture";
    internal const int SchemaVersion = 1;
    internal const string PluginVersion = "4.1.4";
    internal const string Compression = "gzip-record-members";
    internal const int MaximumRecordBytes = 4 * 1024 * 1024;
    private const int MetadataReserveBytes = 64 * 1024;
    private const int MaximumSegments = 64;
    private const int MaximumMetadataBytes = 24 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new();
    private readonly object gate = new();
    private readonly Channel<PendingRecord> channel;
    private readonly LocalCaptureLimits limits;
    private readonly string sessionId;
    private readonly DateTimeOffset startedUtc;
    private readonly Task writerTask;
    private readonly List<SegmentSummary> segments = new();
    private readonly long jsonlBudget;
    private long acceptedRecords;
    private long writtenRecords;
    private long jsonlBytes;
    private long uncompressedJsonlBytes;
    private long markerBytes;
    private bool accepting = true;
    private bool archiveSafe = true;
    private string? fault;
    private string? lastAppendError;
    private string? zipPath;
    private Task<string?>? completion;

    private LocalCaptureRecorder(string root, object immutableHeader, LocalCaptureLimits limits)
    {
        if (limits.ChannelCapacity is < 1 or > 64 || limits.SegmentBytes is < 1024 or > 4 * 1024 * 1024 ||
            limits.SessionBytes is < 2L * MetadataReserveBytes or > 256L * 1024 * 1024 ||
            limits.ExpandedSessionBytes is < 1 or > 2L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(limits));
        this.limits = limits;
        jsonlBudget = (limits.SessionBytes - MetadataReserveBytes) / 2;
        sessionId = Guid.NewGuid().ToString("N");
        startedUtc = DateTimeOffset.UtcNow;
        SessionDirectory = Path.Combine(Path.GetFullPath(root), $"mjcn-session-{startedUtc:yyyyMMdd-HHmmss}-{sessionId}");
        channel = Channel.CreateBounded<PendingRecord>(new BoundedChannelOptions(limits.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        channel.Writer.TryWrite(new PendingRecord(1, startedUtc, "header", immutableHeader));
        acceptedRecords = 1;
        writerTask = Task.Run(WriteAsync);
    }

    internal static LocalCaptureRecorder Start(string root, object immutableHeader, LocalCaptureLimits? limits = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(immutableHeader);
        return new LocalCaptureRecorder(root, immutableHeader, limits ?? new LocalCaptureLimits());
    }

    internal string SessionDirectory { get; }
    internal string? Fault { get { lock (gate) return fault; } }
    internal string? LastAppendError { get { lock (gate) return lastAppendError; } }
    internal string? ZipPath { get { lock (gate) return zipPath; } }

    /// <summary>False never silently drops a record: LastAppendError states why; terminal errors also set Fault.</summary>
    internal bool TryAppend(string kind, object immutablePayload)
    {
        lock (gate)
        {
            if (!accepting)
            {
                lastAppendError = fault ?? "NOT_RECORDING";
                return false;
            }
            if (kind is not ("frame" or "review" or "lifecycle" or "status"))
                return RejectLocked("INVALID_KIND");
            if (immutablePayload is null)
                return RejectLocked("INVALID_PAYLOAD");
            var record = new PendingRecord(acceptedRecords + 1, DateTimeOffset.UtcNow, kind, immutablePayload);
            if (!channel.Writer.TryWrite(record))
                return RejectLocked("QUEUE_FULL");
            acceptedRecords++;
            lastAppendError = null;
            return true;
        }
    }

    /// <summary>
    /// Idempotently stops accepting records, drains queued records, writes the manifest and creates capture.zip.
    /// Never block the game thread waiting for this task. Null means raw committed segments remain at SessionDirectory.
    /// </summary>
    internal Task<string?> CompleteAsync(string reason)
    {
        lock (gate)
        {
            if (completion is not null)
                return completion;
            accepting = false;
            channel.Writer.TryComplete();
            // Keep all finalization I/O off the caller even when writerTask is already complete.
            var stopReason = string.IsNullOrWhiteSpace(reason) ? "STOPPED" : reason.Length > 128 ? reason[..128] : reason;
            completion = Task.Run(() => FinishAsync(stopReason));
            return completion;
        }
    }

    private bool RejectLocked(string code)
    {
        fault ??= code;
        lastAppendError = code;
        accepting = false;
        channel.Writer.TryComplete();
        return false;
    }

    private void SetFault(string code)
    {
        lock (gate)
        {
            fault ??= code;
            accepting = false;
            channel.Writer.TryComplete();
        }
    }

    private async Task WriteAsync()
    {
        FileStream? stream = null;
        IncrementalHash? hash = null;
        string? segmentName = null;
        long segmentBytes = 0;
        long segmentRecords = 0;
        try
        {
            if (Directory.Exists(SessionDirectory))
                throw new IOException("Session already exists.");
            Directory.CreateDirectory(SessionDirectory);
            var marker = JsonSerializer.SerializeToUtf8Bytes(new SessionMarker(SchemaVersion, Format,
                PluginVersion, Compression, sessionId, startedUtc, limits), JsonOptions);
            WriteNewFile(Path.Combine(SessionDirectory, "session.json"), marker);
            markerBytes = marker.Length;

            await foreach (var record in channel.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                byte[] line;
                try
                {
                    line = SerializeRecord(record);
                }
                catch (RecordTooLargeException)
                {
                    SetFault("RECORD_TOO_LARGE");
                    break;
                }
                catch (Exception ex)
                {
                    SetFault($"SERIALIZE_FAILED:{ex.GetType().Name}");
                    break;
                }
                // Match the offline reader's expanded-data ceiling even for extremely compressible DTOs.
                // Include the complete envelope and its LF, not just Payload; retain only a contiguous prefix.
                if (line.Length > limits.ExpandedSessionBytes - uncompressedJsonlBytes)
                {
                    SetFault("EXPANDED_LIMIT");
                    break;
                }
                byte[] member;
                using (var compressed = new MemoryStream())
                {
                    using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                        gzip.Write(line);
                    member = compressed.ToArray();
                }
                if (member.Length > limits.SegmentBytes)
                {
                    SetFault("COMPRESSED_RECORD_TOO_LARGE");
                    break;
                }
                if (jsonlBytes + member.Length > jsonlBudget)
                {
                    SetFault("SESSION_LIMIT");
                    break;
                }
                if (stream is not null && segmentBytes + member.Length > limits.SegmentBytes)
                    CloseSegment();
                if (stream is null)
                {
                    if (segments.Count >= MaximumSegments)
                    {
                        SetFault("SEGMENT_LIMIT");
                        break;
                    }
                    segmentName = $"segment-{segments.Count + 1:D6}.jsonl.gz";
                    stream = new FileStream(Path.Combine(SessionDirectory, segmentName), FileMode.CreateNew,
                        FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
                    hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    segmentBytes = 0;
                    segmentRecords = 0;
                }
                try
                {
                    stream.Write(member);
                    stream.Flush(flushToDisk: true);
                }
                catch
                {
                    // A failed write/flush must not turn a half row into an apparently committed JSONL record.
                    try
                    {
                        stream.SetLength(segmentBytes);
                        stream.Flush(flushToDisk: true);
                    }
                    catch
                    {
                        archiveSafe = false;
                    }
                    throw;
                }
                hash!.AppendData(member);
                segmentBytes += member.Length;
                segmentRecords++;
                jsonlBytes += member.Length;
                uncompressedJsonlBytes += line.Length;
                writtenRecords++;
            }
            CloseSegment();
        }
        catch (Exception ex)
        {
            SetFault($"WRITE_FAILED:{ex.GetType().Name}");
            try { CloseSegment(); }
            catch { archiveSafe = false; }
        }
        finally
        {
            try { stream?.Dispose(); }
            catch (Exception ex) { archiveSafe = false; SetFault($"WRITE_FAILED:{ex.GetType().Name}"); }
            hash?.Dispose();
            // On a fault this may contain accepted records after the first unwriteable row.
            // They are explicitly counted as UnwrittenRecords in the manifest, never serialized out of sequence.
            while (channel.Reader.TryRead(out _)) { }
        }

        void CloseSegment()
        {
            if (stream is null)
                return;
            stream.Dispose();
            stream = null;
            var digest = Convert.ToHexString(hash!.GetHashAndReset());
            hash.Dispose();
            hash = null;
            if (segmentRecords == 0 && archiveSafe)
                File.Delete(Path.Combine(SessionDirectory, segmentName!));
            else
                segments.Add(new SegmentSummary(segmentName!, segmentBytes, segmentRecords, digest));
        }
    }

    private byte[] SerializeRecord(PendingRecord record)
    {
        using var buffer = new BoundedMemoryStream(MaximumRecordBytes - 1);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("SchemaVersion", SchemaVersion);
            writer.WriteNumber("Sequence", record.Sequence);
            writer.WriteString("Utc", record.Utc);
            writer.WriteString("Kind", record.Kind);
            writer.WritePropertyName("Payload");
            JsonSerializer.Serialize(writer, record.Payload, record.Payload.GetType(), JsonOptions);
            writer.WriteEndObject();
        }
        // The format permits object DTOs only, including JsonElement objects; reject scalar/array payloads.
        using (var parsed = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length))))
            if (parsed.RootElement.GetProperty("Payload").ValueKind != JsonValueKind.Object)
                throw new JsonException("Payload must be an object.");
        var line = new byte[buffer.Length + 1];
        buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)).CopyTo(line);
        line[^1] = (byte)'\n';
        return line;
    }

    private async Task<string?> FinishAsync(string reason)
    {
        try
        {
            await writerTask.ConfigureAwait(false);
            if (!Directory.Exists(SessionDirectory) || markerBytes == 0)
                return null;
            var manifestPath = Path.Combine(SessionDirectory, "manifest.json");
            var manifest = BuildManifest(reason);
            if (manifest.Length + markerBytes > MaximumMetadataBytes)
            {
                SetFault("METADATA_LIMIT");
                return null;
            }
            WriteNewFile(manifestPath, manifest);
            if (!archiveSafe || writtenRecords == 0)
                return null;

            // ZIP is stored, not deflated: its maximum physical size is bounded and predictable.
            // All names are generated here. Nothing else in this directory or its parents is enumerated/read.
            var files = new[] { "session.json", "manifest.json" }.Concat(segments.Select(s => s.FileName)).ToArray();
            long metadataBytes = markerBytes + manifest.Length;
            long zipUpperBound = jsonlBytes + metadataBytes + files.Length * 256L + 1024;
            long rawBytes = jsonlBytes + metadataBytes;
            if (rawBytes + zipUpperBound > limits.SessionBytes)
            {
                SetFault("ARCHIVE_BUDGET");
                RewriteManifest(manifestPath, reason);
                return null;
            }
            var temporaryPath = Path.Combine(SessionDirectory, "capture.partial.zip");
            var finalPath = Path.Combine(SessionDirectory, "capture.zip");
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var limited = new LimitedWriteStream(file, zipUpperBound))
                using (var archive = new ZipArchive(limited, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var name in files)
                    {
                        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
                        using var input = new FileStream(Path.Combine(SessionDirectory, name), FileMode.Open,
                            FileAccess.Read, FileShare.Read);
                        using var output = entry.Open();
                        input.CopyTo(output);
                    }
                }
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, finalPath);
            lock (gate) zipPath = finalPath;
            return finalPath;
        }
        catch (Exception ex)
        {
            SetFault($"ARCHIVE_FAILED:{ex.GetType().Name}");
            try { RewriteManifest(Path.Combine(SessionDirectory, "manifest.json"), reason); }
            catch { /* Committed JSONL remains readable even when metadata cannot be updated. */ }
            return null;
        }
    }

    private byte[] BuildManifest(string reason)
    {
        lock (gate)
            return JsonSerializer.SerializeToUtf8Bytes(new SessionManifest(SchemaVersion, Format, PluginVersion, Compression,
                sessionId, startedUtc, DateTimeOffset.UtcNow, fault is null ? "complete" : "faulted", reason,
                fault, writtenRecords, acceptedRecords, acceptedRecords - writtenRecords, jsonlBytes, uncompressedJsonlBytes, limits,
                segments.ToArray()), JsonOptions);
    }

    private void RewriteManifest(string path, string reason)
    {
        var bytes = BuildManifest(reason);
        if (bytes.Length + markerBytes > MaximumMetadataBytes)
            return;
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private sealed record PendingRecord(long Sequence, DateTimeOffset Utc, string Kind, object Payload);
    private sealed record SessionMarker(int SchemaVersion, string Format, string PluginVersion, string Compression, string SessionId,
        DateTimeOffset StartedUtc, LocalCaptureLimits Limits);
    private sealed record SegmentSummary(string FileName, long Bytes, long Records, string Sha256);
    private sealed record SessionManifest(int SchemaVersion, string Format, string PluginVersion, string Compression, string SessionId,
        DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc, string State, string StopReason, string? Fault,
        long RecordCount, long AcceptedRecordCount, long UnwrittenRecords, long JsonlBytes, long UncompressedJsonlBytes, LocalCaptureLimits Limits,
        SegmentSummary[] Segments);

    private sealed class RecordTooLargeException : IOException;

    private sealed class BoundedMemoryStream(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }
        private void Check(int count)
        {
            if (Position + count > maximumBytes)
                throw new RecordTooLargeException();
        }
    }

    /// <summary>Hard limit includes every ZIP byte, including archive headers and the central directory.</summary>
    private sealed class LimitedWriteStream(Stream inner, long maximumBytes) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > maximumBytes - written)
                throw new IOException("Archive budget exhausted.");
            inner.Write(buffer);
            written += buffer.Length;
        }
    }
}
