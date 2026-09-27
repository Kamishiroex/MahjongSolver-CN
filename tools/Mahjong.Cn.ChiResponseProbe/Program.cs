using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mahjong.Cn.Engines;
using Mahjong.Cn.PublicState;
using Mahjong.Core;
using Mahjong.Plugin.CN;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Experimental;
using Mahjong.Plugin.CN.PublicState;
using Mahjong.Policy.Abstractions;

if (args.Length is < 2 or > 3) throw new ArgumentException("ChiResponseProbe <fixture.json> <engine-directory> [new-report.json]");
if (args.Length == 3 && File.Exists(args[2])) throw new IOException("Report already exists.");
byte[] fixtureBytes = File.ReadAllBytes(args[0]);
using var document = JsonDocument.Parse(fixtureBytes);
JsonElement fixture = document.RootElement;
var json = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
var identity = fixture.GetProperty("Source").GetProperty("RuntimeIdentity").Deserialize<RuntimeIdentity>()!;
var context = PublicObservationAssembler.CreateAuditedContext(identity, LowerHandProfile.EmjUldSha256)
    ?? throw new InvalidDataException("Fixture client/profile mismatch.");
long lateSample = fixture.GetProperty("LateReadOnlySample").GetInt64();

T? Section<T>(JsonElement frame, string name) => fixture.GetProperty("Sections")
    [frame.GetProperty("Sections").GetProperty(name).GetInt32()].Deserialize<T>();

PublicTableFaceCandidate Face(PublicTableTile tile)
{
    if (!PublicTableImageReader.TryRoute(tile.SlotPath, out var route)) throw new InvalidDataException("Unknown recorded table path.");
    uint icon = tile.RedFive ? tile.Kind34 switch { 4 => 76035u, 13 => 76036u, 22 => 76037u, _ => throw new InvalidDataException("Invalid red tile") }
        : (uint)(76001 + tile.Kind34);
    return new(tile.Area, tile.ScreenDirection, tile.SlotPath, tile.GroupPath, route.DisplayType,
        tile.X, tile.Y, tile.Width, tile.Height, tile.RotationDegrees, tile.Mirrored,
        icon, LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex"),
        PublicTableImageReader.VerifiedResourceCode, tile.RiverPosition, tile.VisualMark);
}

AddonProbe Addon(JsonElement frame)
{
    var lower = Section<LowerHandReading>(frame, "Lower")!;
    var table = Section<PublicTableReading>(frame, "Table")!;
    var interaction = Section<PublicHandInteractionCandidate>(frame, "HandInteraction");
    var faces = lower.Tiles.Select(tile => new HandFaceCandidate(tile.Path,
        interaction?.Buttons.SingleOrDefault(button => button.FacePath == tile.Path)?.ScreenX ?? 100 + tile.DisplayPosition * 50,
        100, 20, 40, tile.IconId, LowerHandProfile.VerifiedIconStatus, tile.FacePathHash)).ToArray();
    return new("Emj", true, true, true, 109, [], Section<string>(frame, "ProbeError"),
        LowerHandFaces: faces, LowerHandReading: lower,
        PublicTableFaces: table.Tiles.Select(Face).ToArray(), PublicTableReading: table,
        PublicTableAreas: table.Areas,
        PublicStatusCandidates: Section<PublicStatusCandidate[]>(frame, "Status"),
        PublicDoraCandidates: Section<PublicDoraCandidate[]>(frame, "Dora"),
        RoundTitleResource: Section<RoundTitleResourceCandidate>(frame, "RoundTitle"),
        PublicActionMenu: Section<PublicActionMenuCandidate>(frame, "ActionMenu"),
        PublicOpponentHands: Section<PublicOpponentHandStatus[]>(frame, "OpponentHands"),
        PublicHandInteraction: interaction, PublicRiichiCandidates: Section<PublicRiichiCandidate[]>(frame, "Riichi"));
}

StateSnapshot ResponseRuntime(PublicSnapshot snapshot, AddonProbe addon)
{
    if (addon.PublicActionMenu is not { Visible: true, AllVisibleRowsDecoded: true } menu ||
        !menu.Rows.Any(row => row.Action == "Chi" && row.Enabled == true) ||
        !menu.Rows.Any(row => row.Action == "Pass" && row.Enabled == true))
        throw new InvalidDataException("Recorded enabled Chi/Pass menu missing.");
    return StateSnapshot.Empty with
    {
        Hand = snapshot.LowerVisibleFaces.Value.Select(tile => Tile.FromId(tile.Tile.Id)).ToArray(),
        // Original recovery records intentionally strip current legal actions. No hidden
        // runtime field is restored; menu permission is the recorded public test condition.
        Legal = new(ActionFlags.Chi | ActionFlags.Pass, [], [], [], []), AddonStateCode = 30,
    };
}

static void CheckTrigger(AkochanGlobalProjection projection)
{
    if (projection.Snapshot is not { Trigger: { Type: "dahai", Actor: 3, Tile: { Id: 5, Red: false } } })
        throw new InvalidDataException("Expected current left-player six-man response: " + projection.Error);
    if (projection.ResponseLegal is not { } legal || !legal.Can(ActionFlags.Chi) ||
        !legal.ChiCandidates.Any(candidate => candidate.ClaimedTile.Id == 5 && candidate.FromSeat == 3))
        throw new InvalidDataException("Correct legal Chi candidate was not derived.");
}

var monitor = new PublicMonitorSession(); monitor.Start(context);
var projector = new AkochanGlobalObservationProjector();
var replay = new List<object>();
var emitted = new List<PublicRiverObservedEvent>();
AkochanGlobalProjection? observedProjection = null;
StateSnapshot? observedRuntime = null;
JsonElement lateFrame = default;
foreach (JsonElement frame in fixture.GetProperty("Frames").EnumerateArray())
{
    long sample = frame.GetProperty("Sample").GetInt64();
    if (sample == lateSample) { lateFrame = frame; continue; }
    DateTimeOffset utc = frame.GetProperty("Utc").GetDateTimeOffset();
    AddonProbe addon = Addon(frame);
    monitor.Observe(new(sample, utc, "recorded-public-short-segment", "No inserted observations or actions", [addon]));
    projector.Observe(monitor.Current, addon, monitor.RiverProgress, monitor.CallProgress,
        monitor.RoundProgress, monitor.OwnHandProgress, monitor.TrackingEpochToken);
    if (monitor.RiverProgress is { } rivers) emitted.AddRange(rivers.NewEvents);
    if (monitor.Current?.RoundId.IsConfirmed == true || monitor.Current?.Synchronization == SynchronizationState.Synchronized)
        throw new InvalidDataException("Missed opening was incorrectly promoted to complete round history.");
    if (sample == 249)
    {
        // The old logger deduplicated unchanged public states until sample384 (>15s).
        // One explicit hypothetical persistence condition supplies the NEW highlight
        // tracker's extra confirmation; this is not a claimed original client capture.
        JsonElement held = fixture.GetProperty("ManagedHoldCondition");
        long heldSample = held.GetProperty("SimulatedSequence").GetInt64();
        DateTimeOffset heldUtc = utc.AddMilliseconds(held.GetProperty("ElapsedMilliseconds").GetInt32());
        replay.Add(new { Sample = sample, Role = "OriginalRecordedBeforeHoldCondition",
            monitor.TrackingEpochToken, Events = monitor.RiverProgress?.NewEvents });
        monitor.Observe(new(heldSample, heldUtc, "ARTIFICIAL_HELD_PUBLIC_OBSERVATION",
            "Reuses recorded sample249 as an explicit offline persistence condition; not a live observation", [addon]));
        projector.Observe(monitor.Current, addon, monitor.RiverProgress, monitor.CallProgress,
            monitor.RoundProgress, monitor.OwnHandProgress, monitor.TrackingEpochToken);
        if (monitor.RiverProgress is { } heldRivers) emitted.AddRange(heldRivers.NewEvents);
        observedRuntime = ResponseRuntime(monitor.Current!, addon);
        observedProjection = projector.Project(observedRuntime, "recorded-chi-partial-epoch");
        if (observedProjection.Snapshot is null)
            throw new InvalidDataException("Observed response failed: " + JsonSerializer.Serialize(new
            { observedProjection.Error, monitor.TrackingEpochToken, CurrentRound = monitor.Current?.RoundId,
                Progress = monitor.RiverProgress, Left = monitor.Current?.Players[3].RiverImages, Emitted = emitted, Replay = replay }));
        CheckTrigger(observedProjection);
        observedProjection = observedProjection with { Snapshot = observedProjection.Snapshot! with
        { Assumptions = observedProjection.Snapshot!.Assumptions.Add(
            "OFFLINE_HELD_OBSERVATION: sample250是复用实录249的人工静态持续性条件，用于新高亮生命周期确认；不是实机采样，也不是完整实录事件流。") } };
        replay.Add(new { Sample = heldSample, Role = "ArtificialPersistenceCondition", OriginalSample = sample,
            monitor.TrackingEpochToken, Events = monitor.RiverProgress?.NewEvents });
        continue;
    }
    replay.Add(new { Sample = sample, JournalSequence = frame.GetProperty("JournalSequence"),
        ReadError = addon.Error, monitor.Status, monitor.TrackingEpochToken,
        RoundId = monitor.Current?.RoundId, Synchronization = monitor.Current?.Synchronization,
        RiverTrackerCode = monitor.RiverProgress?.Code, Events = monitor.RiverProgress?.NewEvents,
        FullHistoryVerified = monitor.RiverProgress?.HistoryComplete ?? false });
}
if (observedProjection?.Snapshot is not { } observedInput || observedRuntime is null ||
    !emitted.Any(change => change.Kind == "DiscardObserved" && change.ScreenDirection == "left" &&
        change.SlotPath == "Emj/1270004/4" && change.Tile.Kind34 == 5))
    throw new InvalidDataException("Actual tracker did not emit the newly visible left six-man discard.");
if (!observedInput.KnownEvents.Any(change => change.Type == "dahai" && change.Actor == 3 && change.Tile?.Id == 5))
    throw new InvalidDataException("New actual tracked discard was not forwarded to the engine.");

// A different scenario: restore the actual late, already stable typed public snapshot.
// There is no inferred chronology or manufactured event stream between the two scenarios.
PublicSnapshot lateSnapshot = fixture.GetProperty("LateRecordedPublicSnapshot").Deserialize<PublicSnapshot>()!;
AddonProbe lateAddon = Addon(lateFrame);
var restored = new AkochanGlobalObservationProjector();
restored.Observe(lateSnapshot, lateAddon, null, null);
StateSnapshot restoredRuntime = ResponseRuntime(lateSnapshot, lateAddon);
AkochanGlobalProjection restoredProjection = restored.Project(restoredRuntime, "recorded-chi-current-table-recovery");
CheckTrigger(restoredProjection);
if (restoredProjection.Snapshot!.KnownEvents.Length != 0)
    throw new InvalidDataException("Static read-only recovery invented chronological actions.");

var installation = await AkochanInstallation.LoadAsync(args[1]);
async Task<(AkochanGlobalDecision Decision, object Mapped)> Analyze(AkochanGlobalProjection projection, StateSnapshot state)
{
    var input = projection.Snapshot!;
    var decision = await new AkochanGlobalEngine().AnalyzeAsync(installation, input, TimeSpan.FromSeconds(10));
    state = state with { Legal = projection.ResponseLegal! };
    var candidates = decision.Candidates.OrderByDescending(candidate => candidate.Score).Select(candidate => new
    { candidate.Score, candidate.Moves, Choice = AkochanGlobalActionMapper.Map(candidate.Moves, state, input) }).ToArray();
    var selected = candidates.FirstOrDefault(candidate => candidate.Choice.Reasoning.StartsWith("AKOCHAN_GLOBAL:", StringComparison.Ordinal));
    if (selected is null || selected.Choice.Kind is not (ActionKind.Chi or ActionKind.Pass))
        throw new InvalidDataException("No offered Chi or Pass action maps from native candidates.");
    if (!candidates.Any(candidate => candidate.Choice.Kind == ActionKind.Chi &&
            candidate.Choice.Reasoning.StartsWith("AKOCHAN_GLOBAL:", StringComparison.Ordinal)) ||
        !candidates.Any(candidate => candidate.Choice.Kind == ActionKind.Pass &&
            candidate.Choice.Reasoning.StartsWith("AKOCHAN_GLOBAL:", StringComparison.Ordinal)))
        throw new InvalidDataException("Both offered Chi and Pass must map, regardless of which scores highest.");
    return (decision, new { Candidates = candidates, Selected = selected });
}
var observed = await Analyze(observedProjection, observedRuntime);
var recovery = await Analyze(restoredProjection, restoredRuntime);
var report = new
{
    Schema = 1, Mode = "Recorded public missed-opening Chi response plus one explicit held observation: managed tracking/native/mapping",
    Source = fixture.GetProperty("Source"), Conditions = fixture.GetProperty("ReconstructionConditions"),
    FixtureSha256 = Convert.ToHexString(SHA256.HashData(fixtureBytes)).ToLowerInvariant(),
    ProducerAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(PublicMonitorSession).Assembly.Location))),
    ReplayedFrames = replay, ReconstructedHoldSamples = 1, InjectedTypedRiverEvents = 0,
    HoldCondition = fixture.GetProperty("ManagedHoldCondition"),
    ActualPublicInput = observedInput, result = observed.Decision, Mapped = observed.Mapped,
    LateReadOnlyRecovery = new { Sample = lateSample, Input = restoredProjection.Snapshot,
        result = recovery.Decision, Mapped = recovery.Mapped, OriginalRoundId = lateSnapshot.RoundId },
    IsLiveGameDecision = false, GameCallbackSubmitted = false, Passed = true,
};
string output = JsonSerializer.Serialize(report, json);
if (args.Length == 3)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
    File.WriteAllText(args[2], output);
}
Console.WriteLine(output);
