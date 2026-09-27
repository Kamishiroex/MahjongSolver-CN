using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mahjong.Plugin.CN.Journaling;

/// <summary>Lossless storage only. Observations, uncertainty and event chronology are not changed.</summary>
internal sealed class JournalPayloadCodec
{
    // A ledger snapshot permits 256 KiB, plus its event envelope. Disk rows remain 128 KiB.
    internal const int MaximumDecodedBytes = 512 * 1024;
    private readonly Dictionary<string, (long Sequence, JsonNode Data)> previous = new();
    private readonly Dictionary<string, DateTimeOffset> anchors = new();
    internal static bool Eligible(string kind) => kind is "public_event" or "public_table_observation" or
        "runtime_observation" or "recovery_checkpoint" || kind.StartsWith("global_ai_", StringComparison.Ordinal);

    internal JsonElement Encode(string kind, JsonElement data, long sequence, DateTimeOffset utc)
    {
        if (!Eligible(kind)) return data;
        var current = JsonNode.Parse(data.GetRawText())!;
        var full = Pack(new JsonObject { ["Full"] = current.DeepClone() });
        long basis = 0;
        byte[] packed = full;
        bool anchor = kind == "recovery_checkpoint" || !anchors.TryGetValue(kind, out var time) ||
            utc - time >= TimeSpan.FromSeconds(30);
        if (!anchor && previous.TryGetValue(kind, out var before))
        {
            var changes = new JsonArray();
            Diff(before.Data, current, new JsonArray(), changes);
            var patch = new JsonObject { ["Changes"] = changes };
            if (JsonSerializer.SerializeToUtf8Bytes(patch).Length <= MaximumDecodedBytes * 4)
            {
                byte[] delta = Pack(patch);
                if (delta.Length < full.Length) { packed = delta; basis = before.Sequence; }
            }
        }
        if (basis == 0) anchors[kind] = utc;
        previous[kind] = (sequence, current);
        return JsonSerializer.SerializeToElement(new { Codec = "json-delta-gzip-v1", BaseSequence = basis,
            Payload = Convert.ToBase64String(packed) });
    }

    internal JsonElement Decode(string kind, JsonElement encoded, long sequence)
    {
        if (!encoded.TryGetProperty("Codec", out var codec)) return encoded;
        if (!Eligible(kind) || codec.GetString() != "json-delta-gzip-v1") throw new IOException("JOURNAL_CODEC_INVALID");
        byte[] compressed = Convert.FromBase64String(encoded.GetProperty("Payload").GetString()!);
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = gzip.Read(buffer)) > 0)
        {
            if (output.Length + count > MaximumDecodedBytes * 4) throw new IOException("JOURNAL_DECODE_LIMIT");
            output.Write(buffer, 0, count);
        }
        var patch = JsonNode.Parse(output.ToArray(), documentOptions: new() { MaxDepth = 48 })!.AsObject();
        long basis = encoded.GetProperty("BaseSequence").GetInt64();
        JsonNode current;
        if (basis == 0) current = patch["Full"]?.DeepClone() ?? throw new IOException("JOURNAL_BASE_INVALID");
        else
        {
            if (!previous.TryGetValue(kind, out var before) || before.Sequence != basis || basis >= sequence)
                throw new IOException("JOURNAL_BASE_MISSING");
            current = before.Data.DeepClone();
            foreach (var item in patch["Changes"]!.AsArray()) Apply(ref current, item!.AsObject());
        }
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(current);
        if (bytes.Length > MaximumDecodedBytes || current is not JsonObject) throw new IOException("JOURNAL_DECODE_LIMIT");
        previous[kind] = (sequence, current);
        return JsonSerializer.Deserialize<JsonElement>(bytes);
    }

    private static byte[] Pack(JsonNode value)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
            JsonSerializer.Serialize(gzip, value);
        return output.ToArray();
    }

    private static void Diff(JsonNode? before, JsonNode? after, JsonArray path, JsonArray changes)
    {
        if (JsonNode.DeepEquals(before, after)) return;
        if (before is JsonObject a && after is JsonObject b)
        {
            foreach (var pair in a.Where(p => !b.ContainsKey(p.Key)))
                changes.Add(new JsonObject { ["Path"] = Child(path, pair.Key), ["Remove"] = true });
            foreach (var pair in b)
                if (a.ContainsKey(pair.Key)) Diff(a[pair.Key], pair.Value, Child(path, pair.Key), changes);
                else changes.Add(new JsonObject { ["Path"] = Child(path, pair.Key), ["Value"] = pair.Value?.DeepClone() });
        }
        else if (before is JsonArray aa && after is JsonArray bb && aa.Count == bb.Count)
        {
            for (int i = 0; i < aa.Count; i++) Diff(aa[i], bb[i], Child(path, i), changes);
        }
        else changes.Add(new JsonObject { ["Path"] = path.DeepClone(), ["Value"] = after?.DeepClone() });
    }

    private static JsonArray Child<T>(JsonArray path, T key)
    {
        var child = (JsonArray)path.DeepClone(); child.Add(JsonSerializer.SerializeToNode(key)); return child;
    }

    private static void Apply(ref JsonNode root, JsonObject change)
    {
        var path = change["Path"]!.AsArray();
        if (path.Count == 0) { root = change["Value"]!.DeepClone(); return; }
        JsonNode parent = root;
        for (int i = 0; i < path.Count - 1; i++)
            parent = parent is JsonArray array ? array[path[i]!.GetValue<int>()]! : parent[path[i]!.GetValue<string>()]!;
        if (parent is JsonArray target) target[path[^1]!.GetValue<int>()] = change["Value"]?.DeepClone();
        else if (change["Remove"]?.GetValue<bool>() == true) parent.AsObject().Remove(path[^1]!.GetValue<string>());
        else parent[path[^1]!.GetValue<string>()] = change["Value"]?.DeepClone();
    }
}
