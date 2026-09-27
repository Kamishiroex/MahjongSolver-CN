using System.Reflection;
using System.Collections.Immutable;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Mahjong.Core;
using Mahjong.Cn;
using Mahjong.Cn.Events;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.Dalamud;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game;
using RuntimePlugin = Mahjong.Plugin.Dalamud.Plugin;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Managed host substitutes and public-state fixtures, never native callbacks.</summary>
public sealed class RuntimeRecoveryLifecycleTests
{
    [Fact]
    public void Submission_return_after_runtime_teardown_is_recorded_without_confirming_action_or_accessing_addon()
    {
        using var host = new Host();
        GameplayActionSubmission? recorded = null;
        host.Runtime.ActionSubmissionRecorded += value => recorded = value;
        host.Runtime.Dispose();
        Assert.Null(host.Runtime.ActiveDiscardPath);
        host.Runtime.RecordActionSubmission("ron", InputDispatcher.DispatchResult.Submitted,
            option: 0, tile: Tile.FromId(33), route: "(dispatch-route-unavailable-after-teardown)");
        Assert.NotNull(recorded);
        Assert.Equal("Submitted", recorded.Result);
        Assert.False(recorded.ActionConfirmed);
        Assert.Equal("InputDispatcher/submission", recorded.Source);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.False(host.Runtime.CanOperate);
    }

    [Fact]
    public void Submission_diagnostic_bounds_strings_and_isolates_failed_listeners()
    {
        using var host = new Host();
        GameplayActionSubmission? recorded = null;
        host.Runtime.ActionSubmissionRecorded += value => recorded = value;
        host.Runtime.ActionSubmissionRecorded += _ => throw new ObjectDisposedException("synthetic-journal");
        host.Runtime.RecordActionSubmission(new string('x', 300), InputDispatcher.DispatchResult.HookFailed,
            tile: Tile.FromId(255), route: new string('r', 500));
        Assert.NotNull(recorded);
        Assert.Equal(120, recorded.Label.Length);
        Assert.Equal(160, recorded.Route.Length);
        Assert.Null(recorded.TileId);
        Assert.False(recorded.ActionConfirmed);
    }

    [Fact]
    public void Pause_preserves_reader_and_tracker_but_cancels_queue_and_drops_policy_cache()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        var reader = host.Runtime.AddonReader;
        var oldAggregator = host.Runtime.Aggregator;
        var loop = host.Runtime.AutoPlay!;
        var queue = (QueuedActionGate)typeof(AutoPlayLoop).GetField("queuedActions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loop)!;
        long token = queue.Capture();
        Seed(host.Runtime);
        var session = host.Runtime.ObservationSessionId;
        host.Runtime.PauseAutomation("USER_PAUSE");
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.True(host.Runtime.IsObservingPaused);
        Assert.Same(reader, host.Runtime.AddonReader);
        Assert.Single(host.Runtime.MeldTracker.Melds);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Null(oldAggregator.LastChoice);
        Assert.Null(oldAggregator.Latest);
        Assert.Null(host.Runtime.AutoPlay);
        Assert.False(queue.IsCurrent(token));
        Assert.False(host.Runtime.CanOperate);
        Assert.Equal(session, host.Runtime.ObservationSessionId);

        host.Runtime.SetMode(PlayMode.Manual);
        Assert.Same(reader, host.Runtime.AddonReader);
        Assert.NotSame(oldAggregator, host.Runtime.Aggregator);
        Assert.Null(host.Runtime.Aggregator.LastChoice);
        Assert.Null(host.Runtime.Aggregator.Latest);
        Assert.Single(host.Runtime.MeldTracker.Melds);
        Assert.False(host.Runtime.IsObservingPaused);
    }

    [Fact]
    public void Recovery_reader_start_never_creates_policy_autoplay_or_armed_configuration()
    {
        using var host = new Host();
        host.Runtime.BeginRecoveryObservation();
        Assert.True(host.Runtime.IsObservingPaused);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Null(host.Runtime.AutoPlay);
        Assert.False(host.Runtime.Configuration.AutomationArmed);
        Assert.False(host.Runtime.CanOperate);
        Assert.Single(host.FrameworkCallbacks);
    }

    [Fact]
    public void Input_gate_closure_does_not_invalidate_read_identity_or_clear_paused_history()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        Seed(host.Runtime);
        host.InputAllowed = false;
        Assert.False(host.Runtime.CanOperate);
        host.Runtime.PauseAutomation("INPUT_STOPPED");
        var session = host.Runtime.ObservationSessionId;
        Assert.True(host.Runtime.RefreshIdentityGate());
        Assert.True(host.Runtime.IsObservingPaused);
        Assert.Single(host.Runtime.MeldTracker.Melds);
        Assert.Equal(session, host.Runtime.ObservationSessionId);
        host.ReadAllowed = false;
        Assert.False(host.Runtime.RefreshIdentityGate());
        Assert.False(host.Runtime.IsObservingPaused);
        Assert.Empty(host.Runtime.MeldTracker.Melds);
        Assert.NotEqual(session, host.Runtime.ObservationSessionId);
    }

    [Fact]
    public void Paused_reader_still_detects_real_table_exit_and_invalidates_observation_session()
    {
        using var host = new Host();
        host.Runtime.BeginRecoveryObservation();
        Seed(host.Runtime);
        typeof(RuntimePlugin).GetMethod("ObserveTable", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(host.Runtime, [new AddonEmjObservation(true, true, 0, 800, 600, 1, "synthetic-public-presence")]);
        var session = host.Runtime.ObservationSessionId;
        foreach (var callback in host.FrameworkCallbacks.ToArray()) callback.DynamicInvoke(host.Framework);
        Assert.False(host.Runtime.IsObservingPaused);
        Assert.Empty(host.Runtime.MeldTracker.Melds);
        Assert.NotEqual(session, host.Runtime.ObservationSessionId);
        Assert.Contains("SCENE_EXIT", host.Runtime.Status);
    }

    [Fact]
    public void Published_observations_strip_action_state_and_ura_and_deep_copy_meld_tiles()
    {
        using var host = new Host();
        host.Runtime.BeginRecoveryObservation();
        var observed = new List<StateSnapshot>();
        host.Runtime.SnapshotObserved += observed.Add;
        var meld = Meld.Pon(Tile.FromId(33), Tile.FromId(33), 1);
        var snapshot = StateSnapshot.Empty with
        {
            Hand = Tiles.Parse("123m456p789s1z"), OurMelds = [meld], UraDoraIndicators = [Tile.FromId(5)],
            Legal = new(ActionFlags.Discard, [Tile.FromId(0)], [], [], []), WallRemaining = 50,
        };
        host.Runtime.PublishSnapshot(snapshot);
        host.Runtime.PublishSnapshot(snapshot);
        var value = Assert.Single(observed);
        Assert.Equal(ActionFlags.None, value.Legal.Flags);
        Assert.Empty(value.UraDoraIndicators);
        Assert.Equal(1, host.Runtime.ObservationSequence);
        meld.Tiles[0] = Tile.FromId(0);
        Assert.Equal(33, value.OurMelds[0].Tiles[0].Id);
    }

    [Fact]
    public void New_hand_boundary_changes_observation_identity_without_restoring_an_old_choice()
    {
        using var host = new Host();
        host.Runtime.BeginRecoveryObservation();
        host.Runtime.PublishSnapshot(StateSnapshot.Empty with { Hand = Tiles.Parse("123m456p789s1122z"), WallRemaining = 20 });
        var session = host.Runtime.ObservationSessionId;
        host.Runtime.PublishSnapshot(StateSnapshot.Empty with { Hand = Tiles.Parse("123m456p789s1122z"), WallRemaining = 70 });
        Assert.NotEqual(session, host.Runtime.ObservationSessionId);
        Assert.Equal(1, host.Runtime.ObservationSequence);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.False(host.Runtime.CanOperate);
    }

    [Fact]
    public void Independently_matched_current_group_restores_experimental_inventory_without_arming()
    {
        using var host = new Host();
        var (result, anchor) = PrepareExperimentalRecovery(host.Runtime);
        Assert.True(result.CanRestoreExperimentalMelds);
        Assert.False(result.CanRestoreOwnMelds);
        Assert.False(result.HistoryComplete);
        Assert.True(host.Runtime.TryRestoreExperimentalMelds(result, anchor, out string code));
        Assert.Equal("RECOVERY_EXPERIMENTAL_MATCH", code);
        Assert.Equal(MeldKind.Pon, Assert.Single(host.Runtime.MeldTracker.Melds).Kind);
        Assert.NotNull(host.Runtime.ExportMeldCheckpoint());
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.True(host.Runtime.IsObservingPaused);
        Assert.False(host.Runtime.CanOperate);
        Assert.Null(host.Runtime.ActiveAggregator);
    }

    [Fact]
    public void Recovery_proof_cannot_apply_after_a_new_public_revision()
    {
        using var host = new Host();
        var (result, anchor) = PrepareExperimentalRecovery(host.Runtime);
        host.Runtime.PublishSnapshot(host.Runtime.RecoverySnapshot! with { WallRemaining = 49 });
        Assert.False(host.Runtime.TryRestoreExperimentalMelds(result, anchor, out string code));
        Assert.Equal("RECOVERY_STALE_OBSERVATION", code);
        Assert.Empty(host.Runtime.MeldTracker.Melds);
    }

    [Fact]
    public void Runtime_rechecks_anchor_before_mutating_tracker_even_with_a_positive_reconciliation()
    {
        using var host = new Host();
        var (result, anchor) = PrepareExperimentalRecovery(host.Runtime);
        Assert.False(host.Runtime.TryRestoreExperimentalMelds(result,
            anchor with { Scores = [26000, 24000, 25000, 25000] }, out string code));
        Assert.Equal("RECOVERY_CURRENT_ANCHOR_MISMATCH", code);
        Assert.Empty(host.Runtime.MeldTracker.Melds);
    }

    [Fact]
    public void Mere_candidate_match_cannot_import_saved_melds()
    {
        using var host = new Host();
        var (result, anchor) = PrepareExperimentalRecovery(host.Runtime);
        Assert.False(host.Runtime.TryRestoreExperimentalMelds(result with { Status = ReconciliationStatus.MatchedCandidate }, anchor, out _));
        Assert.Empty(host.Runtime.MeldTracker.Melds);
    }

    private static (ReconciliationResult Result, ExperimentalRecoveryAnchor Anchor) PrepareExperimentalRecovery(RuntimePlugin runtime)
    {
        runtime.BeginRecoveryObservation();
        var snapshot = StateSnapshot.Empty with { Hand = Tiles.Parse("123m456p789s1z"), WallRemaining = 50 };
        runtime.PublishSnapshot(snapshot);
        var hand = snapshot.Hand.Select(tile => new VisibleTile(tile.Id)).ToImmutableArray();
        var faces = ImmutableArray.Create(new VisibleTile(33), new VisibleTile(33), new VisibleTile(33));
        var footprint = Enum.GetValues<ScreenPosition>().Select(position => new VisibleAreaFootprint(position,
            position == ScreenPosition.Lower ? hand.AddRange(faces) : [], true, "synthetic public geometry fixture")).ToImmutableArray();
        var anchor = new ExperimentalRecoveryAnchor(hand, snapshot.Scores.ToImmutableArray(), [], [0, 0, 0, 0], footprint);
        var unknownHand = Field<ImmutableArray<VisibleTile>>.Unknown("legacy candidates are not confirmed");
        var unknownMelds = Field<ImmutableArray<VisibleMeld>>.Unknown("only independent faces are known in this fixture");
        var old = new MeldRecoveryCheckpoint(Guid.NewGuid(), 1, DateTimeOffset.UtcNow.AddMinutes(-1), "synthetic-runtime-fingerprint", unknownHand, unknownMelds, []);
        var current = new RecoveryTableObservation(runtime.ObservationSessionId, runtime.ObservationSequence, DateTimeOffset.UtcNow,
            "synthetic-runtime-fingerprint", unknownHand, unknownMelds, []) { Stable = true };
        var proof = new ExperimentalRecoveryEvidence(anchor, anchor,
            [new(MeldKind.Pon, [33, 33, 33], 0, 1, null, 33)],
            [new("synthetic/public-meld/0", faces, 3, true, true, "synthetic complete three-face group")]);
        return (ExperimentalTableReconciler.CompareExperimental(old, current, proof), anchor);
    }

    private static void Seed(RuntimePlugin runtime)
    {
        runtime.MeldTracker.Record(Meld.Pon(Tile.FromId(33), Tile.FromId(33), 1));
        runtime.MeldTracker.ObserveWall(50);
        runtime.MeldTracker.ObserveSnapshot(Tiles.Parse("123m456p789s1z"), [2, 3, 4, 5], 0);
    }

    private sealed class Host : IDisposable
    {
        public RuntimePlugin Runtime { get; }
        public IFramework Framework { get; }
        public List<Delegate> FrameworkCallbacks { get; } = [];
        public bool ReadAllowed { get; set; } = true;
        public bool InputAllowed { get; set; } = true;

        public Host()
        {
            var pi = RuntimeModeLifecycleTests.ServiceProxy.Create<IDalamudPluginInterface>((method, args) => method.Name switch
            {
                "GetPluginConfig" => new Configuration(), "SavePluginConfig" => null,
                "get_AssemblyLocation" => new FileInfo(typeof(RuntimePlugin).Assembly.Location),
                "GetPluginConfigDirectory" => Path.GetTempPath(), _ => throw new NotSupportedException(method.Name),
            });
            Framework = RuntimeModeLifecycleTests.ServiceProxy.Create<IFramework>((method, args) =>
            {
                switch (method.Name)
                {
                    case "get_IsInFrameworkUpdateThread": return true;
                    case "add_Update": FrameworkCallbacks.Add((Delegate)args![0]!); return null;
                    case "remove_Update": FrameworkCallbacks.Remove((Delegate)args![0]!); return null;
                    default: throw new NotSupportedException(method.Name);
                }
            });
            var gui = RuntimeModeLifecycleTests.ServiceProxy.Create<IGameGui>((method, _) => method.Name == "GetAddonByName"
                ? Activator.CreateInstance(method.ReturnType) : throw new NotSupportedException(method.Name));
            var lifecycle = RuntimeModeLifecycleTests.ServiceProxy.Create<IAddonLifecycle>((method, _) => method.Name switch
            {
                "RegisterListener" or "UnregisterListener" => null, _ => throw new NotSupportedException(method.Name),
            });
            Runtime = new(pi, Framework, new StubPluginLog(), gui, lifecycle, () => ReadAllowed, inputGate: () => InputAllowed);
        }
        public void Dispose() => Runtime.Dispose();
    }
}
