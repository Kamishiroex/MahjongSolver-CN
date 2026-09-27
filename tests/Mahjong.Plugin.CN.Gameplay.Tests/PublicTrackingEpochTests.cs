using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Synthetic observation continuity; no inferred identity is promoted to a confirmed round.</summary>
public sealed class PublicTrackingEpochTests
{
    private static readonly Guid Session = Guid.Parse("f0822dc0-3b1f-4dad-809c-70f605031384");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
    private static readonly PublicObservationContext Context = new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
        new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
    private static readonly AddonProbe Probe = new("Emj", true, true, true, 109, [], null);
    private static PublicSnapshot Snapshot(int sample, int honba = 0, int milliseconds = -1)
    {
        var reference = new ObservationReference(sample, Start.AddMilliseconds(milliseconds < 0 ? sample * 100 : milliseconds),
            "synthetic", "current public identity fixture");
        return new()
        {
            SessionId = Session, Observation = reference, StateRevision = sample,
            RoundWind = Field<int>.Known(0, reference), HandNumber = Field<int>.Known(2, reference),
            Honba = Field<int>.Known(honba, reference), DealerPlayerId = Field<int>.Known(1, reference),
            Synchronization = SynchronizationState.HistoryGap,
        };
    }

    [Fact]
    public void Two_current_identity_samples_create_local_segment_without_promoting_round_or_history()
    {
        var epoch = new PublicTrackingEpoch();
        Assert.Null(epoch.Observe(Snapshot(1), Probe, Context));
        var current = Snapshot(2) with { Stability = StabilityState.Transition };
        string? token = epoch.Observe(current, Probe, Context);
        Assert.NotNull(token); Assert.Contains(":observed:", token);
        Assert.Equal("TRACKING_EPOCH_PARTIAL_OBSERVED", epoch.Code);
        Assert.False(current.RoundId.IsConfirmed); Assert.Equal(SynchronizationState.HistoryGap, current.Synchronization);
        Assert.False(epoch.HistoryComplete);
        Assert.Equal(token, epoch.Observe(Snapshot(3), Probe, Context));
    }

    [Theory]
    [InlineData("read")] [InlineData("scene")] [InlineData("version")]
    [InlineData("time-gap")] [InlineData("identity")] [InlineData("unknown")]
    [InlineData("stale-reference")] [InlineData("candidate")]
    public void Broken_continuity_rebuilds_new_baseline_after_two_fresh_identity_samples(string reason)
    {
        var epoch = new PublicTrackingEpoch();
        epoch.Observe(Snapshot(1), Probe, Context);
        string? previous = epoch.Observe(Snapshot(2), Probe, Context);
        Assert.NotNull(previous);
        var current = Snapshot(3, honba: reason == "identity" ? 1 : 0, milliseconds: reason == "time-gap" ? 3000 : 300);
        if (reason == "unknown") current = current with { Honba = Field<int>.Unknown("not available") };
        if (reason == "stale-reference") current = current with { Honba = Snapshot(2).Honba };
        if (reason == "candidate") current = current with { Honba = current.Honba with { MappingStatus = MappingStatus.Candidate } };
        var probe = reason == "read" ? Probe with { Error = "READ_ERROR" } : reason == "scene" ? Probe with { Present = false } : Probe;
        Assert.Null(epoch.Observe(current, probe, reason == "version" ? Context with { ClientVersion = "wrong" } : Context));
        int honba = reason == "identity" ? 1 : 0;
        var next = epoch.Observe(Snapshot(4, honba, milliseconds: 3100), Probe, Context);
        if (reason is not ("time-gap" or "identity")) Assert.Null(next);
        string? recovered = epoch.Observe(Snapshot(5, honba, milliseconds: 3200), Probe, Context);
        Assert.NotNull(recovered); Assert.NotEqual(previous, recovered);
    }

    [Fact]
    public void Same_hand_animation_preserves_epoch_but_new_confirmed_round_retires_it()
    {
        var epoch = new PublicTrackingEpoch();
        epoch.Observe(Snapshot(1), Probe, Context);
        var first = epoch.Observe(Snapshot(2), Probe, Context);
        var unstable = Snapshot(3) with { Stability = StabilityState.Transition };
        Assert.Equal(first, epoch.Observe(unstable, Probe, Context));
        var opening = Snapshot(4);
        opening = opening with { RoundId = Field<string>.Known("actual-confirmed-opening", opening.Observation!) };
        string? next = epoch.Observe(opening, Probe, Context);
        Assert.NotNull(next); Assert.NotEqual(first, next);
        Assert.Equal(next, epoch.Observe(Snapshot(5), Probe, Context)); // unknown score/round does not invent a boundary
        var otherOpening = Snapshot(6);
        otherOpening = otherOpening with { RoundId = Field<string>.Known("actual-confirmed-next-opening", otherOpening.Observation!) };
        Assert.NotEqual(next, epoch.Observe(otherOpening, Probe, Context));
    }

    [Fact]
    public void Restart_and_reused_sequence_cannot_keep_old_epoch()
    {
        var epoch = new PublicTrackingEpoch();
        epoch.Observe(Snapshot(1), Probe, Context);
        var before = epoch.Observe(Snapshot(2), Probe, Context);
        Assert.Null(epoch.Observe(Snapshot(2), Probe, Context));
        Assert.Null(epoch.Observe(Snapshot(3), Probe, Context));
        Assert.NotEqual(before, epoch.Observe(Snapshot(4), Probe, Context));
        epoch.Clear(); Assert.Null(epoch.Token);
        var session2 = Guid.NewGuid();
        Assert.Null(epoch.Observe(Snapshot(1) with { SessionId = session2 }, Probe, Context));
        Assert.NotEqual(before, epoch.Observe(Snapshot(2) with { SessionId = session2 }, Probe, Context));
    }
}
