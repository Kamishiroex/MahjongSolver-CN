using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.PublicState;

/// <summary>
/// A continuous local observation segment, not a confirmed game round. It permits new
/// public transitions after a mid-hand baseline without inventing an opening or history.
/// </summary>
internal sealed class PublicTrackingEpoch
{
    private Guid session;
    private long lastSequence = -1;
    private DateTimeOffset lastUtc;
    private int segment;
    private string? identity;
    private string? confirmedRound;
    private int consecutiveIdentitySamples;

    internal string? Token { get; private set; }
    internal string Code { get; private set; } = "TRACKING_EPOCH_WAITING_FOR_IDENTITY";
    internal bool HistoryComplete => false;

    internal string? Observe(PublicSnapshot snapshot, AddonProbe? addon, PublicObservationContext? context)
    {
        if (snapshot.SessionId == Guid.Empty || snapshot.Observation is not { } observation)
            return Invalidate("TRACKING_EPOCH_OBSERVATION_INVALID");
        if (session != snapshot.SessionId)
        {
            Clear();
            session = snapshot.SessionId;
        }
        if (observation.Sequence <= lastSequence || observation.ObservedAtUtc < lastUtc)
            return Invalidate("TRACKING_EPOCH_OBSERVATION_OUT_OF_ORDER");
        bool gap = lastSequence >= 0 && observation.ObservedAtUtc - lastUtc > TimeSpan.FromSeconds(2);
        lastSequence = observation.Sequence;
        lastUtc = observation.ObservedAtUtc;
        if (!ValidContext(context)) return Invalidate("TRACKING_EPOCH_VERSION_UNVERIFIED");
        if (addon is not { Name: "Emj", Present: true, Visible: true, Ready: true, Error: null })
            return Invalidate("TRACKING_EPOCH_SCENE_OR_READ_ERROR");
        if (gap) Invalidate("TRACKING_EPOCH_OBSERVATION_GAP");
        if (!Current(snapshot.RoundWind, observation, 0, 3) || !Current(snapshot.HandNumber, observation, 1, 4) ||
            !Current(snapshot.Honba, observation, 0, 99) || !Current(snapshot.DealerPlayerId, observation, 0, 3))
            return Invalidate("TRACKING_EPOCH_IDENTITY_UNVERIFIED");

        // Hand animation/sorting and unknown scores do not erase an otherwise continuous
        // observation segment. Each downstream tracker still validates its own inventory.
        string next = $"{snapshot.RoundWind.Value}/{snapshot.HandNumber.Value}/{snapshot.Honba.Value}/{snapshot.DealerPlayerId.Value}";
        if (identity != next)
        {
            Invalidate("TRACKING_EPOCH_IDENTITY_CHANGED");
            identity = next;
        }
        consecutiveIdentitySamples = Math.Min(2, consecutiveIdentitySamples + 1);
        if (snapshot.RoundId.IsConfirmed && confirmedRound != snapshot.RoundId.Value)
        {
            // A newly confirmed opening is also a boundary, even if its displayed title
            // repeats. Current identity continuity is retained, not the old trackers.
            Token = null;
            confirmedRound = snapshot.RoundId.Value;
        }
        if (consecutiveIdentitySamples < 2)
        {
            Code = "TRACKING_EPOCH_WAITING_FOR_SECOND_IDENTITY_SAMPLE";
            return null;
        }
        Token ??= $"{session:N}:observed:{++segment}";
        Code = snapshot.RoundId.IsConfirmed ? "TRACKING_EPOCH_OBSERVED_WITH_OPENING" : "TRACKING_EPOCH_PARTIAL_OBSERVED";
        return Token;
    }

    internal void Clear()
    {
        session = Guid.Empty;
        lastSequence = -1;
        lastUtc = default;
        segment = 0;
        Invalidate("TRACKING_EPOCH_WAITING_FOR_IDENTITY");
    }

    private string? Invalidate(string code)
    {
        Token = null;
        identity = confirmedRound = null;
        consecutiveIdentitySamples = 0;
        Code = code;
        return null;
    }

    private static bool Current(Field<int> field, ObservationReference observation, int minimum, int maximum) =>
        field.IsConfirmed && field.Value >= minimum && field.Value <= maximum &&
        field.Observation!.Sequence == observation.Sequence && field.Observation.ObservedAtUtc == observation.ObservedAtUtc;

    private static bool ValidContext(PublicObservationContext? context) => context is { Profile: { } profile } &&
        context.ClientVersion == RuntimeIdentity.TargetGame && profile.ClientVersion == context.ClientVersion &&
        context.UldSha256 == LowerHandProfile.EmjUldSha256 && profile.UldSha256 == context.UldSha256;
}
