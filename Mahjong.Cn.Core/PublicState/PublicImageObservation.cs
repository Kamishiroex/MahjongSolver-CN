using System.Collections.Immutable;

namespace Mahjong.Cn.PublicState;

/// <summary>One current display image, not a discard event or persistent physical tile.</summary>
public sealed record PublicImageTile(VisibleTile Tile, string SlotPath, string GroupPath,
    float X, float Y, float Width, float Height, float RotationDegrees, bool Mirrored, bool Stable)
{
    public PublicImagePosition? DisplayPosition { get; init; }
    public PublicImageStyle? Style { get; init; }
    /// <summary>Current display meaning only; does not establish when this discard occurred.</summary>
    public Field<bool> Tsumogiri { get; init; } = Field<bool>.Unknown("摸切／手切显示语义尚未核对。");
    public Field<bool> WasClaimed { get; init; } = Field<bool>.Unknown("牌河被鸣标记尚未核对。");
}

public sealed record PublicImagePosition(int DisplayRow, int VisibleColumn, int ReadOrder, bool IsSideways)
{
    public bool IsEventOrder => false;
}
public sealed record PublicImageStyle(short AddRed, short AddGreen, short AddBlue,
    byte MultiplyRed, byte MultiplyGreen, byte MultiplyBlue, string Name)
{
    public bool ResponseHighlight { get; init; }
    public bool CalledTileMeaningVerified => false;
    public bool TsumogiriMeaningVerified => false;
}
public sealed record PublicMeldImageGroup(string GroupPath, int VisibleSlots, int KnownFaces, int VerifiedBackSlots,
    bool Stable, string ShapeCode, int? InferredClosedKanKind34);

/// <summary>Current visible inventory. Completeness is about this image region, never event history.</summary>
public sealed record PublicImageInventory(ImmutableArray<PublicImageTile> Tiles, bool Stable,
    bool RegionReadable, bool AllVisibleSlotsDecoded, bool ObservedEmpty,
    int VisibleSlots, int UnknownComponents, ImmutableArray<string> RejectionCodes)
{
    public ImmutableArray<PublicMeldImageGroup> Groups { get; init; } = [];
    public bool HasChronologicalOrder => false;
    public bool HistoryComplete => false;
}

/// <summary>Only a finite enum or bounded parsed number from an allowlisted public node.</summary>
public sealed record PublicStatusValue(string Path, string Field, ScreenPosition? Position,
    int? Number, string? Value, string Code);

public sealed record PublicStatusObservation(ImmutableArray<PublicStatusValue> Values);

/// <summary>Only public backs, never concealed tile faces or identities.</summary>
public sealed record PublicConcealedDisplaySlot(string SlotPath, long StateRevision, bool SeparateDrawSlot,
    bool BackVerified, string Code);
public sealed record PublicConcealedAppearance(int VisibleSlots, int VerifiedBackCount, bool CountComplete,
    bool? SeparateDrawSlotVisible, ImmutableArray<PublicConcealedDisplaySlot> Slots, string Code)
{
    public bool IsDrawEvent => false;
    public bool HistoryComplete => false;
}
