using System.Collections.Immutable;
using Mahjong.Core;

namespace Mahjong.Cn;

public static class SnapshotValidator
{
    private const ActionFlags AllActions = ActionFlags.Discard | ActionFlags.Riichi | ActionFlags.Tsumo |
        ActionFlags.Ron | ActionFlags.Pon | ActionFlags.Chi | ActionFlags.AnKan |
        ActionFlags.MinKan | ActionFlags.ShouMinKan | ActionFlags.Pass;

    public static ValidationResult Validate(VisibleSnapshot s)
    {
        var errors = ImmutableArray.CreateBuilder<ValidationIssue>();
        void Error(string path, string code, string message) => errors.Add(new(path, code, message));
        void Required(bool exists, string path) { if (!exists) Error(path, "unknown", "字段未识别；禁止使用默认值补全"); }
        Required(!string.IsNullOrWhiteSpace(s.RoundId), nameof(s.RoundId));
        Required(s.RoundWind is not null, nameof(s.RoundWind));
        Required(s.HandNumber is not null, nameof(s.HandNumber));
        Required(s.Honba is not null, nameof(s.Honba));
        Required(s.RiichiSticks is not null, nameof(s.RiichiSticks));
        Required(s.OurSeat is not null, nameof(s.OurSeat));
        Required(s.WallRemaining is not null, nameof(s.WallRemaining));
        Required(s.TurnIndex is not null, nameof(s.TurnIndex));
        Required(s.OurDoubleRiichi is not null, nameof(s.OurDoubleRiichi));
        Required(!s.Hand.IsDefault, nameof(s.Hand));
        Required(!s.Seats.IsDefault, nameof(s.Seats));
        Required(!s.DoraIndicators.IsDefault, nameof(s.DoraIndicators));
        Required(s.LegalFlags is not null, nameof(s.LegalFlags));
        Required(!s.DiscardableSlots.IsDefault, nameof(s.DiscardableSlots));
        Required(s.DrawnTileKnown is true, nameof(s.DrawnTileKnown));
        if (errors.Count > 0) return new(errors.ToImmutable());

        if (s.OurSeat is < 0 or > 3) Error(nameof(s.OurSeat), "range", "自家座位须为 0..3");
        if (s.RoundWind is < 0 or > 3) Error(nameof(s.RoundWind), "range", "场风须为 0..3");
        if (s.HandNumber is < 1 or > 4) Error(nameof(s.HandNumber), "range", "局数须为 1..4");
        if (s.Honba < 0 || s.RiichiSticks < 0 || s.TurnIndex < 0 || s.WallRemaining is < 0 or > 70)
            Error("Round", "range", "本场、供托、巡数或余牌数越界");
        if (s.Seats.Length != 4) Error(nameof(s.Seats), "count", "必须完整识别四家");
        if (s.DoraIndicators.Length is < 1 or > 5) Error(nameof(s.DoraIndicators), "count", "宝牌指示牌数量须为 1..5");
        if ((s.LegalFlags & ~AllActions) != 0) Error(nameof(s.LegalFlags), "flags", "存在未知动作位");
        const ActionFlags reaction = ActionFlags.Ron | ActionFlags.Chi | ActionFlags.Pon | ActionFlags.MinKan;
        const ActionFlags ownTurn = ActionFlags.Discard | ActionFlags.Riichi | ActionFlags.Tsumo | ActionFlags.AnKan | ActionFlags.ShouMinKan;
        if ((s.LegalFlags & reaction) != 0 && (s.LegalFlags & ownTurn) != 0)
            Error(nameof(s.LegalFlags), "legal", "对他家弃牌的反应动作与自家回合动作同时存在");
        if (s.Hand.Length is < 1 or > 14) Error(nameof(s.Hand), "count", "手牌数量越界");
        if (errors.Count > 0) return new(errors.ToImmutable());

        var counts = new int[34];
        bool CheckTile(VisibleTile tile, string path, bool count)
        {
            if (tile.Id is < 0 or >= 34) { Error(path, "tile-id", "牌种编号须为 0..33"); return false; }
            if (tile.Red && tile.Id is not (4 or 13 or 22)) Error(path, "red-five", "只有五万、五筒、五索能标记为赤牌");
            if (count && ++counts[tile.Id] > 4) Error(path, "copies", $"{tile.ChineseName} 的公开实体超过四张");
            return true;
        }
        var slots = new HashSet<int>();
        for (int i = 0; i < s.Hand.Length; i++)
        {
            var tile = s.Hand[i];
            if (tile.Slot is < 0 or > 13 || !slots.Add(tile.Slot)) Error($"Hand[{i}].Slot", "slot", "手牌槽位重复或越界");
            CheckTile(tile.Tile, $"Hand[{i}]", true);
        }
        if (s.DrawnSlot is { } draw && !slots.Contains(draw)) Error(nameof(s.DrawnSlot), "draw-slot", "摸牌槽位不存在于当前手牌");
        if ((s.LegalFlags & ActionFlags.Tsumo) != 0 && s.DrawnSlot is null)
            Error(nameof(s.DrawnSlot), "draw-slot", "自摸动作缺少摸牌身份");
        var discardSlots = new HashSet<int>();
        foreach (int slot in s.DiscardableSlots)
            if (!slots.Contains(slot) || !discardSlots.Add(slot)) Error(nameof(s.DiscardableSlots), "slot", "可弃牌槽位重复或不属于手牌");
        bool discard = (s.LegalFlags & ActionFlags.Discard) != 0;
        if (discard != (s.DiscardableSlots.Length > 0)) Error(nameof(s.DiscardableSlots), "legal", "出牌动作与可弃牌列表矛盾");
        if ((s.LegalFlags & ActionFlags.Riichi) != 0 && !discard) Error(nameof(s.LegalFlags), "legal", "立直必须提供合法弃牌");
        foreach (var tile in s.DoraIndicators) CheckTile(tile, nameof(s.DoraIndicators), true);

        for (int seat = 0; seat < 4; seat++)
        {
            var view = s.Seats[seat];
            if (view is null) { Error($"Seats[{seat}]", "unknown", "座位未识别"); continue; }
            string p = $"Seats[{seat}]";
            Required(view.Wind is not null, p + ".Wind");
            if (view.Wind != seat) Error(p + ".Wind", "seat-order", "座位必须按当前东南西北排序");
            Required(view.Score is not null, p + ".Score");
            Required(view.Riichi is not null, p + ".Riichi");
            Required(view.Ippatsu is not null, p + ".Ippatsu");
            Required(view.RiichiDiscardIndex is not null, p + ".RiichiDiscardIndex");
            Required(!view.River.IsDefault, p + ".River");
            Required(!view.Melds.IsDefault, p + ".Melds");
            if (view.River.IsDefault || view.Melds.IsDefault) continue;
            if (view.Melds.Length > 4) Error(p + ".Melds", "count", "副露不能超过四组");
            if (view.Riichi == true && (view.RiichiDiscardIndex < 0 || view.RiichiDiscardIndex >= view.River.Length))
                Error(p + ".RiichiDiscardIndex", "riichi", "立直宣言牌位置无效");
            if (view.Riichi == false && view.RiichiDiscardIndex != -1) Error(p + ".RiichiDiscardIndex", "riichi", "未立直时宣言牌位置必须为 -1");
            if (view.Ippatsu == true && view.Riichi != true) Error(p + ".Ippatsu", "riichi", "未立直却标记一发");
            if (view.Riichi == true && view.Melds.Any(x => x is not null && x.Kind != MeldKind.AnKan)) Error(p + ".Melds", "riichi", "立直与开放副露矛盾");
            for (int i = 0; i < view.River.Length; i++)
            {
                var riverTile = view.River[i];
                if (riverTile is null) { Error($"{p}.River[{i}]", "unknown", "弃牌未识别"); continue; }
                Required(riverTile.Claimed is not null, $"{p}.River[{i}].Claimed");
                Required(riverTile.Tedashi is not null, $"{p}.River[{i}].Tedashi");
                CheckTile(riverTile.Tile, $"{p}.River[{i}]", riverTile.Claimed == false);
            }
        }
        if (errors.Any(x => x.Code == "unknown")) return new(errors.ToImmutable());
        long scoreSpread = s.Seats.Max(x => (long)x.Score!.Value) - s.Seats.Min(x => (long)x.Score!.Value);
        if (scoreSpread > int.MaxValue)
            Error("Seats.Score", "score-range", "点差超出原引擎 Int32 安全范围，拒绝继续计算");
        var claims = new HashSet<(int Seat, int Index)>();
        for (int seat = 0; seat < 4; seat++)
        {
            for (int i = 0; i < s.Seats[seat].Melds.Length; i++)
            {
                var meld = s.Seats[seat].Melds[i];
                string p = $"Seats[{seat}].Melds[{i}]";
                if (meld is null || meld.Tiles.IsDefault) { Error(p, "unknown", "副露未识别"); continue; }
                bool kan = meld.Kind is MeldKind.AnKan or MeldKind.MinKan or MeldKind.ShouMinKan;
                if (!Enum.IsDefined(meld.Kind) || meld.Tiles.Length != (kan ? 4 : 3)) { Error(p, "meld-shape", "副露类别或张数无效"); continue; }
                bool validTiles = true;
                foreach (var tile in meld.Tiles) validTiles &= CheckTile(tile, p, true);
                if (!validTiles) continue;
                var ids = meld.Tiles.Select(x => x.Id).Order().ToArray();
                if (meld.Kind == MeldKind.Chi)
                {
                    if (ids[0] >= 27 || ids[0] / 9 != ids[2] / 9 || ids[1] != ids[0] + 1 || ids[2] != ids[0] + 2)
                        Error(p, "meld-shape", "吃必须为同花色顺子");
                    if (meld.FromSeat != (seat + 3) % 4) Error(p, "chi-source", "吃牌来源必须为上家");
                }
                else if (ids.Any(id => id != ids[0])) Error(p, "meld-shape", "碰杠必须为同一种牌");
                if (meld.Kind == MeldKind.AnKan)
                {
                    if (meld.FromSeat is not null || meld.RiverIndex is not null || meld.ClaimedTile is not null) Error(p, "meld-source", "暗杠不能有他家来源");
                    continue;
                }
                if (meld.FromSeat is not (>= 0 and <= 3) || meld.FromSeat == seat || meld.RiverIndex is not (>= 0) || meld.ClaimedTile is null)
                { Error(p, "meld-source", "开放副露必须有准确的来源座位、牌河位置和鸣牌"); continue; }
                var source = s.Seats[meld.FromSeat.Value].River;
                int index = meld.RiverIndex.Value;
                if (index >= source.Length || !claims.Add((meld.FromSeat.Value, index)) ||
                    source[index].Claimed != true || source[index].Tile != meld.ClaimedTile.Value || !meld.Tiles.Contains(meld.ClaimedTile.Value))
                    Error(p, "meld-source", "鸣牌与来源牌河不一致或被重复使用");
            }
        }
        for (int seat = 0; seat < 4; seat++)
            for (int i = 0; i < s.Seats[seat].River.Length; i++)
                if (s.Seats[seat].River[i].Claimed == true && !claims.Contains((seat, i)))
                    Error($"Seats[{seat}].River[{i}]", "orphan-claim", "被鸣弃牌缺少对应副露");
        int effectiveTiles = s.Hand.Length + s.Seats[s.OurSeat!.Value].Melds.Length * 3;
        if (effectiveTiles is not (13 or 14) || (discard && effectiveTiles != 14) || (s.DrawnSlot is not null && effectiveTiles != 14))
            Error(nameof(s.Hand), "hand-count", "手牌与副露折算张数不符；可能处于动画或过渡状态");
        if (s.OurDoubleRiichi == true && s.Seats[s.OurSeat.Value].Riichi != true) Error(nameof(s.OurDoubleRiichi), "riichi", "双立直与立直状态矛盾");
        if (s.Seats[s.OurSeat.Value].Riichi == true && (s.LegalFlags & ActionFlags.Riichi) != 0)
            Error(nameof(s.LegalFlags), "riichi", "已立直却再次出现立直动作");
        if (s.Seats[s.OurSeat.Value].Riichi == true && discard && (s.DrawnSlot is null || s.DiscardableSlots.Any(x => x != s.DrawnSlot)))
            Error(nameof(s.DiscardableSlots), "riichi-discard", "立直后普通出牌必须仅允许摸切");
        return new(errors.ToImmutable());
    }
}
