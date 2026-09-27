using Mahjong.Plugin.CN.Automation;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class TableAutomationTests
{
    [Fact]
    public void Busy_table_must_be_continuously_ready_before_autostart()
    {
        var c = Start(false, true);
        var table = Idle with { TableVisible = true };
        c.Tick(1, table);
        Assert.Contains("等待", c.Status);
        Assert.Equal(TableAutomationAction.None, c.Tick(20, table with { Busy = true }));
        Assert.Contains("加载", c.Status);
        Assert.Equal(TableAutomationAction.None, c.Tick(30, table));
        Assert.Equal(TableAutomationAction.None, c.Tick(31, table));
        Assert.Equal(TableAutomationAction.StartPlay, c.Tick(32, table));
    }

    [Fact]
    public void Old_saved_options_keep_the_previous_next_hand_default_without_arming()
    {
        var saved = System.Text.Json.JsonSerializer.Deserialize<TableAutomationOptions>(
            "{\"AutoQueue\":true,\"AutoStart\":true,\"DutyId\":766}")!;
        Assert.True(saved.AutoAdvanceAfterHand);
        Assert.Equal(0, saved.MatchLimit);
        var coordinator = new TableAutomation();
        Assert.False(coordinator.Armed);
        Assert.Equal(TableAutomationAction.None, coordinator.Tick(20, Idle));
    }
    private static TableAutomationObservation Idle => new(true, false, false, false, false, QueuePhase.None, false, false, false);
    private static TableAutomation Start(bool queue = true, bool play = true)
    { var value = new TableAutomation(); value.Arm(new(queue, play), 0); return value; }

    [Fact]
    public void Defaults_and_reload_preferences_do_not_queue()
    {
        var c = new TableAutomation();
        Assert.Equal(766u, c.Options.DutyId);
        Assert.False(c.Options.AutoQueue);
        Assert.False(c.Options.AutoStart);
        Assert.Equal(TableAutomationAction.None, c.Tick(100, Idle));
    }

    [Fact]
    public void One_submission_one_accept_and_one_start_then_next_table()
    {
        var c = Start();
        Assert.Equal(TableAutomationAction.None, c.Tick(1, Idle));
        Assert.Equal(TableAutomationAction.Queue, c.Tick(3, Idle));
        Assert.Equal(TableAutomationAction.None, c.Tick(4, Idle));
        var ready = Idle with { Queue = QueuePhase.Ready, QueueMatches = true, PopMatches = true, AcceptAvailable = true };
        Assert.Equal(TableAutomationAction.Accept, c.Tick(5, ready));
        Assert.Equal(TableAutomationAction.None, c.Tick(6, ready));
        var table = Idle with { TableVisible = true, InDuty = true };
        Assert.Equal(TableAutomationAction.None, c.Tick(8, table));
        Assert.Equal(TableAutomationAction.StartPlay, c.Tick(10, table));
        Assert.Equal(TableAutomationAction.None, c.Tick(20, table));
        Assert.True(c.ObserveMatchCompleted(766, 20));
        Assert.Equal(TableAutomationAction.None, c.Tick(21, Idle));
        Assert.Equal(TableAutomationAction.None, c.Tick(26, Idle));
        Assert.Equal(TableAutomationAction.Queue, c.Tick(31, Idle));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Finite_run_counts_completed_matches_once_and_never_queues_match_n_plus_one(int limit)
    {
        var c = new TableAutomation();
        c.Arm(new(true, true, MatchLimit: limit), 0);
        double now = 3;
        for (int i = 0; i < limit; i++)
        {
            Assert.Equal(TableAutomationAction.Queue, c.Tick(now, Idle));
            var table = Idle with { TableVisible = true, InDuty = true, CanLeave = true };
            c.Tick(now + 1, table);
            Assert.Equal(TableAutomationAction.StartPlay, c.Tick(now + 3, table));
            Assert.False(c.ObserveMatchCompleted(643, now + 4));
            Assert.True(c.ObserveMatchCompleted(766, now + 4));
            Assert.False(c.ObserveMatchCompleted(766, now + 5));
            Assert.Equal(i + 1, c.CompletedMatches);
            Assert.Equal(TableAutomationAction.None, c.Tick(now + 11, table));
            Assert.Equal(TableAutomationAction.LeaveCompletedMatch, c.Tick(now + 12, table));
            Assert.Equal(TableAutomationAction.None, c.Tick(now + 13, table));
            c.Tick(now + 14, Idle);
            c.Tick(now + 19, Idle);
            now += 24;
        }
        Assert.False(c.Armed);
        Assert.Equal(TableAutomationAction.None, c.Tick(now + 100, Idle));
        Assert.Equal(limit, c.CompletedMatches);
    }

    [Fact]
    public void Infinite_run_survives_many_complete_matches_and_stop_cancels_leave_and_queue()
    {
        var c = Start();
        double now = 3;
        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(TableAutomationAction.Queue, c.Tick(now, Idle));
            c.Tick(now + 1, Idle with { TableVisible = true, InDuty = true });
            Assert.True(c.ObserveMatchCompleted(766, now + 2));
            c.Tick(now + 3, Idle);
            c.Tick(now + 8, Idle);
            now += 13;
            Assert.True(c.Armed);
        }
        Assert.Equal(12, c.CompletedMatches);
        c.Disarm("user stop");
        Assert.False(c.ObserveMatchCompleted(766, now));
        Assert.Equal(TableAutomationAction.None, c.Tick(now + 100, Idle));
        c.Arm(new(true, true), now);
        Assert.Equal(0, c.CompletedMatches);
    }

    [Fact]
    public void Leaving_without_completion_does_not_count_or_requeue_and_loading_does_not_count()
    {
        var c = Start();
        Assert.False(c.ObserveMatchCompleted(766, 0));
        c.Tick(1, Idle with { TableVisible = true, InDuty = true });
        c.Tick(5, Idle with { InDuty = true });
        c.Tick(40, Idle with { TableVisible = true, InDuty = true });
        Assert.Equal(0, c.CompletedMatches);
        c.Tick(41, Idle);
        c.Tick(46, Idle);
        Assert.False(c.Armed);
        Assert.Equal(0, c.CompletedMatches);
        Assert.Equal(TableAutomationAction.None, c.Tick(60, Idle));
    }

    [Fact]
    public void Leave_requires_completion_same_duty_ready_game_and_one_submission()
    {
        var c = Start();
        var table = Idle with { TableVisible = true, InDuty = true, CanLeave = true };
        c.Tick(1, table);
        Assert.NotEqual(TableAutomationAction.LeaveCompletedMatch, c.Tick(30, table));
        Assert.True(c.ObserveMatchCompleted(766, 31));
        Assert.Equal(TableAutomationAction.None, c.Tick(40, table with { Busy = true }));
        Assert.Equal(TableAutomationAction.None, c.Tick(41, table with { DutyId = 643 }));
        Assert.Equal(TableAutomationAction.None, c.Tick(42, table with { CanLeave = false }));
        Assert.Equal(TableAutomationAction.LeaveCompletedMatch, c.Tick(43, table));
        Assert.Equal(TableAutomationAction.None, c.Tick(70, table));
        Assert.Equal(TableAutomationAction.None, c.Tick(74, table));
        Assert.False(c.Armed);
        Assert.Equal(1, c.CompletedMatches);
    }

    [Fact]
    public void Start_only_never_leaves_and_completed_table_never_restarts_play()
    {
        var c = Start(false, true);
        var table = Idle with { TableVisible = true, InDuty = true, CanLeave = true };
        c.Tick(1, table);
        Assert.True(c.ObserveMatchCompleted(766, 2));
        Assert.Equal(TableAutomationAction.None, c.Tick(100, table));
        Assert.True(c.Armed);
        c.Disarm("user stop");
        Assert.Equal(TableAutomationAction.None, c.Tick(101, table));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    public void Invalid_limits_cannot_arm(int limit)
    {
        var c = new TableAutomation();
        c.Arm(new(true, true, MatchLimit: limit), 0);
        Assert.False(c.Armed);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void Never_takes_over_an_existing_queue(int phase)
    {
        var c = Start();
        Assert.Equal(TableAutomationAction.None, c.Tick(4, Idle with { Queue = (QueuePhase)phase, QueueMatches = true, PopMatches = true, AcceptAvailable = true }));
        Assert.False(c.Armed);
    }

    [Fact]
    public void Different_pop_cancel_and_rejected_submission_never_loop()
    {
        var c = Start(); c.Tick(3, Idle);
        c.Tick(4, Idle with { Queue = QueuePhase.Ready, QueueMatches = true, PopMatches = false, AcceptAvailable = true });
        Assert.False(c.Armed);
        c = Start(); c.Tick(3, Idle); c.Tick(14, Idle);
        Assert.False(c.Armed);
        c = Start(); c.Tick(3, Idle); c.Tick(4, Idle with { Queue = QueuePhase.Queued, QueueMatches = true }); c.Tick(5, Idle);
        Assert.False(c.Armed);
    }

    [Fact]
    public void Stop_logout_error_and_unknown_duty_do_not_resume()
    {
        var c = Start(); c.Tick(3, Idle); c.Disarm("user pause");
        Assert.Equal(TableAutomationAction.None, c.Tick(20, Idle));
        Assert.Equal(TableAutomationAction.None, c.Tick(23, Idle with { TableVisible = true }));
        c = Start(); c.Tick(1, Idle with { LoggedIn = false }); Assert.False(c.Armed);
        c.Arm(new(true, true, 9999), 1); Assert.False(c.Armed);
    }

    [Fact]
    public void Loading_and_between_hand_visibility_do_not_restart_or_requeue()
    {
        var c = Start();
        c.Tick(1, Idle with { TableVisible = true });
        Assert.Equal(TableAutomationAction.StartPlay, c.Tick(3, Idle with { TableVisible = true }));
        Assert.Equal(TableAutomationAction.None, c.Tick(10, Idle with { InDuty = true }));
        Assert.Equal(TableAutomationAction.None, c.Tick(30, Idle with { TableVisible = true }));
        Assert.Equal(TableAutomationAction.None, c.Tick(40, Idle with { Busy = true }));
    }

    [Fact]
    public void Autostart_only_respects_manually_selected_mode_and_short_visibility_flicker()
    {
        var c = Start(false, true);
        Assert.Equal(TableAutomationAction.None, c.Tick(5, Idle));
        c.Tick(6, Idle with { TableVisible = true, Playing = true });
        Assert.Equal(TableAutomationAction.None, c.Tick(9, Idle with { TableVisible = true }));
        c.Tick(10, Idle);
        Assert.Equal(TableAutomationAction.None, c.Tick(11, Idle with { TableVisible = true }));
    }

    [Theory]
    [InlineData(766, 0, false, false, true)]
    [InlineData(766, 4, false, true, false)]
    [InlineData(768, 4, false, true, true)]
    [InlineData(768, 4, false, false, false)]
    [InlineData(768, 3, false, true, false)]
    [InlineData(768, 4, true, true, false)]
    public void Party_constraints(uint id, int count, bool alliance, bool leader, bool allowed)
        => Assert.Equal(allowed, MahjongDuties.PartyError(MahjongDuties.Find(id)!, count, alliance, leader) is null);
}
