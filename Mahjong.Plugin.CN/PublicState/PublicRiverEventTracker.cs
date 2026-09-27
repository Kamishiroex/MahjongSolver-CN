using System.Collections.Immutable;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.PublicState;

internal sealed record PublicRiverObservedEvent(string Kind, string ScreenDirection, string SlotPath,
    LowerTileIdentity Tile, bool? Tsumogiri, long Sample, long? DiscardOrdinal, bool IsSideways);
internal sealed record PublicRiverHistorySlot(string ScreenDirection, string SlotPath, LowerTileIdentity Tile,
    int DisplayOrder, bool IsSideways, bool? Tsumogiri, bool? WasClaimed, long FirstObservedSample,
    long LastDecodedSample, bool CurrentDecoded, long? DiscardOrdinal);
internal sealed record PublicRiverHistoryIssue(string Code, long FirstSample);
internal sealed record PublicRiverHistoryObservation(string Code, string? RoundToken, bool InitializedFromEmpty,
    bool HasHistoryGap, int UnresolvedSlotCount, ImmutableArray<PublicRiverHistorySlot> Slots,
    ImmutableArray<PublicRiverObservedEvent> NewEvents, ImmutableArray<PublicRiverHistoryIssue> Issues)
{
    public bool HistoryComplete => false;
    public bool HasContiguousDiscardPrefix => InitializedFromEmpty && !HasHistoryGap && UnresolvedSlotCount == 0;
}

/// <summary>Orders discards only by unique new public slots across observations, never screen geometry.</summary>
internal sealed class PublicRiverEventTracker
{
    private static readonly string[] Directions = ["bottom", "right", "top", "left"];
    private readonly Dictionary<string, PublicRiverHistorySlot> known = new(StringComparer.Ordinal);
    private readonly HashSet<string> unordered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> issues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> pending = new(StringComparer.Ordinal);
    private string? token;
    private long lastSequence = -1, ordinal;
    private DateTimeOffset lastUtc;
    private bool started, fromEmpty, gap;

    internal PublicRiverHistoryObservation Observe(long sequence, DateTimeOffset utc, AddonProbe? addon,
        PublicObservationContext? context, string? roundToken)
    {
        var events = ImmutableArray.CreateBuilder<PublicRiverObservedEvent>();
        if (sequence <= lastSequence || utc < lastUtc) return Fail("RIVER_STALE_OBSERVATION", sequence);
        bool timeGap = lastSequence >= 0 && utc - lastUtc > TimeSpan.FromSeconds(2);
        lastSequence = sequence; lastUtc = utc;
        if (!ValidContext(context) || string.IsNullOrWhiteSpace(roundToken) || roundToken.Length > 128)
            return Fail("RIVER_VERSION_OR_BOUNDARY_UNVERIFIED", sequence);
        if (token != roundToken) { ResetRound(); token = roundToken; timeGap = false; }
        foreach (var key in known.Keys.ToArray()) known[key] = known[key] with { CurrentDecoded = false };
        if (timeGap) Issue("RIVER_SAMPLE_GAP", sequence);
        if (addon is not { Name: "Emj", Present: true, Visible: true, Ready: true, Error: null,
                PublicTableReading: { } reading, PublicTableFaces: { } faces } ||
            reading.Areas.IsDefault || reading.Tiles.IsDefault)
            return Fail("RIVER_SCENE_OR_READ_ERROR", sequence);
        var areas = reading.Areas.Where(a => a.Area.StartsWith("river-", StringComparison.Ordinal)).ToArray();
        var raw = faces.Where(f => f.Area.StartsWith("river-", StringComparison.Ordinal)).ToArray();
        if (areas.Length != 4 || Directions.Any(d => areas.Count(a => a.Area == "river-" + d) != 1) ||
            areas.Any(a => !a.ContainerVerified || !a.ContainerVisible || !a.EnumerationCompleted || a.UnknownComponents != 0) ||
            raw.Length > PublicTableImageReader.MaximumCandidates || raw.Select(f => f.SlotPath).Distinct().Count() != raw.Length ||
            raw.Any(f => !Directions.Contains(f.ScreenDirection) || f.Area != "river-" + f.ScreenDirection) ||
            areas.Any(a => a.VisibleSlots != raw.Count(f => f.Area == a.Area)))
            return Fail("RIVER_ENUMERATION_UNVERIFIED", sequence);
        bool empty = raw.Length == 0 && areas.All(a => a.ObservedEmptyCandidate && a.VisibleSlots == 0);
        if (!started)
        {
            if (empty && areas.Any(a => !a.Stable)) return Result("RIVER_WAITING_FOR_STABLE_EMPTY", []);
            started = true; fromEmpty = empty;
            if (!empty) { Issue("RIVER_STARTED_WITHOUT_EMPTY_BASELINE", sequence); unordered.UnionWith(raw.Select(f => f.SlotPath)); }
        }
        var present = raw.Select(f => f.SlotPath).ToHashSet(StringComparer.Ordinal);
        if (known.Keys.Any(key => !present.Contains(key))) Issue("RIVER_PREVIOUS_SLOT_MISSING", sequence);
        if (pending.Keys.Any(key => !present.Contains(key))) Issue("RIVER_PENDING_SLOT_DISAPPEARED", sequence);
        foreach (var key in pending.Keys.Where(key => !present.Contains(key)).ToArray()) pending.Remove(key);
        var newRows = raw.Where(f => !known.ContainsKey(f.SlotPath)).ToArray();
        if (known.Count + newRows.Length > PublicTableImageReader.MaximumCandidates ||
            unordered.Count + newRows.Count(f => !unordered.Contains(f.SlotPath)) > PublicTableImageReader.MaximumCandidates)
            return Fail("RIVER_HISTORY_SLOT_LIMIT", sequence);
        if (newRows.Length > 1)
        { Issue("RIVER_MULTIPLE_NEW_SLOTS_UNORDERED", sequence); unordered.UnionWith(newRows.Select(f => f.SlotPath)); }
        if (timeGap) unordered.UnionWith(newRows.Select(f => f.SlotPath));
        var reference = new ObservationReference(sequence, utc, "PublicRiverEventTracker/current-public-slot",
            "Fixed CN visible river resources; event order requires a unique newly appearing slot.");
        foreach (var row in raw)
        {
            var candidates = reading.Tiles.Where(t => t.Area == row.Area && t.SlotPath == row.SlotPath &&
                t.ScreenDirection == row.ScreenDirection).ToArray();
            LowerTileIdentity identity = default;
            bool resourceVerified = row.Code == PublicTableImageReader.VerifiedResourceCode &&
                LowerTileCatalog.TryDecode(row.IconId, row.FacePathHash, out identity);
            bool decoded = resourceVerified &&
                candidates.Length == 1 && candidates[0].Kind34 == identity.Kind34 && candidates[0].RedFive == identity.RedFive;
            var tile = candidates.Length == 1 ? candidates[0] : null;
            if (known.TryGetValue(row.SlotPath, out var old))
            {
                if (resourceVerified && (old.ScreenDirection != row.ScreenDirection ||
                    old.Tile.Kind34 != identity.Kind34 || old.Tile.RedFive != identity.RedFive))
                { Issue("RIVER_EXISTING_SLOT_IDENTITY_CHANGED", sequence); continue; }
                // A still-present rejected/occluded old slot keeps its recorded history, not a fresh decoded claim.
                if (!decoded || tile is null) continue;
                if (old.ScreenDirection != row.ScreenDirection || old.Tile.Kind34 != tile.Kind34 || old.Tile.RedFive != tile.RedFive)
                { Issue("RIVER_EXISTING_SLOT_IDENTITY_CHANGED", sequence); continue; }
                if (tile.VisualMark?.ResponseHighlight == true)
                {
                    // The first zero-brightness frame may have looked like a normal
                    // discard before the response pulse was recognized. Retract only
                    // display-derived shading, retaining the tile/order and a real claim.
                    old = old with { Tsumogiri = null, WasClaimed = old.WasClaimed == true ? true : null };
                    known[row.SlotPath] = old;
                }
                if (!tile.Stable || tile.RiverPosition is not { } position) continue;
                var tsumo = PublicDiscardSemantics.Tsumogiri(tile.VisualMark, context, reference);
                var claimed = PublicDiscardSemantics.WasClaimed(tile.VisualMark, context, reference);
                bool? nextClaimed = claimed.IsConfirmed ? claimed.Value : null;
                if (old.WasClaimed == true && nextClaimed == false) Issue("RIVER_CLAIM_MARK_REVERSED", sequence);
                if (old.WasClaimed != true && nextClaimed == true)
                    events.Add(new("CalledMarkObserved", old.ScreenDirection, old.SlotPath, old.Tile,
                        old.Tsumogiri, sequence, old.DiscardOrdinal, position.IsSideways));
                if (old.Tsumogiri is bool wasTsumo && tsumo.IsConfirmed && wasTsumo != tsumo.Value)
                    Issue("RIVER_DISCARD_STYLE_CONFLICT", sequence);
                known[row.SlotPath] = old with { CurrentDecoded = true, LastDecodedSample = sequence,
                    DisplayOrder = position.ReadOrder, IsSideways = position.IsSideways,
                    Tsumogiri = old.Tsumogiri ?? (tsumo.IsConfirmed ? tsumo.Value : null),
                    WasClaimed = old.WasClaimed == true ? true : nextClaimed ?? old.WasClaimed };
                continue;
            }
            pending.TryAdd(row.SlotPath, utc);
            if (!decoded || tile is not { Stable: true, RiverPosition: { } display } || display.ReadOrder < 1)
            {
                if (utc - pending[row.SlotPath] > TimeSpan.FromSeconds(2)) Issue("RIVER_NEW_SLOT_UNRESOLVED", sequence);
                continue;
            }
            var tileIdentity = identity;
            var drawDiscard = PublicDiscardSemantics.Tsumogiri(tile.VisualMark, context, reference);
            var callMark = PublicDiscardSemantics.WasClaimed(tile.VisualMark, context, reference);
            bool? tsumogiri = drawDiscard.IsConfirmed ? drawDiscard.Value : null;
            bool? wasClaimed = callMark.IsConfirmed ? callMark.Value : null;
            long? eventOrdinal = !gap && fromEmpty ? ++ordinal : null;
            if (unordered.Contains(row.SlotPath)) eventOrdinal = null;
            var slot = new PublicRiverHistorySlot(row.ScreenDirection, row.SlotPath, tileIdentity,
                display.ReadOrder, display.IsSideways, tsumogiri, wasClaimed, sequence, sequence, true, eventOrdinal);
            known.Add(row.SlotPath, slot); pending.Remove(row.SlotPath);
            if (!unordered.Contains(row.SlotPath))
                events.Add(new("DiscardObserved", row.ScreenDirection, row.SlotPath, tileIdentity, tsumogiri,
                    sequence, eventOrdinal, display.IsSideways));
        }
        return Result(gap ? "RIVER_HISTORY_GAP" : pending.Count > 0 ? "RIVER_NEW_SLOT_STABILIZING" : "RIVER_OBSERVED_PREFIX", events.ToImmutable());
    }

    internal void Clear() { ResetRound(); token = null; lastSequence = -1; lastUtc = default; }
    private void ResetRound() { known.Clear(); unordered.Clear(); issues.Clear(); pending.Clear(); started = fromEmpty = gap = false; ordinal = 0; }
    private void Issue(string code, long sequence) { gap = true; if (issues.Count < 32) issues.TryAdd(code, sequence); }
    private PublicRiverHistoryObservation Fail(string code, long sequence)
    {
        Issue(code, sequence);
        foreach (var key in known.Keys.ToArray()) known[key] = known[key] with { CurrentDecoded = false };
        return Result(code, []);
    }
    private PublicRiverHistoryObservation Result(string code, ImmutableArray<PublicRiverObservedEvent> events)
        => new(code, token, fromEmpty, gap, pending.Count, known.Values.OrderBy(x => x.FirstObservedSample)
            .ThenBy(x => x.SlotPath, StringComparer.Ordinal).ToImmutableArray(), events,
            issues.Select(x => new PublicRiverHistoryIssue(x.Key, x.Value)).ToImmutableArray());
    private static bool ValidContext(PublicObservationContext? c) => c is { Profile: { } p } &&
        c.ClientVersion == RuntimeIdentity.TargetGame && p.ClientVersion == RuntimeIdentity.TargetGame &&
        c.UldSha256 == LowerHandProfile.EmjUldSha256 && p.UldSha256 == c.UldSha256;
}
