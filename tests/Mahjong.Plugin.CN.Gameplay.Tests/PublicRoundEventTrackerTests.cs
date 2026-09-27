using System.Text.Json;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PublicRoundEventTrackerTests
{
    private sealed record Sample(long SampleNumber, DateTimeOffset Utc, AddonProbe Addon);
    private sealed class Cursor { internal long Sequence = -1; }
    private static readonly ConditionalWeakTable<PublicRoundEventTracker, Cursor> Cursors = new();
    private static readonly Guid Session = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly PublicObservationContext Context = PublicObservationAssembler.CreateAuditedContext(
        new(RuntimeIdentity.TargetGame, 15, "15.0.3.5", RuntimeIdentity.TargetDalamud,
            RuntimeIdentity.TargetStructs, "ChineseSimplified", "10.0.0", null), LowerHandProfile.EmjUldSha256)!;

    private static JsonDocument Fixture()
    {
        // Source-tree fixture is intentional: it contains only selected public observation fields.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName,
            "tests", "Mahjong.Plugin.CN.Gameplay.Tests", "fixtures", "round-riichi-20260924.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName,
            "tests", "Mahjong.Plugin.CN.Gameplay.Tests", "fixtures", "round-riichi-20260924.json")));
    }

    private static Sample Load(long number)
    {
        using var json = Fixture();
        var row = json.RootElement.GetProperty("Observations").EnumerateArray().Single(x => x.GetProperty("Sample").GetInt64() == number);
        return new(number, row.GetProperty("Utc").GetDateTimeOffset(), row.GetProperty("Observation").Deserialize<AddonProbe>()!);
    }

    private static PublicSnapshot Snapshot(Sample sample, PublicObservationContext? context = null) =>
        PublicObservationAssembler.Assemble(new PublicSnapshot
        {
            SessionId = Session, StateRevision = sample.SampleNumber,
            Observation = new(sample.SampleNumber, sample.Utc, "private-public-capture", "selected original capture fields"),
            Synchronization = SynchronizationState.HistoryGap,
        }, sample.Addon, context ?? Context);

    private static PublicRoundProgress Feed(PublicRoundEventTracker tracker, long number)
    {
        var cursor = Cursors.GetOrCreateValue(tracker);
        using var fixture = Fixture();
        using var compressed = new MemoryStream(Convert.FromBase64String(fixture.RootElement.GetProperty("ContinuityGzipBase64").GetString()!));
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var continuity = JsonDocument.Parse(gzip);
        // Only actual unchanged heartbeat observations, not interpolated synthetic times.
        foreach (var row in continuity.RootElement.EnumerateArray())
        {
            long sequence = row.GetProperty("Sample").GetInt64();
            if (cursor.Sequence < 0 || sequence <= cursor.Sequence || sequence >= number) continue;
            var repeated = new Sample(sequence, row.GetProperty("Utc").GetDateTimeOffset(),
                row.GetProperty("Observation").Deserialize<AddonProbe>()!);
            tracker.Observe(Snapshot(repeated), repeated.Addon);
        }
        var sample = Load(number);
        cursor.Sequence = number;
        return tracker.Observe(Snapshot(sample), sample.Addon);
    }

    [Fact]
    public void Real_capture_mid_round_stick_never_creates_a_round_or_accepted_reach()
    {
        var result = Feed(new(), 2);
        Assert.False(result.RoundId.IsConfirmed);
        var signal = Assert.Single(result.Signals);
        Assert.Equal(PublicRoundSignalKind.RiichiStickObserved, signal.Kind);
        Assert.Equal(ScreenPosition.Lower, signal.Actor);
        Assert.False(signal.RiichiAcceptanceConfirmed);
        Assert.False(result.HistoryComplete);
    }

    [Fact]
    public void Real_wall70_empty_table_then_initial13_confirms_one_opening()
    {
        var tracker = new PublicRoundEventTracker();
        Assert.Empty(Feed(tracker, 1366).Signals);
        var opening = Feed(tracker, 1422);
        Assert.True(opening.RoundId.IsConfirmed);
        var signal = Assert.Single(opening.Signals);
        Assert.Equal(PublicRoundSignalKind.OpeningObserved, signal.Kind);
        Assert.Equal(1366, signal.FirstObserved!.Sequence);
        Assert.Equal(1422, signal.Observation.Sequence);
        Assert.False(opening.HistoryComplete);
        var current = Feed(tracker, 1431);
        Assert.Empty(current.Signals);
        Assert.True(current.RoundId.IsConfirmed);
        Assert.Equal(1431, current.RoundId.Observation!.Sequence);
        Assert.Contains("opening-observation:1366", current.RoundId.Observation.DerivationInputs);
    }

    [Fact]
    public void Real_same_title_honba_increment_creates_distinct_round_after_reset()
    {
        var tracker = new PublicRoundEventTracker();
        Feed(tracker, 7371);
        var first = Feed(tracker, 7427);
        Assert.True(first.RoundId.IsConfirmed);
        Feed(tracker, 8168);
        Feed(tracker, 8421);
        var second = Feed(tracker, 8477);
        Assert.True(second.RoundId.IsConfirmed);
        Assert.NotEqual(first.RoundId.Value, second.RoundId.Value);
        Assert.Single(second.Signals, x => x.Kind == PublicRoundSignalKind.OpeningObserved);
    }

    [Fact]
    public void Real_local_stick_precedes_discard_and_does_not_imply_acceptance()
    {
        var tracker = new PublicRoundEventTracker();
        Feed(tracker, 7371); Feed(tracker, 7427); Feed(tracker, 8168);
        var sample = Load(8169);
        Assert.Equal(14, sample.Addon.LowerHandReading!.Tiles.Length);
        var result = tracker.Observe(Snapshot(sample), sample.Addon);
        var lower = Assert.Single(result.Signals, x => x.Actor == ScreenPosition.Lower);
        Assert.Equal(PublicRoundSignalKind.RiichiStickObserved, lower.Kind);
        Assert.False(lower.RiichiAcceptanceConfirmed);
        Cursors.GetOrCreateValue(tracker).Sequence = 8169;
        var subsequent = Feed(tracker, 8198);
        Assert.DoesNotContain(subsequent.Signals, x => x.Actor == ScreenPosition.Lower);
    }

    [Fact]
    public void Real_stable_rotated_tile_associates_with_same_round_stick_without_fabricating_acceptance()
    {
        var tracker = new PublicRoundEventTracker();
        Feed(tracker, 7371); Feed(tracker, 7427);
        var first = Feed(tracker, 8146);
        Assert.Contains(first.Signals, x => x.Kind == PublicRoundSignalKind.RiichiStickObserved && x.Actor == ScreenPosition.Upper);
        var second = Feed(tracker, 8149);
        var linked = Assert.Single(second.Signals, x => x.Kind == PublicRoundSignalKind.RiichiDeclarationDiscardObserved);
        Assert.Equal(ScreenPosition.Upper, linked.Actor);
        Assert.Equal(23, linked.Tile!.Value.Id);
        Assert.Equal("Emj/123/4", linked.SlotPath);
        Assert.Equal(8146, linked.FirstObserved!.Sequence);
        Assert.False(linked.RiichiAcceptanceConfirmed);
    }

    [Theory]
    [InlineData("missing-area")]
    [InlineData("wrong-tile-hash")]
    [InlineData("stale-reference")]
    [InlineData("incomplete-hand")]
    public void Invalid_opening_evidence_does_not_create_round(string mutation)
    {
        var tracker = new PublicRoundEventTracker();
        Feed(tracker, 1366);
        var frame = Load(1422);
        var addon = frame.Addon;
        if (mutation == "missing-area") addon = addon with { PublicTableAreas = addon.PublicTableAreas!.Skip(1).ToArray() };
        if (mutation == "wrong-tile-hash") addon = addon with { LowerHandReading = addon.LowerHandReading! with
            { Tiles = addon.LowerHandReading!.Tiles.SetItem(0, addon.LowerHandReading.Tiles[0] with { FacePathHash = 0 }) } };
        if (mutation == "incomplete-hand") addon = addon with { LowerHandReading = addon.LowerHandReading! with
            { Tiles = addon.LowerHandReading!.Tiles.RemoveAt(0) } };
        var snapshot = Snapshot(frame);
        if (mutation == "stale-reference") snapshot = snapshot with { Honba = snapshot.Honba with
            { Observation = snapshot.Honba.Observation! with { Sequence = 1 } } };
        Assert.False(tracker.Observe(snapshot, addon).RoundId.IsConfirmed);
    }

    [Fact]
    public void An_unreadable_frame_invalidates_round_but_does_not_repeat_positive_stick_signal()
    {
        var tracker = new PublicRoundEventTracker();
        Feed(tracker, 7371); Feed(tracker, 7427); Feed(tracker, 8169);
        var frame = Load(8194);
        Assert.False(tracker.Observe(Snapshot(frame), frame.Addon with { Error = "read-gap" }).RoundId.IsConfirmed);
        Assert.DoesNotContain(Feed(tracker, 8198).Signals, x => x.Kind == PublicRoundSignalKind.RiichiStickObserved);
    }

    [Fact]
    public void Reset_and_unvalidated_context_do_not_reuse_previous_round()
    {
        var tracker = new PublicRoundEventTracker();
        Feed(tracker, 7371); Feed(tracker, 7427); tracker.Reset();
        var frame = Load(8169);
        var snapshot = Snapshot(frame, new("unverified", "unverified"));
        Assert.False(tracker.Observe(snapshot, frame.Addon).RoundId.IsConfirmed);
    }

    [Fact]
    public void Duplicate_or_out_of_order_observation_invalidates_previously_known_round()
    {
        var tracker = new PublicRoundEventTracker();
        Feed(tracker, 1366);
        Assert.True(Feed(tracker, 1422).RoundId.IsConfirmed);
        var duplicate = Feed(tracker, 1422);
        Assert.False(duplicate.RoundId.IsConfirmed);
        Assert.Contains("ROUND_OBSERVATION_OUT_OF_ORDER", duplicate.Issues);
    }

    [Fact]
    public void More_than_two_seconds_without_observation_invalidates_round_until_new_opening()
    {
        var tracker = new PublicRoundEventTracker();
        Feed(tracker, 1366); Feed(tracker, 1422);
        var sample = Load(1431);
        var delayed = sample with { Utc = sample.Utc.AddSeconds(3) };
        var result = tracker.Observe(Snapshot(delayed), delayed.Addon);
        Assert.False(result.RoundId.IsConfirmed);
        Assert.Contains("ROUND_OBSERVATION_TIME_GAP", result.Issues);
        Assert.Empty(result.Signals);
    }
}
