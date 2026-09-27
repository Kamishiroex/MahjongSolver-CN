using Mahjong.Plugin.Dalamud;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>Acknowledgement only dismisses the message; it never rearms game input.</summary>
internal sealed class GameplayStopAlert
{
    private GameplayStopInfo? pending;
    private GameplayStopInfo? last;
    internal GameplayStopInfo? Pending => Volatile.Read(ref pending);
    internal void Acknowledge() => Interlocked.Exchange(ref pending, null);

    internal bool Publish(GameplayStopInfo stop, bool disposed, params Action[] notices)
    {
        bool betaAccessRevoked = stop.Reason.StartsWith("BETA_ACCESS_EXPIRED", StringComparison.Ordinal);
        if (disposed || (stop.PreviousMode != PlayMode.Automatic && !betaAccessRevoked) || IsIntentional(stop.Reason)) return false;
        if (last is { } previous && previous.Utc == stop.Utc && previous.Reason == stop.Reason) return false;
        last = stop;
        Volatile.Write(ref pending, stop);
        // A host notification failure must not prevent the other notices or stop journaling.
        foreach (var notice in notices)
            try { notice(); } catch { /* The persistent banner remains available. */ }
        return true;
    }

    private static bool IsIntentional(string reason) => new[]
    {
        "用户暂停", "用户停止", "正在切换模式", "决策来源已切换", "插件已卸载",
        "正在读取日志并核对", "已停止提醒与自动打牌", "已暂停；继续只读核对",
    }.Any(prefix => reason.StartsWith(prefix, StringComparison.Ordinal));
}
