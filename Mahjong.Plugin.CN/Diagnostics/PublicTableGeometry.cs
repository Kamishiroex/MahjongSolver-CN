namespace Mahjong.Plugin.CN.Diagnostics;

internal readonly record struct PublicTableProjection(float X, float Y, float Width, float Height,
    float OriginX, float OriginY, float RotationDegrees, bool Mirrored);

/// <summary>Fixed Dalamud NodeBounds transform, with quarter turns, uniform magnitude and audited reflections.</summary>
internal static class PublicTableGeometry
{
    internal static bool Near(float value, float expected, float tolerance = 0.00001f) =>
        float.IsFinite(value) && Math.Abs(value - expected) <= tolerance;

    internal static bool TryProject(IReadOnlyList<LayoutTransform> chain, out PublicTableProjection result)
    {
        result = default;
        if (chain.Count is < 2 or > 16 || chain[0].Type != 2 ||
            chain[^1] is not { NodeId: 1, Type: 1 }) return false;
        double a = 1, b = 0, c = 0, d = 1;
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var n = chain[i];
            if (n.LocalX is null || n.LocalY is null ||
                !new[] { n.ScaleX, n.ScaleY, n.Rotation, n.OriginX, n.OriginY, n.LocalX.Value,
                    n.LocalY.Value, n.M11, n.M12, n.M21, n.M22 }.All(float.IsFinite) ||
                n.ScaleX == 0 || n.ScaleY == 0 || !Near(Math.Abs(n.ScaleX), Math.Abs(n.ScaleY)) ||
                !Near(n.Rotation / (MathF.PI / 2), MathF.Round(n.Rotation / (MathF.PI / 2)))) return false;
            double co = Math.Cos(n.Rotation), si = Math.Sin(n.Rotation);
            double aa = co * n.ScaleX, bb = -si * n.ScaleY, cc = si * n.ScaleX, dd = co * n.ScaleY;
            (a, b, c, d) = (a * aa + b * cc, a * bb + b * dd, c * aa + d * cc, c * bb + d * dd);
            // Atk Transform stores the transpose of the conventional column-vector matrix.
            double tolerance = Math.Max(Math.Max(Math.Abs(a), Math.Abs(b)), Math.Max(Math.Abs(c), Math.Abs(d))) * 0.0001;
            if (!double.IsFinite(tolerance) || tolerance <= 0 ||
                Math.Abs(n.M11 - a) > tolerance || Math.Abs(n.M12 - c) > tolerance ||
                Math.Abs(n.M21 - b) > tolerance || Math.Abs(n.M22 - d) > tolerance) return false;
        }
        if (chain[0].Width <= 0 || chain[0].Height <= 0) return false;
        double[] xs = [0, chain[0].Width, chain[0].Width, 0];
        double[] ys = [0, 0, chain[0].Height, chain[0].Height];
        foreach (var n in chain)
        {
            double co = Math.Cos(n.Rotation), si = Math.Sin(n.Rotation);
            for (int k = 0; k < 4; k++)
            {
                double dx = (xs[k] - n.OriginX) * n.ScaleX, dy = (ys[k] - n.OriginY) * n.ScaleY;
                xs[k] = n.LocalX!.Value + n.OriginX + dx * co - dy * si;
                ys[k] = n.LocalY!.Value + n.OriginY + dx * si + dy * co;
            }
        }
        float x = (float)xs.Min(), y = (float)ys.Min(), w = (float)(xs.Max() - xs.Min()), h = (float)(ys.Max() - ys.Min());
        float ox = (float)xs[0], oy = (float)ys[0];
        if (!new[] { x, y, w, h, ox, oy, x + w, y + h }.All(float.IsFinite) || w <= 0 || h <= 0 || x + w <= x || y + h <= y)
            return false;
        result = new(x, y, w, h, ox, oy, MathF.Round((float)(Math.Atan2(c, a) * 180 / Math.PI)), a * d - b * c < 0);
        return true;
    }

    internal static bool Overlaps(PublicTableProjection a, PublicTableProjection b)
    {
        float w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        float h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        // Ignore floating-point edge noise, not an overlapping tile or stacked opaque shell.
        return w > Math.Min(a.Width, b.Width) * 0.01f && h > Math.Min(a.Height, b.Height) * 0.01f;
    }
}
