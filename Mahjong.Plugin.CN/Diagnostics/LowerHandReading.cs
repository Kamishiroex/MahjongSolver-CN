using System.Collections.Immutable;
using System.Globalization;

namespace Mahjong.Plugin.CN.Diagnostics;

internal readonly record struct LowerTileIdentity(int Kind34, bool RedFive, string ChineseName);
internal sealed record DecodedLowerFace(string Path, int DisplayPosition, uint IconId, uint FacePathHash,
    int Kind34, bool RedFive, string ChineseName);

/// <summary>A partial visible-image observation, never a complete hand, turn or engine snapshot.</summary>
internal sealed record LowerHandReading(string Code, string Reason, bool Stable, ImmutableArray<DecodedLowerFace> Tiles)
{
    public bool CompleteGameState => false;
}

/// <summary>
/// Only the first two visually audited static icon sets. Third-set glyphs, NPC portraits and
/// placeholders are deliberately absent. Both scalar fields must identify the SAME resource.
/// See docs/cn/TILE-RESOURCE-EVIDENCE.md; runtime ownership is checked before these scalars are read.
/// </summary>
internal static class LowerTileCatalog
{
    private static readonly Dictionary<uint, (uint NormalHash, uint HighHash, LowerTileIdentity Tile)> Entries = Build();

    internal static bool TryDecode(uint? iconId, uint? pathHash, out LowerTileIdentity tile)
    {
        tile = default;
        if (iconId is not { } icon || pathHash is not { } hash || !Entries.TryGetValue(icon, out var entry) ||
            (hash != entry.NormalHash && hash != entry.HighHash)) return false;
        tile = entry.Tile;
        return true;
    }

    private static Dictionary<uint, (uint, uint, LowerTileIdentity)> Build()
    {
        var result = new Dictionary<uint, (uint, uint, LowerTileIdentity)>();
        foreach (uint first in new uint[] { 76001, 76041 })
        {
            for (int offset = 0; offset < 37; offset++)
            {
                uint icon = first + (uint)offset;
                int kind = offset < 34 ? offset : offset switch { 34 => 4, 35 => 13, _ => 22 };
                bool red = offset >= 34;
                string name = kind switch
                {
                    < 9 => $"{(red ? "赤" : "")}{kind + 1}万",
                    < 18 => $"{(red ? "赤" : "")}{kind - 8}筒",
                    < 27 => $"{(red ? "赤" : "")}{kind - 17}索",
                    27 => "东", 28 => "南", 29 => "西", 30 => "北", 31 => "白", 32 => "发", _ => "中",
                };
                string path = "ui/icon/076000/" + icon.ToString("D6", CultureInfo.InvariantCulture);
                result.Add(icon, (LowerHandImageReader.ClientTexturePathHash(path + ".tex"),
                    LowerHandImageReader.ClientTexturePathHash(path + "_hr1.tex"), new(kind, red, name)));
            }
        }
        return result;
    }
}

/// <summary>
/// Requires two independently captured, identical lower-face observations. Repainting one UI
/// frame cannot advance stability. Missing/rejected/changed observations invalidate the old result.
/// Stable images can also appear at settlement: this class NEVER identifies a playable turn.
/// </summary>
internal sealed class LowerHandTracker
{
    private long lastSequence = -1;
    private HandFaceCandidate[]? previous;
    private int previousAtkValueCount;

    internal LowerHandReading Observe(long sequence, AddonProbe? addon)
    {
        if (sequence <= lastSequence) return Reject("STALE_CAPTURE", "采样序号未递增；下方识别已清除。");
        lastSequence = sequence;
        if (addon is not { Name: "Emj", Present: true, Visible: true, Ready: true, Error: null })
            return Reject("NO_VISIBLE_LOWER_ROW", "当前无就绪的下方牌面；已清除旧识别。");
        var faces = addon.LowerHandFaces;
        var layout = LowerHandProfile.CheckLayout(faces);
        if (!layout.Eligible) return Reject("LOWER_LAYOUT_UNVERIFIED", layout.Reason);
        var ordered = faces!.OrderBy(x => x.X).ThenBy(x => x.Y).ToArray();
        var decoded = ImmutableArray.CreateBuilder<DecodedLowerFace>(ordered.Length);
        var counts = new int[34];
        foreach (var face in ordered)
        {
            if (face.DiagnosticStatus != LowerHandProfile.VerifiedIconStatus)
                return Reject("LOWER_RESOURCE_REJECTED", $"下方候选检查失败：{face.Path} · {face.DiagnosticStatus}；已清除旧识别。");
            if (!LowerTileCatalog.TryDecode(face.IconId, face.FacePathHash, out var tile))
                return Reject("LOWER_TILE_UNVERIFIED", "图标及哈希未同时匹配已核对的牌面目录；请导出诊断。");
            if (++counts[tile.Kind34] > 4)
                return Reject("LOWER_TILE_COUNT_CONFLICT", "下方候选中同一种牌超过四张；暂不解释为有效牌面。");
            decoded.Add(new(face.Path, decoded.Count + 1, face.IconId!.Value, face.FacePathHash!.Value,
                tile.Kind34, tile.RedFive, tile.ChineseName));
        }
        bool stable = previous is not null && previousAtkValueCount == addon.AtkValueCount && previous.SequenceEqual(ordered);
        previous = ordered;
        previousAtkValueCount = addon.AtkValueCount;
        return stable
            ? new("STABLE_VISIBLE_FACES", "下方牌面连续两次采样一致；仅为可见图像，不能判断完整手牌、摸牌身份或是否可出牌。", true, decoded.MoveToImmutable())
            : new("LOWER_STABILIZING", "牌面、位置或界面阶段刚变化；等待下一次独立一致采样。", false, []);
    }

    internal void Clear()
    {
        lastSequence = -1;
        previous = null;
        previousAtkValueCount = 0;
    }

    private LowerHandReading Reject(string code, string reason)
    {
        previous = null;
        return new(code, reason, false, []);
    }
}
