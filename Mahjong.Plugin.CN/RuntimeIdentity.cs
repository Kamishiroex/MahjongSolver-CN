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
    internal const string TargetDalamud = "a198a02bdce1f3213cad618850ab1b2736307588";
    internal const string TargetStructs = "1.0.0+243dc41e4d71f350cd80aa5eba8c75517f3d5154";
    internal const string Upstream = "21ae5ca9aa1fa3785baa540245b51efa346f9d37";

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
        string? error = Validate(game, api, version.GitHash, structs, client.ClientLanguage);
        return new(game, api, version.Version.ToString(), version.GitHash ?? "unknown", structs,
            client.ClientLanguage.ToString(), Environment.Version.ToString(), error);
    }

    internal static string? Validate(string game, int api, string? dalamud, string structs, ClientLanguage language) =>
        game != TargetGame ? $"VERSION_GAME：需要 {TargetGame}，实际 {game}。"
            : api != 15 ? $"VERSION_API：需要 API 15，实际 {api}。"
            : dalamud != TargetDalamud ? $"VERSION_DALAMUD：需要框架提交 {TargetDalamud}，实际 {dalamud ?? "unknown"}；请更新插件以匹配框架。"
            : structs != TargetStructs ? $"VERSION_STRUCTS：需要 {TargetStructs}，实际 {structs}；停止读取。"
            : language != ClientLanguage.ChineseSimplified ? "VERSION_LANGUAGE：需要简体中文客户端。"
            : null;
}
