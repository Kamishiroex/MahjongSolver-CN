using System.Globalization;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>
/// Metadata-only scope from the pinned Emj ULD and the third CN capture. Root 112
/// and its observed clone 1120001 are lower-side group candidates, not verified melds.
/// Matching authorizes only AtkResNode geometry and, for shell /5, the current part
/// rectangle. It never authorizes a face image header, asset, texture or icon read.
/// Caller must verify real component ownership, exact group parent pointers ending
/// at addon root, full visible ancestry, pinned identity/ULD and bounded reads.
/// </summary>
internal static class LowerMeldMetadataProfile
{
    internal const string Area = "lower-meld-candidate";

    internal static bool TryMatch(string? path, int nodeType, out PublicLayoutRoute route)
    {
        route = null!;
        if (nodeType != 2 || !TryParse(path, out var group, out var tile, out var image)) return false;
        string root = $"Emj/{group}";
        string tilePath = $"{root}/{tile}";
        bool upright = tile is 2 or 3;
        route = new(Area, root, 1060, [111u, 46u, 1u], ReadShellPart: image == 5,
            RequiredPathTypes: [new(root, 1060), new(tilePath, upright ? 1055 : 1056),
                new(tilePath + "/9", upright ? 1010 : 1013)]);
        return true;
    }

    /// <summary>
    /// Additional caller check of the actual owning component nodes. In the static
    /// ULD, the rotated 1056 branch uses 1013; substituting 1010 is not equivalent.
    /// This does not establish visibility or safe face access.
    /// </summary>
    internal static bool HasExpectedComponentTypes(string? path, int tileType, int buttonType)
    {
        if (!TryParse(path, out _, out var tile, out _)) return false;
        return tile is 2 or 3
            ? tileType == 1055 && buttonType == 1010
            : tileType == 1056 && buttonType == 1013;
    }

    private static bool TryParse(string? path, out int group, out int tile, out int image)
    {
        group = tile = image = 0;
        if (path is null || path.Length > 32) return false;
        var parts = path.Split('/');
        if (parts.Length != 5 || parts[0] != "Emj" || parts[3] != "9" ||
            parts[2] is not ("2" or "3" or "4" or "5") || parts[4] is not ("4" or "5"))
            return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out group) ||
            parts[1] != group.ToString(CultureInfo.InvariantCulture) || group is not (112 or 1120001))
            return false;
        tile = parts[2][0] - '0';
        image = parts[4][0] - '0';
        return true;
    }
}
