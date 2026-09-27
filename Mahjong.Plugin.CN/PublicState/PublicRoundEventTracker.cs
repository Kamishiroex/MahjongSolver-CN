using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.PublicState;

internal enum PublicRoundSignalKind { OpeningObserved, RiichiStickObserved, RiichiDeclarationDiscardObserved }

/// <summary>An observed UI transition, not an mjai event. In particular a stick is not reach_accepted.</summary>
internal sealed record PublicRoundSignal(PublicRoundSignalKind Kind, string? RoundId,
    ScreenPosition? Actor, ObservationReference Observation, ObservationReference? FirstObserved,
    VisibleTile? Tile = null, string? SlotPath = null)
{
    public bool HistoryComplete => false;
    public bool RiichiAcceptanceConfirmed => false;
}

internal sealed record PublicRoundProgress(Field<string> RoundId, ImmutableArray<PublicRoundSignal> Signals,
    ImmutableArray<string> Issues)
{
    public bool HistoryComplete => false;
}

/// <summary>
/// Tracks only transitions supported by the 2026-09-24 public capture. A round requires a
/// wall-70 / empty-table opening followed by the same opening's 13 visible initial faces.
/// Unknown sticks never mean false. No declaration/acceptance ordering is invented when
/// the stick and rotated discard are first seen together. Caller supplies each observation once.
/// </summary>
internal sealed class PublicRoundEventTracker
{
    private static readonly string[] Directions = ["bottom", "right", "top", "left"];
    private Guid session;
    private long lastSequence = -1;
    private DateTimeOffset lastTime;
    private string? openingKey;
    private string? pendingKey;
    private ObservationReference? pendingOpening;
    private bool openingConsumed;
    private readonly Dictionary<ScreenPosition, ObservationReference> sticks = [];
    private readonly HashSet<ScreenPosition> linkedDiscards = [];
    private Field<string> roundId = Field<string>.Unknown("ROUND_OPENING_NOT_OBSERVED");

    internal PublicRoundProgress Observe(PublicSnapshot snapshot, AddonProbe? addon)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var signals = ImmutableArray.CreateBuilder<PublicRoundSignal>();
        var issues = ImmutableArray.CreateBuilder<string>();
        if (snapshot.SessionId == Guid.Empty || snapshot.Observation is not { } observation)
            return new(roundId, [], ["ROUND_OBSERVATION_IDENTITY_MISSING"]);
        if (session != snapshot.SessionId) { Reset(); session = snapshot.SessionId; }
        if (observation.Sequence <= lastSequence || observation.ObservedAtUtc < lastTime)
        {
            LoseBoundary();
            return new(roundId, [], ["ROUND_OBSERVATION_OUT_OF_ORDER"]);
        }
        bool timeGap = lastSequence >= 0 && observation.ObservedAtUtc - lastTime > TimeSpan.FromSeconds(2);
        lastSequence = observation.Sequence;
        lastTime = observation.ObservedAtUtc;
        if (timeGap)
        {
            LoseBoundary();
            return new(roundId, [], ["ROUND_OBSERVATION_TIME_GAP"]);
        }
        if (addon is not { Name: "Emj", Present: true, Visible: true, Ready: true, Error: null })
        {
            LoseBoundary();
            return new(roundId, [], ["ROUND_PUBLIC_DISPLAY_UNAVAILABLE"]);
        }

        string? key = Identity(snapshot, observation);
        if (key is null)
        {
            LoseBoundary();
            return new(roundId, [], ["ROUND_IDENTITY_UNCONFIRMED"]);
        }
        bool empty = EmptyTable(addon);
        bool wallConfirmed = Current(snapshot.WallRemaining, observation);
        bool atOpening = wallConfirmed && snapshot.WallRemaining.Value == 70 && empty;
        if (openingKey is not null && key != openingKey)
        {
            LoseBoundary();
            issues.Add("ROUND_IDENTITY_CHANGED_WAITING_FOR_OPENING");
        }
        if (atOpening)
        {
            if (pendingKey != key)
            {
                pendingKey = key;
                pendingOpening = observation;
            }
            if (!openingConsumed && pendingOpening!.Sequence < observation.Sequence && InitialFaces(addon))
            {
                openingKey = key;
                openingConsumed = true;
                sticks.Clear(); linkedDiscards.Clear();
                var reference = observation with
                {
                    Source = "PublicRoundEventTracker:opening",
                    Evidence = "docs/cn/ROUND-RIICHI-CONTINUOUS-EVIDENCE.md; wall70 + eight verified empty areas + same round identity + 13 resource-verified faces",
                    DerivationInputs = [$"opening-observation:{pendingOpening.Sequence}", $"initial-faces-observation:{observation.Sequence}"],
                };
                roundId = Field<string>.Known($"{session:N}:{pendingOpening.Sequence}", reference, SourceKind.Derived);
                signals.Add(new(PublicRoundSignalKind.OpeningObserved, roundId.Value, null, reference, pendingOpening));
            }
        }
        else
        {
            pendingKey = null; pendingOpening = null;
            // A wall reset may establish another opening even when the title/honba repeat.
            if (wallConfirmed && snapshot.WallRemaining.Value < 70 && !empty) openingConsumed = false;
        }

        var candidates = addon.PublicRiichiCandidates;
        if (candidates is null || candidates.Count != 4 ||
            candidates.Select(x => x.ScreenDirection).Distinct(StringComparer.Ordinal).Count() != 4)
            issues.Add("RIICHI_STICK_SET_INCOMPLETE");
        else
        {
            for (int i = 0; i < Directions.Length; i++)
            {
                var candidate = candidates.SingleOrDefault(x => x.ScreenDirection == Directions[i]);
                var position = (ScreenPosition)i;
                if (candidate is not { StickVisible: true, Code: "PUBLIC_RIICHI_STICK_CANDIDATE" } ||
                    candidate.Path != $"Emj/{100 + i}/2") continue;
                if (!sticks.TryGetValue(position, out var first))
                {
                    first = observation with { Source = "PublicRoundEventTracker:riichi-stick",
                        Evidence = "docs/cn/ROUND-RIICHI-CONTINUOUS-EVIDENCE.md; " + candidate.Path + "; first positive observation, not acceptance" };
                    sticks.Add(position, first);
                    signals.Add(new(PublicRoundSignalKind.RiichiStickObserved, roundId.Value, position, first, first,
                        SlotPath: candidate.Path));
                    if (!roundId.IsConfirmed) issues.Add("RIICHI_FIRST_SEEN_WITHOUT_OPENING");
                }
                if (!roundId.IsConfirmed || linkedDiscards.Contains(position)) continue;
                var player = snapshot.Players.SingleOrDefault(x => x.Position == position);
                if (player?.RiverImages is not { Availability: Availability.Known, Value: { } river } field ||
                    field.Observation?.Sequence != observation.Sequence || !river.RegionReadable) continue;
                // Only the fixed sideways template is needed to observe this particular tile.
                // Other occluded river slots remain a separate gap; no complete river is claimed.
                string sidewaysPath = $"Emj/{117 + i * 3}/4";
                var rotated = river.Tiles.Where(x => x.Stable && x.DisplayPosition?.IsSideways == true &&
                    x.SlotPath == sidewaysPath).ToArray();
                if (rotated.Length != 1) continue;
                var tile = rotated[0];
                linkedDiscards.Add(position);
                var reference = observation with { Source = "PublicRoundEventTracker:riichi-discard",
                    Evidence = "docs/cn/ROUND-RIICHI-CONTINUOUS-EVIDENCE.md; same-round positive stick and stable fixed sideways river slot; acceptance and other river slots unknown",
                    DerivationInputs = [$"stick-observation:{first.Sequence}", $"river-observation:{observation.Sequence}"] };
                signals.Add(new(PublicRoundSignalKind.RiichiDeclarationDiscardObserved, roundId.Value, position,
                    reference, first, tile.Tile, tile.SlotPath));
            }
        }
        // Deliberately not a capability to clear the event ledger's HistoryGap.
        issues.Add("RIICHI_ACCEPTANCE_AND_COMPLETE_EVENT_HISTORY_UNCONFIRMED");
        // The stored identity retains its opening evidence. A current snapshot instead
        // receives a current derivation, so RequireSameObservation remains enforceable.
        var currentRoundId = roundId.IsConfirmed && openingKey == key
            ? Field<string>.Known(roundId.Value!, observation with
            {
                Source = "PublicRoundEventTracker:current-round-identity",
                Evidence = "docs/cn/ROUND-RIICHI-CONTINUOUS-EVIDENCE.md; retained observed opening + matching current round identity; no observation gap",
                DerivationInputs = [.. roundId.Observation!.DerivationInputs,
                    $"confirmed-opening-observation:{roundId.Observation.Sequence}", $"current-identity-observation:{observation.Sequence}"],
            }, SourceKind.Derived)
            : roundId;
        return new(currentRoundId, signals.ToImmutable(), issues.ToImmutable());
    }

    internal void Reset()
    {
        session = Guid.Empty; lastSequence = -1; lastTime = default;
        sticks.Clear(); linkedDiscards.Clear();
        LoseBoundary();
    }

    private void LoseBoundary()
    {
        openingKey = null; pendingKey = null; pendingOpening = null; openingConsumed = false;
        roundId = Field<string>.Unknown("ROUND_BOUNDARY_GAP_WAITING_FOR_OPENING");
    }

    private static bool Current<T>(Field<T> field, ObservationReference observation) =>
        field.IsConfirmed && field.Observation!.Sequence == observation.Sequence &&
        field.Observation.ObservedAtUtc == observation.ObservedAtUtc;

    private static string? Identity(PublicSnapshot snapshot, ObservationReference observation)
    {
        if (!Current(snapshot.RoundWind, observation) || !Current(snapshot.HandNumber, observation) ||
            !Current(snapshot.Honba, observation) || !Current(snapshot.DealerPlayerId, observation) ||
            snapshot.Players.Length != 4 || snapshot.Players.Select(x => x.Position).Distinct().Count() != 4 ||
            snapshot.Players.Any(x => !Current(x.Score, observation) || !Current(x.SeatWind, observation) ||
                !Current(x.PlayerId, observation))) return null;
        return $"{snapshot.RoundWind.Value}/{snapshot.HandNumber.Value}/{snapshot.Honba.Value}/{snapshot.DealerPlayerId.Value}/" +
            string.Join(",", snapshot.Players.OrderBy(x => x.Position).Select(x => $"{x.PlayerId.Value}:{x.SeatWind.Value}"));
    }

    private static bool EmptyTable(AddonProbe addon)
    {
        var areas = addon.PublicTableAreas;
        if (areas is null || areas.Count != 8 || areas.Select(x => x.Area).Distinct(StringComparer.Ordinal).Count() != 8)
            return false;
        return Directions.SelectMany(d => new[] { "river-" + d, "meld-" + d }).All(name =>
            areas.SingleOrDefault(x => x.Area == name) is { ContainerVerified: true, ContainerVisible: true,
                EnumerationCompleted: true, ObservedEmptyCandidate: true, VisibleSlots: 0, UnknownComponents: 0 });
    }

    private static bool InitialFaces(AddonProbe addon)
    {
        if (addon.LowerHandReading is not { Stable: true, Tiles.Length: 13 } reading ||
            reading.Tiles.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != 13) return false;
        return reading.Tiles.All(x => LowerTileCatalog.TryDecode(x.IconId, x.FacePathHash, out var identity) &&
            identity.Kind34 == x.Kind34 && identity.RedFive == x.RedFive) &&
            reading.Tiles.GroupBy(x => x.Kind34).All(g => g.Count() <= 4);
    }
}
