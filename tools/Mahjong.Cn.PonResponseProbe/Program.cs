using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Mahjong.Cn.Engines;
using Mahjong.Cn.PublicState;
using Mahjong.Core;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Experimental;
using Mahjong.Plugin.CN.PublicState;
using Mahjong.Policy.Abstractions;

if (args.Length is < 2 or > 3) throw new ArgumentException("PonResponseProbe <fixture.json> <engine-directory> [new-report.json]");
if (args.Length == 3 && File.Exists(args[2])) throw new IOException("Report destination already exists.");
byte[] fixtureBytes = File.ReadAllBytes(args[0]);
JsonObject fixture = JsonNode.Parse(fixtureBytes)!.AsObject();
JsonArray references = fixture["ObservationReferences"]!.AsArray();
var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

JsonNode? Expand(JsonNode? node)
{
    if (node is JsonObject obj)
    {
        if (obj["$observation"] is { } index) return references[index.GetValue<int>()]!.DeepClone();
        var copy = new JsonObject();
        foreach (var pair in obj) copy[pair.Key] = Expand(pair.Value);
        return copy;
    }
    if (node is JsonArray array) return new JsonArray(array.Select(Expand).ToArray());
    return node?.DeepClone();
}

// Table logs retain resource-validated Kind34/Red identities, rather than resource pointers.
// Rebuild the pinned canonical public resource for the stability-only replay. This does
// not claim that unlogged icon IDs were observed or that the resource reader was retested.
PublicTableFaceCandidate Face(PublicTableTile tile)
{
    uint icon = tile.RedFive ? tile.Kind34 switch { 4 => 76035u, 13 => 76036u, 22 => 76037u, _ => throw new InvalidDataException("Invalid red tile") }
        : (uint)(76001 + tile.Kind34);
    return new(tile.Area, tile.ScreenDirection, tile.SlotPath, tile.GroupPath, 1021,
        tile.X, tile.Y, tile.Width, tile.Height, tile.RotationDegrees, tile.Mirrored,
        icon, LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex"),
        PublicTableImageReader.VerifiedResourceCode, tile.RiverPosition, tile.VisualMark);
}

var projector = new AkochanGlobalObservationProjector();
var tableTracker = new PublicTableTracker();
var events = fixture["RecordedRiverEvents"]!.AsArray().Select(row => row!["Change"]!.Deserialize<PublicRiverObservedEvent>()!).ToArray();
AkochanGlobalSnapshot? initialInput = null, finalInput = null;
StateSnapshot? finalRuntime = null;
var replayed = new List<object>();
foreach (JsonNode? node in fixture["Frames"]!.AsArray())
{
    JsonObject frame = node!.AsObject();
    long sample = frame["Sample"]!.GetValue<long>();
    PublicSnapshot snapshot = Expand(frame["Snapshot"])!.Deserialize<PublicSnapshot>()!;
    PublicTableReading recordedTable = frame["Table"]!.Deserialize<PublicTableReading>()!;
    PublicActionMenuCandidate menu = frame["ActionMenu"]!.Deserialize<PublicActionMenuCandidate>()!;
    if (!menu.Visible || !menu.AllVisibleRowsDecoded || !menu.Rows.Any(row => row.Action == "Pon" && row.Enabled == true) ||
        !menu.Rows.Any(row => row.Action == "Pass" && row.Enabled == true)) throw new InvalidDataException("Recorded Pon/Pass menu missing.");
    var addon = new AddonProbe("Emj", true, true, true, 109, [], null,
        PublicTableFaces: recordedTable.Tiles.Select(Face).ToArray(), PublicTableReading: recordedTable,
        PublicTableAreas: recordedTable.Areas, PublicActionMenu: menu,
        PublicRiichiCandidates: frame["Riichi"]!.Deserialize<PublicRiichiCandidate[]>()!);
    PublicTableReading rechecked = tableTracker.Observe(sample, addon);
    if (frame["Role"]?.GetValue<string>() == "TrackerWarmup")
    {
        replayed.Add(new { Sample = sample, Role = "OriginalRecordedTrackerWarmup",
            RecomputedTableStable = rechecked.Stable, AddedGameEvents = 0, Projected = false });
        continue;
    }
    bool first = initialInput is null;
    if (!first)
    {
        // Only the old brightness-sensitive stability bits are recalculated. All values,
        // exact red identities, geometry, shading, source metadata and mapping status stay.
        if (!rechecked.Stable) throw new InvalidDataException("Fixed tracker did not stabilize the recorded brightness pulse.");
        snapshot = snapshot with { Players = snapshot.Players.Select(player => player with
        {
            RiverImages = player.RiverImages with { Value = player.RiverImages.Value! with
            {
                Tiles = player.RiverImages.Value!.Tiles.Select(tile => tile with
                { Stable = rechecked.Tiles.Single(current => current.SlotPath == tile.SlotPath).Stable }).ToImmutableArray(),
                Stable = rechecked.Areas.Single(area => area.Area == "river-" + Direction((int)player.Position)).Stable,
            } },
        }).ToImmutableArray() };
        addon = addon with { PublicTableReading = rechecked };
    }
    var observed = events.Where(change => change.Sample <= sample && change.Kind == "DiscardObserved").ToArray();
    var slots = observed.Select(change =>
    {
        var tile = recordedTable.Tiles.Single(current => current.SlotPath == change.SlotPath);
        var publicTile = snapshot.Players.SelectMany(player => player.RiverImages.Value!.Tiles)
            .Single(current => current.SlotPath == change.SlotPath);
        return new PublicRiverHistorySlot(change.ScreenDirection, change.SlotPath, change.Tile,
            tile.RiverPosition!.ReadOrder, change.IsSideways, change.Tsumogiri,
            publicTile.WasClaimed.IsConfirmed ? publicTile.WasClaimed.Value : null,
            change.Sample, sample, true, change.DiscardOrdinal);
    }).ToImmutableArray();
    var history = new PublicRiverHistoryObservation("RECORDED_PUBLIC_PREFIX", snapshot.RoundId.Value, false, true, 0,
        slots, first ? observed.ToImmutableArray() : [], []);
    projector.Observe(snapshot, addon, history, null);
    // The persisted runtime snapshot intentionally strips LegalActions. Restore only the
    // recorded enabled menu for this offline test, plus a deliberately wrong legacy claim.
    var runtime = StateSnapshot.Empty with
    {
        Hand = snapshot.LowerVisibleFaces.Value.Select(tile => Tile.FromId(tile.Tile.Id)).ToArray(),
        Legal = new(ActionFlags.Pon | ActionFlags.Pass, [],
            [new(MeldKind.Pon, Tile.FromId(15), [Tile.FromId(15), Tile.FromId(15)], 1)], [], []),
        AddonStateCode = fixture["RecordedRuntime"]!["AddonStateCode"]!.GetValue<int>(),
    };
    var projection = projector.Project(runtime, "recorded-pon-window-sample117");
    if (projection.Snapshot is not { } input) throw new InvalidDataException("Projection failed: " + projection.Error);
    if (input.Trigger.Actor != 2 || input.Trigger.Tile?.Id != 23 || input.Trigger.Type != "dahai")
        throw new InvalidDataException("Wrong actual public response trigger.");
    if (projection.ResponseLegal is not { } response || response.PonCandidates.Count != 1 ||
        response.PonCandidates[0].ClaimedTile.Id != 23 || response.PonCandidates[0].FromSeat != 2)
        throw new InvalidDataException("Public response did not replace the deliberately wrong legacy candidate.");
    if (first) initialInput = input;
    finalInput = input; finalRuntime = runtime with { Legal = response };
    replayed.Add(new { Sample = sample, OriginalTopTileStable = recordedTable.Tiles.Single(tile => tile.Area == "river-top").Stable,
        RecomputedTableStable = rechecked.Stable, InitialSnapshotUsesRecordedStableBits = first,
        input.Trigger, InputSha256 = AkochanGlobalEngine.ComputeInputSha256(input),
        ResponseLegal = response, KnownEventCount = input.KnownEvents.Length });
}
if (initialInput is null || finalInput is null || finalRuntime is null) throw new InvalidDataException("Two frames required.");
bool sameInput = AkochanGlobalEngine.ComputeInputSha256(initialInput) == AkochanGlobalEngine.ComputeInputSha256(finalInput);
if (!sameInput) throw new InvalidDataException("Unchanged public response produced a different native input.");
AkochanInstallation installation = await AkochanInstallation.LoadAsync(args[1]);
AkochanGlobalDecision decision = await new AkochanGlobalEngine().AnalyzeAsync(installation, finalInput, TimeSpan.FromSeconds(10));
var candidates = decision.Candidates.OrderByDescending(candidate => candidate.Score).Select(candidate => new
{ candidate.Score, candidate.Moves, Mapped = AkochanGlobalActionMapper.Map(candidate.Moves, finalRuntime, finalInput) }).ToArray();
var selected = candidates.FirstOrDefault(candidate => candidate.Mapped.Reasoning.StartsWith("AKOCHAN_GLOBAL:", StringComparison.Ordinal));
if (selected is null || selected.Mapped.Kind is not (ActionKind.Pon or ActionKind.Pass))
    throw new InvalidDataException("No current Pon/Pass candidate mapped.");
if (!candidates.Any(candidate => candidate.Mapped.Kind == ActionKind.Pon &&
        candidate.Mapped.Reasoning.StartsWith("AKOCHAN_GLOBAL:", StringComparison.Ordinal)) ||
    !candidates.Any(candidate => candidate.Mapped.Kind == ActionKind.Pass &&
        candidate.Mapped.Reasoning.StartsWith("AKOCHAN_GLOBAL:", StringComparison.Ordinal)))
    throw new InvalidDataException("Both offered Pon and Pass must map, regardless of which scores highest.");
var report = new
{
    Schema = 1, Mode = "Recorded public pon window: offline projector/native/mapper integration",
    Source = fixture["Source"], FixtureSha256 = Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant(),
    ProducerAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(AkochanGlobalObservationProjector).Assembly.Location))),
    Conditions = fixture["ReconstructionConditions"],
    StabilityReplayCondition = "Canonical pinned tile resource identity was reconstructed from logged verified Kind34/Red; the new tracker recomputes only the second snapshot's river stability bits.",
    ReplayedFrames = replayed, SameNativeInputAcross483Samples = sameInput,
    ActualPublicInput = finalInput, AppliedNativeState = JsonSerializer.Deserialize<JsonElement>(decision.AppliedSnapshotJson),
    result = decision, Candidates = candidates, Selected = selected,
    IsLiveGameDecision = false, GameCallbackSubmitted = false, Passed = true,
};
string output = JsonSerializer.Serialize(report, json);
if (args.Length == 3)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    File.WriteAllText(args[2], output);
}
Console.WriteLine(output);

static string Direction(int id) => new[] { "bottom", "right", "top", "left" }[id];
