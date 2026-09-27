using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mahjong.Plugin.CN.Diagnostics;
namespace Mahjong.Plugin.CN.Journaling;

/// <summary>Sanitized derived chain; original disk files and source hashes remain untouched.</summary>
internal sealed class JournalPrivacyExport
{
    private readonly JournalPayloadCodec decoder = new(), encoder = new();
    private string previous = new('0',64);
    private string sourcePrevious = new('0',64);
    internal async Task<long> CopyAsync(Stream source, Stream destination, bool lines)
    {
        using var reader = new StreamReader(source, new UTF8Encoding(false, true), false, 4096, true);
        using var writer = new StreamWriter(destination, new UTF8Encoding(false), 4096, true);
        writer.NewLine = "\n";
        long bytes = 0;
        if (!lines)
        {
            var value = JsonSerializer.Deserialize<JsonElement>(await reader.ReadToEndAsync());
            string safe = DiagnosticPrivacy.Sanitize(value).GetRawText();
            await writer.WriteAsync(safe); return Encoding.UTF8.GetByteCount(safe);
        }
        while (await reader.ReadLineAsync() is { } text)
        {
            if (Encoding.UTF8.GetByteCount(text) > GameJournal.MaximumLineBytes) throw new IOException("EXPORT_LINE_LIMIT");
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("Entry", out var entry) && doc.RootElement.TryGetProperty("Sha256", out var hash))
            {
                if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.GetRawText()))) != hash.GetString())
                    throw new IOException("EXPORT_SOURCE_HASH_MISMATCH");
                var body = entry.Deserialize<JournalBody>()!;
                if (body.PreviousSha256 != sourcePrevious) throw new IOException("EXPORT_SOURCE_CHAIN_MISMATCH");
                sourcePrevious = hash.GetString()!;
                var decoded = decoder.Decode(body.Kind, body.Data, body.Sequence);
                var safe = DiagnosticPrivacy.Sanitize(decoded);
                var data = encoder.Encode(body.Kind, safe, body.Sequence, body.Utc);
                body = body with { Data = data, PreviousSha256 = previous };
                previous = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(body)));
                string line = JsonSerializer.Serialize(new JournalLine(body, previous));
                bytes += Encoding.UTF8.GetByteCount(line) + 1;
                await writer.WriteLineAsync(line);
            }
            else
            {
                string line = DiagnosticPrivacy.Sanitize(doc.RootElement).GetRawText();
                bytes += Encoding.UTF8.GetByteCount(line) + 1;
                await writer.WriteLineAsync(line);
            }
            if (bytes > GameJournal.MaximumFileBytes) throw new IOException("EXPORT_FILE_LIMIT");
        }
        return bytes;
    }
}
