using System.Diagnostics;
using System.Reflection;
using Dalamud.Game;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN;

internal sealed record RuntimeIdentity(string GameVersion, int Api, string DalamudVersion,
    string DalamudCommit, string ClientStructsVersion, string Language, string Runtime, string? Error)
{
    internal const string TargetGame = "2026.09.15.0000.0000";
    // Reproducible build baseline, not a runtime commit allowlist.
    internal const string TargetDalamud = "fbef681c15fd7c57a850b32b4020fcf5474374c7";
    internal const string TargetStructs = "1.0.0+af18b1116ddd23d1eddfc345f8eef6974d8f84d3";
    internal const string MinimumDalamud = "15.0.3.6";
    internal const string Upstream = "21ae5ca9aa1fa3785baa540245b51efa346f9d37";
    public string? CompatibilityProfile { get; init; }

    internal static RuntimeIdentity Read(IDalamudPluginInterface pi, IClientState client)
    {
        var version = pi.GetDalamudVersion();
        string game = "unknown";
        try
        {
            using var process = Process.GetCurrentProcess();
            var exe = process.MainModule?.FileName;
            if (exe is not null && Path.GetFileName(exe).Equals("ffxiv_dx11.exe", StringComparison.OrdinalIgnoreCase))
                game = File.ReadAllText(Path.Combine(Path.GetDirectoryName(exe)!, "ffxivgame.ver")).Trim();
        }
        catch (Exception) { /* An unavailable version is a hard stop, never a permissive fallback. */ }
        int api = typeof(IDalamudPlugin).Assembly.GetName().Version?.Major ?? 0;
        string structs = typeof(AtkUnitBase).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        string? error = Validate(game, api, version.GitHash, structs, client.ClientLanguage,
            version.Version.ToString(), FrameworkCompatibility.Error, Environment.Version.Major);
        return new(game, api, version.Version.ToString(), version.GitHash ?? "unknown", structs,
            client.ClientLanguage.ToString(), Environment.Version.ToString(), error)
        { CompatibilityProfile = error is null ? FrameworkCompatibility.Profile : null };
    }

    internal static string? Validate(string game, int api, string? dalamud, string structs, ClientLanguage language,
        string dalamudVersion, string? contractError, int runtimeMajor = 10) =>
        game != TargetGame ? $"VERSION_GAME：需要 {TargetGame}，实际 {game}。"
            : api != 15 ? $"VERSION_API：需要 API 15，实际 {api}。"
            : runtimeMajor != 10 ? $"VERSION_RUNTIME：需要 .NET 10，实际主版本 {runtimeMajor}。"
            : !Version.TryParse(dalamudVersion, out var parsed) || parsed.Major != api || parsed < Version.Parse(MinimumDalamud)
                ? $"VERSION_DALAMUD：需要 API 15 下 {MinimumDalamud} 或更新的兼容框架，实际 {dalamudVersion}。"
            : !IsCommit(dalamud) ? "VERSION_DALAMUD：框架来源身份不可读；停止读取。"
            : !IsCommit(structs.Split('+').LastOrDefault()) ? "VERSION_STRUCTS：结构库来源身份不可读；停止读取。"
            : language != ClientLanguage.ChineseSimplified ? "VERSION_LANGUAGE：需要简体中文客户端。"
            : contractError;

    private static bool IsCommit(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
}
