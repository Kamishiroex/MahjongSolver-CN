using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.Events;
using Mahjong.Cn.PublicState;
using Mahjong.Core;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.Journaling;

/// <summary>Joins independent public images with the old experimental reader without promoting its quality.</summary>
internal static class RecoveryEvidenceBuilder
{
    internal static bool TryBuildAnchor(StateSnapshot snapshot, int meldReds, LowerHandReading? lower,
        PublicTableReading? table, out ExperimentalRecoveryAnchor anchor, out string code)
    {
        anchor = null!;
        code = "RECOVERY_WAIT_STABLE_HAND";
        if (!RecoveryJournal.ValidSnapshot(snapshot) || meldReds is < 0 or > 3 || lower is not { Stable: true } ||
            table is null || lower.Tiles.IsDefault || table.Areas.IsDefault ||
            table.Tiles.IsDefault || table.Rejections.IsDefault || snapshot.AddonStateCode == 29) return false;
        if (table.Areas.Any(a => a is null) || table.Rejections.Any(r => r is null)) return false;
        if (lower.Tiles.Any(t => t is null || t.Kind34 is < 0 or > 33 || (t.RedFive && t.Kind34 is not (4 or 13 or 22))) ||
            lower.Tiles.Select(t => t.Path).Distinct(StringComparer.Ordinal).Count() != lower.Tiles.Length ||
            table.Tiles.Any(t => t is null || t.Kind34 is < 0 or > 33 || (t.RedFive && t.Kind34 is not (4 or 13 or 22))) ||
            table.Tiles.Select(t => t.SlotPath).Distinct(StringComparer.Ordinal).Count() != table.Tiles.Length)
        { code = "RECOVERY_PUBLIC_SLOT_CONFLICT"; return false; }
        if (lower.Tiles.Any(t => !LowerTileCatalog.TryDecode(t.IconId, t.FacePathHash, out var decoded) ||
            decoded.Kind34 != t.Kind34 || decoded.RedFive != t.RedFive))
        { code = "RECOVERY_LOWER_RESOURCE_MISMATCH"; return false; }
        var hand = lower.Tiles.Select(t => new VisibleTile(t.Kind34, t.RedFive)).ToImmutableArray();
        if (!hand.Select(t => t.Id).Order().SequenceEqual(snapshot.Hand.Select(t => (int)t.Id).Order()) ||
            hand.Count(t => t.Red) != snapshot.AkaDora - meldReds)
        { code = "RECOVERY_HAND_READERS_DISAGREE"; return false; }
        var areas = ImmutableArray.CreateBuilder<VisibleAreaFootprint>();
        foreach (var (name, position) in Directions)
        {
            string area = "river-" + name;
            var statuses = table.Areas.Where(a => a.Area == area).ToArray();
            if (statuses.Length != 1 || statuses[0] is not { ContainerVerified: true, ContainerVisible: true,
                    EnumerationCompleted: true, UnknownComponents: 0, Stable: true } ||
                table.Rejections.Any(r => r.Area == area))
            { code = "RECOVERY_RIVER_NOT_READABLE_" + name; return false; }
            var tiles = table.Tiles.Where(t => t.Area == area).ToArray();
            if (tiles.Any(t => !t.Stable || t.ScreenDirection != name) || tiles.Length != statuses[0].VisibleSlots ||
                (tiles.Length == 0) != statuses[0].ObservedEmptyCandidate)
            { code = "RECOVERY_RIVER_PARTIAL_" + name; return false; }
            areas.Add(new(position, tiles.Select(t => new VisibleTile(t.Kind34, t.RedFive)).ToImmutableArray(),
                true, "pinned-Emj-ULD-visible-images; candidate spatial inventory, not chronology"));
        }
        anchor = new(hand, snapshot.Scores.ToImmutableArray(),
            snapshot.DoraIndicators.Select(t => new VisibleTile(t.Id)).ToImmutableArray(),
            snapshot.Seats.Select(s => s.DiscardCount).ToImmutableArray(), areas.ToImmutable());
        code = "RECOVERY_ANCHOR_CANDIDATE";
        return true;
    }

    internal static bool TryBuild(RecoveryCandidate saved, StateSnapshot current, int currentMeldReds,
        LowerHandReading? lower, PublicTableReading? table, Guid session, long sequence,
        out ReconciliationResult result, out ExperimentalRecoveryAnchor anchor, out string code)
    {
        result = null!;
        anchor = null!;
        code = "RECOVERY_CHECKPOINT_INVALID";
        var old = saved.State;
        if (old.Tracker.SchemaVersion != 1 || old.Snapshot.SchemaVersion != StateSnapshot.CurrentSchemaVersion ||
            current.SchemaVersion != StateSnapshot.CurrentSchemaVersion || current.WallRemaining > old.Snapshot.WallRemaining ||
            DateTimeOffset.UtcNow - saved.Utc > TimeSpan.FromHours(6) || saved.Utc > DateTimeOffset.UtcNow.AddMinutes(1)) return false;
        if (!TryBuildAnchor(old.Snapshot, old.Tracker.MeldAkadora, old.Lower, old.PublicTable, out var before, out code) ||
            !TryBuildAnchor(current, currentMeldReds, lower, table, out anchor, out code)) return false;
        if (old.PublicTable!.MeldGroups.IsDefault || table!.MeldGroups.IsDefault ||
            old.PublicTable.MeldGroups.Any(g => g is null) || table.MeldGroups.Any(g => g is null))
        { code = "RECOVERY_MELD_IMAGES_INCOMPLETE"; return false; }
        var beforeGroups = Groups(old.PublicTable!);
        var currentGroups = Groups(table!);
        if (old.PublicTable!.Rejections.Any(x => x.Area == "meld-bottom") ||
            table!.Rejections.Any(x => x.Area == "meld-bottom") || beforeGroups.Length != old.Tracker.Melds.Length)
        { code = "RECOVERY_MELD_IMAGES_INCOMPLETE"; return false; }
        var unused = beforeGroups.ToList();
        var melds = ImmutableArray.CreateBuilder<ExperimentalMeldCheckpoint>();
        foreach (var meld in old.Tracker.Melds)
        {
            int match = unused.FindIndex(g => g.Stable && (g.AllVisibleSlotsDecoded ||
                (meld.Kind == MeldKind.AnKan && g.HasUnambiguousClosedKanInventory)) &&
                g.Inventory.Select(t => t.Id).Order().SequenceEqual(meld.Tiles.Select(t => (int)t.Id).Order()));
            if (match < 0) { code = "RECOVERY_OLD_MELD_IMAGES_MISMATCH"; return false; }
            var group = unused[match]; unused.RemoveAt(match);
            melds.Add(new(meld.Kind, meld.Tiles.Select(t => (int)t.Id).ToImmutableArray(), group.Inventory.Count(t => t.Red),
                meld.Kind == MeldKind.AnKan ? null : meld.ClaimedFromSeat, null, meld.ClaimedTile?.Id));
        }
        if (melds.Sum(m => m.RedTileCount) != old.Tracker.MeldAkadora)
        { code = "RECOVERY_MELD_RED_MISMATCH"; return false; }
        string profile = old.Identity.GameVersion + ":" + old.Identity.DalamudCommit + ":" + LowerHandProfile.EmjUldSha256;
        var unknownHand = Field<ImmutableArray<VisibleTile>>.Unknown("Independent experimental proof only.");
        var unknownMelds = Field<ImmutableArray<VisibleMeld>>.Unknown("Legacy assumptions are not verified AI fields.");
        var checkpoint = new MeldRecoveryCheckpoint(old.ObservationSessionId, old.ObservationSequence, saved.Utc,
            profile, unknownHand, unknownMelds, []);
        var observation = new RecoveryTableObservation(session, sequence, DateTimeOffset.UtcNow, profile,
            unknownHand, unknownMelds, []) { Stable = true };
        var proof = new ExperimentalRecoveryEvidence(before, anchor, melds.ToImmutable(), currentGroups);
        result = ExperimentalTableReconciler.CompareExperimental(checkpoint, observation, proof);
        if (!result.CanRestoreExperimentalMelds)
            result = ExperimentalTableReconciler.CompareExperimental(checkpoint, observation,
                proof with { Mode = ExperimentalRecoveryMode.InterruptedProgress });
        code = result.CanRestoreExperimentalMelds ? "RECOVERY_EXPERIMENTAL_MATCH" :
            result.Issues.FirstOrDefault()?.Code ?? "RECOVERY_AWAITING_EVIDENCE";
        return result.CanRestoreExperimentalMelds;
    }

    private static ImmutableArray<VisibleMeldGroupEvidence> Groups(PublicTableReading table) => table.MeldGroups.IsDefault ? [] :
        table.MeldGroups.Where(g => g.ScreenDirection == "bottom").Select(g =>
        {
            var faces = table.Tiles.Where(t => t.GroupPath == g.GroupPath).ToArray();
            bool complete = g.AllVisibleSlotsDecoded && !g.SlotPaths.IsDefault && !g.ErrorCodes.IsDefault &&
                g.ErrorCodes.Length == 0 && g.KnownFaces == faces.Length && g.VisibleSlots == faces.Length &&
                g.SlotPaths.Order(StringComparer.Ordinal).SequenceEqual(faces.Select(t => t.SlotPath).Order(StringComparer.Ordinal)) &&
                faces.All(t => t.Area == "meld-bottom" && t.ScreenDirection == "bottom" && t.Stable);
            bool closed = !g.AllVisibleSlotsDecoded && g.ShapeCode == "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN" &&
                g.VerifiedBackSlots == 2 && g.VisibleSlots == 4 && g.KnownFaces == 2 && faces.Length == 2 &&
                !g.SlotPaths.IsDefault && g.SlotPaths.Length == 4 && g.SlotPaths.Distinct(StringComparer.Ordinal).Count() == 4 &&
                !g.ErrorCodes.IsDefault && g.ErrorCodes.IsEmpty &&
                faces.All(t => t.Area == "meld-bottom" && t.ScreenDirection == "bottom" && t.Stable &&
                    g.SlotPaths.Contains(t.SlotPath) && t.Kind34 == g.InferredClosedKanKind34);
            return new VisibleMeldGroupEvidence(g.GroupPath, faces.Select(t => new VisibleTile(t.Kind34, t.RedFive)).ToImmutableArray(),
                g.VisibleSlots, complete, g.Stable, "pinned-visible-own-meld-faces; explicit two-back closed-kan pattern")
            { VerifiedBackSlots = closed ? g.VerifiedBackSlots : 0, ClosedKanKind34 = closed ? g.InferredClosedKanKind34 : null };
        }).ToImmutableArray();

    private static readonly (string, ScreenPosition)[] Directions =
        [("bottom", ScreenPosition.Lower), ("right", ScreenPosition.Right), ("top", ScreenPosition.Upper), ("left", ScreenPosition.Left)];
}
