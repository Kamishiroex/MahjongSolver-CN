using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using System.IO.Compression;
using Mahjong.Core;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record JournalBody(int Schema, Guid SessionId, long Sequence, DateTimeOffset Utc,
    string Kind, JsonElement Data, string PreviousSha256);
internal sealed record JournalLine(JournalBody Entry, string Sha256);
internal sealed record JournalReadResult(ImmutableArray<JournalLine> Lines, bool IncompleteTail, string? Error)
{
    internal bool IntegrityPassed => Error is null;
}

/// <summary>Two append-only local streams. All filesystem work is serialized off the game thread.</summary>
internal sealed class GameJournal
{
    internal const int MaximumLineBytes = 128 * 1024;
    internal const long MaximumFileBytes = 32 * 1024 * 1024;
    private const int MaximumPending = 128;
    private const long MaximumReplayBytes = 256 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { info =>
            {
                // Keep anonymous event DTOs and provenance getters. Omit only redundant core projections.
                for (int i = info.Properties.Count - 1; i >= 0; i--)
                    if ((info.Type == typeof(Tile) && info.Properties[i].Name != nameof(Tile.Id)) ||
                        (info.Type == typeof(StateSnapshot) && info.Properties[i].Name == nameof(StateSnapshot.Us)))
                        info.Properties.RemoveAt(i);
            } },
        },
    };
    private static readonly Regex OwnDirectory = new(@"\Amjcn-game-[0-9]{8}-[0-9]{6}-[0-9a-f]{32}\z", RegexOptions.CultureInvariant);
    private readonly object gate = new();
    private readonly Guid sessionId = Guid.NewGuid();
    private readonly string root;
    private readonly string directory;
    private readonly Dictionary<string, (long Sequence, string Hash)> heads = new()
        { ["events.jsonl"] = (0, new string('0', 64)), ["errors.jsonl"] = (0, new string('0', 64)) };
    private Task completion = Task.CompletedTask;
    private int pending;
    private bool closed;
    private bool initialized;
    private FileStream? sessionLease;
    private string? fault;
    private readonly long segmentBytes;
    private readonly Dictionary<string, int> volumes = new() { ["events.jsonl"] = 0, ["errors.jsonl"] = 0 };
    private readonly Dictionary<string, long> lengths = new();
    private readonly JournalPayloadCodec codec = new();
    private readonly Queue<byte[]> diagnostics = new();
    private int diagnosticBytes;
    private string? errorKey;
    private string? errorCode;
    private int repeatedErrors;
    private DateTimeOffset errorFirst, errorLast;

    internal GameJournal(string root, long segmentBytes = MaximumFileBytes)
    {
        if (segmentBytes < 1024 || segmentBytes > MaximumFileBytes) throw new ArgumentOutOfRangeException(nameof(segmentBytes));
        this.segmentBytes = segmentBytes;
        this.root = Path.GetFullPath(root);
        directory = Path.Combine(this.root, $"mjcn-game-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{sessionId:N}");
    }

    internal string DirectoryPath => directory;
    internal string? Fault { get { lock (gate) return fault; } }
    internal Task Completion { get { lock (gate) return completion; } }

    internal bool Event<T>(string kind, T data) => Append("events.jsonl", kind, data);
    internal bool AppendRecorded(string kind, JsonElement data, DateTimeOffset utc) => Append("events.jsonl", kind, data, utc);
    internal bool Error<T>(string code, T data)
    {
        lock (gate)
        {
            // Sampling sequence and time are not distinct error causes.
            string key = code + ":" + Mahjong.Cn.Events.PublicObservationFingerprint.Compute(JsonSerializer.SerializeToElement(data));
            var now = DateTimeOffset.UtcNow;
            if (key == errorKey)
            {
                repeatedErrors++; errorLast = now;
                if (now - errorFirst < TimeSpan.FromSeconds(30)) return fault is null;
                FlushRepeatedErrors(); errorFirst = now; return fault is null;
            }
            FlushRepeatedErrors();
            errorKey = key; errorCode = code; errorFirst = errorLast = now;
            // Error details may contain positions (@-1:response), file names or
            // localized text. They are payload, not a journal event identifier.
            // Never turn a reported engine error into a permanent writer fault.
            return IsValidKind(code) ? Append("errors.jsonl", code, data)
                : Append("errors.jsonl", "GAMEPLAY_DIAGNOSTIC_ERROR", new { Code = code, Detail = data });
        }
    }

    internal void ErrorsRecovered()
    {
        lock (gate)
        {
            if (errorKey is null) return;
            FlushRepeatedErrors();
            Append("errors.jsonl", "READ_RECOVERED", new { Utc = DateTimeOffset.UtcNow });
            errorKey = null;
        }
    }

    private void FlushRepeatedErrors()
    {
        if (repeatedErrors == 0) return;
        Append("errors.jsonl", "REPEATED_ERROR_SUMMARY", new { FirstUtc = errorFirst, LastUtc = errorLast,
            Code = errorCode, RepeatedCount = repeatedErrors, ErrorKeySha256 = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(errorKey!))) });
        repeatedErrors = 0;
    }

    internal void Diagnostic<T>(string kind, T data)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { Utc = DateTimeOffset.UtcNow, Kind = kind, Data = data }, PayloadOptions);
        lock (gate)
        {
            if (bytes.Length > MaximumLineBytes) return;
            diagnostics.Enqueue(bytes); diagnosticBytes += bytes.Length;
            while (diagnostics.Count > 128 || diagnosticBytes > 2 * 1024 * 1024) diagnosticBytes -= diagnostics.Dequeue().Length;
        }
    }

    private bool Append<T>(string file, string kind, T data, DateTimeOffset? recordedUtc = null)
    {
        lock (gate)
        {
            if (closed || fault is not null) return false;
            if (!IsValidKind(kind))
                return Fail("JOURNAL_KIND_INVALID");
            if (pending >= MaximumPending) return Fail("JOURNAL_QUEUE_FULL");
            // Only explicit managed DTOs are passed by the plugin. No memory or service callback survives serialization.
            JsonElement payload;
            try
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(data, PayloadOptions);
                int payloadLimit = file == "events.jsonl" && JournalPayloadCodec.Eligible(kind)
                    ? JournalPayloadCodec.MaximumDecodedBytes : MaximumLineBytes - 1024;
                if (json.Length > payloadLimit) return Fail("JOURNAL_ENTRY_TOO_LARGE");
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return Fail("JOURNAL_OBJECT_REQUIRED");
                payload = document.RootElement.Clone();
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            { return Fail("JOURNAL_SERIALIZATION_FAILED"); }
            DateTimeOffset utc = recordedUtc ?? DateTimeOffset.UtcNow;
            Task previous = completion;
            pending++;
            completion = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    if (Fault is not null) return;
                    EnsureDirectory();
                    var head = heads[file];
                    var body = new JournalBody(2, sessionId, head.Sequence + 1, utc, kind,
                        file == "events.jsonl" ? codec.Encode(kind, payload, head.Sequence + 1, utc) : payload, head.Hash);
                    string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body)));
                    byte[] line = Utf8.GetBytes(JsonSerializer.Serialize(new JournalLine(body, hash)) + "\n");
                    if (line.Length > MaximumLineBytes) throw new JournalException("JOURNAL_ENTRY_TOO_LARGE");
                    // Retention manages CLOSED records independently. An arbitrary sum
                    // of previous matches must never prevent recording the current one.
                    string name = SegmentName(file, volumes[file]);
                    if (lengths.GetValueOrDefault(name) > 0 && lengths[name] + line.Length > segmentBytes)
                        name = SegmentName(file, ++volumes[file]);
                    string path = Path.Combine(directory, name);
                    RejectLinks(path);
                    await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                        4096, FileOptions.Asynchronous);
                    if (stream.Length + line.Length > MaximumFileBytes) throw new JournalException("JOURNAL_LINE_LIMIT");
                    await stream.WriteAsync(line).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                    heads[file] = (body.Sequence, hash);
                    lengths[name] = stream.Length;
                    WriteIndex();
                }
                catch (Exception ex)
                {
                    lock (gate) fault = ex is JournalException j ? j.Code : "JOURNAL_WRITE_FAILED_" + ex.GetType().Name;
                    SaveEmergency(new { Utc = DateTimeOffset.UtcNow, Reason = Fault, Kind = kind });
                }
                finally { lock (gate) pending--; }
            });
            return true;
        }
    }

    private static bool IsValidKind(string kind) => kind.Length is >= 1 and <= 80 &&
        kind.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    internal Task CompleteAsync()
    {
        lock (gate)
        {
            if (closed) return completion;
            FlushRepeatedErrors(); closed = true;
            Task previous = completion;
            return completion = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    if (initialized)
                    {
                        string marker = Path.Combine(directory, "record-closed.json");
                        RejectLinks(marker);
                        await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new
                        { Schema = 1, SessionId = sessionId, ClosedUtc = DateTimeOffset.UtcNow, Fault })).ConfigureAwait(false);
                    }
                }
                finally { sessionLease?.Dispose(); sessionLease = null; }
            });
        }
    }

    private bool Fail(string code) { fault = code; return false; }

    private void EnsureDirectory()
    {
        if (initialized) return;
        RejectLinks(root);
        Directory.CreateDirectory(root);
        RejectLinks(directory);
        if (Directory.Exists(directory)) throw new JournalException("JOURNAL_SESSION_COLLISION");
        Directory.CreateDirectory(directory);
        sessionLease = new FileStream(Path.Combine(directory, ".active"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
        foreach (string name in new[] { "events.jsonl", "errors.jsonl" })
        {
            using var stream = new FileStream(Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        initialized = true;
        lengths["events.jsonl"] = lengths["errors.jsonl"] = 0;
        WriteIndex();
    }

    private static string SegmentName(string file, int volume) => volume == 0 ? file :
        Path.GetFileNameWithoutExtension(file) + $".{volume:D4}.jsonl";
    internal static bool IsJournalFile(string name) => Regex.IsMatch(name, @"\A(events|errors)(\.[0-9]{4})?\.jsonl\z", RegexOptions.CultureInvariant);

    private void WriteIndex()
    {
        string temporary = Path.Combine(directory, "journal-index.tmp");
        string destination = Path.Combine(directory, "journal-index.json");
        RejectLinks(temporary); RejectLinks(destination);
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(new { Schema = 2, SessionId = sessionId, Files = lengths }));
        ReplaceAtomically(temporary, destination);
    }

    // Windows scanners/readers may briefly deny delete-sharing on an existing index.
    // Retry ONLY the atomic replacement, never append the already committed event again.
    internal static void ReplaceAtomically(string temporary, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            RejectLinks(temporary); RejectLinks(destination);
            try { File.Move(temporary, destination, true); return; }
            catch (Exception ex) when (attempt < 5 && (ex is IOException or UnauthorizedAccessException) &&
                (ex.HResult & 0xffff) is 5 or 32 or 33)
            { Thread.Sleep(10 << attempt); }
        }
    }

    internal static string[] StreamFiles(string directory, string file)
    {
        RejectLinks(directory);
        string stem = Path.GetFileNameWithoutExtension(file);
        var files = Directory.EnumerateFiles(directory).Where(p => IsJournalFile(Path.GetFileName(p)) &&
            Path.GetFileName(p).StartsWith(stem + ".", StringComparison.Ordinal))
            .OrderBy(p => Path.GetFileName(p) == file ? "" : Path.GetFileName(p), StringComparer.Ordinal).ToArray();
        for (int i = 0; i < files.Length; i++)
        {
            if (Path.GetFileName(files[i]) != SegmentName(file, i)) throw new IOException("JOURNAL_SEGMENT_MISSING");
            RejectLinks(files[i]);
        }
        string index = Path.Combine(directory, "journal-index.json");
        if (File.Exists(index))
        {
            RejectLinks(index);
            if (new FileInfo(index).Length > 65536) throw new IOException("JOURNAL_INDEX_LIMIT");
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(index));
            foreach (var entry in manifest.RootElement.GetProperty("Files").EnumerateObject())
            {
                if (!IsJournalFile(entry.Name)) throw new IOException("JOURNAL_INDEX_INVALID");
                if (!entry.Name.StartsWith(stem + ".", StringComparison.Ordinal)) continue;
                string path = Path.Combine(directory, entry.Name); RejectLinks(path);
                if (!File.Exists(path) || new FileInfo(path).Length < entry.Value.GetInt64())
                    throw new IOException("JOURNAL_SEGMENT_MISSING");
            }
        }
        return files.Length == 0 ? [Path.Combine(directory, file)] : files;
    }

    internal Task SaveStopAsync<T>(T value)
    {
        lock (gate)
        {
            Task previous = completion;
            return completion = Task.Run(async () => { await previous.ConfigureAwait(false); SaveEmergency(value); });
        }
    }

    internal void UpdateRecovery<T>(T value)
    {
        JsonElement payload = JsonSerializer.SerializeToElement(value, PayloadOptions);
        if (Utf8.GetByteCount(payload.GetRawText()) > JournalPayloadCodec.MaximumDecodedBytes) return;
        lock (gate)
        {
            if (closed || fault is not null) return;
            Task previous = completion;
            completion = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    if (Fault is not null) return;
                    EnsureDirectory();
                    var body = new JournalBody(2, sessionId, 1, DateTimeOffset.UtcNow, "recovery_checkpoint",
                        new JournalPayloadCodec().Encode("recovery_checkpoint", payload, 1, DateTimeOffset.UtcNow), new string('0', 64));
                    string hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body)));
                    string path = Path.Combine(directory, "recovery-latest.jsonl"), temporary = path + ".tmp";
                    RejectLinks(path); RejectLinks(temporary);
                    string line = JsonSerializer.Serialize(new JournalLine(body, hash)) + "\n";
                    if (Utf8.GetByteCount(line) > MaximumLineBytes) throw new IOException("JOURNAL_ENTRY_TOO_LARGE");
                    await File.WriteAllTextAsync(temporary, line, Utf8).ConfigureAwait(false);
                    ReplaceAtomically(temporary, path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { lock (gate) fault = "JOURNAL_RECOVERY_WRITE_FAILED"; SaveEmergency(new { Reason = Fault }); }
            });
        }
    }

    private void SaveEmergency<T>(T value)
    {
        try
        {
            RejectLinks(root); Directory.CreateDirectory(root);
            string path = Path.Combine(root, "last-stop.json"); string temporary = path + ".tmp";
            RejectLinks(path); RejectLinks(temporary);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { Schema = 1, SessionId = sessionId, Stop = value }, PayloadOptions);
            if (bytes.Length > 16384) return;
            File.WriteAllBytes(temporary, bytes); ReplaceAtomically(temporary, path);
            if (Directory.Exists(directory))
            {
                byte[][] ring;
                lock (gate) ring = diagnostics.ToArray();
                string detail = Path.Combine(directory, "diagnostic-fault.jsonl"), detailTemp = detail + ".tmp";
                RejectLinks(detail); RejectLinks(detailTemp);
                using (var stream = new FileStream(detailTemp, FileMode.Create, FileAccess.Write, FileShare.None))
                    foreach (var row in ring) { stream.Write(row); stream.WriteByte((byte)'\n'); }
                ReplaceAtomically(detailTemp, detail);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Memory Fault remains visible if disk itself is unavailable. */ }
    }

    internal Task<string> ExportAsync(string destination)
    {
        lock (gate)
        {
            FlushRepeatedErrors();
            Task previous = completion;
            byte[][] ring = diagnostics.ToArray();
            var task = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                RejectLinks(destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var zip = new ZipArchive(output, ZipArchiveMode.Create);
                var privacyStreams = new Dictionary<string, JournalPrivacyExport>();
                var exportedLengths = new Dictionary<string, long>();
                foreach (string file in StreamFiles(directory, "events.jsonl").Concat(StreamFiles(directory, "errors.jsonl"))
                    .Concat(new[] { Path.Combine(directory, "journal-index.json"), Path.Combine(directory, "recovery-latest.jsonl"),
                        Path.Combine(directory, "diagnostic-fault.jsonl"), Path.Combine(root, "last-stop.json") }).Where(File.Exists))
                {
                    RejectLinks(file);
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length > MaximumFileBytes) throw new IOException("JOURNAL_FILE_LIMIT");
                    using var entry = zip.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal).Open();
                    string name = Path.GetFileName(file);
                    if (name == "journal-index.json")
                    {
                        using var sourceIndex = await JsonDocument.ParseAsync(input).ConfigureAwait(false);
                        var derivedIndex = System.Text.Json.Nodes.JsonNode.Parse(sourceIndex.RootElement.GetRawText())!;
                        derivedIndex["Files"] = JsonSerializer.SerializeToNode(exportedLengths);
                        await JsonSerializer.SerializeAsync(entry, derivedIndex).ConfigureAwait(false);
                        continue;
                    }
                    string key = name.StartsWith("events", StringComparison.Ordinal) ? "events" : name.StartsWith("errors", StringComparison.Ordinal) ? "errors" : name;
                    if (!privacyStreams.TryGetValue(key, out var privacy)) privacyStreams[key] = privacy = new();
                    exportedLengths[name] = await privacy.CopyAsync(input, entry, name.EndsWith(".jsonl", StringComparison.Ordinal)).ConfigureAwait(false);
                }
                using (var entry = zip.CreateEntry("diagnostic-ring.jsonl", CompressionLevel.Optimal).Open())
                    foreach (byte[] bytes in ring)
                    {
                        var safe = Diagnostics.DiagnosticPrivacy.Sanitize(JsonSerializer.Deserialize<JsonElement>(bytes));
                        await entry.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(safe)).ConfigureAwait(false); entry.WriteByte((byte)'\n');
                    }
                using (var entry = zip.CreateEntry("export-privacy.json", CompressionLevel.Optimal).Open())
                    JsonSerializer.Serialize(entry, new { SchemaVersion = 1, DerivedSanitizedChain = true, OriginalFilesChanged = false,
                        Provenance = "Actual backend and version retained. Export hashes bind sanitized data, not original disk bytes.",
                        Redacted = "credential fields, player/account identifiers, personal absolute paths and recognizable credential strings" });
                return destination;
            });
            // Export failure must not poison the serialized writer task chain.
            completion = task.ContinueWith(_ => { }, TaskScheduler.Default);
            return task;
        }
    }

    /// <summary>Valid newline-terminated prefix only. A crash tail is reported, never interpreted as an event.</summary>
    internal static async Task<JournalReadResult> ReadAsync(string file, CancellationToken cancellation = default, bool checkpointsOnly = false, bool summaryOnly = false)
    {
        var output = ImmutableArray.CreateBuilder<JournalLine>();
        var decoder = new JournalPayloadCodec();
        string previous = new('0', 64);
        Guid? session = null;
        long totalBytes = 0;
        long sequence = 0, retainedBytes = 0;
        try
        {
            string[] files = Path.GetFileName(file) is "events.jsonl" or "errors.jsonl"
                ? StreamFiles(Path.GetDirectoryName(file)!, Path.GetFileName(file)) : [file];
            foreach (string part in files)
            {
            file = part;
            RejectLinks(file);
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.Asynchronous);
            if (stream.Length > MaximumFileBytes) return new([], false, "JOURNAL_FILE_LIMIT");
            // Bounded by the file and line limits; never StreamReader.ReadLine on an unbounded file.
            byte[] chunk = new byte[64 * 1024];
            using var lineBuffer = new MemoryStream();
            long bytesRead = 0;
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) > 0)
            {
                bytesRead += read;
                totalBytes += read;
                if (totalBytes > MaximumReplayBytes) return new(output.ToImmutable(), false, "JOURNAL_REPLAY_SIZE_LIMIT");
                if (bytesRead > MaximumFileBytes) return new(output.ToImmutable(), false, "JOURNAL_FILE_LIMIT");
                for (int i = 0; i < read; i++)
                {
                    if (chunk[i] != '\n')
                    {
                        if (lineBuffer.Length >= MaximumLineBytes) return new(output.ToImmutable(), false, "JOURNAL_LINE_LIMIT");
                        lineBuffer.WriteByte(chunk[i]);
                        continue;
                    }
                    string json = Utf8.GetString(lineBuffer.GetBuffer(), 0, checked((int)lineBuffer.Length));
                    lineBuffer.SetLength(0);
                    if (sequence >= 65536) return new(output.ToImmutable(), false, "JOURNAL_EVENT_LIMIT");
                    using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 48 });
                    if (!UniqueProperties(document.RootElement)) return new(output.ToImmutable(), false, "JOURNAL_DUPLICATE_PROPERTY");
                    var line = JsonSerializer.Deserialize<JournalLine>(json);
                    if (line?.Entry is not { } entry || entry.Schema is not (1 or 2) || entry.SessionId == Guid.Empty ||
                        entry.Sequence != sequence + 1 || entry.PreviousSha256 != previous ||
                        (session is { } expected && entry.SessionId != expected) || entry.Data.ValueKind != JsonValueKind.Object)
                        return new(output.ToImmutable(), false, "JOURNAL_CHAIN_INVALID");
                    if (document.RootElement.ValueKind != JsonValueKind.Object ||
                        !document.RootElement.TryGetProperty("Entry", out var rawEntry))
                        return new(output.ToImmutable(), false, "JOURNAL_SCHEMA_INVALID");
                    string hash = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(rawEntry.GetRawText())));
                    if (hash != line.Sha256) return new(output.ToImmutable(), false, "JOURNAL_HASH_MISMATCH");
                    session = entry.SessionId;
                    previous = hash;
                    sequence++;
                    // Summary projection still verifies every raw hash/sequence, but does not
                    // decompress or retain unrelated per-frame observations.
                    if (summaryOnly && !MatchSummaryBuilder.Retains(entry.Kind)) continue;
                    var decoded = entry.Schema == 2 ? line with { Entry = entry with { Data = decoder.Decode(entry.Kind, entry.Data, entry.Sequence) } } : line;
                    if (!checkpointsOnly || entry.Kind == "recovery_checkpoint")
                    {
                        if (checkpointsOnly && output.Count == 32) output.RemoveAt(0);
                        if (!checkpointsOnly)
                        {
                            retainedBytes += Utf8.GetByteCount(decoded.Entry.Data.GetRawText());
                            if (retainedBytes > 128 * 1024 * 1024) return new(output.ToImmutable(), false, "JOURNAL_REPLAY_MEMORY_LIMIT");
                        }
                        output.Add(decoded);
                    }
                }
            }
            if (lineBuffer.Length != 0) return new(output.ToImmutable(), true,
                part != files[^1] ? "JOURNAL_INCOMPLETE_MIDDLE_SEGMENT" : null);
            }
            return new(output.ToImmutable(), false, null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or DecoderFallbackException or ArgumentException or
            InvalidOperationException or KeyNotFoundException or NullReferenceException or FormatException or IndexOutOfRangeException)
        { return new(output.ToImmutable(), false, Regex.IsMatch(ex.Message, @"\AJOURNAL_[A-Z_]+\z")
            ? ex.Message : "JOURNAL_READ_FAILED_" + ex.GetType().Name); }
    }

    internal static string[] RecentDirectories(string root, int limit = 16)
    {
        RejectLinks(root);
        if (!Directory.Exists(root)) return [];
        var paths = Directory.EnumerateDirectories(root).Where(p => OwnDirectory.IsMatch(Path.GetFileName(p)))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Take(Math.Clamp(limit,1,500)).ToArray();
        foreach (string path in paths) RejectLinks(path);
        return paths;
    }

    private static bool UniqueProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() ==
            element.EnumerateObject().Count() && element.EnumerateObject().All(x => UniqueProperties(x.Value)),
        JsonValueKind.Array => element.EnumerateArray().All(UniqueProperties),
        _ => true,
    };

    internal static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("JOURNAL_LINK_REJECTED");
        }
    }

    private sealed class JournalException(string code) : IOException(code) { internal string Code => Message; }
}
