using System.Collections.Immutable;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PublicIppatsuTrackerTests
{
    private static readonly PublicObservationContext Context = new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
        new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
    private static PublicSnapshot Snapshot(long sequence, bool? declared = true)
    {
        var observation = new ObservationReference(sequence, DateTimeOffset.UnixEpoch.AddMilliseconds(sequence * 100),
            "synthetic", "constructed temporal case; not live validation");
        return new()
        {
            SessionId = Guid.Parse("baae5eca-1ec1-4d6b-a731-444b6ffb55ce"), Observation = observation,
            Stability = StabilityState.Stable, Synchronization = SynchronizationState.HistoryGap,
            Players = Enumerable.Range(0, 4).Select(i => new PlayerPublicState((ScreenPosition)i)
            { RiichiDeclared = declared is { } value ? Field<bool>.Known(value, observation) : Field<bool>.Unknown("gap") })
                .ToImmutableArray(),
        };
    }
    private static PublicCallEvent Call(string kind = "pon", long first = 11, long confirmed = 12,
        string epoch = "epoch1") => new(kind, "right", "top", new(2, false), [new(2, false), new(2, false)],
        "Emj/113", "Emj/124/4", first, confirmed, epoch, kind switch
        {
            "ankan" => "PUBLIC_ANKAN_TWO_FACE_DERIVED", "kakan" => "PUBLIC_KAKAN_VISIBLE_TRANSITION",
            _ => "PUBLIC_CALL_VISIBLE_TRANSITION",
        });
    private static PublicSnapshot Observe(PublicIppatsuTracker tracker, long seq, PublicCallEvent? call = null,
        string epoch = "epoch1", bool? declared = true) =>
        tracker.Observe(Snapshot(seq, declared), epoch, new(call is null ? [] : [call], []), Context);
    private static void Cancelled(PublicSnapshot snapshot) => Assert.All(snapshot.Players, p =>
    { Assert.True(p.Ippatsu.IsConfirmed); Assert.False(p.Ippatsu.Value); Assert.Equal(SourceKind.Derived, p.Ippatsu.SourceKind); });
    private static void Unknown(PublicSnapshot snapshot) => Assert.All(snapshot.Players, p => Assert.False(p.Ippatsu.IsConfirmed));

    [Theory]
    [InlineData("chi")][InlineData("pon")][InlineData("daiminkan")][InlineData("ankan")][InlineData("kakan")]
    public void Later_confirmed_call_cancels_all_observed_declarations_and_persists(string kind)
    {
        var tracker = new PublicIppatsuTracker(); Unknown(Observe(tracker, 10));
        var result = Observe(tracker, 12, Call(kind)); Cancelled(result);
        Assert.All(result.Players, p => { Assert.False(p.RiichiEstablished.IsConfirmed);
            Assert.Equal(2, p.Ippatsu.Observation!.DerivationInputs.Length); });
        Assert.Equal(SynchronizationState.HistoryGap, result.Synchronization);
        Cancelled(Observe(tracker, 13));
    }

    [Theory]
    [InlineData(9, 12)][InlineData(10, 12)][InlineData(11, 11)][InlineData(11, 13)]
    public void Old_same_frame_unconfirmed_or_future_call_cannot_establish_order(long first, long confirmed)
    {
        var tracker = new PublicIppatsuTracker(); Observe(tracker, 10);
        Unknown(Observe(tracker, 12, Call(first: first, confirmed: confirmed)));
    }

    [Theory]
    [InlineData("epoch")][InlineData("session")][InlineData("gap")][InlineData("reverse")]
    [InlineData("duplicate")][InlineData("hidden")][InlineData("unknown")][InlineData("clear")]
    [InlineData("transition")][InlineData("stale-declaration")][InlineData("version")]
    public void Boundaries_and_untrusted_observations_do_not_reuse_cancellation(string mode)
    {
        var tracker = new PublicIppatsuTracker(); Observe(tracker, 10); Cancelled(Observe(tracker, 12, Call()));
        var source = Snapshot(13);
        var context = Context;
        string epoch = "epoch1";
        switch (mode)
        {
            case "epoch": epoch = "epoch2"; break;
            case "session": source = source with { SessionId = Guid.NewGuid() }; break;
            case "gap": source = Snapshot(40); break;
            case "reverse": source = Snapshot(11); break;
            case "duplicate": source = Snapshot(12); break;
            case "hidden": source = Snapshot(13, false); break;
            case "unknown": source = Snapshot(13, null); break;
            case "clear": tracker.Clear(); break;
            case "transition": source = source with { Stability = StabilityState.Transition }; break;
            case "stale-declaration": source = source with { Players = Snapshot(10).Players }; break;
            case "version": context = Context with { ClientVersion = "wrong" }; break;
        }
        Unknown(tracker.Observe(source, epoch, null, context));
    }

    [Fact]
    public void Baseline_melds_wrong_epoch_and_unverified_events_do_not_cancel_new_riichi()
    {
        var tracker = new PublicIppatsuTracker();
        Unknown(Observe(tracker, 12, Call()));
        Unknown(Observe(tracker, 14, Call(first: 13, confirmed: 14, epoch: "old")));
        Unknown(Observe(tracker, 16, Call(first: 15, confirmed: 16) with { Code = "candidate" }));
        Unknown(Observe(tracker, 18, Call("dahai", 17, 18)));
    }
}
