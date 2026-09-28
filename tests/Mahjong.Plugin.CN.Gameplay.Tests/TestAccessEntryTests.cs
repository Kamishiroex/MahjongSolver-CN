using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Dalamud.Plugin.Services;
using Mahjong.Cn;
using Mahjong.Plugin.CN.Access;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Experimental;
using Mahjong.Plugin.CN.Journaling;
using Mahjong.Policy.Efficiency;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

[CollectionDefinition("CN entry services", DisableParallelization = true)]
public sealed class EntryServicesCollection { }

/// <summary>Exercises the actual command targets with no game/client services available.</summary>
[Collection("CN entry services")]
public sealed class TestAccessEntryTests
{
    [Theory]
    [InlineData(false, false)][InlineData(true, false)][InlineData(false, true)][InlineData(true, true)]
    public async Task Background_preparation_requires_both_source_choice_and_lease_and_never_starts_a_task(bool beta, bool valid)
    {
        using var host = new Host();
        Host.Set(host.Entry, "taskRun", new Mahjong.Cn.Tasks.TaskRun());
        Directory.CreateDirectory(host.LogTestDirectory);
        // Deliberately incomplete installation: checks may run, native code must not.
        File.WriteAllText(Path.Combine(host.LogTestDirectory, "mortal-installation.json"), "{}");
        Host.Set(host.Entry, "selectedEngineDirectory", host.LogTestDirectory);
        Host.Set(host.Entry, "<MortalSelected>k__BackingField", true);
        Host.Set(host.Entry, "experimentalHandAiEnabled", beta ? 1 : 0);
        if (valid) Assert.True(host.Access.TryUnlock(Host.Code));
        typeof(Plugin).GetMethod("RequestSelectedMortalWarmup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Entry, null);
        typeof(Plugin).GetMethod("UpdateSelectedMortalWarmup", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Entry, null);
        var session = Host.Get<Mahjong.Cn.Engines.MortalEngineSession?>(host.Entry, "mortalSession");
        try
        {
            if (beta && valid)
            {
                Assert.NotNull(session);
                await Assert.ThrowsAnyAsync<Exception>(async () => await session.Preparation);
                Assert.False(session.IsReady);
            }
            else Assert.Null(session);
            Assert.Null(host.Entry.PlayRuntime);
            Assert.False(host.Entry.TableAutomationArmed);
            Assert.False(host.Entry.GameOperationsAuthorized);
            Assert.Equal(Mahjong.Cn.Tasks.TaskRunPhase.NotStarted, host.Entry.TaskRun.Phase);
        }
        finally { session?.Dispose(); }
    }

    [Fact]
    public void Lease_renewal_alone_does_not_request_background_preparation_for_retained_source()
    {
        using var host = new Host();
        Host.Set(host.Entry, "experimentalHandAiEnabled", 1);
        Host.Set(host.Entry, "<MortalSelected>k__BackingField", true);
        host.Entry.VerifyTestCode(Host.Code);
        Assert.False(Host.Get<bool>(host.Entry, "backgroundWarmupRequested"));
        Assert.Null(Host.Get<object?>(host.Entry, "mortalSession"));
        Assert.False(host.Entry.TableAutomationArmed);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public void Restored_test_source_requests_preparation_only_with_valid_lease(bool valid)
    {
        using var host = new Host();
        Directory.CreateDirectory(host.LogTestDirectory);
        var saved = new SolverPreferenceStore(Path.Combine(host.LogTestDirectory, "preference.json"));
        Assert.True(saved.Save(true)); Host.Set(host.Entry, "solverPreference", saved);
        if (valid) Assert.True(host.Access.TryUnlock(Host.Code));
        typeof(Plugin).GetMethod("RestoreSolverPreference", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host.Entry, null);
        Assert.True(host.Entry.ExperimentalHandAiEnabled);
        Assert.Equal(valid, Host.Get<bool>(host.Entry, "backgroundWarmupRequested"));
        Assert.Null(host.Entry.PlayRuntime); Assert.False(host.Entry.TableAutomationArmed);
    }

    [Theory][InlineData("pause")][InlineData("end")][InlineData("stop")]
    public void Recovery_cannot_survive_manual_pause_end_or_stop(string action)
    {
        using var host=new Host();host.AuthorizeSyntheticTask();
        var retry=new Automation.TransientRecoveryGate();
        Host.Set(host.Entry,"transientRecovery",retry);
        Assert.True(retry.Begin("AUTO_SNAPSHOT_UNAVAILABLE","fixture-original-grant",0));
        Assert.True(host.Entry.GameOperationsAuthorized);
        if(action=="pause")host.Entry.PausePlay();
        else if(action=="stop")host.Entry.Stop("synthetic user stop");
        else host.Entry.EndTask();
        Assert.False(retry.Pending);Assert.False(host.Entry.GameOperationsAuthorized);
        Assert.Equal(Automation.RecoveryCheck.Abandon,retry.Observe("fixture-original-grant","new",1,true,false));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_running_mode_click_preserves_armed_table_automation_and_current_runtime(bool automatic)
    {
        using var host = new Host();
        using var runtime = new RuntimeModeLifecycleTests.Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        if (automatic) host.AuthorizeSyntheticTask();
        var mode = automatic ? Mahjong.Plugin.Dalamud.PlayMode.Automatic : Mahjong.Plugin.Dalamud.PlayMode.Manual;
        runtime.Runtime.SetMode(mode);
        Host.Set(host.Entry, "<PlayRuntime>k__BackingField", runtime.Runtime);
        Host.Set(host.Entry, "<Identity>k__BackingField", new RuntimeIdentity("test", 15, "test", "test", "test", "ChineseSimplified", "10", null));
        Host.Set(host.Entry, "gameplayAllowed", true);
        Host.Set(host.Entry, "modeRequestVersion", 17);
        var coordinator = Host.Get<Mahjong.Plugin.CN.Automation.TableAutomation>(host.Entry, "tableAutomation");
        coordinator.Arm(new(true, true), 0);
        var policy = runtime.Runtime.Policy;
        var loop = runtime.Runtime.AutoPlay;
        host.Entry.ActivatePlay(automatic);
        Assert.True(coordinator.Armed);
        Assert.Empty(host.Queued);
        Assert.Equal(17, Host.Get<int>(host.Entry, "modeRequestVersion"));
        Assert.Equal(mode, runtime.Runtime.Mode);
        Assert.Same(policy, runtime.Runtime.Policy);
        Assert.Same(loop, runtime.Runtime.AutoPlay);

        // A real pause still revokes input and cancels future entry play immediately.
        host.Entry.PausePlay();
        Assert.False(coordinator.Armed);
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        host.Drain();
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Off, runtime.Runtime.Mode);
    }
    [Fact]
    public void Saving_queue_preferences_preserves_a_pending_pause_and_reloads_next_hand_choice()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        Directory.CreateDirectory(host.LogTestDirectory);
        var service = typeof(Plugin).GetField("<Interface>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!;
        var old = service.GetValue(null);
        try
        {
            service.SetValue(null, RuntimeModeLifecycleTests.ServiceProxy.Create<global::Dalamud.Plugin.IDalamudPluginInterface>((method, _) =>
                method.Name == "GetPluginConfigDirectory" ? host.LogTestDirectory : throw new InvalidOperationException(method.Name)));
            host.Entry.PausePlay();
            int pauseRequest = Host.Get<int>(host.Entry, "modeRequestVersion");
            host.Entry.ArmTableAutomation();
            host.Entry.SetTableAutomationOptions(new(true, true, 767, false));
            Assert.True(Host.Get<int>(host.Entry, "modeRequestVersion") > pauseRequest);
            Assert.True(File.Exists(Path.Combine(host.LogTestDirectory, "table-automation.json")));
            host.Drain(); // The newer unified pause cancels the arm and still tears down gameplay.
            Assert.Contains("已暂停", host.Entry.Status);
            Host.Set(host.Entry, "<AutomationOptions>k__BackingField", new Mahjong.Plugin.CN.Automation.TableAutomationOptions());
            typeof(Plugin).GetMethod("LoadTableAutomationOptions", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host.Entry, null);
            Assert.False(host.Entry.AutomationOptions.AutoAdvanceAfterHand);
            Assert.Equal(767u, host.Entry.AutomationOptions.DutyId);
            Assert.False(host.Entry.TableAutomationArmed);
        }
        finally { service.SetValue(null, old); }
    }

    [Fact]
    public void Queue_stop_uses_unified_pause_and_invalidates_pending_gameplay()
    {
        using var host = new Host();
        Host.Set(host.Entry, "gameplayAllowed", true);
        Host.Set(host.Entry, "modeRequestVersion", 21);
        Host.Get<Mahjong.Plugin.CN.Automation.TableAutomation>(host.Entry, "tableAutomation").Arm(new(true, true), 0);
        host.Entry.StopTableAutomation();
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.True(Host.Get<int>(host.Entry, "modeRequestVersion") > 21);
        Assert.False(host.Entry.TableAutomationArmed);
        host.Drain();
        Assert.Contains("已暂停", host.Entry.Status);
    }
    [Fact]
    public void Queue_arming_does_not_cancel_a_previously_queued_pause()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.PausePlay();
        int pauseRequest = Host.Get<int>(host.Entry, "modeRequestVersion");
        host.Entry.ArmTableAutomation();
        Assert.Equal(pauseRequest, Host.Get<int>(host.Entry, "modeRequestVersion"));
        host.Entry.StopTableAutomation();
        host.Drain(); // Pause runs; cancelled arm returns before needing client services.
        Assert.Contains("已暂停", host.Entry.Status);
        Assert.False(host.Entry.TableAutomationArmed);
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("stop")]
    [InlineData("source")]
    [InlineData("queue-stop")]
    public void Stop_and_source_controls_cancel_queued_queue_activation(string control)
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.ArmTableAutomation();
        if (control == "pause") host.Entry.PausePlay();
        else if (control == "stop") host.Entry.Stop("stop");
        else if (control == "source") host.Entry.SetExperimentalHandAiEnabled(true);
        else host.Entry.StopTableAutomation();
        host.Drain(); // Any stale arm would access unavailable host services and fail this test.
        Assert.False(host.Entry.TableAutomationArmed);
        Assert.Null(host.Entry.PlayRuntime);
    }

    [Fact]
    public async Task Queue_status_is_recorded_once_without_a_gameplay_runtime()
    {
        using var host = new Host();
        var journal = new GameJournal(host.LogTestDirectory);
        Host.Set(host.Entry, "journal", journal);
        Host.Set(host.Entry, "journalActive", true);
        host.Entry.StopTableAutomation();
        host.Entry.StopTableAutomation();
        await journal.CompleteAsync();
        var replay = await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "events.jsonl"));
        Assert.True(replay.IntegrityPassed, replay.Error);
        var item = Assert.Single(replay.Lines);
        Assert.Equal("table_automation_state", item.Entry.Kind);
        Assert.False(item.Entry.Data.GetProperty("Armed").GetBoolean());
        Assert.Equal(766u, item.Entry.Data.GetProperty("DutyId").GetUInt32());
    }
    [Theory]
    [InlineData("pause")]
    [InlineData("stop")]
    [InlineData("source")]
    public void Explicit_controls_disarm_table_automation_before_framework_work(string control)
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        var automation = Host.Get<Mahjong.Plugin.CN.Automation.TableAutomation>(host.Entry, "tableAutomation");
        automation.Arm(new(true, true), 0);
        Assert.True(automation.Armed);
        if (control == "pause") host.Entry.PausePlay();
        else if (control == "stop") host.Entry.Stop("user stop");
        else host.Entry.SetExperimentalHandAiEnabled(true);
        Assert.False(automation.Armed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Decision_source_change_cancels_queued_activation_without_arming_the_new_source(bool enabled)
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        Host.Set(host.Entry, "experimentalHandAiEnabled", enabled ? 0 : 1);
        host.Entry.ActivatePlay(true);
        Assert.Single(host.Queued);
        int request = Host.Get<int>(host.Entry, "modeRequestVersion");
        Host.Set(host.Entry, "gameplayAllowed", true);

        host.Entry.SetExperimentalHandAiEnabled(enabled);

        Assert.Equal(enabled, host.Entry.ExperimentalHandAiEnabled);
        Assert.Equal(enabled ? "测试版" : "标准求解器", host.Entry.DecisionSourceLabel);
        Assert.Contains(host.Entry.DecisionSourceLabel, host.Entry.Status);
        Assert.Contains("暂停", host.Entry.Status);
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.True(Host.Get<int>(host.Entry, "modeRequestVersion") > request);
        Assert.Single(host.Queued); // Source selection schedules no new activation.
        host.Drain(); // Stale activation must return before any missing client service is accessed.
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.Capturing);
        Assert.False(host.Entry.Monitoring);
        Assert.False(host.Entry.AiProbe.Busy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selecting_a_source_after_stop_does_not_rearm_or_unlock_gameplay(bool enabled)
    {
        using var host = new Host();
        Host.Set(host.Entry, "experimentalHandAiEnabled", enabled ? 0 : 1);
        Host.Set(host.Entry, "gameplayAllowed", true);
        host.Entry.Stop("synthetic explicit stop");

        host.Entry.SetExperimentalHandAiEnabled(enabled);

        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.False(host.Entry.TestAccessUnlocked);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.Empty(host.Queued);
        Assert.False(host.Entry.Capturing);
        Assert.False(host.Entry.Monitoring);
        Assert.False(host.Entry.AiProbe.Busy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Selecting_the_current_source_is_a_noop_and_does_not_cancel_a_mode_request(bool enabled)
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        Host.Set(host.Entry, "experimentalHandAiEnabled", enabled ? 1 : 0);
        Host.Set(host.Entry, "gameplayAllowed", true);
        Host.Set(host.Entry, "modeRequestVersion", 41);
        Host.Set(host.Entry, "<Status>k__BackingField", "unchanged mode status");

        host.Entry.SetExperimentalHandAiEnabled(enabled);

        Assert.True(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.Equal(41, Host.Get<int>(host.Entry, "modeRequestVersion"));
        Assert.Equal("unchanged mode status", host.Entry.Status);
        Assert.Empty(host.Queued);
    }

    [Fact]
    public void Decision_policy_factory_selects_distinct_sources_without_starting_an_engine_or_gameplay()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        Host.Set(host.Entry, "experimentalHandAiEnabled", 0);
        Assert.IsType<EfficiencyPolicy>(host.Entry.CreateDecisionPolicy());
        host.Entry.SetExperimentalHandAiEnabled(true);
        using var ai = Assert.IsType<AkochanGlobalPolicy>(host.Entry.CreateDecisionPolicy());
        Assert.Null(ai.PendingWork);
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.Null(host.Entry.PlayRuntime);
        Assert.Empty(host.Queued);
        Assert.False(host.Entry.AiProbe.Busy);
    }

    [Fact]
    public void Locked_entry_rejects_operations_but_schedules_standard_hints()
    {
        using var host = new Host();
        host.Entry.StartAiProbe("irrelevant");
        host.Entry.SetExperimentalHandAiEnabled(true);
        Assert.Empty(host.Queued);
        Assert.False(host.Entry.ExperimentalHandAiEnabled);
        Assert.False(host.Entry.AiProbe.Busy);
        host.Entry.ActivatePlay(false);
        host.Entry.ActivatePlay(true);
        Assert.Single(host.Queued); // Only standard hints reach the deferred preflight.
        host.Entry.PausePlay(); host.Drain();
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.TestAccessUnlocked);
    }

    [Fact]
    public void Queued_beta_activation_checks_expiry_before_resource_or_client_access()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.SetExperimentalHandAiEnabled(true);
        host.Entry.ActivatePlay(true);
        Assert.Single(host.Queued);
        host.Now = host.Access.ExpiresAtUtc!.Value;
        host.Drain();
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.TestAccessUnlocked);
        Assert.True(host.Entry.ExperimentalHandAiEnabled); // Never silently fall back.
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
    }

    [Fact]
    public void Verification_preserves_explicit_standard_activation_and_never_arms_more_work()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.ActivatePlay(false);
        int request = Host.Get<int>(host.Entry, "modeRequestVersion");
        host.Entry.VerifyTestCode(Host.Code);
        Assert.True(host.Entry.TestAccessUnlocked);
        Assert.Equal(request, Host.Get<int>(host.Entry, "modeRequestVersion"));
        Assert.Single(host.Queued);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.ExperimentalHandAiEnabled);
        Assert.False(host.Entry.AiProbe.Busy);
        host.Entry.PausePlay(); host.Drain();
    }

    [Fact]
    public void Framework_expiry_preserves_standard_capture_and_permission()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        Host.Set(host.Entry, "<Capturing>k__BackingField", true);
        Host.Set(host.Entry, "gameplayAllowed", true);
        host.Now = host.Access.ExpiresAtUtc!.Value;
        typeof(Plugin).GetMethod("EnforceBetaAccess", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host.Entry, null);
        Assert.True(host.Entry.Capturing);
        Assert.True(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.Null(host.Entry.PendingStopAlert);
    }

    [Fact]
    public void Queued_standard_monitor_request_survives_test_expiry()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.StartPublicMonitor();
        int request = Host.Get<int>(host.Entry, "modeRequestVersion");
        host.Now = host.Access.ExpiresAtUtc!.Value;
        typeof(Plugin).GetMethod("EnforceBetaAccess", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host.Entry, null);
        Assert.Equal(request, Host.Get<int>(host.Entry, "modeRequestVersion"));
        Assert.Single(host.Queued);
        host.Entry.PausePlay(); host.Drain();
    }

    [Fact]
    public void Stop_invalidates_a_queued_monitor_start_and_revokes_gameplay_immediately()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        Host.Set(host.Entry, "gameplayAllowed", true);
        host.Entry.StartPublicMonitor();
        Assert.False((bool)typeof(Plugin).GetField("gameplayAllowed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(host.Entry)!);
        Assert.Single(host.Queued);
        host.Entry.Stop("synthetic stop before queued monitor activation");
        host.Drain();
        Assert.False(host.Entry.Monitoring);
        Assert.False(host.Entry.Capturing);
        Assert.Null(host.Entry.PlayRuntime);
    }

    [Fact]
    public void Verification_preserves_explicit_monitor_request_without_starting_it()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.StartPublicMonitor();
        int request = Host.Get<int>(host.Entry, "modeRequestVersion");
        host.Entry.VerifyTestCode(Host.Code);
        Assert.True(host.Entry.TestAccessUnlocked);
        Assert.Equal(request, Host.Get<int>(host.Entry, "modeRequestVersion"));
        Assert.Single(host.Queued);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.ExperimentalHandAiEnabled);
        Assert.False(host.Entry.AiProbe.Busy);
        host.Entry.PausePlay(); host.Drain();
    }

    [Fact]
    public void Framework_expiry_and_renewal_leave_standard_monitor_running()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.PublicMonitor.Start();
        host.Now = host.Access.ExpiresAtUtc!.Value;
        typeof(Plugin).GetMethod("EnforceBetaAccess", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host.Entry, null);
        Assert.True(host.Entry.Monitoring);
        Assert.False(host.Entry.TestAccessUnlocked);
        host.Entry.VerifyTestCode(Host.Code);
        Assert.True(host.Entry.Monitoring);
        Assert.Empty(host.Queued);
    }

    [Fact]
    public void Resource_validation_failure_cancels_old_queued_automatic_start()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.ActivatePlay(true);
        Assert.Single(host.Queued);
        var validate = typeof(Plugin).GetMethod("CheckLowerHandResource", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.False((bool)validate.Invoke(host.Entry, null)!);
        host.Drain();
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.Capturing);
        Assert.False(host.Entry.Monitoring);
    }

    [Fact]
    public void Queued_ai_probe_checks_expiry_before_touching_client_or_engine_services()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.StartAiProbe("irrelevant");
        Assert.Single(host.Queued);
        Assert.False(host.Entry.AiProbe.Busy);
        host.Now = host.Access.ExpiresAtUtc!.Value;
        host.Drain();
        Assert.False(host.Entry.TestAccessUnlocked);
        Assert.False(host.Entry.AiProbe.Busy);
        Assert.Null(host.Entry.AiProbe.Result);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.Capturing);
        Assert.False(host.Entry.Monitoring);
    }

    [Fact]
    public void Stop_invalidates_queued_ai_probe_and_revokes_gameplay_before_framework_execution()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        Host.Set(host.Entry, "gameplayAllowed", true);
        host.Entry.StartAiProbe("irrelevant");
        Assert.False((bool)typeof(Plugin).GetField("gameplayAllowed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(host.Entry)!);
        Assert.Single(host.Queued);
        host.Entry.Stop("synthetic stop before queued AI probe");
        host.Drain();
        Assert.False(host.Entry.AiProbe.Busy);
        Assert.Null(host.Entry.AiProbe.Result);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.Capturing);
        Assert.False(host.Entry.Monitoring);
    }

    [Fact]
    public void Verification_preserves_explicit_probe_request_without_starting_extra_work()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.StartAiProbe("irrelevant");
        int request = Host.Get<int>(host.Entry, "modeRequestVersion");
        host.Entry.VerifyTestCode(Host.Code);
        Assert.True(host.Entry.TestAccessUnlocked);
        Assert.Equal(request, Host.Get<int>(host.Entry, "modeRequestVersion"));
        Assert.Single(host.Queued);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.ExperimentalHandAiEnabled);
        Assert.False(host.Entry.AiProbe.Busy);
        host.Entry.PausePlay(); host.Drain();
    }

    [Fact]
    public void A_stale_queued_pause_cannot_cancel_a_newer_mode_request_or_recovery_state()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.PausePlay();
        var delayedPause = host.Queued.Dequeue();
        host.Entry.ActivatePlay(true); // A newer request supersedes the paused callback.
        Assert.Single(host.Queued);
        Host.Set(host.Entry, "recoveryPending", true);
        Host.Set(host.Entry, "<Status>k__BackingField", "newer request is waiting");
        delayedPause();
        Assert.Equal("newer request is waiting", host.Entry.Status);
        Assert.True((bool)typeof(Plugin).GetField("recoveryPending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host.Entry)!);
        Assert.Single(host.Queued);
        Assert.Null(host.Entry.PlayRuntime);
        // Do not execute the newer activation: this fixture intentionally owns no client services.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_existing_journal_fault_rejects_manual_or_automatic_rearm_before_client_access(bool automatic)
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        var journal = new GameJournal(Path.Combine(Path.GetTempPath(), "mjcn-fault-entry-" + Guid.NewGuid().ToString("N")));
        Assert.False(journal.Event("invalid-kind!", new { Synthetic = true }));
        Assert.Equal("JOURNAL_KIND_INVALID", journal.Fault);
        Host.Set(host.Entry, "journal", journal);
        Host.Set(host.Entry, "journalActive", true);
        Host.Set(host.Entry, "journalReportedFault", journal.Fault!); // Already paused once; rearm must still fail.
        host.Entry.ActivatePlay(automatic);
        host.Drain();
        Assert.Contains("JOURNAL_ERROR", host.Entry.Status);
        Assert.Contains("JOURNAL_KIND_INVALID", host.Entry.Status);
        Assert.False((bool)typeof(Plugin).GetField("gameplayAllowed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host.Entry)!);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.Capturing);
        Assert.False(host.Entry.Monitoring);
        await journal.CompleteAsync(); // Invalid kind was rejected before any filesystem work.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Recovery_freshness_resets_without_erasing_same_session_public_observation_history(bool changeSession)
    {
        using var host = new Host();
        var runtime = (Mahjong.Plugin.Dalamud.Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Mahjong.Plugin.Dalamud.Plugin));
        Host.Set(runtime, "<ObservationSessionId>k__BackingField", Guid.NewGuid());
        Host.Set(runtime, "<ObservationSequence>k__BackingField", 10L);
        Host.Set(host.Entry, "<PlayRuntime>k__BackingField", runtime);
        Host.Set(host.Entry, "journalActive", true);
        Host.Set(host.Entry, "journalLowerTracker", new LowerHandTracker());
        Host.Set(host.Entry, "journalTableTracker", new PublicTableTracker());
        Host.Set(host.Entry, "journalRiverBaseline", new Dictionary<string, PublicTableTile[]>(StringComparer.Ordinal));
        uint icon = 76001;
        var face = new PublicTableFaceCandidate("river-bottom", "bottom", "Emj/118/4", "Emj/118", 1021,
            100, 200, 40, 56, 0, false, icon,
            LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex"), PublicTableImageReader.VerifiedResourceCode);
        var addon = new AddonProbe("Emj", true, true, true, 109, [], null, PublicTableFaces: [face]);
        var record = typeof(Plugin).GetMethod("RecordPublicTable", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Sample(int sequence) => record.Invoke(host.Entry,
            [new DiagnosticFrame(sequence, DateTimeOffset.UtcNow, "synthetic", "synthetic", [addon])]);
        int Samples() => (int)typeof(Plugin).GetField("publicRecoverySamples", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host.Entry)!;
        Sample(1);
        Assert.False(host.Entry.CurrentPublicTable!.Stable);
        Sample(2);
        Assert.True(host.Entry.CurrentPublicTable!.Stable);
        Assert.Equal(2, Samples());
        if (changeSession) Host.Set(runtime, "<ObservationSessionId>k__BackingField", Guid.NewGuid());
        else Host.Set(runtime, "<ObservationSequence>k__BackingField", 11L);
        Sample(3);
        // A new runtime snapshot requires fresh recovery evidence. It does not invalidate
        // independent, unchanged visible images from the same observation session. Only a
        // session boundary clears those UI trackers and requires visual restabilization.
        Assert.Equal(!changeSession, host.Entry.CurrentPublicTable!.Stable);
        Assert.Equal(1, Samples());
        Assert.Equal(runtime.ObservationSessionId,
            typeof(Plugin).GetField("publicRecoverySession", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host.Entry));
        Assert.Equal(runtime.ObservationSequence,
            typeof(Plugin).GetField("publicRecoveryRuntimeSequence", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(host.Entry));
        Sample(4);
        Assert.True(host.Entry.CurrentPublicTable!.Stable);
        Assert.Equal(2, Samples());
        Assert.Null(runtime.RecoverySnapshot);
        Assert.Null(runtime.AutoPlay);
    }

    [Fact]
    public async Task Probe_failures_are_recorded_in_errors_with_location_and_identical_repeats_are_deduplicated()
    {
        using var host = new Host();
        var journal = new GameJournal(host.LogTestDirectory);
        Host.Set(host.Entry, "journal", journal);
        Host.Set(host.Entry, "journalActive", true);
        Host.Set(host.Entry, "journalLowerTracker", new LowerHandTracker());
        Host.Set(host.Entry, "journalTableTracker", new PublicTableTracker());
        Host.Set(host.Entry, "journalRiverBaseline", new Dictionary<string, PublicTableTile[]>(StringComparer.Ordinal));
        var record = typeof(Plugin).GetMethod("RecordPublicTable", BindingFlags.NonPublic | BindingFlags.Instance)!;
        void Fail(long sequence, string location) => record.Invoke(host.Entry,
            [new DiagnosticFrame(sequence, DateTimeOffset.UtcNow, "synthetic", "synthetic",
                [new AddonProbe("Emj", true, false, false, 0, [], location)])]);
        Fail(1, "publicDora.resource: BUDGET_EXCEEDED");
        Fail(2, "publicDora.resource: BUDGET_EXCEEDED");
        Fail(3, "publicStatus.textHeader: READ_FAILED");
        await journal.CompleteAsync();
        var errors = await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "errors.jsonl"));
        Assert.True(errors.IntegrityPassed);
        Assert.Equal(2, errors.Lines.Length);
        Assert.All(errors.Lines, e => Assert.Equal("PUBLIC_TABLE_READ_ERROR", e.Entry.Kind));
        Assert.Equal("publicDora.resource: BUDGET_EXCEEDED", errors.Lines[0].Entry.Data.GetProperty("Location").GetString());
        Assert.Equal(3, errors.Lines[1].Entry.Data.GetProperty("Sequence").GetInt64());
        Assert.False(host.Entry.CurrentPublicTable!.Stable);
    }

    [Fact]
    public void Standard_runtime_runs_without_lease_or_models_and_never_calls_beta_factory()
    {
        using var host = new Host();
        int betaFactories = 0;
        using var runtime = new RuntimeModeLifecycleTests.Host(
            inputGate: () => host.Entry.GameOperationsAuthorized,
            policyFactory: () => host.Entry.CreateDecisionPolicy(() => { betaFactories++; throw new Exception("Beta must stay cold"); }));
        // Retained backend preference does not change the effective startup source.
        Host.Set(host.Entry, "<MortalSelected>k__BackingField", true);
        runtime.Runtime.SetMode(Mahjong.Plugin.Dalamud.PlayMode.Manual);
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Manual, runtime.Runtime.Mode);
        var policy = Assert.IsType<EfficiencyPolicy>(runtime.Runtime.Policy);
        var hand = new[] { 0, 1, 2, 3, 4, 5, 9, 10, 11, 18, 19, 20, 27, 33 }.Select(Mahjong.Core.Tile.FromId).ToArray();
        var state = Mahjong.Core.StateSnapshot.Empty with { Hand = hand,
            Legal = new(Mahjong.Core.ActionFlags.Discard, hand, [], [], []) };
        var choice = policy.Choose(state);
        Assert.Equal(Mahjong.Policy.Abstractions.ActionKind.Discard, choice.Kind);
        Assert.Contains(choice.DiscardTile!.Value, hand);
        Assert.Null(runtime.Runtime.AutoPlay);
        Assert.False(runtime.Runtime.CanOperate);
        Assert.Equal(0, betaFactories);
        Assert.Null(Host.Get<object?>(host.Entry, "mortalSession"));
        Assert.Null(Host.Get<object?>(host.Entry, "aiProbe"));
        Assert.False(host.Entry.TestAccessUnlocked);
        Assert.Equal("标准求解器", host.Entry.DecisionSourceLabel);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Validation_success_or_failure_never_stops_standard_runtime_or_switches_source(bool valid)
    {
        using var host = new Host();
        using var runtime = new RuntimeModeLifecycleTests.Host(policyFactory: host.Entry.CreateDecisionPolicy);
        runtime.Runtime.SetMode(Mahjong.Plugin.Dalamud.PlayMode.Manual);
        Host.Set(host.Entry, "<PlayRuntime>k__BackingField", runtime.Runtime);
        Host.Set(host.Entry, "gameplayAllowed", true);
        host.Entry.VerifyTestCode(valid ? Host.Code : "wrong-synthetic-value");
        Assert.Equal(valid, host.Entry.TestAccessUnlocked);
        Assert.True(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Manual, runtime.Runtime.Mode);
        Assert.IsType<EfficiencyPolicy>(runtime.Runtime.Policy);
        Assert.False(host.Entry.ExperimentalHandAiEnabled);
        Assert.Empty(host.Queued);
    }

    [Fact]
    public void Direct_unverified_beta_factory_import_and_probe_are_rejected_without_stopping_standard()
    {
        using var host = new Host();
        Host.Set(host.Entry, "gameplayAllowed", true);
        host.Entry.ImportEngine("must-not-open.zip");
        host.Entry.CheckEngineDirectory("must-not-open", true);
        host.Entry.StartAiProbe("must-not-open");
        host.Entry.SetExperimentalHandAiEnabled(true);
        Assert.True(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.False(host.Entry.EngineMaintenanceBusy);
        Assert.False(host.Entry.ExperimentalHandAiEnabled);
        Assert.Empty(host.Queued);
        Host.Set(host.Entry, "experimentalHandAiEnabled", 1);
        int calls = 0;
        Assert.Throws<InvalidOperationException>(() => host.Entry.CreateDecisionPolicy(() => { calls++; return null!; }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Beta_runtime_without_a_qualified_task_receipt_pauses_on_expiry_and_keeps_selection()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.SetExperimentalHandAiEnabled(true);
        var task = new Mahjong.Cn.Tasks.TaskRun();
        task.Start(new(true, true, 766, "synthetic-beta", new(MatchLimit: 5), "synthetic"), "local-test", null, 0, host.Now);
        task.ObserveTable(true); task.CompleteMatch(task.MatchId!.Value);
        var id = task.RunId;
        Host.Set(host.Entry, "taskRun", task);
        Host.Set(host.Entry, "stopAlert", new GameplayStopAlert());
        using var runtime = new RuntimeModeLifecycleTests.Host(policyFactory: host.Entry.CreateDecisionPolicy);
        runtime.Runtime.SetMode(Mahjong.Plugin.Dalamud.PlayMode.Automatic);
        Host.Set(host.Entry, "<PlayRuntime>k__BackingField", runtime.Runtime);
        Host.Set(host.Entry, "gameplayAllowed", true);
        var auto = Host.Get<Mahjong.Plugin.CN.Automation.TableAutomation>(host.Entry, "tableAutomation");
        auto.Arm(new(true, true), 0);
        host.Now = host.Access.ExpiresAtUtc!.Value;
        typeof(Plugin).GetMethod("EnforceBetaAccess", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host.Entry, null);
        Assert.False(auto.Armed);
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Off, runtime.Runtime.Mode);
        Assert.False(runtime.Runtime.CanOperate);
        Assert.Equal(Mahjong.Cn.Tasks.TaskRunPhase.Paused, task.Phase);
        Assert.Equal(id, task.RunId); Assert.Equal(1, task.CompletedMatches);
        Assert.True(host.Entry.ExperimentalHandAiEnabled);
        Assert.Contains("BETA_ACCESS_EXPIRED", host.Entry.PendingStopAlert!.Reason);
        host.Entry.VerifyTestCode(Host.Code);
        Assert.Null(host.Entry.PendingStopAlert);
        Assert.True(host.Entry.ExperimentalHandAiEnabled);
        Assert.True(new SolverPreferenceStore(host.PreferencePath).PreferBeta);
        Assert.False(auto.Armed);
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.Equal(Mahjong.Cn.Tasks.TaskRunPhase.Paused, task.Phase);
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Off, runtime.Runtime.Mode);
        host.Entry.SetExperimentalHandAiEnabled(false);
        Assert.Equal(id, task.RunId); Assert.Equal(1, task.CompletedMatches);
        Assert.False(host.Entry.ExperimentalHandAiEnabled);
        Assert.False(new SolverPreferenceStore(host.PreferencePath).PreferBeta);
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Restoring_beta_choice_keeps_backend_cold_and_expiry_never_changes_the_source(bool expired)
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        var preference = new SolverPreferenceStore(host.PreferencePath);
        Assert.True(preference.Save(true));
        Host.Set(host.Entry, "solverPreference", new SolverPreferenceStore(host.PreferencePath));
        Host.Set(host.Entry, "<MortalSelected>k__BackingField", true);
        Host.Set(host.Entry, "<SelectedEngineId>k__BackingField", "synthetic-personal-model");
        Host.Set(host.Entry, "stopAlert", new GameplayStopAlert());
        if (expired) host.Now = host.Access.ExpiresAtUtc!.Value;
        typeof(Plugin).GetMethod("RestoreSolverPreference", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host.Entry, null);
        Assert.True(host.Entry.ExperimentalHandAiEnabled);
        Assert.Equal("synthetic-personal-model", host.Entry.SelectedEngineId);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.Null(Host.Get<object?>(host.Entry, "mortalSession"));
        Assert.Null(Host.Get<object?>(host.Entry, "aiProbe"));
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.False(Host.Get<Mahjong.Plugin.CN.Automation.TableAutomation>(host.Entry, "tableAutomation").Armed);
        Assert.Empty(host.Queued);
        typeof(Plugin).GetMethod("EnforceBetaAccess", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(host.Entry, null);
        if (expired)
        {
            Assert.Contains("BETA_ACCESS_EXPIRED", host.Entry.PendingStopAlert!.Reason);
            host.Entry.VerifyTestCode(Host.Code);
            Assert.Null(host.Entry.PendingStopAlert);
        }
        Assert.True(host.Entry.TestAccessUnlocked);
        Assert.True(host.Entry.ExperimentalHandAiEnabled);
        Assert.Equal("synthetic-personal-model", host.Entry.SelectedEngineId);
        Assert.Null(host.Entry.PlayRuntime); Assert.Null(Host.Get<object?>(host.Entry, "mortalSession"));
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed")); Assert.Empty(host.Queued);
    }

    [Fact]
    public void Toolbar_auto_obeys_beta_access_and_does_not_silently_start_standard()
    {
        using var host = new Host();
        Host.Set(host.Entry, "experimentalHandAiEnabled", 1);
        host.Entry.StartAutomaticFromToolbar();
        Assert.True(host.Entry.ExperimentalHandAiEnabled);
        Assert.Empty(host.Queued); Assert.Null(host.Entry.PlayRuntime);
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
    }

    [Fact]
    public void Pausing_after_toolbar_auto_cancels_the_pending_mode_change_and_keeps_task_progress()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        using var runtime = new RuntimeModeLifecycleTests.Host(policyFactory: host.Entry.CreateDecisionPolicy);
        runtime.Runtime.SetMode(Mahjong.Plugin.Dalamud.PlayMode.Manual);
        Host.Set(host.Entry, "<PlayRuntime>k__BackingField", runtime.Runtime);
        Host.Set(host.Entry, "gameplayAllowed", true);
        var run = new Mahjong.Cn.Tasks.TaskRun();
        run.Start(new(false, true, 766, "synthetic", new(MatchLimit: 5), "synthetic"), "local-test", null, 0, host.Now);
        run.ObserveTable(true); run.CompleteMatch(run.MatchId!.Value);
        Host.Set(host.Entry, "taskRun", run); var id = run.RunId;
        host.Entry.StartAutomaticFromToolbar();
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Off, runtime.Runtime.Mode);
        host.Entry.PausePlay(); host.Drain();
        Assert.Equal(Mahjong.Cn.Tasks.TaskRunPhase.Paused, run.Phase);
        Assert.Equal(id, run.RunId); Assert.Equal(1, run.CompletedMatches);
        Assert.Equal(5, run.Plan!.Rules.MatchLimit); Assert.False(run.Plan.Automatic);
        Assert.False(Host.Get<bool>(host.Entry, "gameplayAllowed"));
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Off, runtime.Runtime.Mode);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Qualification_opens_all_capabilities_without_starting_operations_or_backend(bool valid)
    {
        using var host = new Host();
        host.Entry.VerifyTestCode(valid ? Host.Code : "synthetic-invalid");
        Assert.Empty(host.Queued);
        Assert.Equal(valid, host.Entry.GameOperationsAvailable);
        Assert.False(host.Entry.GameOperationsAuthorized);
        Assert.False(host.Entry.ExperimentalHandAiEnabled);
        Assert.False(host.Entry.RatingRefreshBusy);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.Null(Host.Get<object?>(host.Entry, "mortalSession"));
    }

    [Fact]
    public void Selecting_beta_solver_requires_no_separate_enable_and_keeps_backend_cold()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.SetExperimentalHandAiEnabled(true);
        Assert.True(host.Entry.GameOperationsAvailable);
        Assert.False(host.Entry.GameOperationsAuthorized);
        Assert.Empty(host.Queued);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.Null(Host.Get<object?>(host.Entry, "mortalSession"));
        Assert.False(host.Entry.TableAutomationArmed);
        Assert.False(host.Entry.RatingRefreshBusy);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Expiry_and_renewal_never_revive_queued_start(bool queue)
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        if(queue)host.Entry.ArmTableAutomation(); else host.Entry.ActivatePlay(true);
        Assert.Single(host.Queued);
        host.Now = host.Access.ExpiresAtUtc!.Value;
        host.Entry.VerifyTestCode(Host.Code); // Enforces expiry before granting the new lease.
        Assert.True(host.Entry.GameOperationsAvailable);
        host.Drain(); // Stale requests must return before client/resource access.
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.GameOperationsAuthorized);
        Assert.False(host.Entry.TableAutomationArmed);
    }

    [Fact]
    public void Paused_ui_intent_cannot_create_new_task_authority_while_qualified()
    {
        using var host=new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        host.Entry.DispatchUi(()=>host.Entry.ActivatePlay(true));
        host.Entry.PausePlay();
        host.Entry.VerifyTestCode(Host.Code);
        typeof(Plugin).GetMethod("DrainUiIntents",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(host.Entry,null);
        Assert.Single(host.Queued); // Only the already requested pause remains.
        host.Drain();
        Assert.Empty(host.Queued);Assert.False(host.Entry.GameOperationsAuthorized);
    }

    [Theory]
    [InlineData("auto")] [InlineData("toolbar")] [InlineData("queue")]
    public void One_normal_start_after_qualification_needs_no_extra_enable_or_authorize_button(string entry)
    {
        using var host = new Host();
        host.Entry.VerifyTestCode(Host.Code);
        if (entry == "auto") host.Entry.ActivatePlay(true);
        else if (entry == "toolbar") host.Entry.StartAutomaticFromToolbar();
        else host.Entry.ArmTableAutomation();
        Assert.Single(host.Queued);
        Assert.True(host.Entry.GameOperationsAvailable);
        Assert.False(host.Entry.GameOperationsAuthorized); // The normal framework preflight still must pass.
        host.Entry.PausePlay(); host.Drain();
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.TableAutomationArmed);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Unqualified_or_expired_access_blocks_all_operation_entry_points(bool expired)
    {
        using var host = new Host();
        if (expired) { host.Entry.VerifyTestCode(Host.Code); host.Now = host.Access.ExpiresAtUtc!.Value; }
        host.Entry.ActivatePlay(true);
        host.Entry.StartAutomaticFromToolbar();
        host.Entry.ArmTableAutomation();
        host.Entry.ReadOwnRatingWithNavigation();
        host.Entry.SetExperimentalHandAiEnabled(true);
        Assert.Empty(host.Queued);
        Assert.False(host.Entry.GameOperationsAvailable);
        Assert.False(host.Entry.GameOperationsAuthorized);
        Assert.False(host.Entry.ExperimentalHandAiEnabled);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.Null(Host.Get<object?>(host.Entry, "mortalSession"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Reloaded_lease_directly_restores_availability_but_not_running_tasks(bool expired)
    {
        using var host = new Host();
        host.Entry.VerifyTestCode(Host.Code);
        if (expired) host.Now = host.Access.ExpiresAtUtc!.Value;
        var reloaded = new TestCodeAccess(host.AccessPath, () => host.Now,
            SHA256.HashData(Encoding.UTF8.GetBytes(Host.Code)));
        Host.Set(host.Entry, "testAccess", reloaded);
        Assert.Equal(!expired, host.Entry.GameOperationsAvailable);
        Assert.False(host.Entry.GameOperationsAuthorized);
        Assert.Empty(host.Queued);
        Assert.Null(host.Entry.PlayRuntime);
        Assert.False(host.Entry.TableAutomationArmed);
    }

    [Fact]
    public void Direct_runtime_automatic_request_cannot_bypass_production_operation_gate()
    {
        using var host = new Host();
        int calls=0;
        using var runtime = new RuntimeModeLifecycleTests.Host(inputGate: () => host.Entry.GameOperationsAuthorized,
            policyFactory: () => host.Entry.CreateDecisionPolicy(() => {calls++;throw new Exception("Must stay cold");}));
        runtime.Runtime.SetMode(Mahjong.Plugin.Dalamud.PlayMode.Automatic);
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Off,runtime.Runtime.Mode);
        Assert.False(runtime.Runtime.CanOperate);
        Assert.Null(runtime.Runtime.AutoPlay);
        Assert.Equal(0,calls);
        Assert.All(runtime.Saved,c => Assert.False(c.AutomationArmed));
    }

    [Theory]
    [InlineData(false,1)] [InlineData(false,0)] [InlineData(true,1)] [InlineData(true,0)]
    public void Qualified_running_task_survives_expiry_including_unlimited_loops(bool beta, int limit)
    {
        using var host = new Host();
        host.AuthorizeSyntheticTask(beta, continuous:true, matchLimit:limit);
        Host.Set(host.Entry,"stopAlert",new GameplayStopAlert());
        int betaCalls = 0;
        using var runtime = new RuntimeModeLifecycleTests.Host(inputGate: () => host.Entry.GameOperationsAuthorized,
            policyFactory: () => host.Entry.CreateDecisionPolicy(() => { betaCalls++; return new EfficiencyPolicy(new Mahjong.Rules.Rulesets.DomanRuleSet()); }));
        runtime.Runtime.SetMode(Mahjong.Plugin.Dalamud.PlayMode.Automatic);
        Host.Set(host.Entry,"<PlayRuntime>k__BackingField",runtime.Runtime);
        Host.Set(host.Entry,"gameplayAllowed",true);
        var coordinator=Host.Get<Mahjong.Plugin.CN.Automation.TableAutomation>(host.Entry,"tableAutomation");
        coordinator.Arm(new(true,true,MatchLimit:limit),0);
        Assert.True(host.Entry.GameOperationsAuthorized);
        var id=host.Entry.TaskRun.RunId;
        host.Now=host.Access.ExpiresAtUtc!.Value;
        Assert.False(host.Entry.TestAccessUnlocked);
        Assert.True(host.Entry.GameOperationsAuthorized);
        Host.Enforce(host.Entry);
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Automatic,runtime.Runtime.Mode);
        Assert.True(coordinator.Armed);
        Assert.Null(host.Entry.PendingStopAlert);
        Assert.True(host.Entry.SelectedSourceAccessValid);
        host.Entry.ArmTableAutomation(); // Cannot replace the retained task with another start.
        Assert.Empty(host.Queued);Assert.True(coordinator.Armed);
        // Policies constructed for later hands also inherit only this original task.
        host.Entry.CreateDecisionPolicy(() => { betaCalls++; return new EfficiencyPolicy(new Mahjong.Rules.Rulesets.DomanRuleSet()); });
        Assert.Equal(beta ? 2 : 0,betaCalls);
        host.Entry.VerifyTestCode(Host.Code);
        Assert.Equal(id,host.Entry.TaskRun.RunId);
        Assert.True(host.Entry.GameOperationsAuthorized); // Renewal cannot interrupt a retained task.
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Automatic,runtime.Runtime.Mode);
        host.Entry.PausePlay();host.Drain();
        Assert.False(host.Entry.GameOperationsAuthorized);
        Assert.True(host.Entry.QualifiedTaskContinues);
        Assert.False(coordinator.Armed);
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Off,runtime.Runtime.Mode);
        Assert.Empty(host.Queued);
    }

    [Fact]
    public void Expired_original_task_can_resume_but_cannot_start_new_task_or_replay_paused_work()
    {
        using var host = new Host(); host.AuthorizeSyntheticTask();
        host.Now = host.Access.ExpiresAtUtc!.Value;
        host.Entry.PausePlay(); host.Drain();
        Assert.True(host.Entry.QualifiedTaskContinues);
        Assert.False(host.Entry.GameOperationsAuthorized);
        host.Entry.ResumeTask();
        Assert.Single(host.Queued); // No extra enable/verification step for the same paused task.
        host.Entry.PausePlay(); host.Drain();
        host.Entry.EndTask(); host.Drain();
        Assert.False(host.Entry.QualifiedTaskContinues);
        host.Entry.StartAutomaticFromToolbar();host.Entry.ArmTableAutomation();
        Assert.Empty(host.Queued);Assert.False(host.Entry.GameOperationsAuthorized);
    }

    [Fact]
    public void Expired_running_task_keeps_quick_recovery_but_ending_it_revokes_it()
    {
        using var host = new Host(); host.AuthorizeSyntheticTask();
        var retry = new Automation.TransientRecoveryGate();
        Host.Set(host.Entry,"transientRecovery",retry);
        Assert.True(retry.Begin("AUTO_SNAPSHOT_UNAVAILABLE","original",0));
        host.Now = host.Access.ExpiresAtUtc!.Value;
        Host.Enforce(host.Entry);
        Assert.True(retry.Pending);Assert.True(host.Entry.GameOperationsAuthorized);
        host.Entry.EndTask();
        Assert.False(retry.Pending);Assert.False(host.Entry.GameOperationsAuthorized);
    }

    [Fact]
    public void Retained_task_is_only_an_expiry_exception_not_a_clock_rollback_bypass()
    {
        using var host = new Host();host.AuthorizeSyntheticTask();
        host.Now = host.Now.AddDays(-1);
        Assert.False(host.Entry.QualifiedTaskContinues);
        Assert.False(host.Entry.GameOperationsAuthorized);
    }

    [Fact]
    public void Expired_pending_window_operation_does_not_stop_standard_hints()
    {
        using var host = new Host();
        Assert.True(host.Access.TryUnlock(Host.Code));
        using var runtime = new RuntimeModeLifecycleTests.Host(inputGate: () => host.Entry.GameOperationsAuthorized);
        runtime.Runtime.SetMode(Mahjong.Plugin.Dalamud.PlayMode.Manual);
        Host.Set(host.Entry,"<PlayRuntime>k__BackingField",runtime.Runtime);
        Host.Set(host.Entry,"gameplayAllowed",true);
        host.Now=host.Access.ExpiresAtUtc!.Value;
        // Cancellation only forgets ownership; it cannot invoke a game operation.
        Host.Set(host.Entry,"ratingRefresh",new Mahjong.Plugin.CN.Readers.RatingRefresh(new InertProfile(),()=>host.Entry.GameOperationsAvailable));
        var pending=Host.Get<Mahjong.Plugin.CN.Readers.RatingRefresh>(host.Entry,"ratingRefresh");
        pending.Request("synthetic",0,true);
        Host.Enforce(host.Entry);
        Assert.False(pending.Busy);
        Assert.Equal(Mahjong.Plugin.Dalamud.PlayMode.Manual,runtime.Runtime.Mode);
        Assert.True(Host.Get<bool>(host.Entry,"gameplayAllowed"));
        Assert.Null(host.Entry.PendingStopAlert);
    }

    private sealed class InertProfile : Mahjong.Plugin.CN.Readers.IRatingProfileAccess
    {
        public bool IsOpen=>false;
        public bool IsMahjongSelected=>false;
        public bool Open()=>throw new InvalidOperationException();
        public bool SelectMahjong()=>throw new InvalidOperationException();
        public void CloseOwned()=>throw new InvalidOperationException();
        public void ForgetOwnership() { }
    }

    private sealed class Host : IDisposable
    {
        internal const string Code = "synthetic-entry-test-code";
        private readonly string directory = Path.Combine(Path.GetTempPath(), "mjcn-access-entry-" + Guid.NewGuid().ToString("N"));
        private readonly object? oldFramework;
        private readonly object? oldDataManager;
        private readonly object? oldClient;
        private readonly object? oldPlayerState;
        internal DateTimeOffset Now = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
        internal Plugin Entry { get; }
        internal TestCodeAccess Access { get; }
        internal Queue<Action> Queued { get; } = new();
        internal string LogTestDirectory => Path.Combine(directory, "logs");
        internal string PreferencePath => Path.Combine(directory, "solver-preference.json");
        internal string AccessPath => Path.Combine(directory, "test-access.json");

        internal Host()
        {
            oldClient=typeof(Plugin).GetField("<Client>k__BackingField",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null);
            oldPlayerState=typeof(Plugin).GetField("<PlayerState>k__BackingField",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null);
            Access = new(Path.Combine(directory, "test-access.json"), () => Now,
                SHA256.HashData(Encoding.UTF8.GetBytes(Code)));
            Entry = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            // This host intentionally has no Dalamud plugin-interface service. Supply
            // a private engine selection as the normal constructor/settings loader does.
            Set(Entry, "selectedEngineDirectory", Path.Combine(directory, "engines", "akochan-global-v5"));
            Set(Entry, "engineMaintenanceCancellation", new CancellationTokenSource());
            Set(Entry, "gate", new object());
            Set(Entry, "uiIntents", new System.Collections.Concurrent.ConcurrentQueue<(int,Action)>());
            Set(Entry, "tableAutomation", new Mahjong.Plugin.CN.Automation.TableAutomation());
            Set(Entry, "<AutomationOptions>k__BackingField", new Mahjong.Plugin.CN.Automation.TableAutomationOptions());
            Set(Entry, "testAccess", Access);
            Set(Entry, "pendingOperationRequest", -1);
            Set(Entry, "solverPreference", new SolverPreferenceStore(PreferencePath));
            Set(Entry, "<Session>k__BackingField", new CnSession(new("test", 15), "test", 15));
            Set(Entry, "<Actions>k__BackingField", new DisabledActionAdapter());
            Set(Entry, "lowerHandTracker", new LowerHandTracker());
            var field = typeof(Plugin).GetField("<Framework>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!;
            oldFramework = field.GetValue(null);
            field.SetValue(null, RuntimeModeLifecycleTests.ServiceProxy.Create<IFramework>((method, args) =>
            {
                if (method.Name == "get_IsInFrameworkUpdateThread") return true;
                if (method.Name == "RunOnFrameworkThread")
                {
                    Queued.Enqueue((Action)args![0]!);
                    return Task.CompletedTask;
                }
                throw new InvalidOperationException("Unexpected host access: " + method.Name);
            }));
            var dataField = typeof(Plugin).GetField("<DataManager>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!;
            oldDataManager = dataField.GetValue(null);
            dataField.SetValue(null, RuntimeModeLifecycleTests.ServiceProxy.Create<IDataManager>((method, _) =>
            {
                if (method.Name == "GetFile") return null; // Synthetic missing ULD; no client files are opened.
                throw new InvalidOperationException("Unexpected resource service access: " + method.Name);
            }));
        }

        internal static void Set(object instance, string name, object value) =>
            instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(instance, value);

        internal static T Get<T>(object instance, string name) =>
            (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;

        internal void Drain() { while (Queued.TryDequeue(out var callback)) callback(); }

        internal static void Enforce(Plugin entry)=>typeof(Plugin).GetMethod("EnforceBetaAccess",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(entry,null);
        internal void AuthorizeSyntheticTask(bool beta = false, bool continuous = false, int matchLimit = 1)
        {
            Assert.True(Access.TryUnlock(Code));
            if (beta) Entry.SetExperimentalHandAiEnabled(true);
            typeof(Plugin).GetField("<Client>k__BackingField",BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null,
                RuntimeModeLifecycleTests.ServiceProxy.Create<IClientState>((m,_)=>m.Name=="get_IsLoggedIn"?true:throw new InvalidOperationException(m.Name)));
            typeof(Plugin).GetField("<PlayerState>k__BackingField",BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null,
                RuntimeModeLifecycleTests.ServiceProxy.Create<IPlayerState>((m,_)=>m.Name switch {
                    "get_IsLoaded"=>true,"get_ContentId"=>1UL,_=>throw new InvalidOperationException(m.Name)}));
            Set(Entry,"ratingContentId",1UL);Set(Entry,"ratingContext","synthetic");
            var run=new Mahjong.Cn.Tasks.TaskRun();
            run.Start(new(true,continuous,continuous?766u:0u,Entry.DecisionSourceLabel+":"+(beta?Entry.SelectedEngineId:"upstream"),new(MatchLimit:matchLimit),"synthetic"),"synthetic",null,0,Now);
            Set(Entry,"taskRun",run);
            typeof(Plugin).GetMethod("PreserveQualifiedTaskAccess",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(Entry,null);
            Assert.True((bool)typeof(Plugin).GetMethod("GrantTaskOperations",BindingFlags.NonPublic|BindingFlags.Instance)!
                .Invoke(Entry,[Get<int>(Entry,"operationGeneration")])!);
        }

        public void Dispose()
        {
            typeof(Plugin).GetField("<Framework>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, oldFramework);
            typeof(Plugin).GetField("<DataManager>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, oldDataManager);
            typeof(Plugin).GetField("<Client>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, oldClient);
            typeof(Plugin).GetField("<PlayerState>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, oldPlayerState);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
