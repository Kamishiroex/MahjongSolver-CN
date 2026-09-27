using System.Collections.Immutable;
using Mahjong.Core;
using Mahjong.Policy.Abstractions;
using Mahjong.Policy.Efficiency;
using Mahjong.Policy.Opponents;
using Mahjong.Policy.Placement;
using Mahjong.Rules.Rulesets;

namespace Mahjong.Cn;

public sealed record CnDiscardOption(int Slot, VisibleTile Tile, int Shanten,
    int UkeireKinds, int UkeireTiles, int DoraRetained, double Score, string Reason);
public sealed record CnSuggestion(string Action, int? DiscardSlot, VisibleTile? DiscardTile,
    string Reason, ImmutableArray<CnDiscardOption> Alternatives, ImmutableArray<string> Limitations);

/// <summary>Managed, offline bridge. Live callers MUST use CnSession's profile/stability gates.</summary>
public static class CnSuggestionBridge
{
    public static CnSuggestion Evaluate(VisibleSnapshot visible)
    {
        var validation = SnapshotValidator.Validate(visible);
        if (!validation.IsValid)
            throw new ArgumentException($"{validation.Issues[0].Path}: {validation.Issues[0].Message}");
        var state = ToEngineSnapshot(visible);
        var unsupported = ActionFlags.Chi | ActionFlags.Pon | ActionFlags.AnKan | ActionFlags.MinKan | ActionFlags.ShouMinKan;
        if ((state.Legal.Flags & unsupported) != 0)
            throw new NotSupportedException("吃碰杠候选的国服读取未验证；此次不生成建议");
        if (state.OurMelds.Any(x => x.IsKan))
            throw new NotSupportedException("上游杠后手牌计数检查不一致；杠后建议暂停，等待单独修复和验证");
        if (state.Legal.Can(ActionFlags.Tsumo) && visible.DrawnSlot is null)
            throw new ArgumentException("DrawnSlot: 自摸计算需要可靠的摸牌槽位");
        var opponent = new CorrectedOwnMeldOpponent();
        var scorer = new CapturingScorer(visible, opponent);
        var policy = new EfficiencyPolicy(opponent, scorer, new HeuristicCallPolicy(new DomanRuleSet()),
            new HeuristicRiichiPolicy(), new HeuristicPushFoldPolicy(), new DomanRuleSet());
        var choice = policy.Choose(state);
        HandTile? discard = choice.DiscardTile is { } chosen ? ResolveSlot(visible, chosen.Id) : null;
        var options = scorer.LastScores.Select(x =>
        {
            var physical = ResolveSlot(visible, x.Discard.Id);
            return new CnDiscardOption(physical.Slot, physical.Tile, x.ShantenAfter, x.UkeireKinds,
                x.UkeireWeighted, x.DoraRetained, x.Score, FormatScore(x));
        }).ToImmutableArray();
        string action = choice.Kind switch
        {
            ActionKind.Discard => "弃牌", ActionKind.Riichi => "立直", ActionKind.Ron => "荣和",
            ActionKind.Tsumo => "自摸", _ => "等待/跳过",
        };
        var reasons = choice.ReasonSteps.Where(x => x.Code != "discard").Select(x => TranslateReason(x.Code)).ToList();
        if (choice.DiscardTile is { } d)
        {
            // Never turn a missing score into default(ScoredDiscard), which would invent zero-valued reasons.
            var option = scorer.LastScores.Single(x => x.Discard.Id == d.Id);
            reasons.Add(FormatScore(option));
        }
        if (reasons.Count == 0) reasons.Add(choice.Kind switch
        {
            ActionKind.Ron => "读取结果提供荣和合法标志；具体和牌质量仍须实测",
            ActionKind.Tsumo => "原引擎使用多玛规则评估自摸；满足引擎最低番数要求",
            _ => "原引擎未给出可执行建议",
        });
        return new(action, discard?.Slot, discard?.Tile, string.Join("；", reasons), options,
        [
            "启发式分值和危险度估计不是胜率或最优保证；国服实机决策质量尚未验证。",
            "原引擎按 34 种牌评分，不单独估值赤五；同牌种优先映射到合法的普通五槽位。",
            "原危险度模型可能重复计算被鸣弃牌；进张数量已按公开实体去重。",
            "原引擎以南场且余牌较少近似终局；不能据此认定实际最后一局。",
        ]);
    }

    /// <summary>Creates a fresh engine object; upstream mutable arrays never alias the public snapshot.</summary>
    public static StateSnapshot ToEngineSnapshot(VisibleSnapshot s)
    {
        var check = SnapshotValidator.Validate(s);
        if (!check.IsValid) throw new ArgumentException($"{check.Issues[0].Path}: {check.Issues[0].Message}");
        var ordered = s.Hand.Where(x => x.Slot != s.DrawnSlot).OrderBy(x => x.Slot).ToList();
        if (s.DrawnSlot is { } draw) ordered.Add(s.Hand.Single(x => x.Slot == draw));
        var seats = s.Seats.Select(x => new SeatView(
            x.River.Select(d => new Tile((byte)d.Tile.Id)).ToArray(),
            x.River.Select(d => d.Tedashi!.Value).ToArray(), x.Melds.Select(ToMeld).ToArray(),
            x.Riichi!.Value, x.RiichiDiscardIndex!.Value, x.Ippatsu!.Value, false, x.River.Length)).ToArray();
        int us = s.OurSeat!.Value;
        return new StateSnapshot(ordered.Select(x => new Tile((byte)x.Tile.Id)).ToArray(),
            s.Seats[us].Melds.Select(ToMeld).ToArray(), us, s.Seats[us].Riichi!.Value,
            s.Seats[us].Ippatsu!.Value, s.OurDoubleRiichi!.Value, s.RoundWind!.Value,
            s.Honba!.Value, s.RiichiSticks!.Value, s.Seats.Select(x => x.Score!.Value).ToArray(),
            s.DoraIndicators.Select(x => new Tile((byte)x.Id)).ToArray(), [], s.WallRemaining!.Value,
            s.TurnIndex!.Value, 0, seats, new LegalActions(s.LegalFlags!.Value,
                s.Hand.Where(x => s.DiscardableSlots.Contains(x.Slot)).Select(x => new Tile((byte)x.Tile.Id)).ToArray(), [], [], []),
            StateSnapshot.CurrentSchemaVersion, SeatInfoKnown: true, AkaDora: s.Hand.Count(x => x.Tile.Red));
    }

    private static Meld ToMeld(VisibleMeld meld) => new(meld.Kind,
        meld.Tiles.Select(x => new Tile((byte)x.Id)).ToArray(),
        meld.ClaimedTile is { } claimed ? new Tile((byte)claimed.Id) : null, meld.FromSeat ?? -1);

    private static HandTile ResolveSlot(VisibleSnapshot visible, int tileId) => visible.Hand
        .Where(x => x.Tile.Id == tileId && visible.DiscardableSlots.Contains(x.Slot))
        .OrderBy(x => x.Tile.Red).ThenBy(x => x.Slot).First();

    private static string FormatScore(ScoredDiscard d) =>
        $"弃后 {d.ShantenAfter} 向听，进张 {d.UkeireKinds} 种/{d.UkeireWeighted} 张未见牌，" +
        $"保留宝牌 {d.DoraRetained} 张，启发式评分 {d.Score:F1}，估计放铳损失 {d.DealInCost:F0}";

    private static string TranslateReason(string code) => code switch
    {
        "ev-push" => "原引擎预期收益估计倾向进攻", "ev-fold" => "原引擎预期收益估计倾向防守",
        "far-vs-riichi" => "向听数较远且他家立直，倾向防守", "late-round-far" => "余牌较少且向听数较远，倾向防守",
        "far-vs-tenpai-prob" => "他家听牌概率估计较高，倾向防守", "riichi-ready" => "门清听牌且点数、余牌、进张满足原引擎立直条件",
        "not-tenpai" => "弃牌后未听牌", "hand-open" => "开放副露不能立直", "low-score" => "点数不足以立直",
        "late-round" => "余牌不足以满足原引擎立直阈值", "thin-waits" => "原引擎估计进张不足",
        _ => $"原引擎理由代码：{code}",
    };

    private sealed class CorrectedOwnMeldOpponent : IOpponentModel
    {
        private readonly OpponentModel inner = new();
        public int OpponentCount => inner.OpponentCount;
        // Upstream counts both OurMelds and Seats[OurSeat].Melds. Keep a single representation for its model.
        public void Update(StateSnapshot state) => inner.Update(state with { OurMelds = [] });
        public double TenpaiProbability(int opponentIndex) => inner.TenpaiProbability(opponentIndex);
        public double ExpectedDealInCost(int tileId) => inner.ExpectedDealInCost(tileId);
    }

    private sealed class CapturingScorer(VisibleSnapshot visible, IOpponentModel opponent) : IDiscardPolicy
    {
        public ScoredDiscard[] LastScores { get; private set; } = [];
        public ScoredDiscard[] Score(StateSnapshot state)
        {
            var wall = new Wall();
            foreach (var t in visible.Hand) wall.Observe(new Tile((byte)t.Tile.Id));
            foreach (var t in visible.DoraIndicators) wall.Observe(new Tile((byte)t.Id));
            foreach (var seat in visible.Seats)
            {
                foreach (var d in seat.River.Where(x => x.Claimed == false)) wall.Observe(new Tile((byte)d.Tile.Id));
                foreach (var meld in seat.Melds)
                    foreach (var t in meld.Tiles) wall.Observe(new Tile((byte)t.Id));
            }
            var allowed = state.Legal.DiscardableTiles.Select(x => x.Id).ToHashSet();
            LastScores = DiscardScorer.Score(state, wall: wall,
                    placement: new PlacementAdjuster().ComputeFor(state), opponentModel: opponent)
                .Where(x => allowed.Contains(x.Discard.Id)).ToArray();
            return LastScores;
        }
    }
}
