namespace Mahjong.Plugin.CN.Diagnostics;

internal sealed record RoundTitleMeaning(int RoundWind, int HandNumber, string Label, string Evidence);

/// <summary>Each of these eight local Chinese title textures was decoded and visually inspected.</summary>
internal static class RoundTitleCatalog
{
    internal const string GameVersion = "2026.09.15.0000.0000";
    internal const string Evidence = "cn-emj-round-title-icons-20260924";
    private sealed record Entry(uint Icon, uint NormalHash, uint HighResolutionHash, int Wind, int Hand, string Label);
    // Explicit lookup, never icon arithmetic. 121459 onwards contain table names and
    // ranks, not West/North rounds. Hashes are client CRC32 of actual local resource paths.
    private static readonly Entry[] Entries =
    [
        new(121451, 0xBBC15A27, 0x2D2D7E34, 0, 1, "东一局"),
        new(121452, 0xFC6120F7, 0x14A042F1, 0, 2, "东二局"),
        new(121453, 0xC1010947, 0x03DB56B2, 0, 3, "东三局"),
        new(121454, 0x7321D557, 0x67BA3B7B, 0, 4, "东四局"),
        new(121455, 0x4E41FCE7, 0x70C12F38, 1, 1, "南一局"),
        new(121456, 0x09E18637, 0x494C13FD, 1, 2, "南二局"),
        new(121457, 0x3481AF87, 0x5E3707BE, 1, 3, "南三局"),
        new(121458, 0xB6D13856, 0x818EC86F, 1, 4, "南四局"),
    ];

    internal static bool TryDecode(string gameVersion, RoundTitleResourceCandidate candidate, out RoundTitleMeaning meaning)
    {
        meaning = null!;
        if (gameVersion != GameVersion || candidate.Path != "Emj/19" || candidate.Code != "ROUND_TITLE_RESOURCE_CANDIDATE" ||
            candidate.PartsListId != 0 || candidate.PartCount != 1 || candidate.SelectedPart != 0 ||
            candidate.U != 0 || candidate.V != 0 || candidate.Width != 640 || candidate.Height != 80 ||
            candidate.AssetId != 0 || candidate.TextureType != 1 || candidate.IconId is not { } icon ||
            candidate.TexturePathHash is not { } hash) return false;
        var entry = Entries.SingleOrDefault(x => x.Icon == icon && (x.NormalHash == hash || x.HighResolutionHash == hash));
        if (entry is null) return false;
        meaning = new(entry.Wind, entry.Hand, entry.Label, Evidence);
        return true;
    }
}
