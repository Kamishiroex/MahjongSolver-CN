using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>Export boundary only. Stored records and true technical provenance are not rewritten.</summary>
internal static class DiagnosticPrivacy
{
    private static readonly Regex PersonalPath = new(@"(?<![A-Za-z0-9])[A-Za-z]:[\\/][^\r\n\""<>|]*|\\\\[^\r\n\""<>|]+|/Users/[^\s\""<>]+|/home/[^\s\""<>]+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Credential = new(@"\b(?:Bearer\s+\S+|gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+)|(?i:(?:password|token|testcode|cookie|authorization)\s*[:=]\s*[^\s,;]+)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly HashSet<string> SecretKeys = new(StringComparer.OrdinalIgnoreCase)
    { "TestCode", "Password", "Token", "AccessToken", "RefreshToken", "Authorization", "Cookie", "PrivateKey", "ConnectionString",
      "CharacterName", "PlayerName", "AccountName", "ContentId", "CharacterContext", "UserName" };

    internal static JsonElement Sanitize(JsonElement data)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) Write(writer, data);
        return JsonSerializer.Deserialize<JsonElement>(output.ToArray());
    }
    internal static void Serialize<T>(Stream output, T value) =>
        JsonSerializer.Serialize(output, Sanitize(JsonSerializer.SerializeToElement(value)), new JsonSerializerOptions { WriteIndented = true });
    private static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var item in value.EnumerateObject())
                {
                    writer.WritePropertyName(item.Name);
                    if (SecretKeys.Contains(item.Name)) writer.WriteStringValue("[redacted]");
                    else Write(writer, item.Value);
                }
                writer.WriteEndObject(); break;
            case JsonValueKind.Array:
                writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) Write(writer, item); writer.WriteEndArray(); break;
            case JsonValueKind.String:
                string text = value.GetString()!;
                writer.WriteStringValue(Credential.Replace(PersonalPath.Replace(text, "[local-path]"), "[redacted]")); break;
            default: value.WriteTo(writer); break;
        }
    }
}
