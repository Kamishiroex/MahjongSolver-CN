using System.Globalization;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>Only user-visible candidate geometry and guarded resource scalars; never a Mahjong hand model.</summary>
// Width/Height are screen units after ancestor projection (fractional scales are not rounded).
// Earlier identity-only captures used integer numbers, which deserialize to float unchanged.
internal sealed record HandFaceCandidate(string Path, float X, float Y, float Width, float Height,
    uint? IconId, string DiagnosticStatus, uint? FacePathHash = null);

internal readonly record struct LowerHandRoute(int RootId, int ImageNodeId)
{
    internal bool IsFace => ImageNodeId == 4;
    internal bool IsShell => ImageNodeId == 5;
}

internal sealed record LowerHandPreviewCheck(bool Eligible, string Reason);

/// <summary>
/// Explicit lower-table UI scope established from the pinned local Emj ULD and observed root-134 clones.
/// Root 133 is the lower layout container, NOT a permitted payload node. Opponent roots 137/140/143,
/// EmjL and every unlisted path are excluded. Root/node counts never establish hand membership.
/// These checks do not replace the caller's version, resource-hash, visibility and memory-budget guards.
/// </summary>
internal static class LowerHandProfile
{
    internal const string EmjUldSha256 = "DA6B6A98B5E1ECDD01EF7A37F3851A0BC90A8935F163CA6B0EEDCF2DA23F2E5F";
    internal const int RequiredRootType = 1055;
    internal const int RequiredButtonType = 1010;
    internal const int RequiredImageType = 2;
    internal const string VerifiedIconStatus = "RESOURCE_VERIFIED";
    internal const int MaximumPreviewFaces = 14;

    internal static bool TryMatchPath(string? path, out LowerHandRoute route)
    {
        route = default;
        if (path is null || path.Length > 32) return false;
        string[] parts = path.Split('/');
        if (parts.Length != 4 || parts[0] != "Emj" || parts[2] != "9" || parts[3] is not ("4" or "5"))
            return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int root) ||
            parts[1] != root.ToString(CultureInfo.InvariantCulture))
            return false;
        if (root is not (134 or 135) && root is not (>= 1340001 and <= 1340016)) return false;
        route = new(root, parts[3] == "4" ? 4 : 5);
        return true;
    }

    /// <summary>
    /// Caller must independently identify all four nodes in the same observed template branch.
    /// /5 is the blank front shell; /4 is only a dynamic-icon candidate. No opponent resource is permitted.
    /// </summary>
    internal static bool CanReadResource(string? path, int rootType, int buttonType, int faceType, int shellType) =>
        TryMatchPath(path, out _) && rootType == RequiredRootType && buttonType == RequiredButtonType &&
        faceType == RequiredImageType && shellType == RequiredImageType;

    /// <summary>
    /// Geometry-only gate for use BEFORE shell or face-resource reads. Deliberately ignores icon ID/status.
    /// An eligible layout is only a bounded, non-overlapping set of explicit lower-face paths.
    /// </summary>
    internal static LowerHandPreviewCheck CheckLayout(IReadOnlyList<HandFaceCandidate>? faces)
    {
        if (faces is null || faces.Count is < 1 or > MaximumPreviewFaces)
            return Reject("候选数量必须为 1..14；数量本身不能证明这些图像属于合法手牌。");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < faces.Count; i++)
        {
            var face = faces[i];
            if (face is null || !TryMatchPath(face.Path, out var route) || !route.IsFace || !paths.Add(face.Path))
                return Reject("候选路径未在下方 /4 白名单中，或同一路径重复出现。");
            if (!float.IsFinite(face.X) || !float.IsFinite(face.Y) || face.X <= 0 || face.Y <= 0 ||
                face.Width <= 0 || face.Height <= 0 || !float.IsFinite(face.X + face.Width) ||
                !float.IsFinite(face.Y + face.Height) || face.X + face.Width <= face.X || face.Y + face.Height <= face.Y)
                return Reject("候选屏幕矩形需要有限且为正的坐标和尺寸。");
            for (int j = 0; j < i; j++)
            {
                var previous = faces[j];
                // Compare in double to avoid intermediate precision loss. Touching edges are allowed.
                if ((double)face.X < (double)previous.X + previous.Width &&
                    (double)previous.X < (double)face.X + face.Width &&
                    (double)face.Y < (double)previous.Y + previous.Height &&
                    (double)previous.Y < (double)face.Y + face.Height)
                    return Reject("候选屏幕矩形重叠；不能排除过渡状态或模板重复。");
            }
        }
        return new(true, "下方候选路径和矩形符合受控采集前置条件；尚未读取或确认任何图标与麻将语义。");
    }

    /// <summary>
    /// Eligible means guarded lower-face images can be shown for user comparison, never used as engine input.
    /// iconIsVerified must validate actual known/available resources, not merely accept positive integers.
    /// </summary>
    internal static LowerHandPreviewCheck CheckPreview(IReadOnlyList<HandFaceCandidate>? faces,
        Func<uint, bool> iconIsVerified)
    {
        ArgumentNullException.ThrowIfNull(iconIsVerified);
        var layout = CheckLayout(faces);
        if (!layout.Eligible) return layout;
        foreach (var face in faces!)
        {
            if (face.DiagnosticStatus != VerifiedIconStatus || face.IconId is not (> 0))
                return Reject("存在未通过资源检查、读取异常或图标未知的候选。");
        }

        // Do not query resources at all until every path and layout precondition passes.
        foreach (var face in faces!)
        {
            try
            {
                if (!iconIsVerified(face.IconId!.Value)) return Reject("候选图标资源尚未验证或不可用。");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Reject($"候选图标资源检查失败：{ex.GetType().Name}。");
            }
        }
        return new(true, "可显示这些下方牌面候选供用户逐张对照；尚未证明完整手牌、摸牌或麻将语义，禁止用作引擎输入。");
    }

    private static LowerHandPreviewCheck Reject(string reason) => new(false, reason);
}
