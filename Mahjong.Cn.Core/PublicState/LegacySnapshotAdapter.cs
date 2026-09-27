using System.Collections.Immutable;

namespace Mahjong.Cn.PublicState;

/// <summary>
/// Lossless conservative migration of values, not a trust upgrade. Old wind-ordered seats cannot
/// populate screen positions or match-long IDs without independently verified mapping evidence.
/// </summary>
public static class LegacySnapshotAdapter
{
    public static PublicSnapshot Adapt(VisibleSnapshot legacy, Guid sessionId, long stateRevision,
        ObservationReference observation)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(observation);
        Field<T> Legacy<T>(T value) => Field<T>.Known(value, observation,
            SourceKind.LegacyAssumption, MappingStatus.Candidate) with
            { Reason = "保留旧快照值；旧字段缺少独立语义验证，不能作为可信后端输入。" };
        Field<T> Nullable<T>(T? value, string name) where T : struct => value.HasValue
            ? Legacy(value.Value) : Field<T>.Unknown($"旧快照没有 {name}。");
        Field<ImmutableArray<T>> Array<T>(ImmutableArray<T> value, string name) => value.IsDefault
            ? Field<ImmutableArray<T>>.Unknown($"旧快照没有 {name}；未知不等于空集合。") : Legacy(value);
        UiTileSlot Slot(int slot) => new($"legacy/hand/{slot}", slot, stateRevision);
        var hand = legacy.Hand.IsDefault ? default : legacy.Hand.Select(t => new PublicHandTile(t.Tile, Slot(t.Slot))).ToImmutableArray();
        var discardable = legacy.DiscardableSlots.IsDefault ? default : legacy.DiscardableSlots.Select(Slot).ToImmutableArray();
        return new()
        {
            SessionId = sessionId, StateRevision = stateRevision, Observation = observation,
            Synchronization = SynchronizationState.HistoryGap,
            SynchronizationReason = "旧快照没有完整事件历史、固定玩家映射和可信开局边界。",
            RoundId = legacy.RoundId is null ? Field<string>.Unknown("旧快照未保存牌局 ID。") : Legacy(legacy.RoundId),
            RoundWind = Nullable(legacy.RoundWind, nameof(legacy.RoundWind)),
            HandNumber = Nullable(legacy.HandNumber, nameof(legacy.HandNumber)),
            Honba = Nullable(legacy.Honba, nameof(legacy.Honba)),
            RiichiSticks = Nullable(legacy.RiichiSticks, nameof(legacy.RiichiSticks)),
            WallRemaining = Nullable(legacy.WallRemaining, nameof(legacy.WallRemaining)),
            OurDoubleRiichi = Nullable(legacy.OurDoubleRiichi, nameof(legacy.OurDoubleRiichi)),
            OwnHand = Array(hand, nameof(legacy.Hand)),
            HasDrawnTile = legacy.DrawnTileKnown == true && legacy.DrawnSlot.HasValue
                ? Legacy(true) : Field<bool>.Unknown("旧摸牌身份标记不能证明没有摸牌。"),
            DrawnTileSlot = legacy.DrawnSlot.HasValue ? Legacy(Slot(legacy.DrawnSlot.Value))
                : Field<UiTileSlot>.Unknown("旧快照缺少摸牌槽位。"),
            DoraMode = legacy.DoraIndicators.IsDefault ? Field<DoraDisplayMode>.Unknown("旧快照没有宝牌显示信息。")
                : Legacy(DoraDisplayMode.Indicator),
            DoraDisplay = Array(legacy.DoraIndicators, nameof(legacy.DoraIndicators)),
            LegalActions = Nullable(legacy.LegalFlags, nameof(legacy.LegalFlags)),
            DiscardableSlots = Array(discardable, nameof(legacy.DiscardableSlots)),
            LegacySeats = legacy.Seats.IsDefault ? [] : legacy.Seats.Select((seat, index) => new LegacySeatObservation(index, seat, observation)).ToImmutableArray(),
            LegacySeatsWerePresent = !legacy.Seats.IsDefault,
            LegacyOurSeat = Nullable(legacy.OurSeat, nameof(legacy.OurSeat)),
            LegacyTurnIndex = Nullable(legacy.TurnIndex, nameof(legacy.TurnIndex)),
            LegacyDrawnTileKnown = Nullable(legacy.DrawnTileKnown, nameof(legacy.DrawnTileKnown)),
        };
    }
}
