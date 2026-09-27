using System.Collections.Immutable;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>One current public image slot. Paths are display slots, never discard sequence or player winds.</summary>
internal sealed record PublicTableFaceCandidate(string Area, string ScreenDirection, string SlotPath,
    string GroupPath, int Template, float X, float Y, float Width, float Height,
    float RotationDegrees, bool Mirrored, uint? IconId, uint? FacePathHash, string Code,
    PublicRiverPosition? RiverPosition = null, PublicTileVisualMark? VisualMark = null);
/// <summary>One-based current display order. VisibleColumn skips missing slots; this is not an event index.</summary>
internal sealed record PublicRiverPosition(int DisplayRow, int VisibleColumn, int ReadOrder, bool IsSideways);
/// <summary>Copied from the fixed public template's Res 3, not inferred from the tile icon's red artwork.</summary>
internal sealed record PublicTileVisualMark(short AddRed, short AddGreen, short AddBlue,
    byte MultiplyRed, byte MultiplyGreen, byte MultiplyBlue, string Style)
{
    /// <summary>Observed equal-channel brightness animation during this unchanged response menu; includes its zero phase.</summary>
    public bool ResponseHighlight { get; init; }
    // ULD confirms the display styles; their called/tsumogiri meaning needs an independent event contract.
    public bool CalledTileMeaningVerified => false;
    public bool TsumogiriMeaningVerified => false;
}
internal sealed record PublicTableTile(string Area, string ScreenDirection, string SlotPath, string GroupPath,
    float X, float Y, float Width, float Height, float RotationDegrees, bool Mirrored,
    int Kind34, bool RedFive, string ChineseName, bool Stable,
    PublicRiverPosition? RiverPosition = null, PublicTileVisualMark? VisualMark = null)
{
    public string Quality => "Candidate";
}
internal sealed record PublicTableRejection(string Area, string SlotPath, string Code);
internal sealed record PublicMeldGroup(string GroupPath, string ScreenDirection, int VisibleSlots, int KnownFaces,
    bool AllVisibleSlotsDecoded, bool Stable, ImmutableArray<string> SlotPaths, ImmutableArray<string> ErrorCodes,
    int VerifiedBackSlots = 0, string ShapeCode = "PUBLIC_MELD_SHAPE_UNKNOWN", int? InferredClosedKanKind34 = null);
internal sealed record PublicTableAreaStatus(string Area, string Code, bool ContainerVerified, bool ContainerVisible,
    bool EnumerationCompleted, bool ObservedEmptyCandidate, int VisibleSlots, int UnknownComponents, bool Stable = false,
    int VisibleGroups = 0, int VerifiedRegionRoots = 0);
internal sealed record PublicTableReading(string Code, string Reason, bool Stable,
    ImmutableArray<PublicTableTile> Tiles, ImmutableArray<PublicTableRejection> Rejections,
    ImmutableArray<PublicMeldGroup> MeldGroups = default, ImmutableArray<PublicTableAreaStatus> Areas = default)
{
    public bool HistoryComplete => false;
    public bool CurrentDisplayComplete => false;
}

/// <summary>
/// Two independent observations validate persistence of individual current public images, not game history.
/// A changed area restabilizes as a unit. Missing images are removed immediately; no stale tiles are retained.
/// Region semantics remain Candidate until separately compared with the ordinary CN display.
/// </summary>
internal sealed class PublicTableTracker
{
    private long lastSequence = -1;
    private int lastAtkValueCount;
    private PublicTableFaceCandidate[]? previous;
    private PublicTableAreaStatus[]? previousAreas;
    private string? responseMenu;
    private readonly Dictionary<string, PublicTableFaceCandidate> responseCandidates = new(StringComparer.Ordinal);
    private readonly HashSet<string> responseHighlights = new(StringComparer.Ordinal);

    internal PublicTableReading Observe(long sequence, AddonProbe? addon)
    {
        if (sequence <= lastSequence) return Reject("PUBLIC_STALE_CAPTURE", "采样序号未递增，已清除桌面识别。");
        lastSequence = sequence;
        if (addon is not { Name: "Emj", Present: true, Visible: true, Ready: true, Error: null, PublicTableFaces: not null })
            return Reject("PUBLIC_TABLE_UNAVAILABLE", "当前公开桌面不可读，已清除旧牌。");
        var current = addon.PublicTableFaces.OrderBy(x => x.SlotPath, StringComparer.Ordinal).ToArray();
        if (current.Length > PublicTableImageReader.MaximumCandidates ||
            current.Select(x => x.SlotPath).Distinct(StringComparer.Ordinal).Count() != current.Length)
            return Reject("PUBLIC_SLOT_CONFLICT", "公开槽位重复或超过读取上限。");
        // Recognize a pulse only from this response menu and repeated fixed resources /
        // geometry. Its zero phase is still animation, not fresh hand-discard evidence.
        current = ObserveResponseHighlights(current, addon);
        var identities = current.Select(StabilityIdentity).ToArray();
        var tiles = ImmutableArray.CreateBuilder<PublicTableTile>();
        var errors = ImmutableArray.CreateBuilder<PublicTableRejection>();
        foreach (var area in current.GroupBy(x => x.Area))
        {
            var areaRows = area.ToArray();
            bool stable = previous is not null && lastAtkValueCount == addon.AtkValueCount &&
                previous.Where(x => x.Area == area.Key).SequenceEqual(identities.Where(x => x.Area == area.Key));
            foreach (var face in areaRows)
            {
                // The fixed shell resource itself is public evidence. Its concealed face is not read.
                if (face.Code == PublicTableImageReader.VerifiedBackCode) continue;
                if (face.Code != PublicTableImageReader.VerifiedResourceCode ||
                    !LowerTileCatalog.TryDecode(face.IconId, face.FacePathHash, out var tile))
                {
                    errors.Add(new(face.Area, face.SlotPath, face.Code == PublicTableImageReader.VerifiedResourceCode
                        ? "PUBLIC_TILE_CATALOG_MISMATCH" : face.Code));
                    continue;
                }
                tiles.Add(new(face.Area, face.ScreenDirection, face.SlotPath, face.GroupPath,
                    face.X, face.Y, face.Width, face.Height, face.RotationDegrees, face.Mirrored,
                    tile.Kind34, tile.RedFive, tile.ChineseName, stable, face.RiverPosition, face.VisualMark));
            }
        }
        // This is a display inventory, not a physical tile inventory. A claimed discard may remain
        // displayed in its river and also occur in a meld. Do not apply a four-copy rule until a
        // separately verified event/marking contract establishes which images are the same tile.
        var areas = (addon.PublicTableAreas ?? []).Select(a => a with
        {
            Stable = a.EnumerationCompleted && a.ContainerVerified && a.ContainerVisible && a.UnknownComponents == 0 && previous is not null &&
                lastAtkValueCount == addon.AtkValueCount && previousAreas?.Contains(a with { Stable = false }) == true &&
                previous.Where(x => x.Area == a.Area).SequenceEqual(identities.Where(x => x.Area == a.Area)),
        }).ToImmutableArray();
        previous = identities;
        previousAreas = (addon.PublicTableAreas ?? []).Select(x => x with { Stable = false }).ToArray();
        lastAtkValueCount = addon.AtkValueCount;
        bool allStable = tiles.Count > 0 && errors.Count == 0 && tiles.All(x => x.Stable);
        var meldGroups = current.Where(x => x.Area.StartsWith("meld-", StringComparison.Ordinal)).GroupBy(x => x.GroupPath)
            .Select(g =>
            {
                var decoded = tiles.Where(x => x.GroupPath == g.Key).ToArray();
                var rejected = errors.Where(x => g.Any(c => c.SlotPath == x.SlotPath)).ToArray();
                var slots = g.Where(x => x.SlotPath.EndsWith("/4", StringComparison.Ordinal)).Select(x => x.SlotPath).ToImmutableArray();
                bool allDecoded = slots.Length is 3 or 4 && decoded.Length == slots.Length && rejected.Length == 0;
                int backs = g.Count(x => x.Code == PublicTableImageReader.VerifiedBackCode);
                bool closedKan = slots.Length == 4 && backs == 2 && decoded.Length == 2 && rejected.Length == 0 &&
                    decoded[0].Kind34 == decoded[1].Kind34;
                int[] kinds = decoded.Select(x => x.Kind34).Order().ToArray();
                string shape = closedKan ? "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN" : allDecoded && kinds.Distinct().Count() == 1
                    ? slots.Length == 3 ? "PUBLIC_PON_THREE_FACE_PATTERN" : "PUBLIC_KAN_FOUR_FACE_PATTERN"
                    : allDecoded && slots.Length == 3 && kinds[2] < 27 && kinds[0] / 9 == kinds[2] / 9 &&
                        kinds[1] == kinds[0] + 1 && kinds[2] == kinds[0] + 2 ? "PUBLIC_CHI_THREE_FACE_PATTERN" : "PUBLIC_MELD_SHAPE_UNKNOWN";
                return new PublicMeldGroup(g.Key, g.First().ScreenDirection, slots.Length, decoded.Length, allDecoded,
                    (allDecoded || closedKan) && decoded.All(x => x.Stable), slots, rejected.Select(x => x.Code).ToImmutableArray(),
                    backs, shape, closedKan ? decoded[0].Kind34 : null);
            }).ToImmutableArray();
        return new(allStable ? "PUBLIC_IMAGES_STABLE_CANDIDATE" : "PUBLIC_IMAGES_PARTIAL_CANDIDATE",
            "仅为当前可见牌面候选；屏幕方向不是门风，槽位不是出牌顺序，副露种类、来源及缺失历史未知。",
            allStable, tiles.ToImmutable(), errors.ToImmutable(), meldGroups, areas);
    }

    private static PublicTableFaceCandidate StabilityIdentity(PublicTableFaceCandidate face) => face with
    {
        VisualMark = face.VisualMark is { ResponseHighlight: true } mark ? mark with
        { AddRed = 0, AddGreen = 0, AddBlue = 0, Style = "response-highlight" } : face.VisualMark,
    };

    private PublicTableFaceCandidate[] ObserveResponseHighlights(PublicTableFaceCandidate[] faces, AddonProbe addon)
    {
        var menu = addon.PublicActionMenu;
        string? currentMenu = menu is { Visible: true, AllVisibleRowsDecoded: true } &&
            menu.Rows.Count is > 0 and <= 8 &&
            menu.Rows.Any(r => r.Enabled == true && r.Action is "Chi" or "Pon" or "Kan" or "Ron") &&
            menu.ListState is not { IsUpdatePending: true } && menu.ListState is not { IsScrollRefreshPending: true }
            ? string.Join("|", menu.Rows.OrderBy(r => r.Path, StringComparer.Ordinal)
                .Select(r => $"{r.Path}:{r.ListItemIndex}:{r.Action}:{r.Enabled}")) : null;
        if (currentMenu != responseMenu || addon.AtkValueCount != lastAtkValueCount) ClearResponseHighlights();
        responseMenu = currentMenu;
        var present = faces.Select(f => f.SlotPath).ToHashSet(StringComparer.Ordinal);
        foreach (string path in responseCandidates.Keys.Where(p => !present.Contains(p)).ToArray())
        { responseCandidates.Remove(path); responseHighlights.Remove(path); }
        return faces.Select(face =>
        {
            if (face.VisualMark is not { } raw)
            { responseCandidates.Remove(face.SlotPath); responseHighlights.Remove(face.SlotPath); return face; }
            var mark = raw with { ResponseHighlight = false }; // Never trust a carried-over tag as new evidence.
            var identity = face with { VisualMark = null };
            bool eligible = currentMenu is not null && face.Area.StartsWith("river-", StringComparison.Ordinal) &&
                face.Code == PublicTableImageReader.VerifiedResourceCode &&
                LowerTileCatalog.TryDecode(face.IconId, face.FacePathHash, out _) &&
                mark.MultiplyRed == 100 && mark.MultiplyGreen == 100 && mark.MultiplyBlue == 100 &&
                mark.AddRed >= 0 && mark.AddRed == mark.AddGreen && mark.AddRed == mark.AddBlue &&
                mark.Style is "normal" or "unclassified";
            if (!eligible)
            { responseCandidates.Remove(face.SlotPath); responseHighlights.Remove(face.SlotPath); }
            else
            {
                bool same = responseCandidates.TryGetValue(face.SlotPath, out var prior) && prior == identity;
                if (!same) responseHighlights.Remove(face.SlotPath);
                if (same && mark.AddRed > 0) responseHighlights.Add(face.SlotPath);
                responseCandidates[face.SlotPath] = identity;
                mark = mark with { ResponseHighlight = responseHighlights.Contains(face.SlotPath) };
            }
            return face with { VisualMark = mark };
        }).ToArray();
    }

    private void ClearResponseHighlights()
    { responseMenu = null; responseCandidates.Clear(); responseHighlights.Clear(); }

    internal void Clear()
    { lastSequence = -1; previous = null; previousAreas = null; lastAtkValueCount = 0; ClearResponseHighlights(); }
    private PublicTableReading Reject(string code, string reason)
    { previous = null; previousAreas = null; ClearResponseHighlights(); return new(code, reason, false, [], [], [], []); }
}
