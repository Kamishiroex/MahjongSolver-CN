using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Mahjong.Core;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.Dalamud;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game;
using Mahjong.Policy.Abstractions;
using RuntimePlugin = Mahjong.Plugin.Dalamud.Plugin;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Managed service substitutes, no game process, callbacks, or native addon pointers.</summary>
public sealed class RuntimeModeLifecycleTests
{
    [Fact]
    public void Auto_loop_before_runtime_update_classifies_disposed_addon_as_scene_exit()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        var loop = Assert.IsType<AutoPlayLoop>(host.Runtime.AutoPlay);
        typeof(RuntimePlugin).GetProperty(nameof(RuntimePlugin.HasObservedTable))!
            .SetValue(host.Runtime, true);
        var reports = new List<GameplayStopInfo>();
        host.Runtime.Stopped += reports.Add;
        typeof(AutoPlayLoop).GetMethod("OnUpdateCore", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(loop, null);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.StartsWith("SCENE_EXIT：", Assert.Single(reports).Reason);
        Assert.False(host.Runtime.CanOperate);
        Assert.Null(host.Runtime.AutoPlay);
    }

    [Fact]
    public void Disabled_next_hand_preference_survives_pause_and_mode_switches()
    {
        using var host = new Host();
        host.Runtime.AutoAdvancePreference = false;
        host.Runtime.SetMode(PlayMode.Automatic);
        Assert.False(host.Runtime.Configuration.AutoAdvanceAfterHand);
        host.Runtime.PauseAutomation("user pause");
        host.Runtime.SetMode(PlayMode.Manual);
        host.Runtime.SetMode(PlayMode.Automatic);
        Assert.False(host.Runtime.Configuration.AutoAdvanceAfterHand);
        Assert.True(host.Runtime.CanOperate);
        host.Runtime.AutoAdvancePreference = true;
        host.Runtime.SetMode(PlayMode.Manual);
        host.Runtime.SetMode(PlayMode.Automatic);
        Assert.True(host.Runtime.Configuration.AutoAdvanceAfterHand);
        host.Runtime.StopAutomation("done");
        Assert.False(host.Runtime.Configuration.AutoAdvanceAfterHand);
    }
    [Fact]
    public void Pause_and_resume_disposes_old_policy_and_obtains_the_new_selected_source()
    {
        var original = new DisposablePolicy();
        var selected = new DisposablePolicy();
        IPolicy current = original;
        int factoryCalls = 0;
        using var host = new Host(policyFactory: () => { factoryCalls++; return current; });
        host.Runtime.SetMode(PlayMode.Automatic);
        var reader = host.Runtime.AddonReader;
        var loop = Assert.IsType<AutoPlayLoop>(host.Runtime.AutoPlay);
        var queue = (QueuedActionGate)typeof(AutoPlayLoop)
            .GetField("queuedActions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loop)!;
        long token = queue.Capture();
        Assert.Same(original, host.Runtime.Policy);

        host.Runtime.PauseAutomation("synthetic decision source change");
        current = selected;

        Assert.True(original.Disposed);
        Assert.False(selected.Disposed);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.False(host.Runtime.CanOperate);
        Assert.True(host.Runtime.IsObservingPaused);
        Assert.Same(reader, host.Runtime.AddonReader);
        Assert.False(queue.IsCurrent(token));
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Null(host.Runtime.AutoPlay);

        host.Runtime.SetMode(PlayMode.Automatic); // Explicit user mode selection creates the new policy.
        Assert.Equal(2, factoryCalls);
        Assert.Same(selected, host.Runtime.Policy);
        Assert.Same(reader, host.Runtime.AddonReader);
        Assert.NotSame(loop, host.Runtime.AutoPlay);
        Assert.Equal(PlayMode.Automatic, host.Runtime.Mode);
        Assert.True(host.Runtime.CanOperate);
        host.Runtime.StopAutomation("synthetic test complete");
        Assert.True(selected.Disposed);
    }

    [Fact]
    public void Previously_armed_disk_configuration_is_disabled_before_any_mode_is_selected()
    {
        using var host = new Host(ArmedConfiguration());
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.False(host.Runtime.CanOperate);
        Assert.Null(host.Runtime.AutoPlay);
        AssertDisabled(host.Runtime.Configuration);
        Assert.NotEmpty(host.Saved);
        Assert.All(host.Saved, AssertDisabled);
        Assert.Equal(0, host.AddonLookups);
    }

    [Fact]
    public void Manual_mode_dispatcher_blocks_all_input_routes_before_accessing_addon()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Manual);
        Assert.Equal(PlayMode.Manual, host.Runtime.Mode);
        Assert.Null(host.Runtime.AutoPlay);
        AssertDisabled(host.Runtime.Configuration);
        int initialLookups = host.AddonLookups;
        var dispatcher = host.Runtime.Dispatcher;
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, dispatcher.DispatchDiscard(0));
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, dispatcher.DispatchPass());
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, dispatcher.DispatchCall());
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, dispatcher.DispatchChiVariant(0));
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, dispatcher.DispatchHandResultNext());
        Assert.Equal(initialLookups, host.AddonLookups);
        Assert.All(host.Saved, AssertDisabled);
    }

    [Fact]
    public void Automatic_mode_is_session_only_and_stop_invalidates_its_actual_pending_queue()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        Assert.Equal(PlayMode.Automatic, host.Runtime.Mode);
        Assert.True(host.Runtime.Configuration.AutomationArmed);
        Assert.True(host.Runtime.Configuration.AutoAdvanceAfterHand);
        Assert.False(host.Runtime.Configuration.SuggestionOnly);
        var loop = Assert.IsType<AutoPlayLoop>(host.Runtime.AutoPlay);
        var dispatcher = host.Runtime.Dispatcher;
        var queue = (QueuedActionGate)typeof(AutoPlayLoop)
            .GetField("queuedActions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loop)!;
        long token = queue.Capture();
        Assert.True(queue.IsCurrent(token));
        var callbacksBeforeStop = host.FrameworkCallbacks.ToArray();
        host.Runtime.StopAutomation("test stop");
        Assert.False(queue.IsCurrent(token));
        bool sent = false;
        Assert.False(queue.TryExecute(token, () => true, () => sent = true));
        Assert.False(sent);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.False(host.Runtime.CanOperate);
        Assert.Null(host.Runtime.AutoPlay);
        AssertDisabled(host.Runtime.Configuration);
        Assert.All(host.Saved, AssertDisabled);
        int initialLookups = host.AddonLookups;
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, dispatcher.DispatchDiscard(0));
        // Even an invocation list already captured by a host frame must not revive stopped modules.
        foreach (var callback in callbacksBeforeStop) callback.DynamicInvoke(host.Framework);
        Assert.Equal(initialLookups, host.AddonLookups);
        host.Runtime.SetMode(PlayMode.Manual);
        Assert.Equal(PlayMode.Manual, host.Runtime.Mode);
        AssertDisabled(host.Runtime.Configuration);
        Assert.False(queue.IsCurrent(token));
    }

    [Fact]
    public void Stop_reports_previous_mode_once_after_blocking_input_and_before_teardown()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        var reports = new List<GameplayStopInfo>();
        host.Runtime.Stopped += report =>
        {
            Assert.False(host.Runtime.CanOperate);
            Assert.Equal(PlayMode.Off, host.Runtime.Mode);
            Assert.NotNull(host.Runtime.ActiveAggregator);
            reports.Add(report);
        };
        host.Runtime.StopAutomation("TEST_READ_ERROR");
        host.Runtime.StopAutomation("already stopped");
        var saved = Assert.Single(reports);
        Assert.Equal(PlayMode.Automatic, saved.PreviousMode);
        Assert.Equal("TEST_READ_ERROR", saved.Reason);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Null(host.Runtime.AutoPlay);
    }

    [Fact]
    public void Failing_stop_recorder_cannot_prevent_cancellation_or_teardown()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        host.Runtime.Stopped += _ => throw new IOException("simulated recorder failure");
        host.Runtime.StopAutomation("TEST_STOP");
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.False(host.Runtime.CanOperate);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Null(host.Runtime.AutoPlay);
        AssertDisabled(host.Runtime.Configuration);
    }

    [Fact]
    public async Task Consecutive_off_thread_stops_keep_the_first_active_reason_until_actual_cleanup()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        var reports = new List<GameplayStopInfo>();
        host.Runtime.Stopped += reports.Add;
        host.IsFrameworkThread = false;
        await Task.Run(() =>
        {
            host.Runtime.StopAutomation("FIRST_READ_ERROR");
            host.Runtime.StopAutomation("redundant stop");
        });
        Task completion = host.Runtime.StopCompletion;
        Assert.False(completion.IsCompleted);
        Assert.False(host.Runtime.CanOperate);
        Assert.Equal("FIRST_READ_ERROR", host.Runtime.Status);
        Assert.Empty(reports);
        Assert.NotNull(host.Runtime.ActiveAggregator);
        Assert.Equal(2, host.PendingFrameworkActions.Count);

        host.DrainFrameworkActions();
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        var report = Assert.Single(reports);
        Assert.Equal("FIRST_READ_ERROR", report.Reason);
        Assert.Equal(PlayMode.Automatic, report.PreviousMode);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Null(host.Runtime.AutoPlay);
    }

    [Fact]
    public async Task Off_thread_stop_then_dispose_records_once_before_closing_the_background_writer()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        string directory = Path.Combine(Path.GetTempPath(), "mjcn-stop-lifecycle-test-" + Guid.NewGuid().ToString("N"));
        var recorder = new GameplayStopRecorder(directory);
        int reports = 0;
        void Record(GameplayStopInfo stop)
        {
            reports++;
            Assert.True(recorder.TryRecord(JsonSerializer.Serialize(stop)));
        }
        host.Runtime.Stopped += Record;
        Task? finish = null;
        try
        {
            host.IsFrameworkThread = false;
            await Task.Run(() =>
            {
                // This is the ordering used by the actual CN entry's DisposeCore.
                host.Runtime.StopAutomation("ORIGINAL_UNLOAD_REASON");
                host.Runtime.Dispose();
                finish = Mahjong.Plugin.CN.Plugin.FinishGameplayStopRecordingAsync(host.Runtime, recorder, Record);
            });
            Assert.NotNull(finish);
            Assert.False(finish.IsCompleted);
            Assert.False(host.Runtime.StopCompletion.IsCompleted);
            Assert.Equal(0, reports);
            Assert.False(Directory.Exists(directory));

            host.DrainFrameworkActions();
            await finish.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, reports);
            Assert.Null(host.Runtime.ActiveAggregator);
            Assert.Empty(host.FrameworkCallbacks);
            Assert.Null(recorder.LastError);
            string reportPath = Assert.IsType<string>(recorder.LastPath);
            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath));
            Assert.Equal("ORIGINAL_UNLOAD_REASON", report.RootElement.GetProperty("Reason").GetString());
            Assert.Equal((int)PlayMode.Automatic, report.RootElement.GetProperty("PreviousMode").GetInt32());
            Assert.False(recorder.TryRecord("{}"));
            Assert.Equal("STOP_RECORDER_CLOSED", recorder.LastError);
        }
        finally
        {
            host.DrainFrameworkActions();
            if (finish is not null) await finish;
            await recorder.CompleteAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Host_scheduler_failure_cannot_throw_out_of_a_stop_or_reenable_input()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        host.IsFrameworkThread = false;
        host.ThrowOnSchedule = true;
        await Task.Run(() => host.Runtime.StopAutomation("TEST_STOP"));
        await host.Runtime.StopCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(host.Runtime.CanOperate);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.Contains("STOP_CLEANUP_FAILED", host.Runtime.Status);
        // Restore the host so fixture disposal can execute the deferred native teardown.
        host.IsFrameworkThread = true;
        host.ThrowOnSchedule = false;
    }

    [Fact]
    public void Failed_stop_observer_and_closed_logger_cannot_skip_remaining_teardown()
    {
        bool loggerClosed = false;
        var log = ServiceProxy.Create<IPluginLog>((_, _) =>
        {
            if (loggerClosed) throw new InvalidOperationException("host logger closed");
            return null;
        });
        using var host = new Host(pluginLog: log);
        host.Runtime.SetMode(PlayMode.Automatic);
        host.Runtime.Stopped += _ => throw new IOException("observer failed");
        loggerClosed = true;
        host.Runtime.StopAutomation("TEST_STOP");
        Assert.False(host.Runtime.CanOperate);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Null(host.Runtime.AutoPlay);
        AssertDisabled(host.Runtime.Configuration);
    }

    [Fact]
    public void Stop_retains_menu_and_dispatch_after_redundant_cleanup_until_a_new_mode_starts()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Manual);
        var menu = new CallMenuObservation(6, true, ["Tsumo", "Pass"], ["Tsumo"]);
        var dispatch = new InputDispatcher.CallDispatchObservation(6, 0, ["Tsumo"], "list-select",
            InputDispatcher.DispatchResult.Submitted, null);
        // Managed diagnostic properties only: no addon pointers or game calls are supplied.
        typeof(AddonEmjReader).GetProperty(nameof(AddonEmjReader.LastCallMenu))!.SetValue(host.Runtime.AddonReader, menu);
        typeof(InputDispatcher).GetProperty(nameof(InputDispatcher.LastCallDispatch))!.SetValue(host.Runtime.Dispatcher, dispatch);
        var reports = new List<GameplayStopInfo>();
        host.Runtime.Stopped += reports.Add;
        host.Runtime.StopAutomation("TEST_STOP");
        host.Runtime.StopAutomation("already stopped");
        var report = Assert.Single(reports);
        Assert.Same(menu, report.Menu);
        Assert.Same(dispatch, report.Dispatch);
        Assert.Same(menu, host.Runtime.LastStoppedMenu);
        Assert.Same(dispatch, host.Runtime.LastStoppedDispatch);
        Assert.Null(host.Runtime.ActiveCallMenu);
        Assert.Null(host.Runtime.ActiveCallDispatch);
        host.Runtime.SetMode(PlayMode.Manual);
        Assert.Null(host.Runtime.LastStoppedMenu);
        Assert.Null(host.Runtime.LastStoppedDispatch);
    }

    [Fact]
    public void Lease_expiry_blocks_dispatch_before_addon_access_and_invalidates_the_existing_queue()
    {
        var issued = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        var expires = issued.AddDays(7);
        var now = expires.AddTicks(-1);
        const string expired = "TEST_ACCESS_EXPIRED：测试有效期已结束。";
        using var host = new Host(accessError: () => now < expires ? null : expired);
        host.Runtime.SetMode(PlayMode.Automatic);
        var dispatcher = host.Runtime.Dispatcher;
        var loop = Assert.IsType<AutoPlayLoop>(host.Runtime.AutoPlay);
        var queue = (QueuedActionGate)typeof(AutoPlayLoop)
            .GetField("queuedActions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loop)!;
        long token = queue.Capture();
        Assert.True(host.Runtime.CanOperate);
        int lookupsBeforeExpiry = host.AddonLookups;

        // No intervening framework update: the dispatcher itself must observe exact expiry.
        now = expires;
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, dispatcher.DispatchDiscard(0));
        Assert.Equal(lookupsBeforeExpiry, host.AddonLookups);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.Equal(expired, host.Runtime.Status);
        Assert.False(queue.IsCurrent(token));
        bool sent = false;
        Assert.False(queue.TryExecute(token, () => true, () => sent = true));
        Assert.False(sent);
        Assert.Null(host.Runtime.ActiveAggregator);
        AssertDisabled(host.Runtime.Configuration);
    }

    [Fact]
    public void Manual_mode_stops_on_the_expiring_framework_tick_and_renewal_does_not_resume_it()
    {
        var now = new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        var expires = now.AddDays(7);
        const string expired = "TEST_ACCESS_EXPIRED：请重新输入测试码。";
        using var host = new Host(accessError: () => now < expires ? null : expired);
        host.Runtime.SetMode(PlayMode.Manual);
        var callbacksBeforeExpiry = host.FrameworkCallbacks.ToArray();
        int lookupsBeforeExpiry = host.AddonLookups;
        now = expires;
        foreach (var callback in callbacksBeforeExpiry) callback.DynamicInvoke(host.Framework);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.Equal(expired, host.Runtime.Status);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Equal(lookupsBeforeExpiry, host.AddonLookups);

        expires = now.AddDays(7);
        foreach (var callback in host.FrameworkCallbacks.ToArray()) callback.DynamicInvoke(host.Framework);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.False(host.Runtime.CanOperate);
        Assert.Null(host.Runtime.ActiveAggregator);
        AssertDisabled(host.Runtime.Configuration);
        host.Runtime.SetMode(PlayMode.Manual);
        Assert.Equal(PlayMode.Manual, host.Runtime.Mode);
    }

    [Fact]
    public void Expired_access_denies_mode_selection_and_preserves_the_specific_reason()
    {
        const string expired = "TEST_ACCESS_EXPIRED：测试有效期已结束。";
        using var host = new Host(accessError: () => expired);
        host.Runtime.SetMode(PlayMode.Manual);
        host.Runtime.SetMode(PlayMode.Automatic);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.Equal(expired, host.Runtime.Status);
        Assert.False(host.Runtime.CanOperate);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Equal(0, host.AddonLookups);
        AssertDisabled(host.Runtime.Configuration);
    }

    [Fact]
    public void Access_getter_failure_stops_without_escaping_or_resuming_after_the_getter_recovers()
    {
        bool fail = false;
        using var host = new Host(accessError: () => fail ? throw new IOException("PRIVATE_FAILURE_DETAIL") : null);
        host.Runtime.SetMode(PlayMode.Automatic);
        fail = true;
        Assert.False(host.Runtime.CanOperate);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.Contains("TEST_ACCESS_CHECK_FAILED", host.Runtime.Status);
        Assert.DoesNotContain("PRIVATE_FAILURE_DETAIL", host.Runtime.Status);
        Assert.Null(host.Runtime.AutoPlay);
        Assert.Null(host.Runtime.ActiveAggregator);
        fail = false;
        foreach (var callback in host.FrameworkCallbacks.ToArray()) callback.DynamicInvoke(host.Framework);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.False(host.Runtime.CanOperate);
        AssertDisabled(host.Runtime.Configuration);
    }

    [Fact]
    public void Active_mode_switch_preserves_continuous_reader_policy_and_melds_but_cancels_old_actions()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Manual);
        var reader = host.Runtime.AddonReader;
        var aggregator = host.Runtime.Aggregator;
        var dispatcher = host.Runtime.Dispatcher;
        var policy = host.Runtime.Policy;
        var tracker = host.Runtime.MeldTracker;
        var tile = Mahjong.Core.Tile.FromId(27);
        tracker.Record(Mahjong.Core.Meld.Pon(tile, tile, 1));
        var reports = new List<GameplayStopInfo>();
        host.Runtime.Stopped += reports.Add;
        Assert.Equal(2, host.FrameworkCallbacks.Count);

        host.Runtime.SetMode(PlayMode.Automatic);
        Assert.Same(reader, host.Runtime.AddonReader);
        Assert.Same(aggregator, host.Runtime.Aggregator);
        Assert.Same(dispatcher, host.Runtime.Dispatcher);
        Assert.Same(policy, host.Runtime.Policy);
        Assert.Same(tracker, host.Runtime.MeldTracker);
        Assert.Single(tracker.Melds);
        var oldLoop = Assert.IsType<AutoPlayLoop>(host.Runtime.AutoPlay);
        var oldQueue = (QueuedActionGate)typeof(AutoPlayLoop)
            .GetField("queuedActions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(oldLoop)!;
        long oldToken = oldQueue.Capture();
        Assert.Equal(3, host.FrameworkCallbacks.Count);

        host.Runtime.SetMode(PlayMode.Manual);
        Assert.Same(reader, host.Runtime.AddonReader);
        Assert.Same(aggregator, host.Runtime.Aggregator);
        Assert.Same(dispatcher, host.Runtime.Dispatcher);
        Assert.Same(policy, host.Runtime.Policy);
        Assert.Single(tracker.Melds);
        Assert.False(oldQueue.IsCurrent(oldToken));
        bool sent = false;
        Assert.False(oldQueue.TryExecute(oldToken, () => true, () => sent = true));
        Assert.False(sent);
        Assert.Null(host.Runtime.AutoPlay);
        int lookups = host.AddonLookups;
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, dispatcher.DispatchDiscard(0));
        Assert.Equal(lookups, host.AddonLookups);
        Assert.Equal(2, host.FrameworkCallbacks.Count);
        Assert.Empty(reports);
        AssertDisabled(host.Runtime.Configuration);
        Assert.All(host.Saved, AssertDisabled);

        host.Runtime.SetMode(PlayMode.Automatic);
        Assert.NotSame(oldLoop, host.Runtime.AutoPlay);
        Assert.Single(tracker.Melds);
        Assert.Same(reader, host.Runtime.AddonReader);
        host.Runtime.StopAutomation("REAL_STOP");
        Assert.Empty(tracker.Melds);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Null(host.Runtime.AutoPlay);
        Assert.Single(host.FrameworkCallbacks);
        Assert.Equal("REAL_STOP", Assert.Single(reports).Reason);
    }

    [Fact]
    public void Active_switch_configuration_failure_stops_and_clears_history_instead_of_remaining_armed()
    {
        using var host = new Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        var dispatcher = host.Runtime.Dispatcher;
        var tile = Mahjong.Core.Tile.FromId(27);
        host.Runtime.MeldTracker.Record(Mahjong.Core.Meld.Pon(tile, tile, 1));
        var loop = Assert.IsType<AutoPlayLoop>(host.Runtime.AutoPlay);
        var queue = (QueuedActionGate)typeof(AutoPlayLoop)
            .GetField("queuedActions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loop)!;
        long token = queue.Capture();
        host.FailConfigSave = true;
        host.Runtime.SetMode(PlayMode.Manual);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.Contains("模式切换失败", host.Runtime.Status);
        Assert.False(host.Runtime.CanOperate);
        Assert.False(queue.IsCurrent(token));
        Assert.Null(host.Runtime.AutoPlay);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Empty(host.Runtime.MeldTracker.Melds);
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, dispatcher.DispatchDiscard(0));
        host.FailConfigSave = false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stop_during_mode_save_wins_without_new_callbacks_or_restored_armed_configuration(bool startAutomatic)
    {
        using var host = new Host();
        host.Runtime.SetMode(startAutomatic ? PlayMode.Automatic : PlayMode.Manual);
        QueuedActionGate? oldQueue = host.Runtime.AutoPlay is { } loop
            ? (QueuedActionGate)typeof(AutoPlayLoop)
                .GetField("queuedActions", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(loop)!
            : null;
        long oldToken = oldQueue?.Capture() ?? 0;
        var dispatcher = host.Runtime.Dispatcher;
        var reports = new List<GameplayStopInfo>();
        host.Runtime.Stopped += reports.Add;
        int subscriptionsBeforeSwitch = host.FrameworkCallbackAdds;
        host.OnSave = () => host.Runtime.StopAutomation("STOP_DURING_MODE_SAVE");

        host.Runtime.SetMode(startAutomatic ? PlayMode.Manual : PlayMode.Automatic);

        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.Equal("STOP_DURING_MODE_SAVE", host.Runtime.Status);
        Assert.Equal("STOP_DURING_MODE_SAVE", Assert.Single(reports).Reason);
        Assert.False(host.Runtime.CanOperate);
        Assert.Null(host.Runtime.AutoPlay);
        Assert.Null(host.Runtime.ActiveAggregator);
        Assert.Equal(subscriptionsBeforeSwitch, host.FrameworkCallbackAdds);
        Assert.Single(host.FrameworkCallbacks);
        if (oldQueue is not null)
        {
            Assert.False(oldQueue.IsCurrent(oldToken));
            bool sent = false;
            Assert.False(oldQueue.TryExecute(oldToken, () => true, () => sent = true));
            Assert.False(sent);
        }
        AssertDisabled(host.Runtime.Configuration);
        AssertDisabled(host.Runtime.ConfigService.Current);
        Assert.All(host.Saved, AssertDisabled);
        int lookups = host.AddonLookups;
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, dispatcher.DispatchDiscard(0));
        Assert.Equal(lookups, host.AddonLookups);
    }

    private static Configuration ArmedConfiguration() => new()
    {
        AutomationArmed = true, SuggestionOnly = false, TosAccepted = true,
        AutoPlayConfirmed = true, AutoAdvanceAfterHand = true, EnableGameLogging = true, DevMode = true,
    };

    private sealed class DisposablePolicy : IPolicy, IDisposable
    {
        internal bool Disposed { get; private set; }
        public ActionChoice Choose(StateSnapshot state) => throw new InvalidOperationException("No game observation is supplied by this fixture.");
        public void Dispose() => Disposed = true;
    }

    private static void AssertDisabled(Configuration config)
    {
        Assert.False(config.AutomationArmed);
        Assert.True(config.SuggestionOnly);
        Assert.False(config.TosAccepted);
        Assert.False(config.AutoPlayConfirmed);
        Assert.False(config.AutoAdvanceAfterHand);
        Assert.False(config.EnableGameLogging);
        Assert.False(config.DevMode);
    }

    internal sealed class Host : IDisposable
    {
        public RuntimePlugin Runtime { get; }
        public IFramework Framework { get; }
        public List<Configuration> Saved { get; } = [];
        public List<Delegate> FrameworkCallbacks { get; } = [];
        public Queue<(Action Callback, TaskCompletionSource Completion)> PendingFrameworkActions { get; } = new();
        public bool IsFrameworkThread { get; set; } = true;
        public bool ThrowOnSchedule { get; set; }
        public bool FailConfigSave { get; set; }
        public Action? OnSave { get; set; }
        public int FrameworkCallbackAdds { get; private set; }
        public int AddonLookups { get; private set; }

        public Host(Configuration? initial = null, IPluginLog? pluginLog = null, Func<string?>? accessError = null,
            Func<IPolicy>? policyFactory = null, Func<bool>? identityGate = null, Func<string?>? identityError = null,
            Func<bool>? inputGate = null)
        {
            var pi = ServiceProxy.Create<IDalamudPluginInterface>((method, args) => method.Name switch
            {
                "GetPluginConfig" => initial ?? new Configuration(),
                "SavePluginConfig" => Save((Configuration)args![0]!),
                "get_AssemblyLocation" => new FileInfo(typeof(RuntimePlugin).Assembly.Location),
                "GetPluginConfigDirectory" => Path.GetTempPath(),
                _ => throw new NotSupportedException(method.Name),
            });
            Framework = ServiceProxy.Create<IFramework>((method, args) =>
            {
                switch (method.Name)
                {
                    case "get_IsInFrameworkUpdateThread": return IsFrameworkThread;
                    case "add_Update": FrameworkCallbackAdds++; FrameworkCallbacks.Add((Delegate)args![0]!); return null;
                    case "remove_Update": FrameworkCallbacks.Remove((Delegate)args![0]!); return null;
                    case "RunOnFrameworkThread":
                        if (ThrowOnSchedule) throw new InvalidOperationException("simulated host scheduler shutdown");
                        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        PendingFrameworkActions.Enqueue(((Action)args![0]!, completion));
                        return completion.Task;
                    default: throw new NotSupportedException(method.Name);
                }
            });
            var gui = ServiceProxy.Create<IGameGui>((method, _) =>
            {
                if (method.Name != "GetAddonByName") throw new NotSupportedException(method.Name);
                AddonLookups++;
                return Activator.CreateInstance(method.ReturnType);
            });
            var lifecycle = ServiceProxy.Create<IAddonLifecycle>((method, _) => method.Name switch
            {
                "RegisterListener" or "UnregisterListener" => null,
                _ => throw new NotSupportedException(method.Name),
            });
            Runtime = new RuntimePlugin(pi, Framework, pluginLog ?? new StubPluginLog(), gui, lifecycle,
                identityGate ?? (() => true), accessError, inputGate: inputGate ?? (() => true), policyFactory: policyFactory, identityError: identityError);
        }

        private object? Save(Configuration config)
        {
            var callback = OnSave;
            OnSave = null; // A Stop callback may recursively persist disabled configuration.
            callback?.Invoke();
            if (FailConfigSave) throw new IOException("synthetic configuration save failure");
            Saved.Add(config);
            return null;
        }
        public void DrainFrameworkActions()
        {
            IsFrameworkThread = true;
            while (PendingFrameworkActions.TryDequeue(out var item))
            {
                try { item.Callback(); item.Completion.SetResult(); }
                catch (Exception ex) { item.Completion.SetException(ex); throw; }
            }
        }
        public void Dispose()
        {
            IsFrameworkThread = true;
            Runtime.Dispose();
            DrainFrameworkActions();
        }
    }

    public class ServiceProxy : DispatchProxy
    {
        private Func<MethodInfo, object?[]?, object?> handler = null!;
        public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            var result = Create<T, ServiceProxy>();
            ((ServiceProxy)(object)result).handler = handler;
            return result;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => handler(targetMethod!, args);
    }
}
