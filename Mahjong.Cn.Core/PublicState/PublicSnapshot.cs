using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Mahjong.Core;

namespace Mahjong.Cn.PublicState;

// Zero values deliberately never mean a confirmed observation.
public enum Availability { Unknown, Known, Conflict }
public enum SourceKind { LegacyAssumption, Observed, Derived }
public enum MappingStatus { Candidate, Validated }
public enum ScreenPosition { Lower, Right, Upper, Left }
public enum StabilityState { Unknown, Transition, Stable }
public enum SynchronizationState { Unknown, HistoryGap, Synchronized }
public enum DoraDisplayMode { Unknown, ActualDora, Indicator }
public enum PlayerIdentityBasis { Unknown, RelativeToLocalPlayer }

/// <summary>References a public UI observation, never a process address or a player name.</summary>
public sealed record ObservationReference(long Sequence, DateTimeOffset ObservedAtUtc, string Source,
    string? Evidence = null, ImmutableArray<string> DerivationInputs = default)
{
    public ImmutableArray<string> DerivationInputs { get; init; } = DerivationInputs.IsDefault ? [] : DerivationInputs;
}

public interface IPublicField
{
    Availability Availability { get; }
    SourceKind SourceKind { get; }
    MappingStatus MappingStatus { get; }
    ObservationReference? Observation { get; }
    string? Reason { get; }
    bool HasValue { get; }
    object? UntypedValue { get; }
}

/// <summary>Existence, semantic validation and animation stability are independent concerns.</summary>
[JsonConverter(typeof(PublicFieldJsonConverter))]
public sealed record Field<T> : IPublicField
{
    private T? value;
    private bool hasValue;
    public Availability Availability { get; init; }
    public SourceKind SourceKind { get; init; }
    public MappingStatus MappingStatus { get; init; }
    public T? Value { get => value; init { this.value = value; hasValue = true; } }
    [JsonIgnore] public bool HasValue => hasValue && value is not null && (value switch
    {
        ImmutableArray<PublicHandTile> a => !a.IsDefault,
        ImmutableArray<VisibleTile> a => !a.IsDefault,
        ImmutableArray<VisibleDiscard> a => !a.IsDefault,
        ImmutableArray<VisibleMeld> a => !a.IsDefault,
        ImmutableArray<UiTileSlot> a => !a.IsDefault,
        _ => true,
    });
    public ObservationReference? Observation { get; init; }
    public string? Reason { get; init; }
    [JsonIgnore] public object? UntypedValue => Value;
    [JsonIgnore] public bool IsConfirmed => Availability == Availability.Known && HasValue
        && Observation is not null && !string.IsNullOrWhiteSpace(Observation.Source)
        && !string.IsNullOrWhiteSpace(Observation.Evidence)
        && MappingStatus == MappingStatus.Validated && SourceKind is SourceKind.Observed or SourceKind.Derived;

    public static Field<T> Unknown(string reason) => new() { Reason = reason };
    public static Field<T> Known(T value, ObservationReference observation,
        SourceKind sourceKind = SourceKind.Observed, MappingStatus mappingStatus = MappingStatus.Validated) =>
        new() { Availability = Availability.Known, SourceKind = sourceKind, MappingStatus = mappingStatus,
            Value = value, Observation = observation };
    public static Field<T> Conflict(T value, ObservationReference observation, string reason) =>
        new() { Availability = Availability.Conflict, Value = value, Observation = observation, Reason = reason };
}

/// <summary>A slot belongs to one state revision. It is not a persistent physical tile identity.</summary>
public sealed record UiTileSlot(string Path, int DisplayPosition, long StateRevision);
public sealed record PublicHandTile(VisibleTile Tile, UiTileSlot Slot);

/// <summary>Screen position, match-long player ID and current seat wind are separate mappings.</summary>
public sealed record PlayerPublicState(ScreenPosition Position)
{
    public Field<int> PlayerId { get; init; } = Field<int>.Unknown("固定玩家身份映射未验证。");
    public Field<int> SeatWind { get; init; } = Field<int>.Unknown("门风未读取。");
    public Field<int> Score { get; init; } = Field<int>.Unknown("点数未读取。");
    public Field<bool> RiichiDeclared { get; init; } = Field<bool>.Unknown("立直声明未读取。");
    public Field<bool> RiichiEstablished { get; init; } = Field<bool>.Unknown("立直成立未读取。");
    public Field<bool> Ippatsu { get; init; } = Field<bool>.Unknown("一发状态未读取。");
    public Field<bool> DoubleRiichi { get; init; } = Field<bool>.Unknown("首张弃牌立直及此前鸣牌时序尚未确认。");
    public Field<int> RiichiDiscardIndex { get; init; } = Field<int>.Unknown("立直声明牌位置未确认。");
    public Field<ImmutableArray<VisibleDiscard>> River { get; init; } = Field<ImmutableArray<VisibleDiscard>>.Unknown("牌河及顺序未验证。");
    public Field<ImmutableArray<VisibleMeld>> Melds { get; init; } = Field<ImmutableArray<VisibleMeld>>.Unknown("公开副露未读取；未知不等于无副露。");
    public Field<PublicImageInventory> RiverImages { get; init; } = Field<PublicImageInventory>.Unknown("当前牌河区域图像尚未读取；图像不等于出牌历史。");
    public Field<PublicImageInventory> MeldImages { get; init; } = Field<PublicImageInventory>.Unknown("当前副露区域图像尚未读取；图像不确认鸣牌类型和来源。");
    public Field<PublicConcealedAppearance> OpponentHandAppearance { get; init; } = Field<PublicConcealedAppearance>.Unknown("对手当前公开背牌外观未读取；不读取暗手牌面。");
}

public sealed record PublicRuleState
{
    public Field<string> RuleSetId { get; init; } = Field<string>.Unknown("规则配置身份未验证。");
    public Field<string> MatchType { get; init; } = Field<string>.Unknown("对局类型未读取。");
    public Field<string> EndCondition { get; init; } = Field<string>.Unknown("结束条件未验证。");
    public Field<int> MinimumHan { get; init; } = Field<int>.Unknown("最低役番规则未确认。");
    public Field<bool> RedFivesEnabled { get; init; } = Field<bool>.Unknown("赤牌规则未确认。");
    public Field<bool> OpenTanyao { get; init; } = Field<bool>.Unknown("食断规则未读取。");
}

/// <summary>Immutable public observations only. The session ID identifies observation, not a game round.</summary>
public sealed record PublicSnapshot
{
    public int SchemaVersion { get; init; } = 1;
    public Guid SessionId { get; init; }
    public long StateRevision { get; init; }
    public ObservationReference? Observation { get; init; }
    public StabilityState Stability { get; init; }
    public SynchronizationState Synchronization { get; init; }
    public string? SynchronizationReason { get; init; }
    public Field<string> RoundId { get; init; } = Field<string>.Unknown("尚无可信开局边界，不能生成牌局 ID。");
    public Field<string> DecisionWindowId { get; init; } = Field<string>.Unknown("决策窗口未建立。");
    public PublicRuleState Rules { get; init; } = new();
    public ImmutableArray<PlayerPublicState> Players { get; init; } =
        [new(ScreenPosition.Lower), new(ScreenPosition.Right), new(ScreenPosition.Upper), new(ScreenPosition.Left)];
    public Field<int> OurPlayerId { get; init; } = Field<int>.Unknown("本人固定玩家身份未映射。");
    public Field<PlayerIdentityBasis> PlayerIdBasis { get; init; } = Field<PlayerIdentityBasis>.Unknown("固定玩家编号的方位约定未验证。");
    public Field<int> DealerPlayerId { get; init; } = Field<int>.Unknown("庄家身份未知，不能套用庄家估值。");
    public Field<int> RoundWind { get; init; } = Field<int>.Unknown("场风未读取。");
    public Field<int> HandNumber { get; init; } = Field<int>.Unknown("局数未读取。");
    public Field<int> Honba { get; init; } = Field<int>.Unknown("本场未读取。");
    public Field<int> RiichiSticks { get; init; } = Field<int>.Unknown("供托未读取。");
    public Field<int> WallRemaining { get; init; } = Field<int>.Unknown("剩余牌数未读取，不能以弃牌数代替精确牌山事件。");
    public Field<int> TurnPlayerId { get; init; } = Field<int>.Unknown("当前行动玩家未映射。");
    public Field<bool> OurDoubleRiichi { get; init; } = Field<bool>.Unknown("双立直状态未读取。");
    public Field<bool> OurTemporaryFuriten { get; init; } = Field<bool>.Unknown("临时振听尚未确认。");
    public Field<bool> OurRiichiFuriten { get; init; } = Field<bool>.Unknown("立直后过和振听尚未确认。");
    public Field<ImmutableArray<PublicHandTile>> LowerVisibleFaces { get; init; } = Field<ImmutableArray<PublicHandTile>>.Unknown("下方可见牌面尚未稳定识别。");
    public Field<ImmutableArray<PublicHandTile>> OwnHand { get; init; } = Field<ImmutableArray<PublicHandTile>>.Unknown("完整实体手牌尚未确认；下方可见牌面不能代替。");
    public Field<bool> HasDrawnTile { get; init; } = Field<bool>.Unknown("本观察是否有独立摸牌未确认。");
    public Field<string> OwnDrawKind { get; init; } = Field<string>.Unknown("当前摸牌来源未确认。");
    public Field<UiTileSlot> DrawnTileSlot { get; init; } = Field<UiTileSlot>.Unknown("摸牌身份未确认，不能假设最右牌就是摸牌。");
    public Field<DoraDisplayMode> DoraMode { get; init; } = Field<DoraDisplayMode>.Unknown("直接宝牌与指示牌显示模式未读取。");
    public Field<ImmutableArray<VisibleTile>> DoraDisplay { get; init; } = Field<ImmutableArray<VisibleTile>>.Unknown("已公开宝牌显示未读取。");
    public Field<ActionFlags> LegalActions { get; init; } = Field<ActionFlags>.Unknown("合法动作菜单未验证。");
    public Field<PublicActionMenuObservation> VisibleActionMenu { get; init; } = Field<PublicActionMenuObservation>.Unknown("当前可见动作菜单尚未读取；菜单不等于完整合法动作集。");
    public Field<ImmutableArray<UiTileSlot>> DiscardableSlots { get; init; } = Field<ImmutableArray<UiTileSlot>>.Unknown("当前可弃牌槽位未验证。");
    public Field<PublicStatusObservation> StatusCandidates { get; init; } = Field<PublicStatusObservation>.Unknown("公开状态文本尚未读取。");
    // Legacy seats were ordered by wind, not screen position or stable identity. Preserve them
    // without inventing either mapping, and never include them in readiness field resolution.
    public ImmutableArray<LegacySeatObservation> LegacySeats { get; init; } = [];
    public bool LegacySeatsWerePresent { get; init; }
    public Field<int> LegacyOurSeat { get; init; } = Field<int>.Unknown("没有旧座位信息。");
    public Field<int> LegacyTurnIndex { get; init; } = Field<int>.Unknown("没有旧轮转信息。");
    public Field<bool> LegacyDrawnTileKnown { get; init; } = Field<bool>.Unknown("没有旧摸牌标记。");
}

[JsonConverter(typeof(LegacySeatObservationJsonConverter))]
public sealed record LegacySeatObservation(int WindOrderIndex, VisibleSeat Seat, ObservationReference Observation)
{
    public SourceKind SourceKind => SourceKind.LegacyAssumption;
    public MappingStatus MappingStatus => MappingStatus.Candidate;
}
