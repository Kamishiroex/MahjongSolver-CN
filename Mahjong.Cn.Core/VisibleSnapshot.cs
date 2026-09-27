using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Mahjong.Core;

namespace Mahjong.Cn;

/// <summary>Construct only when identity AND red status are known; otherwise the reader must return an error.</summary>
public readonly record struct VisibleTile([property: JsonRequired] int Id, [property: JsonRequired] bool Red = false)
{
    public string ChineseName => Id switch
    {
        >= 0 and < 9 => $"{(Red ? "赤" : "")}{Id + 1}万",
        >= 9 and < 18 => $"{(Red ? "赤" : "")}{Id - 8}筒",
        >= 18 and < 27 => $"{(Red ? "赤" : "")}{Id - 17}索",
        27 => "东", 28 => "南", 29 => "西", 30 => "北", 31 => "白", 32 => "发", 33 => "中",
        _ => "未知牌",
    };
}

/// <summary>Slot is the observed UI slot; sorting must never silently change it.</summary>
public readonly record struct HandTile([property: JsonRequired] int Slot, [property: JsonRequired] VisibleTile Tile);
public sealed record VisibleDiscard(VisibleTile Tile, bool? Tedashi, bool? Claimed);
public sealed record VisibleMeld(MeldKind Kind, ImmutableArray<VisibleTile> Tiles,
    int? FromSeat, int? RiverIndex, VisibleTile? ClaimedTile);

/// <summary>Seat order is current wind: 0 east (dealer), 1 south, 2 west, 3 north.</summary>
public sealed record VisibleSeat
{
    public int? Wind { get; init; }
    public int? Score { get; init; }
    public bool? Riichi { get; init; }
    public bool? Ippatsu { get; init; }
    public int? RiichiDiscardIndex { get; init; }
    public ImmutableArray<VisibleDiscard> River { get; init; }
    public ImmutableArray<VisibleMeld> Melds { get; init; }
}

/// <summary>Default arrays/null mean unknown. Empty arrays/false/zero are explicit observations.</summary>
public sealed record VisibleSnapshot
{
    public string? RoundId { get; init; }
    public int? RoundWind { get; init; }
    public int? HandNumber { get; init; }
    public int? Honba { get; init; }
    public int? RiichiSticks { get; init; }
    public int? OurSeat { get; init; }
    public int? WallRemaining { get; init; }
    public int? TurnIndex { get; init; }
    public bool? OurDoubleRiichi { get; init; }
    public ImmutableArray<HandTile> Hand { get; init; }
    public bool? DrawnTileKnown { get; init; }
    public int? DrawnSlot { get; init; }
    public ImmutableArray<VisibleSeat> Seats { get; init; }
    public ImmutableArray<VisibleTile> DoraIndicators { get; init; }
    public ActionFlags? LegalFlags { get; init; }
    public ImmutableArray<int> DiscardableSlots { get; init; }
}

public enum ObservationPhase { OutsideTable, Transition, Active, ReadError }
public sealed record ReadObservation(long CaptureId, ObservationPhase Phase,
    VisibleSnapshot? Snapshot = null, string? Error = null);
/// <summary>Implementations must observe public UI only and return unknown/error when evidence is missing.</summary>
public interface ICnStateReader
{
    ReadObservation Read(long captureId);
}
public sealed record ValidationIssue(string Path, string Code, string Message);
public sealed record ValidationResult(ImmutableArray<ValidationIssue> Issues)
{
    public bool IsValid => Issues.IsEmpty;
}
