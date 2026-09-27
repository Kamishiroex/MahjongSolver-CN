using System.Globalization;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>
/// Metadata-only scope from the fixed Emj ULD and the 0.1.1.2 / 0.1.2.0 visible captures.
/// A match NEVER permits a face image header, face resource/ID/hash, unselected parts, or native calls.
/// The reader must verify the actual root address, component ancestry and owner chain before export.
/// See docs/cn/PUBLIC-TABLE-EVIDENCE.md; directions are screen regions, not absolute player winds.
/// </summary>
internal static class PublicTableMetadataProfile
{
    internal static bool TryMatch(string path, int nodeType, out PublicLayoutRoute route)
    {
        route = null!;
        if (path is null || path.Length > 32) return false;
        string[] parts = path.Split('/');
        if (parts.Length is not (2 or 3) || parts[0] != "Emj" ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint root) ||
            parts[1] != root.ToString(CultureInfo.InvariantCulture))
            return false;

        string area;
        int rootType;
        uint owner;
        bool doraDisplay = false;
        // Fourth capture visibly contains these exact continuous clone ranges. Only the earlier
        // 1..5 / 1..5 / 1..5 / 1..6 subsets have exported owner/shell metadata so far.
        // New matches still require the reader's actual-address ancestry/type/visibility checks;
        // they authorize metadata only, never face/resource reads or verified river semantics.
        if (root == 118 || root is >= 1180001 and <= 1180014)
            (area, rootType, owner) = ("river-bottom", 1021, 116);
        else if (root == 121 || root is >= 1210001 and <= 1210015)
            (area, rootType, owner) = ("river-right", 1023, 119);
        else if (root == 124 || root is >= 1240001 and <= 1240014)
            (area, rootType, owner) = ("river-top", 1024, 122);
        else if (root == 127 || root is >= 1270001 and <= 1270014)
            (area, rootType, owner) = ("river-left", 1022, 125);
        else if (root is >= 28 and <= 32)
        {
            // All five roots exist in the ULD; only 28 has runtime layout evidence in the third capture.
            // No icon semantics or visible-slot counts are inferred for 29..32.
            (area, rootType, owner) = ("dora-display", 1006, 26);
            doraDisplay = true;
        }
        else return false;

        string? leaf = parts.Length == 3 ? parts[2] : null;
        int expectedType = leaf is null ? rootType : doraDisplay
            ? leaf switch { "2" => 2, "3" or "4" => 4, _ => -1 }
            : leaf is "4" or "5" ? 2 : -1;
        if (expectedType < 0 || nodeType != expectedType) return false;

        string rootPath = "Emj/" + parts[1];
        route = new PublicLayoutRoute(area, rootPath, rootType,
            doraDisplay ? [owner, 21, 1] : [owner, 46, 1],
            ReadShellPart: !doraDisplay && leaf == "5",
            RequiredPathTypes: !doraDisplay && leaf is not null
                ? [new(rootPath + "/2", 1), new(rootPath + "/3", 1)]
                : null);
        return true;
    }
}
