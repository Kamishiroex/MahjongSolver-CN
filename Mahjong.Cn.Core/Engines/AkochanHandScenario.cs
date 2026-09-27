using System.Collections.Immutable;
using System.Text.Json;

namespace Mahjong.Cn.Engines;

/// <summary>
/// An explicitly counterfactual closed-hand experiment. The supplied fourteen visible tiles
/// are real input; every other round field below is a declared assumption, never a live replay.
/// Callers must independently reject open hands, ambiguous inventories and stale game windows.
/// </summary>
public sealed class AkochanHandScenario
{
    public const string Label = "experimental-hand-only";
    public string SourceLabel => Label;
    public bool IsSynthetic => true;
    public bool IsLiveGameDecision => false;
    public AkochanReplay ScenarioReplay { get; }
    public ImmutableArray<VisibleTile> ActualHand { get; }
    public ImmutableArray<string> Assumptions { get; }
    public VisibleTile DoraIndicator { get; }
    public bool UsesProvidedDoraIndicator { get; }

    private AkochanHandScenario(AkochanReplay replay, ImmutableArray<VisibleTile> hand,
        VisibleTile indicator, bool provided, ImmutableArray<string> assumptions)
    {
        ScenarioReplay = replay; ActualHand = hand; DoraIndicator = indicator;
        UsesProvidedDoraIndicator = provided; Assumptions = assumptions;
    }

    /// <summary>The optional tile must be a known indicator, not an unconverted actual-dora display.</summary>
    public static AkochanHandScenario Create(IReadOnlyList<VisibleTile> hand14, VisibleTile? knownDoraIndicator = null)
    {
        ArgumentNullException.ThrowIfNull(hand14);
        if (hand14.Count != 14) throw new AkochanException("AKOCHAN_HAND_SCENARIO_REQUIRES_FOURTEEN");
        var hand = hand14.ToImmutableArray();
        ValidateTiles(hand);
        VisibleTile indicator;
        if (knownDoraIndicator is { } supplied)
        {
            ValidateTiles(hand.Add(supplied));
            indicator = supplied;
        }
        else
        {
            // No true neutral indicator exists in this engine. Avoid giving the input hand
            // an invented immediate bonus, and avoid consuming a fifth physical copy.
            var kinds = hand.Select(t => t.Id).ToHashSet();
            int kind = Enumerable.Range(27, 7).Concat(Enumerable.Range(0, 27))
                .First(id => !kinds.Contains(id) && !kinds.Contains(DoraAfterIndicator(id)));
            indicator = new(kind);
        }
        var assumptions = ImmutableArray.CreateBuilder<string>();
        assumptions.Add("实验手牌分析：实际输入限于这14张手牌和可选的明确指标；其他局况采用以下假设，绝非真实完整牌谱。");
        assumptions.Add("假设门清、东一局、本家为庄家、半庄赛程，四家各25000点，本场和供托均为0。");
        assumptions.Add("假设启用每种花色一张赤五；无历史弃牌、副露或立直，其他三家手牌全部为未知标记。");
        assumptions.Add("假设为开局首次摸牌：输入末张仅充当场景摸牌，不证明真实摸牌身份或当前剩余牌数。");
        assumptions.Add(knownDoraIndicator is null
            ? $"未提供可靠指标：假设指标为{indicator.ChineseName}，对应宝牌不在输入手中；该假设仍会影响未来摸牌估值，并非无宝牌。"
            : $"场景采用所提供的一个公开指标{indicator.ChineseName}；不包含其他指标、暗宝牌或实际杠后历史。");
        assumptions.Add("只提取弃牌牌种与赤牌身份；不采用场景中的立直、杠、和牌或流局动作，不提供真实防守/点差判断。");

        var initial = hand.Take(13).Select(MjaiTileCodec.Encode).ToArray();
        string[][] hands = Enumerable.Range(0, 4).Select(i => i == 0 ? initial : Enumerable.Repeat("?", 13).ToArray()).ToArray();
        string[] events =
        [
            JsonSerializer.Serialize(new { type = "start_game", kyoku_first = 0, aka_flag = true }),
            JsonSerializer.Serialize(new { type = "start_kyoku", bakaze = "E", dora_marker = MjaiTileCodec.Encode(indicator),
                honba = 0, kyotaku = 0, kyoku = 1, oya = 0, scores = new[] { 25000, 25000, 25000, 25000 }, tehais = hands }),
            JsonSerializer.Serialize(new { type = "tsumo", actor = 0, pai = MjaiTileCodec.Encode(hand[13]) }),
        ];
        var replay = AkochanReplay.Create(events, 0, Label, synthetic: true);
        return new(replay, hand, indicator, knownDoraIndicator.HasValue, assumptions.ToImmutable());
    }

    /// <summary>Returns only a physical tile identity, never an authorized game action or slot.</summary>
    public bool TryGetDiscard(AkochanDecision decision, out VisibleTile tile)
    {
        ArgumentNullException.ThrowIfNull(decision);
        tile = default;
        if (!decision.IsSynthetic || decision.IsLiveGameDecision || decision.SourceLabel != ScenarioReplay.SourceLabel ||
            decision.InputSha256 != ScenarioReplay.InputSha256 || decision.EngineCommit != AkochanInstallation.ExpectedCommit)
            return false;
        var moves = decision.Moves;
        if (moves.IsDefaultOrEmpty || moves.Length > 2 || moves.Any(m => m.Actor != 0)) return false;
        if (moves.Length == 2 && moves[0].Type != "reach") return false;
        var discard = moves[^1];
        if (discard.Type != "dahai" || discard.Tile is not { } chosen || !ActualHand.Contains(chosen)) return false;
        tile = chosen;
        return true;
    }

    private static int DoraAfterIndicator(int id) => id switch
    {
        < 27 => id / 9 * 9 + (id % 9 + 1) % 9,
        < 31 => 27 + (id - 27 + 1) % 4,
        _ => 31 + (id - 31 + 1) % 3,
    };

    private static void ValidateTiles(ImmutableArray<VisibleTile> tiles)
    {
        if (tiles.Any(t => t.Id is < 0 or > 33 || t.Red && t.Id is not (4 or 13 or 22)))
            throw new AkochanException("AKOCHAN_HAND_SCENARIO_TILE_INVALID");
        if (tiles.GroupBy(t => t.Id).Any(g => g.Count() > 4 || g.Count(t => t.Red) > 1 ||
                g.Key is 4 or 13 or 22 && g.Count(t => !t.Red) > 3))
            throw new AkochanException("AKOCHAN_HAND_SCENARIO_TILE_COUNT_CONFLICT");
    }
}
