using Mahjong.Cn.Engines;
using Mahjong.Cn.PublicState;
using Mahjong.Cn;
using Mahjong.Core;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.Experimental;

/// <summary>Bind a response popup to its observed public action, for the lifetime of that popup.</summary>
internal sealed class AkochanResponseWindow
{
    private string? handKey;
    private (AkochanGlobalEvent Action, string Slot)? bound;
    private long retiredSequence = -1;
    private readonly HashSet<string> retiredSources = new(StringComparer.Ordinal);
    private (int Actor, PublicImageTile Tile)? inferred;
    private string? meldKey;
    private long openedSequence;
    internal AkochanGlobalTrigger? Trigger { get; private set; }
    internal string? Assumption { get; private set; }
    internal string Diagnostic { get; private set; } = "等待当前响应菜单。";

    internal void Clear()
    {
        handKey = meldKey = null; bound = null; inferred = null; retiredSequence = -1; Trigger = null;
        retiredSources.Clear(); Assumption = null;
    }

    internal void Observe(PublicSnapshot snapshot, AddonProbe addon,
        IEnumerable<(AkochanGlobalEvent Action, string Slot)> events, bool unresolvedNewRiverSlots = false)
    {
        Trigger = null;
        Assumption = null;
        Diagnostic = "等待已解码且允许交互的响应菜单和当前手牌。";
        var menu = addon.PublicActionMenu;
        if (menu is { Visible: false }) { Close(); return; }
        // An unreadable frame suspends output; it is not evidence that a popup closed.
        if (menu is not { Visible: true, AllVisibleRowsDecoded: true } ||
            !snapshot.LowerVisibleFaces.IsConfirmed) return;
        var enabled = menu.Rows.Where(r => r.Enabled == true && r.Code == "ACTION_MENU_LABEL_CANDIDATE")
            .Select(r => r.Action).ToHashSet(StringComparer.Ordinal);
        if (!menu.Rows.Any(r => r.Action is "Pon" or "Chi" or "Kan" or "Ron") ||
            snapshot.LowerVisibleFaces.Value.Length % 3 != 1)
        { Close(); return; }
        // Disabled controls during menu animation suspend output, not the identity of
        // the still visible response window. Sorting does not change owned tiles.
        if (!enabled.Contains("Pass") || !enabled.Overlaps(["Pon", "Chi", "Kan", "Ron"])) return;
        string nextHand = string.Join(';', snapshot.LowerVisibleFaces.Value
            .Select(t => $"{t.Tile.Id}:{t.Tile.Red}").Order(StringComparer.Ordinal));
        if (handKey is not null && handKey != nextHand) Close();
        if (handKey is null) openedSequence = snapshot.Observation!.Sequence;
        handKey = nextHand;
        if (snapshot.Players.All(p => p.MeldImages is { Availability: Availability.Known,
            Value: { RegionReadable: true, AllVisibleSlotsDecoded: true } m } && m.Tiles.All(t => t.Stable)))
        {
            string nextMelds = string.Join(';', snapshot.Players.SelectMany(p => p.MeldImages.Value!.Tiles
                .Select(t => $"{p.Position}:{t.GroupPath}:{t.SlotPath}:{t.Tile.Id}:{t.Tile.Red}")).Order(StringComparer.Ordinal));
            if (meldKey is not null && meldKey != nextMelds) Retire();
            meldKey = nextMelds;
        }
        if (unresolvedNewRiverSlots)
        { Diagnostic = "新出现的牌河槽位尚未解码，等待确认当前弃牌。"; return; }
        var latest = events.Where(e => e.Action.Type is "dahai" or "chi" or "pon" or "daiminkan" or "ankan" or "kakan")
            .OrderByDescending(e => e.Action.Sequence).ToArray();
        bool simultaneous = latest.Length > 1 && latest[1].Action.Sequence == latest[0].Action.Sequence;
        if (simultaneous && (latest[0].Action.Sequence >= openedSequence - 3 || bound is not null))
        { Retire(); Diagnostic = "同次采样出现多个动作，无法确认响应先后。"; return; }
        // Old ambiguous actions before this popup do not establish its trigger, nor
        // forbid independently inferring its uniquely matching CURRENT visible tail.
        // Ambiguity belonging to this popup remains blocked even after many samples.
        if (latest.Length > 0 && !simultaneous)
        {
            var candidate = latest[0];
            if (bound is { } previous && previous != candidate) Retire();
            if (bound is not null && !SourceStillVisible(snapshot, candidate))
            {
                if (SourceContradicted(snapshot,candidate)) Retire();
                Diagnostic = "响应来源牌暂未确认或已改变，等待当前牌面。"; return;
            }
            long age = snapshot.Observation!.Sequence - candidate.Action.Sequence;
            if (age < 0) { Retire(); Diagnostic = "公开动作晚于当前快照，等待一致采样。"; return; }
            bool isWindowEvent = age <= 20 && candidate.Action.Sequence >= openedSequence - 3;
            if (candidate.Action.Actor is >= 1 and <= 3 && candidate.Action.Tile is { } tile &&
                candidate.Action.Type is "dahai" or "kakan" && SourceStillVisible(snapshot, candidate) &&
                (candidate.Action.Type == "kakan" || Compatible(snapshot, enabled, candidate.Action.Actor, tile)))
            {
                if (bound is not null || isWindowEvent &&
                    candidate.Action.Sequence > retiredSequence && !retiredSources.Contains(SourceKey(candidate)))
                {
                    bound = candidate; inferred = null;
                    Trigger = new(candidate.Action.Type, candidate.Action.Actor, tile);
                    Diagnostic = "当前窗口已绑定实际观察到的公开动作。"; return;
                }
            }
            // A later actual action invalidates an old response, even when it is ours.
            if (bound is not null || isWindowEvent)
            { Retire(); Diagnostic = "当前窗口与最新公开动作不一致。"; return; }
        }
        // A mid-hand enable/read recovery may have no event prefix. The visible menu
        // constrains the current source (Chi only from the left; Pon/Kan owned counts).
        // Infer only when ALL currently visible opponent tails give one possibility.
        // This never creates a dahai event or claims that the history was recovered.
        if (!enabled.Overlaps(["Chi", "Pon", "Kan"]))
        { Diagnostic = "缺少和牌触发事件；仅凭荣和菜单无法确认来源牌。"; return; }
        if (!TryCurrentTails(snapshot, out var tails))
        { Diagnostic = "牌河存在未稳定、未解码或顺序不明的槽位；等待完整的当前河尾。"; return; }
        var matches = tails.Where(t => Compatible(snapshot, enabled, t.Actor, t.Tile.Tile)).ToArray();
        if (matches.Length != 1)
        { Diagnostic = $"当前菜单与牌河有{matches.Length}个可能来源，不能唯一确认响应牌。"; return; }
        var match = matches[0];
        string source = SourceKey(match.Actor, match.Tile.SlotPath, match.Tile.Tile);
        if (retiredSources.Contains(source))
        { Diagnostic = "该响应窗口已经关闭或手牌已改变，等待新的公开动作。"; return; }
        if (inferred is { } old && SourceKey(old.Actor, old.Tile.SlotPath, old.Tile.Tile) != source) Retire();
        inferred = match;
        Trigger = new("dahai", match.Actor, match.Tile.Tile);
        Assumption = "本次响应来源由当前菜单、手牌及三家可见河尾唯一匹配推导；未捕获该弃牌事件，不补造事件历史。";
        Diagnostic = Assumption;
    }

    private static bool Compatible(PublicSnapshot snapshot, HashSet<string?> enabled, int actor, VisibleTile tile)
    {
        var derived = Mahjong.Engine.CallCandidateDeriver.Derive(snapshot.LowerVisibleFaces.Value
            .Select(t => Tile.FromId(t.Tile.Id)).ToArray(), Tile.FromId(tile.Id), actor);
        return (!enabled.Contains("Pon") || derived.Pon.Count > 0) &&
            (!enabled.Contains("Chi") || derived.Chi.Count > 0) &&
            (!enabled.Contains("Kan") || derived.Kan.Count > 0);
    }

    private static bool TryCurrentTails(PublicSnapshot snapshot, out List<(int Actor, PublicImageTile Tile)> tails)
    {
        tails = [];
        foreach (int actor in new[] { 1, 2, 3 })
        {
            var river = snapshot.Players.SingleOrDefault(p => (int)p.Position == actor)?.RiverImages;
            if (river is not { Availability: Availability.Known, Value: { RegionReadable: true, AllVisibleSlotsDecoded: true } faces } ||
                faces.Tiles.Length != faces.VisibleSlots || faces.Tiles.Any(t => !t.Stable || t.DisplayPosition is null ||
                    t.WasClaimed.Availability == Availability.Conflict) ||
                faces.Tiles.Select(t => t.DisplayPosition!.ReadOrder).Distinct().Count() != faces.Tiles.Length) return false;
            if (faces.Tiles.Length == 0) continue;
            var last = faces.Tiles.OrderBy(t => t.DisplayPosition!.ReadOrder).Last();
            if (!(last.WasClaimed.IsConfirmed && last.WasClaimed.Value)) tails.Add((actor, last));
        }
        return true;
    }

    private static string SourceKey((AkochanGlobalEvent Action, string Slot) source) =>
        SourceKey(source.Action.Actor, source.Slot, source.Action.Tile!.Value);
    private static string SourceKey(int actor, string slot, VisibleTile tile) => $"{actor}:{slot}:{tile.Id}:{tile.Red}";

    private static bool SourceContradicted(PublicSnapshot snapshot, (AkochanGlobalEvent Action, string Slot) source)
    {
        if (source.Action.Type != "dahai") return false;
        var field = snapshot.Players.SingleOrDefault(p => (int)p.Position == source.Action.Actor)?.RiverImages;
        if (field?.Availability == Availability.Conflict) return true;
        if (field?.Value is not { AllVisibleSlotsDecoded: true } faces ||
            faces.Tiles.Any(t => !t.Stable || t.DisplayPosition is null) ||
            faces.Tiles.Select(t => t.DisplayPosition!.ReadOrder).Distinct().Count() != faces.Tiles.Length) return false;
        var tail = faces.Tiles.OrderBy(t => t.DisplayPosition!.ReadOrder).LastOrDefault();
        return tail is null || tail.SlotPath != source.Slot || tail.Tile != source.Action.Tile ||
            tail.WasClaimed.Availability == Availability.Conflict || tail.WasClaimed.IsConfirmed && tail.WasClaimed.Value;
    }

    private static bool SourceStillVisible(PublicSnapshot snapshot, (AkochanGlobalEvent Action, string Slot) source)
    {
        var player = snapshot.Players.SingleOrDefault(p => (int)p.Position == source.Action.Actor);
        if (source.Action.Type == "kakan")
        {
            var melds = player?.MeldImages;
            return melds is { Availability: Availability.Known, Value: { AllVisibleSlotsDecoded: true } inventory } &&
                inventory.Groups.Any(g => g.GroupPath == source.Slot && g.Stable && g.ShapeCode == "PUBLIC_KAN_FOUR_FACE_PATTERN") &&
                inventory.Tiles.Any(t => t.GroupPath == source.Slot && t.Stable && t.Tile == source.Action.Tile);
        }
        var river = player?.RiverImages;
        if (river is not { Availability: Availability.Known, Value: { AllVisibleSlotsDecoded: true } faces } ||
            faces.Tiles.IsDefaultOrEmpty) return false;
        if (faces.Tiles.Any(t => t.DisplayPosition is null) ||
            faces.Tiles.Select(t => t.DisplayPosition!.ReadOrder).Distinct().Count() != faces.Tiles.Length) return false;
        var last = faces.Tiles.OrderBy(t => t.DisplayPosition!.ReadOrder).Last();
        return last.Stable && last.SlotPath == source.Slot && last.Tile == source.Action.Tile &&
            last.WasClaimed.Availability != Availability.Conflict &&
            !(last.WasClaimed.IsConfirmed && last.WasClaimed.Value);
    }

    private void Retire()
    {
        if (bound is { } previous)
        {
            retiredSequence = Math.Max(retiredSequence, previous.Action.Sequence);
            retiredSources.Add(SourceKey(previous));
        }
        if (inferred is { } source) retiredSources.Add(SourceKey(source.Actor, source.Tile.SlotPath, source.Tile.Tile));
        bound = null; inferred = null;
    }
    private void Close() { Retire(); handKey = null; }
}
