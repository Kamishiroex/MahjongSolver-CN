using System.Reflection;
using System.Text.Json;
using Mahjong.Core;
using Mahjong.Plugin.Dalamud;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Game;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class NextHandTransitionTests
{
    [Theory]
    [InlineData(27)]
    [InlineData(35)]
    public void Continuous_wait_survives_minutes_then_resumes_without_rearming(int state)
    {
        using var host = new RuntimeModeLifecycleTests.Host();
        host.Runtime.KeepAutomaticBetweenHandsPreference = true;
        host.Runtime.SetMode(PlayMode.Automatic);
        var loop = host.Runtime.AutoPlay!;
        var stops = new List<GameplayStopInfo>();
        host.Runtime.Stopped += stops.Add;
        ObserveResult(loop);
        var now = DateTime.UtcNow;
        Assert.True(loop.TryWaitForNextHand(state, null, now));
        Assert.True(loop.TryWaitForNextHand(state, null, now.AddMinutes(5)));
        Assert.Equal(PlayMode.Automatic, host.Runtime.Mode);
        Assert.False(loop.TryWaitForNextHand(NewHand.AddonStateCode, NewHand, now.AddMinutes(6)));
        Assert.Empty(stops);
        Assert.False(loop.TryWaitForNextHand(state, null, now.AddMinutes(7))); // Boundary consumed.
        ObserveResult(loop);
        Assert.True(loop.TryWaitForNextHand(state, null, now.AddMinutes(8)));
        host.Runtime.PauseAutomation("user pause");
        Assert.False(loop.TryWaitForNextHand(NewHand.AddonStateCode, NewHand, now.AddMinutes(9)));
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
    }

    [Fact]
    public void Continuous_wait_does_not_hide_unknown_state_or_read_failure()
    {
        using var host = new RuntimeModeLifecycleTests.Host();
        host.Runtime.KeepAutomaticBetweenHandsPreference = true;
        host.Runtime.SetMode(PlayMode.Automatic);
        var loop = host.Runtime.AutoPlay!;
        Assert.False(loop.TryWaitForNextHand(27, null, DateTime.UtcNow));
        ObserveResult(loop);
        Assert.False(loop.TryWaitForNextHand(-1, null, DateTime.UtcNow));
        var now = DateTime.UtcNow;
        Assert.True(loop.TryWaitForNextHand(27, null, now));
        Assert.True(loop.TryWaitForNextHand(15, null, now.AddMinutes(2)));
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Submitted_next_result_can_wait_without_repeat_and_option_is_live(bool keep)
    {
        using var host = new RuntimeModeLifecycleTests.Host();
        host.Runtime.AutoAdvancePreference = true;
        host.Runtime.KeepAutomaticBetweenHandsPreference = keep;
        host.Runtime.SetMode(PlayMode.Automatic);
        var loop = host.Runtime.AutoPlay!;
        ObserveResult(loop);
        typeof(AutoPlayLoop).GetField("handResultDispatchedThisInstance", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(loop, true);
        typeof(AutoPlayLoop).GetField("handResultSubmittedAt", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(loop, DateTime.UtcNow.AddMinutes(-5));
        ObserveResult(loop);
        Assert.Equal(keep ? PlayMode.Automatic : PlayMode.Off, host.Runtime.Mode);
        if (!keep) return;
        ObserveResult(loop); // No second native dispatch; stub has no addon.
        host.Runtime.ConfigService.Update(c => c with { KeepAutomaticBetweenHands = false });
        ObserveResult(loop);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
    }

    [Fact]
    public void Complete_hand_at_timeout_wins_over_timeout_check()
    {
        using var host = new RuntimeModeLifecycleTests.Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        var loop = host.Runtime.AutoPlay!;
        ObserveResult(loop);
        var now = DateTime.UtcNow;
        Assert.True(loop.TryWaitForNextHand(27, null, now));
        Assert.False(loop.TryWaitForNextHand(NewHand.AddonStateCode, NewHand, now.AddSeconds(31)));
        Assert.Equal(PlayMode.Automatic, host.Runtime.Mode);
    }

    [Fact]
    public void Missing_special_draw_confirmation_waits_then_alerts_once_and_pause_revokes_wait()
    {
        using var host = new RuntimeModeLifecycleTests.Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        var loop = host.Runtime.AutoPlay!;
        var stops = new List<GameplayStopInfo>();
        host.Runtime.Stopped += stops.Add;
        var field = typeof(AutoPlayLoop).GetField("kyushuSubmittedAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var now = DateTime.UtcNow;
        field.SetValue(loop, now);
        Assert.True(loop.TryWaitForKyushu(now.AddSeconds(1)));
        Assert.True(loop.TryWaitForKyushu(now.AddSeconds(29)));
        Assert.Equal(PlayMode.Automatic, host.Runtime.Mode);
        Assert.True(loop.TryWaitForKyushu(now.AddSeconds(30)));
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.StartsWith("AUTO_KYUSHU_RESULT_TIMEOUT", Assert.Single(stops).Reason);
        Assert.Null(field.GetValue(loop));
        Assert.False(loop.TryWaitForKyushu(now.AddSeconds(31)));
        host.Runtime.SetMode(PlayMode.Automatic);
        loop = host.Runtime.AutoPlay!;
        field.SetValue(loop, now);
        host.Runtime.PauseAutomation("user pause");
        Assert.False(loop.TryWaitForKyushu(now.AddSeconds(2)));
        Assert.Null(field.GetValue(loop));
    }

    [Fact]
    public void Special_draw_result_and_stop_clear_pending_confirmation()
    {
        using var host = new RuntimeModeLifecycleTests.Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        var loop = host.Runtime.AutoPlay!;
        var field = typeof(AutoPlayLoop).GetField("kyushuSubmittedAt", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(loop, DateTime.UtcNow);
        ObserveResult(loop);
        Assert.Null(field.GetValue(loop));
        Assert.True(loop.TryWaitForNextHand(35, null, DateTime.UtcNow));
        Assert.False(loop.TryWaitForNextHand(15, NewHand, DateTime.UtcNow));
        field.SetValue(loop, DateTime.UtcNow);
        loop.Stop();
        Assert.Null(field.GetValue(loop));
    }
    private static void ObserveResult(AutoPlayLoop loop) => Assert.True((bool)typeof(AutoPlayLoop)
        .GetMethod("TryHandleHandResult", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(loop, [29])!);

    private static StateSnapshot NewHand => StateSnapshot.Empty with
    {
        AddonStateCode = 15,
        Hand = Enumerable.Range(0, 13).Select(Tile.FromId).ToArray(),
    };

    [Fact]
    public void Recorded_empty_and_null_result_transition_keeps_mode_then_resumes_new_hand()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "result-transition-20260925.json")));
        using var host = new RuntimeModeLifecycleTests.Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        var loop = host.Runtime.AutoPlay!;
        ObserveResult(loop);
        var queue = (QueuedActionGate)typeof(AutoPlayLoop)
            .GetField("queuedActions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(loop)!;
        long oldToken = queue.Capture();
        var now = DateTime.UtcNow;
        int emptyState = fixture.RootElement.GetProperty("EmptyHandState").GetInt32();
        int nullState = fixture.RootElement.GetProperty("Stop").GetProperty("Menu").GetProperty("StateCode").GetInt32();
        Assert.True(loop.TryWaitForNextHand(emptyState, StateSnapshot.Empty with { AddonStateCode = emptyState }, now));
        Assert.False(queue.IsCurrent(oldToken));
        Assert.True(loop.TryWaitForNextHand(nullState, null, now.AddMilliseconds(23)));
        Assert.True(loop.TryWaitForNextHand(nullState, null, now.AddSeconds(2)));
        Assert.Equal(PlayMode.Automatic, host.Runtime.Mode);
        Assert.True(host.Runtime.CanOperate);
        Assert.False(loop.TryWaitForNextHand(NewHand.AddonStateCode, NewHand, now.AddSeconds(3)));
        // Recovery consumes the allowance. Later unrelated read failures are not hidden.
        Assert.False(loop.TryWaitForNextHand(15, null, now.AddSeconds(4)));
        ObserveResult(loop); // A consecutive hand earns a new boundary, no old timeout.
        Assert.True(loop.TryWaitForNextHand(nullState, null, now.AddMinutes(2)));
        Assert.Equal(PlayMode.Automatic, host.Runtime.Mode);
    }

    [Theory]
    [InlineData(27)]
    [InlineData(35)]
    [InlineData(15)]
    public void Read_failure_without_result_boundary_is_not_suppressed(int state)
    {
        using var host = new RuntimeModeLifecycleTests.Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        Assert.False(host.Runtime.AutoPlay!.TryWaitForNextHand(state, null, DateTime.UtcNow));
    }

    [Fact]
    public void Missing_addon_is_not_treated_as_a_next_hand_wait()
    {
        using var host = new RuntimeModeLifecycleTests.Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        ObserveResult(host.Runtime.AutoPlay!);
        Assert.False(host.Runtime.AutoPlay!.TryWaitForNextHand(-1, null, DateTime.UtcNow));
    }

    [Fact]
    public void Partial_or_inconsistent_hand_does_not_end_wait_and_timeout_stops_once()
    {
        using var host = new RuntimeModeLifecycleTests.Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        var stops = new List<GameplayStopInfo>();
        host.Runtime.Stopped += stops.Add;
        var loop = host.Runtime.AutoPlay!;
        ObserveResult(loop);
        var now = DateTime.UtcNow;
        Assert.True(loop.TryWaitForNextHand(15, NewHand with { Hand = NewHand.Hand.Take(12).ToArray() }, now));
        Assert.True(loop.TryWaitForNextHand(27, NewHand, now.AddSeconds(29)));
        Assert.True(loop.TryWaitForNextHand(27, null, now.AddSeconds(30)));
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.False(host.Runtime.CanOperate);
        Assert.StartsWith("AUTO_NEXT_HAND_TIMEOUT:", Assert.Single(stops).Reason);
    }

    [Fact]
    public void User_pause_revokes_wait_and_old_loop_cannot_resume_mode()
    {
        using var host = new RuntimeModeLifecycleTests.Host();
        host.Runtime.SetMode(PlayMode.Automatic);
        var loop = host.Runtime.AutoPlay!;
        ObserveResult(loop);
        Assert.True(loop.TryWaitForNextHand(27, null, DateTime.UtcNow));
        host.Runtime.PauseAutomation("user pause");
        Assert.False(loop.TryWaitForNextHand(15, NewHand, DateTime.UtcNow));
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
    }
}
