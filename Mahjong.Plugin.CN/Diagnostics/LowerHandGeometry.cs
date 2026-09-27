namespace Mahjong.Plugin.CN.Diagnostics;

internal readonly record struct LowerScreenBounds(float X, float Y, float Width, float Height);

/// <summary>
/// Pure projection of an already owned, visible lower-row chain, ordered leaf to root.
/// The caller must establish the actual parent links, terminating root and visibility.
/// No image, memory or game API access occurs here.
/// </summary>
internal static class LowerHandGeometry
{
    internal const int MaximumDepth = 16;
    private const double MatrixRelativeTolerance = 1e-4;

    internal static bool TryProject(IReadOnlyList<LayoutTransform>? leafToRoot,
        out LowerScreenBounds bounds, out string? rejection)
    {
        bounds = default;
        rejection = null;
        if (leafToRoot is null || leafToRoot.Count < 2 || leafToRoot.Count > MaximumDepth)
        {
            rejection = "LOWER_GEOMETRY_CHAIN_INVALID";
            return false;
        }
        // A numerical list cannot prove parent ownership. These are the minimal
        // endpoint invariants; the reader retains its stricter template/path checks.
        if (leafToRoot[0] is not { Type: 2 } ||
            leafToRoot[^1] is not { NodeId: 1, Type: 1 })
        {
            rejection = "LOWER_GEOMETRY_CHAIN_INVALID";
            return false;
        }
        var leaf = leafToRoot[0];
        if (leaf.Width <= 0 || leaf.Height <= 0)
        {
            rejection = "LOWER_GEOMETRY_SIZE_INVALID";
            return false;
        }

        double accumulatedScale = 1;
        for (int i = leafToRoot.Count - 1; i >= 0; i--)
        {
            var node = leafToRoot[i];
            if (node is null || node.LocalX is null || node.LocalY is null)
            {
                rejection = "LOWER_GEOMETRY_CHAIN_INCOMPLETE";
                return false;
            }
            if (!float.IsFinite(node.ScaleX) || !float.IsFinite(node.ScaleY) ||
                !float.IsFinite(node.Rotation) || !float.IsFinite(node.OriginX) ||
                !float.IsFinite(node.OriginY) || !float.IsFinite(node.LocalX.Value) ||
                !float.IsFinite(node.LocalY.Value) || !float.IsFinite(node.M11) ||
                !float.IsFinite(node.M12) || !float.IsFinite(node.M21) || !float.IsFinite(node.M22))
            {
                rejection = "LOWER_GEOMETRY_NONFINITE";
                return false;
            }
            // This change supports positive uniform scaling only. It does not
            // broaden the audited lower-row geometry to rotations or reflections.
            if (node.ScaleX <= 0 || node.ScaleY <= 0 || node.ScaleX != node.ScaleY ||
                node.Rotation != 0 || node.M12 != 0 || node.M21 != 0 || node.M11 <= 0 || node.M22 <= 0)
            {
                rejection = "LOWER_GEOMETRY_TRANSFORM_UNSUPPORTED";
                return false;
            }
            accumulatedScale *= node.ScaleX;
            if (!double.IsFinite(accumulatedScale) || accumulatedScale <= 0 ||
                !Matches(node.M11, accumulatedScale) || !Matches(node.M22, accumulatedScale))
            {
                rejection = "LOWER_GEOMETRY_MATRIX_MISMATCH";
                return false;
            }
        }

        // Pinned Dalamud cac6159a2c76e62a4e1f7bc347454c205808bb04:
        // Dalamud/Interface/Internal/UiDebug/Utility/NodeBounds.cs, TransformPoints.
        // For each ancestor: origin + (point + offset - origin) * scale,
        // where origin = offset + localOrigin. Rotation is explicitly zero above.
        // Cached Transform is already accumulated; do not multiply it at every level.
        Span<double> xs = stackalloc double[4] { 0, leaf.Width, leaf.Width, 0 };
        Span<double> ys = stackalloc double[4] { 0, 0, leaf.Height, leaf.Height };
        foreach (var node in leafToRoot)
        {
            for (int point = 0; point < 4; point++)
            {
                xs[point] = node.LocalX!.Value + (double)node.OriginX +
                    (xs[point] - node.OriginX) * node.ScaleX;
                ys[point] = node.LocalY!.Value + (double)node.OriginY +
                    (ys[point] - node.OriginY) * node.ScaleY;
                if (!double.IsFinite(xs[point]) || !double.IsFinite(ys[point]))
                {
                    rejection = "LOWER_GEOMETRY_BOUNDS_UNREPRESENTABLE";
                    return false;
                }
            }
        }
        double minX = xs[0], maxX = xs[0], minY = ys[0], maxY = ys[0];
        for (int point = 1; point < 4; point++)
        {
            minX = Math.Min(minX, xs[point]); maxX = Math.Max(maxX, xs[point]);
            minY = Math.Min(minY, ys[point]); maxY = Math.Max(maxY, ys[point]);
        }
        var projected = new LowerScreenBounds((float)minX, (float)minY,
            (float)(maxX - minX), (float)(maxY - minY));
        if (!float.IsFinite(projected.X) || !float.IsFinite(projected.Y) ||
            !float.IsFinite(projected.Width) || !float.IsFinite(projected.Height) ||
            projected.Width <= 0 || projected.Height <= 0 ||
            !float.IsFinite(projected.X + projected.Width) || !float.IsFinite(projected.Y + projected.Height) ||
            projected.X + projected.Width <= projected.X || projected.Y + projected.Height <= projected.Y)
        {
            rejection = "LOWER_GEOMETRY_BOUNDS_UNREPRESENTABLE";
            return false;
        }
        bounds = projected;
        return true;
    }

    private static bool Matches(float actual, double expected) =>
        Math.Abs(actual - expected) <= Math.Abs(expected) * MatrixRelativeTolerance;
}
