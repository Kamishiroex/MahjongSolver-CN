using Mahjong.Cn.PublicState;

namespace Mahjong.Plugin.CN.PublicState;

/// <summary>Complete public group geometry; verified backs are never decoded as hidden faces.</summary>
internal static class PublicMeldInventoryProof
{
    internal static bool TryRead(Field<PublicImageInventory> field, ObservationReference reference,
        out PublicImageInventory inventory)
    {
        inventory = field.Value!;
        if (field.Availability != Availability.Known || field.SourceKind is not (SourceKind.Observed or SourceKind.Derived) ||
            field.Observation is not { } observed || observed.Sequence != reference.Sequence ||
            observed.ObservedAtUtc != reference.ObservedAtUtc ||
            inventory is not { RegionReadable: true, UnknownComponents: 0 } ||
            inventory.Groups.IsDefault || inventory.Tiles.IsDefault || !inventory.RejectionCodes.IsEmpty ||
            inventory.Groups.Length > 4 || inventory.ObservedEmpty != (inventory.VisibleSlots == 0) ||
            inventory.Groups.Select(g => g.GroupPath).Distinct().Count() != inventory.Groups.Length ||
            inventory.Tiles.Select(t => t.SlotPath).Distinct().Count() != inventory.Tiles.Length ||
            inventory.Tiles.Any(t => !t.Stable) ||
            inventory.Groups.Sum(g => g.VisibleSlots) != inventory.VisibleSlots ||
            inventory.Groups.Sum(g => g.KnownFaces) != inventory.Tiles.Length) return false;
        var tiles = inventory.Tiles;
        if (inventory.Groups.Any(g => !g.Stable || tiles.Count(t => t.GroupPath == g.GroupPath) != g.KnownFaces ||
            (g.ShapeCode switch
            {
                "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN" => g.VisibleSlots != 4 || g.KnownFaces != 2 ||
                    g.VerifiedBackSlots != 2 || g.InferredClosedKanKind34 is not (>= 0 and <= 33) ||
                    tiles.Any(t => t.GroupPath == g.GroupPath && t.Tile.Id != g.InferredClosedKanKind34),
                "PUBLIC_CHI_THREE_FACE_PATTERN" or "PUBLIC_PON_THREE_FACE_PATTERN" =>
                    g.VisibleSlots != 3 || g.KnownFaces != 3 || g.VerifiedBackSlots != 0,
                "PUBLIC_KAN_FOUR_FACE_PATTERN" => g.VisibleSlots != 4 || g.KnownFaces != 4 || g.VerifiedBackSlots != 0,
                _ => true,
            }))) return false;
        return inventory.Groups.Any(g => g.ShapeCode == "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN") ||
            inventory is { Stable: true, AllVisibleSlotsDecoded: true };
    }
}
