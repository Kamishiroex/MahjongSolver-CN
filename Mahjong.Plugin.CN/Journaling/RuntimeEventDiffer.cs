using System.Collections.Immutable;
using Mahjong.Core;

namespace Mahjong.Plugin.CN.Journaling;

/// <summary>A change in legacy public observations, never an acknowledged native/mjai action.</summary>
internal sealed record RuntimeObservedChange(Guid ObservationSessionId, long Sequence, string Type,
    int? RelativePlayer, ImmutableArray<int> TileIds, int? CountDelta, int? RedCountDelta,
    MeldKind? MeldKind, int? ScoreBefore, int? ScoreAfter, bool HistoryGap, string Reason)
{
    public string Source => "StateSnapshot/public-runtime-diff";
    public string SourceKind => "LegacyAssumption";
    public string MappingStatus => "Candidate";
    public bool HistoryComplete => false;
}

/// <summary>
/// Bounded adjacent-observation differ. No calls, menus, policy choices or callbacks are
/// evidence that a win occurred. Opponent draws have no field here and are not invented.
/// An observation batch is unordered; it is not a complete event stream for a real AI.
/// </summary>
internal sealed class RuntimeEventDiffer
{
    internal const int MaximumEventsPerObservation = 32;
    private Guid session;
    private long lastSequence = -1;
    private StateSnapshot? previous;
    private bool inResult;

    internal void Reset()
    { session = Guid.Empty; lastSequence = -1; previous = null; inResult = false; }

    internal ImmutableArray<RuntimeObservedChange> Observe(Guid observationSessionId, long sequence, StateSnapshot snapshot)
    {
        var events = ImmutableArray.CreateBuilder<RuntimeObservedChange>();
        void Add(string type, string reason, int? player = null, IEnumerable<int>? tiles = null, int? count = null,
            int? reds = null, MeldKind? kind = null, int? before = null, int? after = null, bool gap = false)
        {
            if (events.Count < MaximumEventsPerObservation)
                events.Add(new(observationSessionId, sequence, type, player, tiles?.ToImmutableArray() ?? [], count,
                    reds, kind, before, after, gap, reason));
        }
        void Gap(string reason) => Add("history_gap", reason, gap: true);

        if (snapshot is null || observationSessionId == Guid.Empty || sequence < 0)
        { Reset(); Gap("观察身份、序号或快照无效，不能建立连续历史。"); return events.ToImmutable(); }
        bool boundary = session != observationSessionId;
        if (boundary)
        { previous = null; inResult = false; session = observationSessionId; lastSequence = -1; }
        if (sequence <= lastSequence)
        { previous = null; Gap("观察序号未递增，已丢弃差分基线。"); return events.ToImmutable(); }
        if (previous is not null && sequence != lastSequence + 1)
        { previous = null; Gap("中间观察序号缺失，不能跨缺口生成相邻动作。"); }
        lastSequence = sequence;
        if (snapshot.AddonStateCode == 29)
        {
            if (!inResult)
            {
                Add("round_end_candidate", "上游状态码29结果界面候选；不能确定荣和、自摸或流局。", gap: true);
                Gap("结果类型与漏过事件未知，不根据菜单或操作提交生成获胜事件。");
            }
            previous = null; inResult = true;
            return events.ToImmutable();
        }
        if (!Usable(snapshot))
        { previous = null; Gap("公开快照牌数、牌种或容器长度不完整，停止差分，等待新基线。"); return events.ToImmutable(); }
        var current = Copy(snapshot);
        if (previous is not { } old)
        {
            previous = current;
            Add("observation_baseline", inResult ? "结果界面后的当前公开基线；不推造新局配牌或遗漏动作。" : "当前公开状态作为新基线，此前历史未知。", gap: true);
            inResult = false;
            return events.ToImmutable();
        }
        if (current.WallRemaining > old.WallRemaining + 5 ||
            (current.SeatInfoKnown && old.SeatInfoKnown &&
             (current.RoundWind != old.RoundWind || current.DealerSeat != old.DealerSeat)))
        {
            Add("round_boundary_candidate", "剩牌上跳或已知局/庄上下文改变；旧事件基线作废。", gap: true);
            previous = current;
            return events.ToImmutable();
        }

        bool meldsSame = SameMelds(old.OurMelds, current.OurMelds);
        var added = Difference(current.Hand, old.Hand);
        var removed = Difference(old.Hand, current.Hand);
        int ownCountDelta = current.Seats[0].DiscardCount - old.Seats[0].DiscardCount;
        bool explainedHand = old.Hand.SequenceEqual(current.Hand) || (added.Length == 0 && removed.Length == 0);
        if (meldsSame && old.Hand.Count + old.OurMelds.Count * 3 == 13 &&
            current.Hand.Count + current.OurMelds.Count * 3 == 14 && ownCountDelta == 0)
        {
            int reds = current.AkaDora - old.AkaDora;
            if (added.Length == 1 && removed.Length == 0 && reds is 0 or 1 &&
                (reds == 0 || added[0] is 4 or 13 or 22))
                Add("draw", "本家闭手净增加唯一一张，副露及本家牌河计数不变；仍是原读牌器候选。", 0, added, 1, reds);
            else
            {
                Add("draw_unknown_tile", "本家归一化手牌13→14，但牌种或赤牌净差分不唯一；只保留未知牌摸牌候选。", 0, count: 1, gap: true);
                Gap("手牌增长期间存在无法唯一解释的牌种变化，不能填造摸牌身份。");
            }
            explainedHand = true;
        }

        for (int player = 0; player < 4; player++)
        {
            var before = old.Seats[player]; var after = current.Seats[player];
            int delta = after.DiscardCount - before.DiscardCount;
            if (delta == 1)
            {
                bool readableAppend = before.Discards.Count == before.DiscardCount && after.Discards.Count == after.DiscardCount &&
                    after.Discards.Count == before.Discards.Count + 1 && before.Discards.SequenceEqual(after.Discards.Take(before.Discards.Count));
                if (readableAppend)
                    Add("discard", "该相对方位牌河计数+1且公开尾牌追加；旧模型不含牌河赤牌身份，未猜测赤色或摸切。", player, [after.Discards[^1].Id], 1);
                else if (player == 0 && meldsSame && removed.Length == 1 && added.Length == 0)
                    Add("discard", "本家牌河计数+1且闭手净减少唯一一张；牌河显示未完整，来源保持候选。", 0, removed, 1);
                else
                {
                    Add("discard_unknown_tile", "牌河计数+1但新增牌面无法唯一确认，未填造牌种。", player, count: 1, gap: true);
                    Gap("至少一张弃牌身份未知。");
                }
                if (player == 0 && meldsSame && removed.Length == 1 && added.Length == 0) explainedHand = true;
            }
            else if (delta != 0)
            {
                Add("discard_count_changed", "同次采样跨过多张弃牌或计数缩短，不生成逐张事件或顺序。", player, count: delta, gap: true);
                Gap("牌河计数变化无法由一个相邻公开动作解释。");
            }
            else if (!before.Discards.SequenceEqual(after.Discards))
                Gap("牌河内容改变但计数未变，可能是被鸣走、重排或过渡，不能猜测事件。");

            if (before.Riichi != after.Riichi)
                Add("riichi_status_changed", "原公开座位视图的立直标记改变；不推造立直接受、供托扣除或宣言牌位置。", player, count: after.Riichi ? 1 : -1);
            if (old.Scores[player] != current.Scores[player])
                Add("score_changed", "公开点数变化；不据此判定荣和、自摸、罚符或立直付款。", player, before: old.Scores[player], after: current.Scores[player]);
        }
        if (!meldsSame)
        {
            // MeldTracker can also be changed by submitted input or recovery. Never call
            // that an observed chi/pon/kan acknowledgement.
            Add("meld_inventory_changed", "本家实验副露库存改变，来源可能是手牌差分、操作提交后的假设或恢复；不是动作确认。",
                0, current.OurMelds.SelectMany(m => m.Tiles).Select(t => (int)t.Id), current.OurMelds.Count - old.OurMelds.Count, gap: true);
            Gap("副露库存变化不能独立证明种类、来源及时序，保留缺失历史标记。");
            explainedHand = true;
        }
        if (!explainedHand)
            Gap("本家手牌变化无法唯一对应摸牌、弃牌或副露，可能跨过多步或正处过渡。");
        if (!old.DoraIndicators.SequenceEqual(current.DoraIndicators))
            Add("dora_display_changed", "旧读牌器的宝牌显示数组改变；实际宝牌/指示牌语义未在此认证。", tiles: current.DoraIndicators.Select(x => (int)x.Id));
        if (events.Count(x => x.Type is "draw" or "discard" or "meld_inventory_changed") > 1)
            Gap("同次观察出现多个玩家/动作变化，无法证明发生先后；此批不能直接当完整引擎事件流。");
        previous = current;
        return events.ToImmutable();
    }

    private static bool Usable(StateSnapshot value) => value.SchemaVersion == StateSnapshot.CurrentSchemaVersion &&
        value.Hand.Count is >= 1 and <= 14 && value.OurMelds.Count <= 4 && value.Seats.Count == 4 && value.Scores.Count == 4 &&
        value.Hand.Count + value.OurMelds.Count * 3 is 13 or 14 && value.AkaDora is >= 0 and <= 4 &&
        value.Hand.All(t => t.Id < 34) && value.DoraIndicators.Count <= 5 && value.DoraIndicators.All(t => t.Id < 34) &&
        value.Seats.All(s => s.DiscardCount is >= 0 and <= 40 && s.Discards.Count <= 40 && s.Discards.All(t => t.Id < 34)) &&
        value.OurMelds.All(m => Enum.IsDefined(m.Kind) && m.Tiles is not null && m.Tiles.Length == m.TileCount && m.Tiles.All(t => t.Id < 34));

    private static int[] Difference(IReadOnlyList<Tile> a, IReadOnlyList<Tile> b)
    {
        var counts = new int[34];
        foreach (var tile in a) counts[tile.Id]++;
        foreach (var tile in b) counts[tile.Id]--;
        return Enumerable.Range(0, 34).SelectMany(id => Enumerable.Repeat(id, Math.Max(0, counts[id]))).ToArray();
    }

    private static bool SameMelds(IReadOnlyList<Meld> a, IReadOnlyList<Meld> b) => a.Count == b.Count &&
        a.Zip(b).All(pair => pair.First.Kind == pair.Second.Kind && pair.First.ClaimedTile == pair.Second.ClaimedTile &&
            pair.First.ClaimedFromSeat == pair.Second.ClaimedFromSeat && pair.First.Tiles.SequenceEqual(pair.Second.Tiles));

    private static StateSnapshot Copy(StateSnapshot snapshot) => snapshot with
    {
        Hand = snapshot.Hand.ToArray(), OurMelds = snapshot.OurMelds.Select(m => m with { Tiles = m.Tiles.ToArray() }).ToArray(),
        Scores = snapshot.Scores.ToArray(), DoraIndicators = snapshot.DoraIndicators.ToArray(), UraDoraIndicators = [], Legal = LegalActions.None,
        Seats = snapshot.Seats.Select(s => s with { Discards = s.Discards.ToArray(), DiscardIsTedashi = [], Melds = [] }).ToArray(),
    };
}
