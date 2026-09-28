using Mahjong.Core;
using Mahjong.Policy.Abstractions;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.Presentation;

/// <summary>Visible geometry only. Never grants input authority or infers a drawn slot.</summary>
internal sealed record DiscardHighlight(float X, float Y, float Width, float Height, string Label,
    DateTimeOffset ExpiresUtc)
{
    internal static DiscardHighlight? Create(StateSnapshot? state, ActionChoice? choice,
        LowerHandReading? reading, HandFaceCandidate[]? faces, DateTimeOffset sampledUtc,
        DateTimeOffset now, string? confirmedDrawnPath)
    {
        if (state is null || choice is not { Kind: ActionKind.Discard or ActionKind.Riichi, DiscardTile: { } tile } ||
            reading is not { Stable: true } || faces is null || now < sampledUtc ||
            now - sampledUtc > TimeSpan.FromMilliseconds(350) || state.SchemaVersion != StateSnapshot.CurrentSchemaVersion ||
            !state.Legal.Can(ActionFlags.Discard) || state.Legal.Can(ActionFlags.Ron) || state.Legal.Can(ActionFlags.Tsumo) ||
            !state.Legal.DiscardableTiles.Contains(tile) ||
            choice.Kind == ActionKind.Riichi && !state.Legal.Can(ActionFlags.Riichi) || state.AddonStateCode is 25 or 29 ||
            state.Hand.Count + 3 * state.OurMelds.Count != 14 ||
            !state.Hand.Select(t => (int)t.Id).Order().SequenceEqual(reading.Tiles.Select(t => t.Kind34).Order()) ||
            state.AkaDora != reading.Tiles.Count(t => t.RedFive) || !LowerHandProfile.CheckLayout(faces).Eligible)
            return null;
        if ((choice.DiscardTsumogiri.HasValue || state.OurRiichi) && confirmedDrawnPath is null) return null;
        var candidates = reading.Tiles.Where(t => t.Kind34 == tile.Id &&
            (!choice.DiscardRed.HasValue || t.RedFive == choice.DiscardRed.Value) &&
            (!choice.DiscardTsumogiri.HasValue || (t.Path == confirmedDrawnPath) == choice.DiscardTsumogiri.Value) &&
            (!state.OurRiichi || t.Path == confirmedDrawnPath));
        // The upstream solver has no physical red identity: prefer a visible ordinary copy.
        var target = candidates.OrderBy(t => t.RedFive).ThenBy(t => t.DisplayPosition).FirstOrDefault();
        if (target is null) return null;
        var matches = faces.Where(f => f.Path == target.Path && f.IconId == target.IconId &&
            f.FacePathHash == target.FacePathHash && f.DiagnosticStatus == LowerHandProfile.VerifiedIconStatus).ToArray();
        if (matches.Length != 1) return null;
        var face = matches[0];
        return new(face.X, face.Y, face.Width, face.Height,
            (choice.Kind == ActionKind.Riichi ? "立直后打 " : "建议打 ") + target.ChineseName,
            sampledUtc.AddMilliseconds(350));
    }
}
