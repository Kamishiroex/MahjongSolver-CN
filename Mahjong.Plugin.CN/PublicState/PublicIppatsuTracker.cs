using System.Collections.Immutable;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.PublicState;

/// <summary>Negative eligibility only, from a declaration followed by a confirmed public call.</summary>
internal sealed class PublicIppatsuTracker
{
    private readonly Dictionary<ScreenPosition, ObservationReference> declarations = new();
    private readonly Dictionary<ScreenPosition, PublicCallEvent> cancelled = new();
    private string? epoch;
    private Guid session;
    private ObservationReference? last;

    internal void Clear()
    {
        declarations.Clear(); cancelled.Clear(); epoch = null; session = default; last = null;
    }

    internal PublicSnapshot Observe(PublicSnapshot snapshot, string? trackingEpoch,
        PublicCallTrackingResult? calls, PublicObservationContext? context)
    {
        if (!PublicCurrentFacts.Audited(context) || string.IsNullOrWhiteSpace(trackingEpoch) ||
            snapshot.SessionId == Guid.Empty || snapshot.Observation is not { } observation ||
            snapshot.Stability != StabilityState.Stable || snapshot.Players.Length != 4 ||
            snapshot.Players.Select(p => p.Position).Distinct().Count() != 4 ||
            snapshot.Players.Any(p => (int)p.Position is < 0 or > 3))
        { Clear(); return snapshot; }
        if (epoch != trackingEpoch || session != snapshot.SessionId || last is { } previous &&
            (observation.Sequence <= previous.Sequence || observation.ObservedAtUtc <= previous.ObservedAtUtc ||
             observation.ObservedAtUtc - previous.ObservedAtUtc > TimeSpan.FromSeconds(2))) Clear();
        epoch = trackingEpoch; session = snapshot.SessionId; last = observation;
        return snapshot with { Players = snapshot.Players.Select(player =>
        {
            var declared = player.RiichiDeclared;
            if (!declared.IsConfirmed || !declared.Value ||
                declared.Observation!.Sequence != observation.Sequence ||
                declared.Observation.ObservedAtUtc != observation.ObservedAtUtc)
            {
                declarations.Remove(player.Position); cancelled.Remove(player.Position); return player;
            }
            declarations.TryAdd(player.Position, observation);
            var anchor = declarations[player.Position];
            // First observation, not delayed confirmation, establishes ordering. A meld already
            // present in the baseline or a call first seen with the stick cannot establish it.
            var call = calls?.Events.FirstOrDefault(c => c.RoundToken == epoch &&
                c.FirstObservedSequence > anchor.Sequence && c.ConfirmedSequence > c.FirstObservedSequence &&
                c.ConfirmedSequence <= observation.Sequence && VerifiedCall(c));
            if (call is not null) cancelled[player.Position] = call;
            if (!cancelled.TryGetValue(player.Position, out var evidence)) return player;
            var reference = observation with
            {
                Source = "PublicIppatsuTracker/declaration-before-public-call",
                Evidence = "docs/cn/PUBLIC-INPUT-GAPS-20260925.md#连续事件补充",
                DerivationInputs = [$"riichi:{(int)player.Position}:{anchor.Sequence}",
                    $"call:{evidence.Kind}:{evidence.FirstObservedSequence}:{evidence.ConfirmedSequence}:{evidence.GroupPath}"],
            };
            // Does not imply an acceptance time, positive ippatsu interval or complete history.
            return player with { Ippatsu = Field<bool>.Known(false, reference, SourceKind.Derived) };
        }).ToImmutableArray() };
    }

    private static bool VerifiedCall(PublicCallEvent call) => call.Kind switch
    {
        "chi" or "pon" or "daiminkan" => call.Code == "PUBLIC_CALL_VISIBLE_TRANSITION",
        "ankan" => call.Code == "PUBLIC_ANKAN_TWO_FACE_DERIVED",
        "kakan" => call.Code == "PUBLIC_KAKAN_VISIBLE_TRANSITION",
        _ => false,
    };
}
