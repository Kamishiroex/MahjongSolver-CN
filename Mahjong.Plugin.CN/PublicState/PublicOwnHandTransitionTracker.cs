using System.Collections.Immutable;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.PublicState;

internal sealed record PublicOwnHandDelta(string Kind, LowerTileIdentity Tile, long BeforeSample, long AfterSample)
{
    public bool PhysicalCopyKnown => false;
    public bool HistoryComplete => false;
}
internal sealed record PublicOwnHandTransitionObservation(string Code, ImmutableArray<LowerTileIdentity> Tiles,
    LowerTileIdentity? SeparateSlotTile, LowerTileIdentity? DrawnTileCandidate, int? MeldCount, string Phase,
    PublicOwnHandDelta? Transition)
{
    public bool HistoryComplete => false;
    public bool LegalTurnKnown => false;
}

/// <summary>Public, stable inventory deltas only. Never derives action legality or a physical copy identity.</summary>
internal sealed class PublicOwnHandTransitionTracker
{
    private long lastSequence = -1, baselineSequence;
    private DateTimeOffset lastUtc, unstableSince;
    private string? boundary, meldFingerprint;
    private ImmutableArray<LowerTileIdentity> baseline = [];
    private string[] oldGroups = [];
    private int previousMeldCount;
    private LowerTileIdentity? drawn;

    internal PublicOwnHandTransitionObservation Observe(long sequence, DateTimeOffset utc, AddonProbe? addon,
        PublicObservationContext? context, string? roundKey)
    {
        if (sequence <= lastSequence || utc < lastUtc) return Reject("OWN_STALE_OBSERVATION");
        bool timeGap = lastSequence >= 0 && utc - lastUtc > TimeSpan.FromSeconds(2);
        lastSequence = sequence; lastUtc = utc;
        if (!ValidContext(context) || string.IsNullOrWhiteSpace(roundKey) || roundKey.Length > 128)
            return Reject("OWN_VERSION_OR_BOUNDARY_UNVERIFIED");
        if (addon is not { Name: "Emj", Present: true, Visible: true, Ready: true, Error: null })
            return Reject("OWN_SCENE_OR_READ_ERROR");
        bool changedBoundary = boundary != roundKey || timeGap ||
            unstableSince != default && utc - unstableSince > TimeSpan.FromSeconds(2);
        if (changedBoundary) ResetInventory();
        boundary = roundKey;
        if (addon.LowerHandReading is not { Stable: true, Code: "STABLE_VISIBLE_FACES" } hand ||
            addon.PublicTableReading is not { } table || table.Areas.IsDefault || table.MeldGroups.IsDefault || table.Tiles.IsDefault)
            return Stabilizing(utc);
        var areas = table.Areas.Where(a => a.Area == "meld-bottom").ToArray();
        var groups = table.MeldGroups.Where(g => g.ScreenDirection == "bottom").ToArray();
        if (areas.Length != 1 || !areas[0].ContainerVerified || !areas[0].ContainerVisible ||
            !areas[0].EnumerationCompleted || areas[0].UnknownComponents != 0 || groups.Length > 4 ||
            groups.Select(g => g.GroupPath).Distinct().Count() != groups.Length || areas[0].VisibleGroups != groups.Length)
            return Reject("OWN_MELD_COUNT_UNVERIFIED");
        if (!areas[0].Stable || groups.Any(g => !g.Stable)) return Stabilizing(utc);
        if (groups.Any(g => g.ErrorCodes.IsDefault || !g.ErrorCodes.IsEmpty || !KnownShape(g) ||
                table.Tiles.Count(t => t.GroupPath == g.GroupPath) != g.KnownFaces) ||
            areas[0].VisibleSlots != groups.Sum(g => g.VisibleSlots)) return Reject("OWN_MELD_SHAPE_UNVERIFIED");
        if (hand.Tiles.IsDefaultOrEmpty || hand.Tiles.Select(t => t.Path).Distinct().Count() != hand.Tiles.Length)
            return Reject("OWN_FACE_INVENTORY_INVALID");
        foreach (var tile in hand.Tiles)
            if (!LowerHandProfile.TryMatchPath(tile.Path, out _) ||
                !LowerTileCatalog.TryDecode(tile.IconId, tile.FacePathHash, out var decoded) ||
                decoded.Kind34 != tile.Kind34 || decoded.RedFive != tile.RedFive)
                return Reject("OWN_FACE_RESOURCE_UNVERIFIED");
        var tiles = hand.Tiles.Select(t => new LowerTileIdentity(t.Kind34, t.RedFive, t.ChineseName)).ToImmutableArray();
        if (tiles.GroupBy(t => t.Kind34).Any(g => g.Count() > 4) ||
            tiles.Where(t => t.RedFive).GroupBy(t => t.Kind34).Any(g => g.Count() > 1))
            return Reject("OWN_TILE_COUNT_CONFLICT");
        var separate = hand.Tiles.Where(t => t.Path == "Emj/135/9/4").ToArray();
        if (separate.Length > 1) return Reject("OWN_SEPARATE_SLOT_CONFLICT");
        LowerTileIdentity? slot = separate.Length == 1
            ? new(separate[0].Kind34, separate[0].RedFive, separate[0].ChineseName) : null;
        int meldCount = groups.Length, resting = 13 - 3 * meldCount;
        if (tiles.Length != resting && tiles.Length != resting + 1)
            return Reject("OWN_HAND_MELD_COUNT_CONFLICT");
        if (slot is not null && tiles.Length != resting + 1) return Reject("OWN_SLOT_COUNT_CONFLICT");
        string[] signatures = groups.Select(g => g.GroupPath + ":" + g.ShapeCode + ":" +
            string.Join(",", table.Tiles.Where(t => t.GroupPath == g.GroupPath).Select(t => t.Kind34 + ":" + t.RedFive).Order()))
            .Order().ToArray();
        string fingerprint = string.Join("|", signatures);
        string phase = tiles.Length == resting ? "RestingCountCandidate" : slot is not null
            ? "ExtraTileWithSeparateSlotCandidate" : "ExtraTileWithoutSeparateSlotCandidate";
        string code = changedBoundary ? "OWN_BASELINE_RESET" : "OWN_STABLE_INVENTORY";
        PublicOwnHandDelta? delta = null;
        if (!baseline.IsEmpty && meldFingerprint == fingerprint && previousMeldCount == meldCount)
        {
            var added = Difference(tiles, baseline); var removed = Difference(baseline, tiles);
            if (baseline.Length == resting && tiles.Length == resting + 1 && added.Count == 1 && removed.Count == 0 &&
                slot is { } visibleDraw && Same(added[0], visibleDraw))
            { drawn = added[0]; delta = new("DrawCandidate", drawn.Value, baselineSequence, sequence); }
            else if (baseline.Length == resting + 1 && tiles.Length == resting && removed.Count == 1 && added.Count == 0)
            { delta = new("DiscardCandidate", removed[0], baselineSequence, sequence); drawn = null; }
            else if (added.Count != 0 || removed.Count != 0)
            { code = "OWN_NON_UNIQUE_TRANSITION"; drawn = null; }
        }
        else
        {
            drawn = null;
            if (!baseline.IsEmpty) code = "OWN_MELD_CHANGED_BASELINE_RESET";
            if (!baseline.IsEmpty && baseline.Length == 13 - 3 * previousMeldCount &&
                Difference(baseline, tiles).Count == 2 && Difference(tiles, baseline).Count == 0 &&
                meldCount == previousMeldCount + 1 && oldGroups.All(signatures.Contains) &&
                tiles.Length == resting + 1 && slot is null && groups.Any(g =>
                    !oldGroups.Any(s => s.StartsWith(g.GroupPath + ":", StringComparison.Ordinal)) &&
                    g.ShapeCode is "PUBLIC_CHI_THREE_FACE_PATTERN" or "PUBLIC_PON_THREE_FACE_PATTERN" &&
                    Difference(Difference(baseline, tiles).ToImmutableArray(), table.Tiles.Where(t => t.GroupPath == g.GroupPath)
                        .Select(t => new LowerTileIdentity(t.Kind34, t.RedFive, t.ChineseName)).ToImmutableArray()).Count == 0))
                phase = "PostCallExtraTileCandidate";
        }
        baseline = tiles; baselineSequence = sequence; previousMeldCount = meldCount;
        meldFingerprint = fingerprint; oldGroups = signatures; unstableSince = default;
        return new(delta is null ? code : "OWN_UNIQUE_INVENTORY_DELTA", tiles, slot, drawn, meldCount, phase, delta);
    }

    internal void Clear()
    { lastSequence = -1; lastUtc = default; ResetInventory(); }
    private static bool ValidContext(PublicObservationContext? c) => c is { Profile: { } p } &&
        c.ClientVersion == RuntimeIdentity.TargetGame && p.ClientVersion == RuntimeIdentity.TargetGame &&
        string.Equals(c.UldSha256, LowerHandProfile.EmjUldSha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(p.UldSha256, LowerHandProfile.EmjUldSha256, StringComparison.OrdinalIgnoreCase);
    private static bool KnownShape(PublicMeldGroup g) => g.ShapeCode switch
    {
        "PUBLIC_CHI_THREE_FACE_PATTERN" or "PUBLIC_PON_THREE_FACE_PATTERN" => g.VisibleSlots == 3 && g.KnownFaces == 3 && g.VerifiedBackSlots == 0 && g.AllVisibleSlotsDecoded,
        "PUBLIC_KAN_FOUR_FACE_PATTERN" => g.VisibleSlots == 4 && g.KnownFaces == 4 && g.VerifiedBackSlots == 0 && g.AllVisibleSlotsDecoded,
        "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN" => g.VisibleSlots == 4 && g.VerifiedBackSlots == 2 &&
            g.KnownFaces == 2 && g.InferredClosedKanKind34 is >= 0 and < 34,
        _ => false,
    };
    private static bool Same(LowerTileIdentity a, LowerTileIdentity b) => a.Kind34 == b.Kind34 && a.RedFive == b.RedFive;
    private static List<LowerTileIdentity> Difference(ImmutableArray<LowerTileIdentity> a, ImmutableArray<LowerTileIdentity> b)
    {
        var rest = b.ToList(); var result = new List<LowerTileIdentity>();
        foreach (var tile in a) { int i = rest.FindIndex(x => Same(x, tile)); if (i < 0) result.Add(tile); else rest.RemoveAt(i); }
        return result;
    }
    private PublicOwnHandTransitionObservation Stabilizing(DateTimeOffset utc)
    {
        if (unstableSince == default) unstableSince = utc;
        else if (utc - unstableSince > TimeSpan.FromSeconds(2)) ResetInventory();
        return new("OWN_STABILIZING", [], null, null, null, "Unknown", null);
    }
    private PublicOwnHandTransitionObservation Reject(string code)
    { ResetInventory(); return new(code, [], null, null, null, "Unknown", null); }
    private void ResetInventory()
    { baseline = []; oldGroups = []; drawn = null; meldFingerprint = boundary = null; previousMeldCount = 0; unstableSince = default; }
}
