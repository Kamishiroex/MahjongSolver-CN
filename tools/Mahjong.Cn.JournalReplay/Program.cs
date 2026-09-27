using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mahjong.Plugin.CN.Journaling;

if (args.Length != 2) throw new ArgumentException("JournalReplay <original events.jsonl> <new output directory>");
if (Directory.Exists(args[1]) || File.Exists(args[1])) throw new IOException("Output must not exist.");
var source = await GameJournal.ReadAsync(args[0]);
if (!source.IntegrityPassed || source.IncompleteTail || source.Lines.Length == 0)
    throw new IOException("Input must be a verified complete recorded prefix: " + source.Error);
byte[] original = File.ReadAllBytes(args[0]);
var watch = Stopwatch.StartNew();
// Force rotation below the production 32 MiB ceiling, through exactly the same writer.
var journal = new GameJournal(args[1], 256 * 1024);
for (int i = 0; i < source.Lines.Length; i++)
{
    var entry = source.Lines[i].Entry;
    if (!journal.AppendRecorded(entry.Kind, entry.Data, entry.Utc)) throw new IOException(journal.Fault);
    if (i % 16 == 0) await journal.Completion;
}
await journal.CompleteAsync();
if (journal.Fault is not null) throw new IOException(journal.Fault);
var replay = await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "events.jsonl"));
if (!replay.IntegrityPassed || replay.IncompleteTail || replay.Lines.Length != source.Lines.Length)
    throw new IOException("Replay integrity/count failure: " + replay.Error);
for (int i = 0; i < source.Lines.Length; i++)
{
    var a = source.Lines[i].Entry; var b = replay.Lines[i].Entry;
    if (a.Kind != b.Kind || a.Sequence != b.Sequence || a.Utc != b.Utc || !JsonElement.DeepEquals(a.Data, b.Data))
        throw new IOException("Lossless replay mismatch at event " + a.Sequence);
}
string[] parts = GameJournal.StreamFiles(journal.DirectoryPath, "events.jsonl");
long stored = parts.Sum(p => new FileInfo(p).Length) + new FileInfo(Path.Combine(journal.DirectoryPath, "journal-index.json")).Length;
double reduction = 1 - (double)stored / original.Length;
if (reduction < .8 || parts.Length < 2) throw new IOException("Reduction or forced-rotation target not met.");
await journal.ExportAsync(Path.Combine(args[1], "verified-export.zip"));
var report = new
{
    Schema = 1, Passed = true, Mode = "Lossless replay of all original records, including raw diagnostics; conservative storage bound",
    SourceSha256 = Convert.ToHexString(SHA256.HashData(original)), SourceBytes = original.Length,
    SourceRecords = source.Lines.Length, ReplayedRecords = replay.Lines.Length, StoredBytes = stored,
    ReductionPercent = Math.Round(reduction * 100, 2), Segments = parts.Length, ForcedSegmentBytes = 256 * 1024,
    AllPayloadsAndKindsAndSequencesAndTimesEqual = true, JournalFault = journal.Fault,
    ProducerAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(GameJournal).Assembly.Location))),
    ElapsedMilliseconds = watch.ElapsedMilliseconds, GameCallbacksSubmitted = false,
    Notes = "Fresh writer session envelope; all original event Data including uncertainties is retained. This is offline storage verification, not live game verification. Normal plugin logging additionally moves raw table diagnostics to a bounded ring and rate-limits historical recovery checkpoints.",
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(args[1], "report.json"), json);
Console.WriteLine(json);
