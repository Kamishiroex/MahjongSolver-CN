using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mahjong.Plugin.CN.Access;

/// <summary>Remembers a source choice, never execution authority or a test lease.</summary>
internal sealed class SolverPreferenceStore
{
    private readonly string path;
    private bool writable = true;
    private sealed record Preference(int SchemaVersion, bool PreferBeta);
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal bool PreferBeta { get; private set; }
    internal string? Error { get; private set; }
    internal SolverPreferenceStore(string path)
    {
        this.path = path;
        try
        {
            if (!File.Exists(path)) return;
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException();
            var saved = JsonSerializer.Deserialize<Preference>(File.ReadAllText(path), Json);
            if (saved is not { SchemaVersion: 1 }) throw new InvalidDataException();
            PreferBeta = saved.PreferBeta;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        { writable = false; Error = "求解器偏好读取失败，暂用标准求解器：" + ex.GetType().Name; }
    }
    internal bool Save(bool preferBeta)
    {
        if (!writable) return false; // Preserve unknown/corrupt records for local repair.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Preference(1, preferBeta)));
            File.Move(temp, path, true);
            PreferBeta = preferBeta; Error = null; return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Error = "求解器偏好保存失败，本次选择仍有效：" + ex.GetType().Name; return false; }
    }
}
