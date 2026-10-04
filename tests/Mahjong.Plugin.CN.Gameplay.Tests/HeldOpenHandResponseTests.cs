using System.Text.Json;
using System.Collections.Immutable;
using Mahjong.Core;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Experimental;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class HeldOpenHandResponseTests
{
    [Fact]
    public void Captured_two_pon_chi_menu_reaches_global_AI_without_inventing_event_history()
    {
        using var file = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "held-chi-open-hand-20260925.json")));
        var addon = file.RootElement.GetProperty("Addon").Deserialize<AddonProbe>()!;
        var runtime = file.RootElement.GetProperty("Runtime").Deserialize<StateSnapshot>()!;
        Assert.Equal(30, runtime.AddonStateCode);
        Assert.Equal(ActionFlags.None, runtime.Legal.Flags); // Reproduces the old stalled reader.
        Assert.Equal(7, runtime.Hand.Count);
        Assert.Equal(2, runtime.OurMelds.Count);
        Assert.Equal(new[] { "Chi", "Pass" }, addon.PublicActionMenu!.Rows.Select(x => x.Action));
        Assert.Equal(2, addon.PublicActionMenu.ListState!.ListLength);
        Assert.Equal(3, addon.PublicActionMenu.ListState.AllocatedRendererCount);
        Assert.All(addon.PublicTableFaces!.Where(t => t.Area == "river-left"), t => Assert.Null(t.RiverPosition));
        // Replay the corrected spatial mapping, preserving captured tile identities.
        // Nine earlier positions were recorded before the sideways tile appeared.
        var prior = file.RootElement.GetProperty("PriorLeftPositions").Deserialize<Dictionary<string, PublicRiverPosition>>()!;
        var anchors = prior.ToDictionary(p => p.Key,
            p => new PublicRiverAnchor(p.Value.DisplayRow, (p.Value.VisibleColumn - 1) * 34, false));
        var root = file.RootElement.GetProperty("SidewaysRoot");
        Assert.True(PublicTableImageReader.TryRoute("Emj/126/4", out var route));
        Assert.True(PublicRiverSpatialMapper.TryAnchor(route, root.GetProperty("X").GetSingle(), root.GetProperty("Y").GetSingle(), out var anchor));
        anchors["Emj/126/4"] = anchor;
        var fixedFaces = PublicRiverSpatialMapper.Assign(addon.PublicTableFaces!.Where(t => t.Area == "river-left").ToArray(), anchors)
            .ToDictionary(t => t.SlotPath);
        Assert.Equal(10, fixedFaces["Emj/126/4"].RiverPosition!.ReadOrder);
        addon = addon with
        {
            PublicTableFaces = addon.PublicTableFaces!.Select(t => fixedFaces.GetValueOrDefault(t.SlotPath) ?? t).ToArray(),
            PublicTableReading = addon.PublicTableReading! with
            { Tiles = addon.PublicTableReading.Tiles.Select(t => fixedFaces.TryGetValue(t.SlotPath, out var f)
                ? t with { RiverPosition = f.RiverPosition } : t).ToImmutableArray() },
        };
        var context = PublicObservationAssembler.CreateAuditedContext(new(RuntimeIdentity.TargetGame, 15,
            RuntimeIdentity.MinimumDalamud, RuntimeIdentity.TargetDalamud, RuntimeIdentity.TargetStructs, "ChineseSimplified", "test", null),
            LowerHandProfile.EmjUldSha256)!;
        var monitor = new PublicMonitorSession(); monitor.Start(context);
        var projector = new AkochanGlobalObservationProjector();
        var utc = DateTimeOffset.Parse("2026-09-24T18:06:00Z");
        for (int i = 1; i <= 12; i++)
        {
            monitor.Observe(new(i, utc.AddMilliseconds(i * 100), "captured-chi", "offline", [addon]));
            projector.Observe(monitor.Current, addon, monitor.RiverProgress, monitor.CallProgress,
                monitor.RoundProgress, monitor.OwnHandProgress, monitor.TrackingEpochToken);
        }
        // Permission comes from the captured current menu; no synthetic discard is added.
        runtime = runtime with { Legal = new(ActionFlags.Chi | ActionFlags.Pass, [], [], [], []) };
        var result = projector.Project(runtime, "held-current-chi");
        Assert.True(result.Snapshot is not null, result.Error + ":" + result.ErrorDetail);
        Assert.Equal("dahai", result.Snapshot.Trigger.Type);
        Assert.Equal(3, result.Snapshot.Trigger.Actor);
        Assert.NotEmpty(result.ResponseLegal!.ChiCandidates);
        Assert.False(result.Snapshot.HistoryComplete);
    }
}
