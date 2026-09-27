using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>
/// Writes only an already copied, caller-selected JSON payload. No game/native, account,
/// network, or serialization callbacks are used on the background worker.
/// </summary>
internal sealed class GameplayStopRecorder
{
    internal const int RetainedFiles = 20;
    internal const int MaximumPending = 8;
    internal const int MaximumCharacters = 256 * 1024;
    private static readonly Regex OwnFileName = new(
        @"\Amjcn-stop-[0-9]{8}T[0-9]{13}Z-[0-9a-f]{32}\.json\z",
        RegexOptions.CultureInvariant);
    private readonly object gate = new();
    private readonly string diagnosticsDirectory;
    private Task completion = Task.CompletedTask;
    private int pending;
    private bool completed;
    private string? lastPath;
    private string? lastError;

    // Resolve/check paths on the worker as well: inaccessible storage must never break loading.
    internal GameplayStopRecorder(string diagnosticsDirectory) => this.diagnosticsDirectory = diagnosticsDirectory;

    internal string? LastPath { get { lock (gate) return lastPath; } }
    internal string? LastError { get { lock (gate) return lastError; } }

    /// <summary>A snapshot of the task that drains all writes accepted so far; it never faults.</summary>
    internal Task Completion { get { lock (gate) return completion; } }

    /// <summary>Nonblocking; an immutable string is the only data retained from the caller.</summary>
    internal bool TryRecord(string copiedJson)
    {
        lock (gate)
        {
            if (completed) return Reject("STOP_RECORDER_CLOSED");
            if (string.IsNullOrWhiteSpace(copiedJson) || copiedJson.Length > MaximumCharacters)
                return Reject("STOP_RECORD_INVALID_SIZE");
            if (pending >= MaximumPending) return Reject("STOP_RECORDER_QUEUE_FULL");
            Task previous = completion;
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            pending++;
            completion = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try { await WriteAsync(copiedJson, timestamp).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    lock (gate) lastError = $"STOP_RECORD_WRITE_FAILED: {ex.GetType().Name}: {ex.Message}";
                }
                finally { lock (gate) pending--; }
            });
            return true;
        }
    }

    internal Task CompleteAsync()
    {
        lock (gate)
        {
            completed = true;
            return completion;
        }
    }

    private bool Reject(string error)
    {
        lastError = error;
        return false;
    }

    private async Task WriteAsync(string json, DateTimeOffset timestamp)
    {
        using (var document = JsonDocument.Parse(json))
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Stop record must be a JSON object.");

        string directory = Path.Combine(Path.GetFullPath(diagnosticsDirectory), "gameplay-stops");
        RejectReparseAncestors(directory);
        Directory.CreateDirectory(directory);
        RejectReparseAncestors(directory);
        string name = $"mjcn-stop-{timestamp.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.json";
        string path = Path.Combine(directory, name);
        bool created = false;
        try
        {
            // CreateNew never overwrites a pre-existing file (including an existing link).
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                created = true;
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(bytes).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            Prune(directory);
            lock (gate)
            {
                lastPath = path;
                lastError = null;
            }
        }
        catch
        {
            if (created)
            {
                // Only this worker's newly-created file, never a recursive/foreign cleanup.
                try { DeleteOwnFile(directory, path); }
                catch { /* Preserve the original write error. */ }
            }
            throw;
        }
    }

    private static void Prune(string directory)
    {
        RejectReparseAncestors(directory);
        var files = Directory.EnumerateFiles(directory, "mjcn-stop-*.json", SearchOption.TopDirectoryOnly)
            .Where(path => OwnFileName.IsMatch(Path.GetFileName(path)))
            .Where(path => (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0)
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        foreach (string path in files.Skip(RetainedFiles)) DeleteOwnFile(directory, path);
    }

    private static void DeleteOwnFile(string directory, string path)
    {
        RejectReparseAncestors(directory);
        string absolute = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(absolute), directory, StringComparison.OrdinalIgnoreCase) ||
            !OwnFileName.IsMatch(Path.GetFileName(absolute)))
            throw new IOException("Refused to remove a file outside the stop recorder's own namespace.");
        if ((File.GetAttributes(absolute) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("Refused to remove a linked stop record.");
        File.Delete(absolute);
    }

    private static void RejectReparseAncestors(string path)
    {
        var ancestors = new Stack<string>();
        for (DirectoryInfo? item = new(path); item is not null; item = item.Parent)
            ancestors.Push(item.FullName);
        foreach (string item in ancestors)
        {
            // Inspect parents first. GetAttributes also sees dangling links, which Exists hides.
            FileAttributes attributes;
            try { attributes = File.GetAttributes(item); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Stop recording refuses reparse points in its directory path.");
        }
    }
}
