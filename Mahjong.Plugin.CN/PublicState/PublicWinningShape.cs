using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Engine;

namespace Mahjong.Plugin.CN.PublicState;

/// <summary>Shape only, without yaku/scoring or permission to call Ron.</summary>
internal static class PublicWinningShape
{
    internal static bool Completes(ImmutableArray<VisibleTile> hand, PublicImageInventory melds, VisibleTile discard)
    {
        if (hand.IsDefault || melds.Groups.IsDefault || melds.Groups.Length > 4 ||
            hand.Length + 3 * melds.Groups.Length != 13 || discard.Id is < 0 or > 33) return false;
        var counts = new int[34];
        foreach (var tile in hand)
        {
            if (tile.Id is < 0 or > 33 || ++counts[tile.Id] > 4) return false;
        }
        if (++counts[discard.Id] > 4) return false;
        var physical = (int[])counts.Clone();
        foreach (var group in melds.Groups)
        {
            var faces = melds.Tiles.Where(t => t.GroupPath == group.GroupPath).Select(t => t.Tile.Id).Order().ToArray();
            if (faces.Length != group.KnownFaces || faces.Any(id => id is < 0 or > 33)) return false;
            if (group.ShapeCode == "PUBLIC_CHI_THREE_FACE_PATTERN")
            {
                if (faces.Length != 3 || faces[0] >= 27 || faces[0] / 9 != faces[2] / 9 ||
                    faces[1] != faces[0] + 1 || faces[2] != faces[0] + 2) return false;
                foreach (int id in faces) physical[id]++;
            }
            else
            {
                if (faces.Length == 0 || faces.Distinct().Count() != 1 ||
                    group.ShapeCode is not ("PUBLIC_PON_THREE_FACE_PATTERN" or "PUBLIC_KAN_FOUR_FACE_PATTERN" or "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN")) return false;
                // Closed-kan kind is visible on both public faces. No hidden red identity is inferred.
                physical[faces[0]] += group.VisibleSlots;
            }
        }
        if (physical.Any(c => c > 4)) return false;
        return ShantenCalculator.Standard(counts, melds.Groups.Length) == -1 ||
            melds.Groups.Length == 0 && (ShantenCalculator.Chiitoitsu(counts) == -1 || ShantenCalculator.Kokushi(counts) == -1);
    }
}
