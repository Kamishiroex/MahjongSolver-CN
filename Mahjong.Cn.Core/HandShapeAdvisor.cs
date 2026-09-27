using System.Collections.Immutable;
using Mahjong.Core;
using Mahjong.Engine;

namespace Mahjong.Cn;

/// <summary>The caller must explicitly select the limited purpose; this does not certify a playable turn.</summary>
public enum HandShapePurpose { Unspecified, ClosedHandEfficiencyOnly }
public enum HandShapeStatus { InvalidInput, Suggestions, CompleteShape, EngineError }

/// <summary>One-based visible position (1..14), never a callback index or proof that the tile is discardable.</summary>
public readonly record struct HandShapeTile(int DisplayPosition, VisibleTile Tile);

/// <summary>A 34-kind structural improvement. Copies outside our original 14 are an upper bound, not live-wall tiles.</summary>
public sealed record HandShapeImprovingTile(VisibleTile Tile, int MaximumCopiesOutsideHand);

public sealed record HandShapeDiscardOption(int DisplayPosition, VisibleTile Tile, int ShantenAfter,
    ImmutableArray<HandShapeImprovingTile> ImprovingTiles, int TheoreticalImprovingCopies, string Reason)
{
    public int TheoreticalImprovingKinds => ImprovingTiles.Length;
}

public sealed record HandShapeAdvice(HandShapeStatus Status, string Code, string Message, int? CurrentShanten,
    ImmutableArray<HandShapeDiscardOption> Options, ImmutableArray<string> Limitations)
{
    public HandShapeDiscardOption? Recommended => Options.IsEmpty ? null : Options[0];
}

/// <summary>
/// Closed, complete 14-tile shape analysis only. No VisibleSnapshot, invented opponents/round/dora,
/// legal actions, live-wall estimate or automation permission is constructed. The caller must supply
/// verified own tiles, no chi/pon/kan (including concealed kan), and independently handle all turn,
/// riichi, win-legality and execution guards. This stateless result never certifies those conditions.
/// </summary>
public static class HandShapeAdvisor
{
    private static readonly ImmutableArray<string> Limitations =
    [
        "仅分析没有吃、碰、杠的完整14枚本家手牌；不判断当前是否轮到你或哪些牌允许打出。",
        "理论进张只扣除本家这14枚已知牌；未计牌河、副露、其他玩家手牌及牌山，不代表实际可摸数量。",
        "按向听数、理论进张上限比较牌形；未评估役种、宝牌、点数、防守、振听或立直后的弃牌限制。",
        "显示位置不是游戏回调索引；此结果不授权弃牌、立直、荣和、自摸或其他自动操作。",
    ];

    public static HandShapeAdvice Analyze(ImmutableArray<HandShapeTile> tiles, HandShapePurpose purpose)
    {
        if (purpose != HandShapePurpose.ClosedHandEfficiencyOnly)
            return Invalid("SHAPE_PURPOSE_REQUIRED", "必须明确选择闭门14枚手牌的牌效率分析用途。");
        if (tiles.IsDefault || tiles.Length != 14)
            return Invalid("SHAPE_TILE_COUNT", "需要14枚已确认的完整本家手牌；缺失或多出的牌不能补齐或忽略。");

        var counts = new int[Tile.Count34];
        var positions = new HashSet<int>();
        foreach (var entry in tiles)
        {
            if (entry.DisplayPosition is < 1 or > 14 || !positions.Add(entry.DisplayPosition))
                return Invalid("SHAPE_DISPLAY_POSITION", "显示位置必须为互不重复的1至14；它不代表可执行的弃牌槽位。");
            if (entry.Tile.Id is < 0 or >= Tile.Count34)
                return Invalid("SHAPE_UNKNOWN_TILE", "存在未知牌种；暂停牌效率分析。");
            if (entry.Tile.Red && entry.Tile.Id is not (4 or 13 or 22))
                return Invalid("SHAPE_RED_TILE", "赤牌只能标记五万、五筒或五索。");
            if (++counts[entry.Tile.Id] > Tile.CopiesPerKind)
                return Invalid("SHAPE_TILE_COPIES", "同种牌（含普通五和赤五）超过4枚；暂停牌效率分析。");
        }

        try
        {
            var hand = new Hand(counts);
            var shanten = ShantenCalculator.Compute(hand);
            if (shanten.IsAgari)
                return new(HandShapeStatus.CompleteShape, "SHAPE_COMPLETE",
                    "已组成和牌形状，暂停弃牌建议；是否有役、满足最低番数及允许荣和/自摸仍需另行判断。",
                    shanten.Min, [], Limitations);

            var options = ImmutableArray.CreateBuilder<HandShapeDiscardOption>();
            foreach (var candidate in UkeireEnumerator.Enumerate(hand))
            {
                // Upstream collapses red and normal fives into the same kind. Resolve to one observed
                // physical tile without reordering input or pretending a display position is a callback.
                var physical = tiles.Where(t => t.Tile.Id == candidate.Discard.Id)
                    .OrderBy(t => t.Tile.Red).ThenBy(t => t.DisplayPosition).First();
                var improving = candidate.AcceptedKinds
                    .Where(t => counts[t.Id] < Tile.CopiesPerKind)
                    .OrderBy(t => t.Id)
                    .Select(t => new HandShapeImprovingTile(new VisibleTile(t.Id), Tile.CopiesPerKind - counts[t.Id]))
                    .ToImmutableArray();
                int maximum = improving.Sum(t => t.MaximumCopiesOutsideHand);
                // Do not use candidate.WeightedCount: upstream with wall=null assigns FOUR per kind,
                // including our own held copies. Even the discarded tile is known, not a drawable copy.
                string progress = candidate.ShantenAfter == 0 ? "听牌（0向听）" : $"{candidate.ShantenAfter}向听";
                bool preserveRed = !physical.Tile.Red && tiles.Any(t => t.Tile.Id == physical.Tile.Id && t.Tile.Red);
                string reason = $"弃后{progress}；理论进张{improving.Length}种、至多{maximum}枚（只扣本家已知牌，非牌山余量）。" +
                    (preserveRed ? "同种优先弃普通五，保留赤五。" : "");
                options.Add(new(physical.DisplayPosition, physical.Tile, candidate.ShantenAfter, improving, maximum, reason));
            }
            var ordered = options.OrderBy(t => t.ShantenAfter).ThenByDescending(t => t.TheoreticalImprovingCopies)
                .ThenByDescending(t => t.TheoreticalImprovingKinds).ThenBy(t => t.Tile.Red)
                .ThenBy(t => t.Tile.Id).ThenBy(t => t.DisplayPosition).ToImmutableArray();
            if (ordered.IsEmpty)
                return new(HandShapeStatus.EngineError, "SHAPE_NO_CANDIDATES", "原引擎未给出牌形候选；暂停建议。", null, [], Limitations);
            return new(HandShapeStatus.Suggestions, "SHAPE_SUGGESTIONS",
                "仅按闭门手牌效率排序：先降低向听，再比较理论进张；同等牌形优先保留赤五。",
                shanten.Min, ordered, Limitations);
        }
        catch (Exception ex)
        {
            return new(HandShapeStatus.EngineError, "SHAPE_ENGINE_ERROR",
                $"牌形引擎异常（{ex.GetType().Name}）；本次不提供建议。", null, [], Limitations);
        }
    }

    private static HandShapeAdvice Invalid(string code, string message) =>
        new(HandShapeStatus.InvalidInput, code, message, null, [], Limitations);
}
