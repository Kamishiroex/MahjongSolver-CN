using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.Dalamud;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class GameplayStopAlertTests
{
    private static GameplayStopInfo Stop(string reason, PlayMode mode = PlayMode.Automatic) =>
        new(DateTimeOffset.UtcNow, reason, mode, null, null, null, null, null, null, null);

    [Fact]
    public void Unexpected_stop_opens_notifies_and_sounds_once_even_if_one_sink_fails()
    {
        var alert = new GameplayStopAlert();
        int opens = 0, sounds = 0;
        var stop = Stop("AUTO_SNAPSHOT_UNAVAILABLE: test");
        Assert.True(alert.Publish(stop, false, () => opens++, () => throw new IOException(), () => sounds++));
        Assert.Same(stop, alert.Pending);
        Assert.False(alert.Publish(stop, false, () => opens++));
        Assert.Equal(1, opens);
        Assert.Equal(1, sounds);
        alert.Acknowledge();
        Assert.Null(alert.Pending);
        Assert.False(alert.Publish(stop, false));
        Assert.True(alert.Publish(stop with { Utc = stop.Utc.AddSeconds(10) }, false, () => opens++));
        Assert.Equal(2, opens);
    }

    [Theory]
    [InlineData("用户暂停打牌")]
    [InlineData("用户停止。")]
    [InlineData("正在切换模式，旧排队操作已取消。")]
    [InlineData("决策来源已切换为实验全局 AI")]
    [InlineData("插件已卸载；提醒和自动打牌已停止。")]
    [InlineData("正在读取日志并核对当前公开桌面；操作关闭。")]
    [InlineData("已停止提醒与自动打牌；继续只读记牌")]
    [InlineData("已暂停；继续只读核对当前牌桌。")]
    public void Intentional_changes_do_not_raise_an_error_alarm(string reason)
    {
        var alert = new GameplayStopAlert();
        Assert.False(alert.Publish(Stop(reason), false, () => Assert.Fail("unexpected alarm")));
        Assert.Null(alert.Pending);
    }

    [Fact]
    public void No_alarm_after_disposal_or_for_inactive_manual_modes()
    {
        var alert = new GameplayStopAlert();
        Assert.False(alert.Publish(Stop("error"), true));
        Assert.False(alert.Publish(Stop("error", PlayMode.Manual), false));
        Assert.False(alert.Publish(Stop("error", PlayMode.Off), false));
        Assert.Null(alert.Pending);
    }

    [Fact]
    public void Runtime_pause_is_visible_once_and_acknowledgement_does_not_resume_inputs()
    {
        using var host = new RuntimeModeLifecycleTests.Host();
        var alert = new GameplayStopAlert();
        int notices = 0;
        host.Runtime.Stopped += stop => alert.Publish(stop, false, () => notices++);
        host.Runtime.SetMode(PlayMode.Automatic);
        host.Runtime.PauseAutomation("AUTO_NEXT_HAND_TIMEOUT: test");
        host.Runtime.StopAutomation("redundant cleanup");
        Assert.Equal(1, notices);
        Assert.StartsWith("AUTO_NEXT_HAND_TIMEOUT:", alert.Pending!.Reason);
        alert.Acknowledge();
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.False(host.Runtime.CanOperate);
    }

    [Theory]
    [InlineData(PlayMode.Off)]
    [InlineData(PlayMode.Manual)]
    internal void Beta_expiry_alert_preserves_actual_mode_for_queue_only_or_manual_tasks(PlayMode mode)
    {
        var alert = new GameplayStopAlert();
        var stop = Stop("BETA_ACCESS_EXPIRED: synthetic expiry", mode);
        Assert.True(alert.Publish(stop, false));
        Assert.Equal(mode, alert.Pending!.PreviousMode);
        Assert.Same(stop, alert.Pending);
        alert.Acknowledge();
        Assert.Null(alert.Pending);
    }
}
