using System.Security.Cryptography;
using System.Text.Json;

namespace Mahjong.Cn.Events;

/// <summary>
/// Comparison only: ignores observation freshness and explicitly identified response
/// highlight animation (including its zero phase). The complete original observation remains the durable
/// payload, and its integrity digest must use the original bytes instead of this value.
/// </summary>
public static class PublicObservationFingerprint
{
    public static string Compute(JsonElement observation)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, observation, null);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void Write(Utf8JsonWriter writer, JsonElement node, string? context)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            // Only the current response-menu lifecycle can authorize pulse normalization.
            // A same-named property, unknown color, changed multiply or real tint is retained.
            bool publicStyle = (context is "Style" or "VisualMark") &&
                (node.TryGetProperty("Name", out _) || node.TryGetProperty("Style", out _)) &&
                node.TryGetProperty("AddRed", out _) && node.TryGetProperty("AddGreen", out _) &&
                node.TryGetProperty("AddBlue", out _) && node.TryGetProperty("MultiplyRed", out _) &&
                node.TryGetProperty("MultiplyGreen", out _) && node.TryGetProperty("MultiplyBlue", out _);
            bool responseHighlight = publicStyle &&
                node.TryGetProperty("ResponseHighlight", out var highlight) && highlight.ValueKind == JsonValueKind.True &&
                Integer(node, "AddRed", out int red) && red >= 0 &&
                Integer(node, "AddGreen", out int green) && green == red &&
                Integer(node, "AddBlue", out int blue) && blue == red &&
                Integer(node, "MultiplyRed", out int mr) && mr == 100 &&
                Integer(node, "MultiplyGreen", out int mg) && mg == 100 &&
                Integer(node, "MultiplyBlue", out int mb) && mb == 100 &&
                (node.TryGetProperty("Name", out var name) ? name : node.GetProperty("Style")).GetString() is "normal" or "unclassified";
            writer.WriteStartObject();
            foreach (var property in node.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (property.Name is "Sequence" or "ObservedAtUtc" or "StateRevision") continue;
                if (responseHighlight && property.Name is "AddRed" or "AddGreen" or "AddBlue") continue;
                writer.WritePropertyName(property.Name);
                if (responseHighlight && property.Name is "Style" or "Name") writer.WriteStringValue("response-highlight");
                else Write(writer, property.Value, property.Name);
            }
            writer.WriteEndObject();
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in node.EnumerateArray()) Write(writer, item, context);
            writer.WriteEndArray();
        }
        else if (context == "DerivationInputs" && node.ValueKind == JsonValueKind.String &&
            IsCurrentIdentitySample(node.GetString()!))
            writer.WriteStringValue("current-identity-observation:<sample>");
        else node.WriteTo(writer);
    }

    private static bool Integer(JsonElement node, string name, out int value)
    {
        value = 0;
        return node.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    private static bool IsCurrentIdentitySample(string value)
    {
        const string prefix = "current-identity-observation:";
        return value.StartsWith(prefix, StringComparison.Ordinal) && value.Length > prefix.Length &&
            value.AsSpan(prefix.Length).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
