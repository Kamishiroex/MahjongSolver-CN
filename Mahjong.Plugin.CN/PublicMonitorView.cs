using System.Collections.Immutable;
using Dalamud.Bindings.ImGui;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN;

/// <summary>Presentation only; no game reads, strategy calls or native input.</summary>
internal static class PublicMonitorView
{
    internal static void Draw(Plugin plugin)
    {
        ImGui.TextWrapped("只读公开牌局：开启会停止实验打牌。目标每秒十次观察；图像、局况和事件完整性分别显示。");
        ImGui.BeginDisabled(plugin.Monitoring || plugin.Identity.Error is not null);
        if (ImGui.Button("开启只读监视（/mjcn monitor）")) plugin.StartPublicMonitor();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("导出公开牌局诊断")) plugin.ExportPublicMonitor();
        ImGui.TextWrapped(plugin.ExportStatus);
        var monitor = plugin.PublicMonitor;
        ImGui.TextWrapped(monitor.Status);
        if ((monitor.Current ?? plugin.CurrentJournalPublicSnapshot) is not { } snapshot)
        {
            ImGui.TextWrapped("当前字段：未知。停止后不沿用上一帧；历史观察只用于显式导出。输入测试码和版本核验规则同其他功能。");
            DrawPublicTable(plugin.CurrentPublicTable);
            return;
        }
        ImGui.TextUnformatted($"观察会话 {snapshot.SessionId.ToString("N")[..8]} · 状态版本 {snapshot.StateRevision}");
        if (snapshot.Observation is { } observation)
            ImGui.TextUnformatted($"采样 #{observation.Sequence} · {observation.ObservedAtUtc.ToLocalTime():HH:mm:ss.fff} · 数据年龄 {Math.Max(0, (DateTimeOffset.UtcNow - observation.ObservedAtUtc).TotalSeconds):F1} 秒");
        ImGui.TextWrapped("图像稳定性：" + (snapshot.Stability == StabilityState.Stable ? "连续独立采样一致" : "未稳定/不可用") +
            "；牌局同步：" + (snapshot.Synchronization == SynchronizationState.Synchronized ? "已同步" : "未同步") + "。稳定不代表语义已验证。");
        ImGui.TextWrapped(snapshot.SynchronizationReason ?? "尚无完整事件历史。");

        ImGui.Separator();
        Show("下方可见牌面（非完整手牌）", snapshot.LowerVisibleFaces,
            xs => string.Join("  ", xs.Select(x => x.Tile.ChineseName + $"[{x.Slot.DisplayPosition}]")));
        if (snapshot.LowerVisibleFaces.IsConfirmed)
            ImGui.TextWrapped("方括号是本次画面的显示顺序。槽位仅属于当前状态版本；相同牌面不具备可持续追踪的实体 ID。结算画面也可能出现稳定牌面。");
        Show("完整实体手牌", snapshot.OwnHand, xs => string.Join("  ", xs.Select(x => x.Tile.ChineseName)));
        Show("摸牌身份", snapshot.DrawnTileSlot, x => $"本帧位置 {x.DisplayPosition}，版本 {x.StateRevision}");
        Show("合法动作", snapshot.LegalActions, x => x.ToString());
        Show("当前可见动作菜单", snapshot.VisibleActionMenu, menu => !menu.Visible ? "当前未显示菜单"
            : menu.Rows.IsEmpty ? "菜单可见，行尚未读出" : string.Join(" / ", menu.Rows.Select(row =>
                MenuActionName(row.Action) + (row.Enabled == true ? "" : row.Enabled == false ? "[不可用]" : "[状态未知]"))));
        if (snapshot.VisibleActionMenu.HasValue)
            ImGui.TextWrapped("菜单显示的是当前可见选项候选，尚不等于完整合法动作集或已完成的游戏操作；通用杠选项没有区分明杠、暗杠和加杠。");

        ImGui.Separator();
        ImGui.TextWrapped("四方表示屏幕方位；匿名编号与门风分别核对。下方编号 0 不表示东家。");
        Show("玩家编号约定", snapshot.PlayerIdBasis, basis => basis == PlayerIdentityBasis.RelativeToLocalPlayer
            ? "以本人方位为 0，右方 1、上方 2、左方 3；庄家由当前门风确定" : "未知");
        foreach (var player in snapshot.Players)
        {
            ImGui.TextUnformatted(Position(player.Position));
            Show("固定玩家", player.PlayerId, x => x.ToString());
            Show("门风", player.SeatWind, Wind);
            Show("分数", player.Score, x => x.ToString());
            Show("立直声明", player.RiichiDeclared, x => x ? "已声明" : "未声明");
            Show("立直成立", player.RiichiEstablished, x => x ? "已成立" : "未成立");
            Show("当前牌河图像", player.RiverImages, Inventory);
            Show("当前副露图像", player.MeldImages, Inventory);
            if (player.Position != ScreenPosition.Lower)
                Show("公开背牌外观", player.OpponentHandAppearance, appearance =>
                    $"背壳核对 {appearance.VerifiedBackCount}/{appearance.VisibleSlots} 个可见槽；" +
                    (appearance.CountComplete ? "当前区域枚举完整" : "存在未确认槽") + "；独立显示槽" +
                    (appearance.SeparateDrawSlotVisible == true ? "可见" : appearance.SeparateDrawSlotVisible == false ? "未见" : "未知"));
            Show("按事件还原的牌河", player.River, xs => xs.IsEmpty ? "空牌河" : string.Join(" ", xs.Select(x => x.Tile.ChineseName)));
            Show("已确认类型与来源的副露", player.Melds, xs => xs.IsEmpty ? "无副露" : string.Join(" / ", xs.Select(x => x.Kind + ":" + string.Join(" ", x.Tiles.Select(t => t.ChineseName)))));
        }
        ImGui.Separator();
        Show("局标识", snapshot.RoundId, x => x);
        Show("决策窗口", snapshot.DecisionWindowId, x => x);
        Show("庄家固定玩家", snapshot.DealerPlayerId, x => x.ToString());
        Show("场风", snapshot.RoundWind, Wind);
        Show("局数", snapshot.HandNumber, x => x.ToString());
        Show("本场", snapshot.Honba, x => x.ToString());
        Show("供托", snapshot.RiichiSticks, x => x.ToString());
        Show("剩余牌", snapshot.WallRemaining, x => x.ToString());
        Show("规则", snapshot.Rules.RuleSetId, x => x);
        Show("对局类型", snapshot.Rules.MatchType, x => x);
        Show("结束条件", snapshot.Rules.EndCondition, x => x);
        Show("宝牌显示模式", snapshot.DoraMode, x => x switch { DoraDisplayMode.ActualDora => "直接显示宝牌", DoraDisplayMode.Indicator => "显示指示牌", _ => "未知" });
        Show("已公开宝牌显示", snapshot.DoraDisplay, xs => string.Join(" ", xs.Select(x => x.ChineseName)));
        ImGui.TextWrapped("当前牌河与副露图像按屏幕位置呈现，不代表完整出牌顺序；未命名计数器不会自动当作本场、供托或剩余牌数。");
        if (snapshot.StatusCandidates.HasValue && snapshot.StatusCandidates.Value is { } status &&
            ImGui.CollapsingHeader("公开局况候选与读取位置"))
        {
            foreach (var value in status.Values)
            {
                string position = value.Position is { } place ? Position(place) + " · " : "";
                string display = value.Code == "PUBLIC_STATUS_VALUE_CANDIDATE"
                    ? value.Value is not null && value.Number is not null ? $"{value.Value} / {value.Number}"
                    : value.Value ?? value.Number?.ToString() ?? "未知" : "读取未通过";
                ImGui.TextWrapped($"{position}{StatusName(value.Field)}：{display}（候选） · {value.Path}");
            }
        }

        if (ImGui.CollapsingHeader("公开区域候选诊断（不是牌义或牌数）"))
        {
            if (monitor.Evidence is { } evidence)
            {
                ImGui.TextWrapped($"下方候选 {evidence.LowerCandidates}，本次接受 {evidence.AcceptedLowerFaces}；{evidence.Code}：{evidence.Reason}");
                foreach (var region in evidence.Regions)
                    ImGui.TextWrapped($"{Region(region.Area)}：元数据记录 {region.MetadataRecords}，检查拒绝 {region.RejectedRecords}。不能用节点或克隆编号推断牌数与顺序。");
            }
            ImGui.TextWrapped("上述统计是布局元数据。下方另列固定公开区域的当前正面图像候选；已公开宝牌单独显示，对手暗手保持未知。");
        }
        DrawReadiness(snapshot);
        DrawPublicTable(plugin.CurrentPublicTable);
    }

    private static void DrawPublicTable(PublicTableReading? reading)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("当前公开桌面（Candidate 候选）");
        ImGui.TextWrapped("下列牌按当前屏幕位置排列，不是出牌时间顺序。屏幕方位不是门风；被鸣走的牌和漏采事件不会自动补齐。稳定只表示连续图像一致。");
        if (reading is null)
        {
            ImGui.TextWrapped("尚无当前桌面观察。开启只读监视或事件记录后显示；停止或读错时不保留旧牌。");
            return;
        }
        ImGui.TextWrapped(reading.Reason);
        ImGui.TextWrapped("历史完整性：未知，存在缺口；不作为完整 AI 决策输入。");
        foreach (string direction in new[] { "bottom", "right", "top", "left" })
        {
            string area = "river-" + direction;
            var tiles = reading.Tiles.Where(x => x.Area == area).OrderBy(x => x.Y).ThenBy(x => x.X).ToArray();
            var status = reading.Areas.IsDefault ? null : reading.Areas.FirstOrDefault(x => x.Area == area);
            var rejected = reading.Rejections.Count(x => x.Area == area);
            ImGui.TextUnformatted(Region(area));
            if (tiles.Length > 0) ImGui.TextWrapped(string.Join("  ", tiles.Select(TileLabel)));
            else if (status is { ContainerVisible: true, ObservedEmptyCandidate: true })
                ImGui.TextWrapped(status.Stable ? "连续两次未见公开牌面（空候选，非历史为空）。" : "本次未见公开牌面，等待独立观察。");
            else ImGui.TextWrapped("未知：区域不可见、尚未枚举，或尚无可识别图像。");
            if (rejected > 0) ImGui.TextWrapped($"另有 {rejected} 个未知/拒绝槽，不补猜牌面。");
            if (status is { UnknownComponents: > 0 }) ImGui.TextWrapped($"有 {status.UnknownComponents} 个未支持的可见组件，本区域不完整。");
        }
        ImGui.TextUnformatted("公开副露候选（按屏幕组显示）");
        if (reading.MeldGroups.IsDefaultOrEmpty) ImGui.TextWrapped("尚无可核对的副露组；不能据此判断四家均无副露。");
        else foreach (var group in reading.MeldGroups.OrderBy(g => g.ScreenDirection, StringComparer.Ordinal)
            .ThenBy(g => reading.Tiles.Where(t => t.GroupPath == g.GroupPath).Select(t => t.Y).DefaultIfEmpty(float.MaxValue).Min())
            .ThenBy(g => reading.Tiles.Where(t => t.GroupPath == g.GroupPath).Select(t => t.X).DefaultIfEmpty(float.MaxValue).Min()))
        {
            string label = group.ScreenDirection switch { "bottom" => "下方", "right" => "右方", "top" => "上方", _ => "左方" };
            var faces = reading.Tiles.Where(x => x.GroupPath == group.GroupPath).OrderBy(x => x.Y).ThenBy(x => x.X);
            ImGui.TextWrapped($"{label}组 · 识别 {group.KnownFaces}/{group.VisibleSlots} 个可见槽 · " +
                (group.Stable ? "图像稳定" : "待稳定或存在未知槽") + "：" + string.Join("  ", faces.Select(TileLabel)));
            if (group.ShapeCode == "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN" && group.VerifiedBackSlots == 2)
                ImGui.TextWrapped("已核对两张正面与两个背壳，为暗杠外观候选；没有读取背面隐藏的牌面或赤牌身份。");
            else if (!group.AllVisibleSlotsDecoded) ImGui.TextWrapped("本组不完整；遮挡或第四槽未读出时，不沿用旧碰/杠判断。");
        }
        if (reading.Rejections.Length > 0 && ImGui.CollapsingHeader("公开桌面读取拒绝位置"))
            foreach (var rejected in reading.Rejections)
                ImGui.TextWrapped($"{rejected.SlotPath} · {rejected.Code}");
        ImGui.TextWrapped("副露牌面不自动证明吃、碰、杠种类或来源座位；未知背牌保持未知。");
    }

    private static string TileLabel(PublicTableTile tile) =>
        tile.ChineseName + (tile.Stable ? "[稳定]" : "[待稳定]") +
        (tile.RotationDegrees == 0 && !tile.Mirrored ? "" : $"({tile.RotationDegrees:0}°{(tile.Mirrored ? ",镜像" : "")})");

    private static string Inventory(PublicImageInventory inventory)
    {
        string tiles = inventory.ObservedEmpty ? "当前可见区域为空" : inventory.Tiles.IsEmpty ? "未识别到牌面"
            : string.Join("  ", inventory.Tiles.OrderBy(x => x.Y).ThenBy(x => x.X).Select(x => x.Tile.ChineseName +
                (x.Tsumogiri.IsConfirmed ? x.Tsumogiri.Value ? "[摸切]" : "[手切]" : "") +
                (x.WasClaimed is { IsConfirmed: true, Value: true } ? "[已被鸣]" : "")));
        string coverage = inventory.AllVisibleSlotsDecoded ? "可见槽已读全" : inventory.Groups.Any(g => g.ShapeCode == "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN")
            ? "含两正面两背壳的暗杠外观候选" : "仍有不可读或未确认的槽位";
        return $"{tiles}；{coverage}；" + (inventory.Stable ? "图像稳定" : "待稳定/局部读取");
    }

    private static string StatusName(string field) => field switch
    {
        "PlayerScore" => "点数", "SeatWind" => "门风", "RoundHeader" => "局况标题", "RoundResultLabel" => "结算提示",
        "TopRedStickCount" => "上方红点棒计数", "TopBlackStickCount" => "上方黑点棒计数",
        "CenterCounterLeft" => "中央左位数字", "CenterCounterRight" => "中央右位数字", "DoraDisplayLabel" => "宝牌显示标签",
        _ => "公开状态",
    };

    private static string MenuActionName(PublicMenuAction action) => action switch
    {
        PublicMenuAction.Pon => "碰", PublicMenuAction.Chi => "吃", PublicMenuAction.Kan => "杠",
        PublicMenuAction.Ron => "荣和", PublicMenuAction.Riichi => "立直", PublicMenuAction.Tsumo => "自摸",
        PublicMenuAction.Pass => "放弃", PublicMenuAction.Cancel => "取消", _ => "未识别选项",
    };

    private static void DrawReadiness(PublicSnapshot snapshot)
    {
        ImGui.Separator();
        var report = ReadinessEvaluator.Evaluate(snapshot, ReadinessProfiles.CompleteDecision);
        ImGui.TextWrapped("下方检查完整实机决策所需数据；离线 AI 回放成功不代表当前牌局已经具备完整决策输入。");
        ImGui.TextWrapped(report.IsReady ? "字段契约检查通过；仍需后端与规则验收。" : "完整决策输入未就绪；只读诊断仍可用。");
        if (ImGui.CollapsingHeader("缺失字段、冲突及兼容性原因"))
            foreach (var issue in report.Issues)
                ImGui.TextWrapped($"{issue.Path} · {issue.Code}：{issue.Reason}");
    }

    internal static string Describe<T>(Field<T> field, Func<T, string> format)
    {
        if (field.Availability == Availability.Conflict) return "冲突：" + (field.Reason ?? "观察结果不一致。");
        if (field.Availability != Availability.Known || !field.HasValue || field.Value is null)
            return "未知：" + (field.Reason ?? "尚未读取或验证。");
        string value = format(field.Value);
        string quality = field.IsConfirmed ? "已确认" : "候选，未验证";
        string source = field.SourceKind switch { SourceKind.Observed => "观察", SourceKind.Derived => "推导", _ => "旧逻辑假设" };
        return $"{value}（{quality}；{source}）";
    }

    private static void Show<T>(string label, Field<T> field, Func<T, string> format) =>
        ImGui.TextWrapped(label + "：" + Describe(field, format));
    private static string Wind(int value) => value switch { 0 => "东", 1 => "南", 2 => "西", 3 => "北", _ => "无效门风" };
    private static string Position(ScreenPosition value) => value switch
    { ScreenPosition.Lower => "下方", ScreenPosition.Right => "右方", ScreenPosition.Upper => "上方", _ => "左方" };
    private static string Region(string value) => value switch
    {
        "river-bottom" => "下方牌河候选区", "river-right" => "右方牌河候选区",
        "river-top" => "上方牌河候选区", "river-left" => "左方牌河候选区",
        "lower-row-geometry" => "下方牌面缩放与边界",
        "lower-meld-candidate" => "下方副露候选区", "dora-display" => "宝牌显示候选区", _ => "未知区域",
    };
}
