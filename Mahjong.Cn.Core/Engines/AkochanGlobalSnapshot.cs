using System.Collections.Immutable;
using Mahjong.Core;

namespace Mahjong.Cn.Engines;

/// <summary>
/// Experimental current public table input. Missing chronology remains explicit; neither
/// a plausible event ordering nor an opponent's concealed hand is asserted by this model.
/// Player IDs are relative to the local player: lower=0, right=1, upper=2, left=3.
/// </summary>
public sealed record AkochanGlobalSnapshot(int OurPlayerId, int RoundWind, int HandNumber,
    int Honba, int RiichiSticks, int DealerPlayerId, int WallRemaining,
    ImmutableArray<VisibleTile> Hand, ImmutableArray<VisibleTile> DoraIndicators,
    ImmutableArray<AkochanGlobalPlayer> Players, AkochanGlobalTrigger Trigger,
    ImmutableArray<string> Assumptions)
{
    public string ContextKey { get; init; } = "";
    public DateTimeOffset Utc { get; init; }
    public ActionFlags LegalActions { get; init; }
    public int MatchFirstRound { get; init; } = 0;
    public bool HistoryComplete { get; init; }
    public string? OwnDrawKind { get; init; }
    public bool? OwnTemporaryFuriten { get; init; }
    public bool? OwnRiichiFuriten { get; init; }
    public ImmutableArray<AkochanGlobalEvent> KnownEvents { get; init; } = [];
}

public sealed record AkochanGlobalPlayer(int PlayerId, int SeatWind, int Score,
    bool RiichiDeclared, bool RiichiEstablished, int? RiichiDiscardIndex,
    ImmutableArray<AkochanGlobalDiscard> River, ImmutableArray<AkochanGlobalMeld> Melds)
{
    public bool? Ippatsu { get; init; }
    public bool? DoubleRiichi { get; init; }
}

public sealed record AkochanGlobalDiscard(VisibleTile Tile, bool? Tsumogiri, bool WasClaimed)
{
    public string? SlotPath { get; init; }
    public long? ObservedSequence { get; init; }
    public bool RiichiDeclaration { get; init; }
}

public sealed record AkochanGlobalMeld(string Type, int? FromPlayerId, VisibleTile? ClaimedTile,
    ImmutableArray<VisibleTile> Tiles)
{
    public string? GroupPath { get; init; }
}

public sealed record AkochanGlobalTrigger(string Type, int Actor, VisibleTile? Tile);

/// <summary>Only observed public transitions; a partial list is never a complete mjai replay.</summary>
public sealed record AkochanGlobalEvent(long Sequence, string Type, int Actor, int? Target,
    VisibleTile? Tile, ImmutableArray<VisibleTile> Consumed);
