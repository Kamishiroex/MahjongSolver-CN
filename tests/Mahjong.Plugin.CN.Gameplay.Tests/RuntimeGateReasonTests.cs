using Mahjong.Plugin.Dalamud;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class RuntimeGateReasonTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate_rejection_keeps_input_disabled_and_reports_exact_reason_or_diagnostic_failure(bool throws)
    {
        bool allowed = true;
        using var host = new RuntimeModeLifecycleTests.Host(identityGate: () => allowed,
            identityError: () => throws ? throw new IOException() : "CLIENT_NOT_LOGGED_IN：客户端已退出登录");
        host.Runtime.SetMode(PlayMode.Automatic);
        var stops = new List<GameplayStopInfo>();
        host.Runtime.Stopped += stops.Add;
        allowed = false;
        Assert.False(host.Runtime.RefreshIdentityGate());
        Assert.False(host.Runtime.CanOperate);
        Assert.Equal(PlayMode.Off, host.Runtime.Mode);
        Assert.StartsWith(throws ? "RUNTIME_GATE_DIAGNOSTIC_FAILED：IOException" : "CLIENT_NOT_LOGGED_IN：",
            Assert.Single(stops).Reason);
    }
}
