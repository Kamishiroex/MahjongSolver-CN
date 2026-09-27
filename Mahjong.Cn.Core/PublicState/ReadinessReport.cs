using System.Collections.Immutable;

namespace Mahjong.Cn.PublicState;

public sealed record ReadinessIssue(string Path, string Code, string Reason);
public sealed record ReadinessReport(string BackendId, ImmutableArray<ReadinessIssue> Issues)
{
    public bool IsReady => Issues.IsEmpty;
}

/// <summary>Requirements are a backend capability declaration, not an installed engine.</summary>
public sealed record ReadinessProfile(string BackendId, ImmutableArray<string> RequiredFields)
{
    public bool RequireStable { get; init; } = true;
    public bool RequireSynchronized { get; init; } = true;
    public bool RequireSession { get; init; } = true;
    public bool RequireSameObservation { get; init; } = true;
    public bool ValidateActionSlots { get; init; } = true;
    public bool RequireDrawnIdentity { get; init; }
    public ImmutableArray<string> SupportedRuleSetIds { get; init; } = [];
}

public static class ReadinessProfiles
{
    public static ReadinessProfile Diagnostics { get; } = new("public-monitor", [])
    {
        RequireStable = false, RequireSynchronized = false, RequireSession = false, RequireSameObservation = false,
        ValidateActionSlots = false,
    };

    // Deliberately not named after an AI: this is the future full-state input gate only.
    public static ReadinessProfile CompleteDecision { get; } = new("complete-public-decision", BuildCompleteFields())
    { RequireDrawnIdentity = true };

    private static ImmutableArray<string> BuildCompleteFields()
    {
        var fields = ImmutableArray.CreateBuilder<string>();
        fields.AddRange(new[] { "RoundId", "DecisionWindowId", "Rules.RuleSetId", "Rules.MatchType",
            "Rules.EndCondition", "Rules.MinimumHan", "Rules.RedFivesEnabled", "OurPlayerId", "DealerPlayerId",
            "RoundWind", "HandNumber", "Honba", "RiichiSticks", "WallRemaining", "TurnPlayerId",
            "OwnHand", "HasDrawnTile", "DoraMode", "DoraDisplay", "LegalActions", "DiscardableSlots" });
        foreach (var position in Enum.GetValues<ScreenPosition>())
            foreach (string name in new[] { "PlayerId", "SeatWind", "Score", "RiichiDeclared", "RiichiEstablished", "River", "Melds" })
                fields.Add($"Players.{position}.{name}");
        return fields.ToImmutable();
    }
}

public static class ReadinessEvaluator
{
    public static ReadinessReport Evaluate(PublicSnapshot snapshot, ReadinessProfile profile)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(profile);
        var issues = ImmutableArray.CreateBuilder<ReadinessIssue>();
        void Add(string path, string code, string reason) => issues.Add(new(path, code, reason));

        if (profile.RequireSession && snapshot.SessionId == Guid.Empty)
            Add("SessionId", "session-unknown", "缺少观察会话身份。");
        if (profile.RequireStable && snapshot.Stability != StabilityState.Stable)
            Add("Stability", "unstable", "观察未稳定；稳定本身不证明字段映射正确。");
        if (profile.RequireSynchronized && snapshot.Synchronization != SynchronizationState.Synchronized)
            Add("Synchronization", "history-gap", snapshot.SynchronizationReason ?? "未建立完整、连续且可信的当前牌局历史。");
        if (profile.RequiredFields.IsDefault)
        {
            Add("Profile.RequiredFields", "profile-invalid", "后端未声明所需字段。");
            return new(profile.BackendId, issues.ToImmutable());
        }
        if (profile.RequireSameObservation && !profile.RequiredFields.IsEmpty && snapshot.Observation is null)
            Add("Observation", "observation-boundary-missing", "缺少本次观察边界，无法确认字段来自同一次采样。");

        var fields = PublicSnapshotFields.Enumerate(snapshot).ToDictionary(x => x.Key, x => x.Value);
        void Check(string path)
        {
            if (!fields.TryGetValue(path, out var field) || field is null)
            { Add(path, "field-unavailable", "快照中不存在后端要求的字段。"); return; }
            if (field.Availability != Availability.Known)
            { Add(path, field.Availability == Availability.Conflict ? "conflict" : "unknown", field.Reason ?? "尚未读取或无法确认。"); return; }
            if (!field.HasValue || IsDefaultArray(field.UntypedValue))
                Add(path, "value-missing", "字段没有实际值；不能以类型默认值替代观察。");
            if (field.MappingStatus != MappingStatus.Validated)
                Add(path, "mapping-candidate", "字段语义仍是候选映射。");
            if (field.SourceKind is not (SourceKind.Observed or SourceKind.Derived))
                Add(path, "legacy-assumption", "旧快照或占位值不能作为可信引擎输入。");
            if (field.Observation is null)
                Add(path, "source-missing", "缺少采样来源引用。");
            else
            {
                if (string.IsNullOrWhiteSpace(field.Observation.Source) || string.IsNullOrWhiteSpace(field.Observation.Evidence))
                    Add(path, "evidence-missing", "缺少来源标识或适用证据。");
                if (field.SourceKind == SourceKind.Derived && field.Observation.DerivationInputs.IsDefaultOrEmpty)
                    Add(path, "derivation-inputs-missing", "推导字段缺少可审计的输入引用。");
                if (profile.RequireSameObservation && snapshot.Observation is { } boundary
                    && (field.Observation.Sequence != boundary.Sequence || field.Observation.ObservedAtUtc != boundary.ObservedAtUtc))
                    Add(path, "observation-mismatch", "字段来自另一观察边界，不能拼成同一可信快照。");
            }
        }
        foreach (string path in profile.RequiredFields.Distinct(StringComparer.Ordinal)) Check(path);
        if (profile.RequireDrawnIdentity && snapshot.HasDrawnTile is { IsConfirmed: true, Value: true })
            Check("DrawnTileSlot");

        if (profile.RequiredFields.Any(p => p.StartsWith("Players.", StringComparison.Ordinal)))
        {
            if (snapshot.Players.IsDefault || snapshot.Players.Length != 4 || snapshot.Players.Any(p => p is null)
                || snapshot.Players.Select(p => p.Position).Distinct().Count() != 4)
                Add("Players", "screen-positions", "四个固定屏幕方位缺失或重复。");
            else
            {
                var ids = snapshot.Players.Select(p => p.PlayerId).ToArray();
                if (ids.All(f => f is { IsConfirmed: true }) && (ids.Any(f => f.Value is < 0 or > 3) || ids.Select(f => f.Value).Distinct().Count() != 4))
                    Add("Players.PlayerId", "player-identity-conflict", "固定玩家身份必须互不重复，且不能由当前门风代替。");
                var winds = snapshot.Players.Select(p => p.SeatWind).ToArray();
                if (winds.All(f => f is { IsConfirmed: true }) && (winds.Any(f => f.Value is < 0 or > 3) || winds.Select(f => f.Value).Distinct().Count() != 4))
                    Add("Players.SeatWind", "seat-wind-conflict", "当前四家门风互相矛盾。");
            }
        }
        if (profile.RequiredFields.Contains("DoraMode") && snapshot.DoraMode is { IsConfirmed: true, Value: DoraDisplayMode.Unknown })
            Add("DoraMode", "unknown", "未知宝牌模式禁止转换为指示牌。");
        if (!profile.SupportedRuleSetIds.IsDefaultOrEmpty && snapshot.Rules?.RuleSetId is { IsConfirmed: true }
            && !profile.SupportedRuleSetIds.Contains(snapshot.Rules.RuleSetId.Value!, StringComparer.Ordinal))
            Add("Rules.RuleSetId", "rules-unsupported", "当前规则不在后端已声明的支持范围。");
        if (profile.ValidateActionSlots && profile.RequiredFields.Any(p => p is "OwnHand" or "DiscardableSlots" or "DrawnTileSlot"))
            ValidateSlots(snapshot, Add);
        return new(profile.BackendId, issues.ToImmutable());
    }

    private static bool IsDefaultArray(object? value) => value switch
    {
        ImmutableArray<PublicHandTile> a => a.IsDefault,
        ImmutableArray<VisibleTile> a => a.IsDefault,
        ImmutableArray<VisibleDiscard> a => a.IsDefault,
        ImmutableArray<VisibleMeld> a => a.IsDefault,
        ImmutableArray<UiTileSlot> a => a.IsDefault,
        _ => false,
    };

    private static void ValidateSlots(PublicSnapshot snapshot, Action<string, string, string> add)
    {
        if (snapshot.OwnHand is not { IsConfirmed: true } || snapshot.OwnHand.Value.IsDefault) return;
        var slots = new HashSet<UiTileSlot>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var positions = new HashSet<int>();
        foreach (var tile in snapshot.OwnHand.Value)
        {
            if (tile?.Slot is null) { add("OwnHand", "slot-missing", "手牌缺少槽位映射。"); continue; }
            if (tile.Slot.StateRevision != snapshot.StateRevision)
                add("OwnHand", "stale-slot", "手牌槽位属于旧状态版本，排序后必须重新定位。");
            if (string.IsNullOrWhiteSpace(tile.Slot.Path) || tile.Slot.DisplayPosition < 0 || !paths.Add(tile.Slot.Path) || !positions.Add(tile.Slot.DisplayPosition))
                add("OwnHand", "slot-conflict", "手牌槽位路径或显示位置缺失、重复。");
            if (tile.Tile.Id is < 0 or > 33 || (tile.Tile.Red && tile.Tile.Id is not (4 or 13 or 22)))
                add("OwnHand", "tile-invalid", "牌种或赤五身份无效。");
            slots.Add(tile.Slot);
        }
        if (snapshot.DrawnTileSlot is { IsConfirmed: true } && !slots.Contains(snapshot.DrawnTileSlot.Value!))
            add("DrawnTileSlot", "drawn-slot-missing", "摸牌槽位不属于当前版本的实体手牌。");
        if (snapshot.DiscardableSlots is { IsConfirmed: true } && !snapshot.DiscardableSlots.Value.IsDefault)
            foreach (var slot in snapshot.DiscardableSlots.Value)
                if (!slots.Contains(slot)) add("DiscardableSlots", "discard-slot-missing", "可弃牌槽位不属于当前版本的实体手牌。");
    }
}

public static class PublicSnapshotFields
{
    public static IEnumerable<KeyValuePair<string, IPublicField>> Enumerate(PublicSnapshot snapshot)
    {
        yield return new("RoundId", snapshot.RoundId);
        yield return new("DecisionWindowId", snapshot.DecisionWindowId);
        var rules = snapshot.Rules ?? new PublicRuleState();
        yield return new("Rules.RuleSetId", rules.RuleSetId);
        yield return new("Rules.MatchType", rules.MatchType);
        yield return new("Rules.EndCondition", rules.EndCondition);
        yield return new("Rules.MinimumHan", rules.MinimumHan);
        yield return new("Rules.RedFivesEnabled", rules.RedFivesEnabled);
        yield return new("OurPlayerId", snapshot.OurPlayerId);
        yield return new("PlayerIdBasis", snapshot.PlayerIdBasis);
        yield return new("DealerPlayerId", snapshot.DealerPlayerId);
        yield return new("RoundWind", snapshot.RoundWind);
        yield return new("HandNumber", snapshot.HandNumber);
        yield return new("Honba", snapshot.Honba);
        yield return new("RiichiSticks", snapshot.RiichiSticks);
        yield return new("WallRemaining", snapshot.WallRemaining);
        yield return new("TurnPlayerId", snapshot.TurnPlayerId);
        yield return new("OurDoubleRiichi", snapshot.OurDoubleRiichi);
        yield return new("OurTemporaryFuriten", snapshot.OurTemporaryFuriten);
        yield return new("OurRiichiFuriten", snapshot.OurRiichiFuriten);
        yield return new("LowerVisibleFaces", snapshot.LowerVisibleFaces);
        yield return new("OwnHand", snapshot.OwnHand);
        yield return new("HasDrawnTile", snapshot.HasDrawnTile);
        yield return new("DrawnTileSlot", snapshot.DrawnTileSlot);
        yield return new("DoraMode", snapshot.DoraMode);
        yield return new("DoraDisplay", snapshot.DoraDisplay);
        yield return new("LegalActions", snapshot.LegalActions);
        yield return new("VisibleActionMenu", snapshot.VisibleActionMenu);
        yield return new("DiscardableSlots", snapshot.DiscardableSlots);
        yield return new("StatusCandidates", snapshot.StatusCandidates);
        if (snapshot.Players.IsDefault) yield break;
        // Duplicate positions are reported separately; never let malformed input crash dictionary construction.
        foreach (var player in snapshot.Players.Where(p => p is not null).DistinctBy(p => p.Position))
        {
            string prefix = $"Players.{player.Position}.";
            yield return new(prefix + "PlayerId", player.PlayerId);
            yield return new(prefix + "SeatWind", player.SeatWind);
            yield return new(prefix + "Score", player.Score);
            yield return new(prefix + "RiichiDeclared", player.RiichiDeclared);
            yield return new(prefix + "RiichiEstablished", player.RiichiEstablished);
            yield return new(prefix + "Ippatsu", player.Ippatsu);
            yield return new(prefix + "DoubleRiichi", player.DoubleRiichi);
            yield return new(prefix + "RiichiDiscardIndex", player.RiichiDiscardIndex);
            yield return new(prefix + "River", player.River);
            yield return new(prefix + "Melds", player.Melds);
            yield return new(prefix + "RiverImages", player.RiverImages);
            yield return new(prefix + "MeldImages", player.MeldImages);
            yield return new(prefix + "OpponentHandAppearance", player.OpponentHandAppearance);
        }
    }
}
