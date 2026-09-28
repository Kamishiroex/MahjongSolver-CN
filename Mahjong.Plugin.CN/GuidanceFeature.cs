using System.Numerics;
using Dalamud.Bindings.ImGui;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Presentation;
using Mahjong.Plugin.CN.Ui;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private HandFaceCandidate[]? guidanceFaces;
    private DiscardHighlight? discardHighlight;

    private void PublishDiscardHighlight(bool active, DateTimeOffset now)
    {
        var drawn = CurrentJournalPublicSnapshot?.DrawnTileSlot;
        string? drawnPath = drawn is { IsConfirmed: true, Observation: { } observed } &&
            observed.ObservedAtUtc == journalLowerUtc ? drawn.Value?.Path : null;
        var aggregator = PlayRuntime?.ActiveAggregator;
        Volatile.Write(ref discardHighlight, active && gameplayAllowed && GlassTheme.HighlightDiscard
            ? DiscardHighlight.Create(aggregator?.Latest, aggregator?.LastChoice, journalLower, guidanceFaces,
                journalLowerUtc, now, drawnPath, PlayRuntime?.MeldTracker.MeldAkadora ?? 0) : null);
    }

    private void DrawDiscardHighlight()
    {
        // Only managed, short-lived geometry crosses to Draw; no native reads or clicks here.
        if (disposed || !gameplayAllowed || !GlassTheme.HighlightDiscard ||
            Volatile.Read(ref discardHighlight) is not { } hint || DateTimeOffset.UtcNow > hint.ExpiresUtc) return;
        var viewport = ImGui.GetMainViewport();
        var min = viewport.Pos + new Vector2(hint.X, hint.Y);
        var max = min + new Vector2(hint.Width, hint.Height);
        if (min.X < viewport.Pos.X || min.Y < viewport.Pos.Y ||
            max.X > viewport.Pos.X + viewport.Size.X || max.Y > viewport.Pos.Y + viewport.Size.Y) return;
        var draw = ImGui.GetBackgroundDrawList();
        var accent = GlassTheme.Accent;
        draw.AddRectFilled(min, max, ImGui.GetColorU32(new Vector4(accent.X, accent.Y, accent.Z, .16f)), 4);
        draw.AddRect(min, max, ImGui.GetColorU32(accent), 4, ImDrawFlags.None, Math.Clamp(hint.Width / 18, 2, 4));
        var textSize = ImGui.CalcTextSize(hint.Label);
        var pos = new Vector2(Math.Clamp(min.X, viewport.Pos.X, Math.Max(viewport.Pos.X, viewport.Pos.X + viewport.Size.X - textSize.X - 8)),
            Math.Max(viewport.Pos.Y, min.Y - textSize.Y - 10));
        draw.AddRectFilled(pos - new Vector2(4, 2), pos + textSize + new Vector2(4, 2), 0xEE202820, 3);
        draw.AddText(pos, ImGui.GetColorU32(Vector4.One), hint.Label);
    }
}
