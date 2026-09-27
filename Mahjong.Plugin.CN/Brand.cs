namespace Mahjong.Plugin.CN;

/// <summary>Display copy only. Installation, configuration and window IDs are unchanged.</summary>
internal static class Brand
{
    internal const string ProductName = "MahjongSolver";
    internal const string ProductSubtitle = "多玛方城战求解器（国服优化版）";
    internal const string ShortDescription = "默认采用上游求解器，提供国服对局辅助与任务管理。";

    // ImGui hashes the suffix after ###; preserve existing window/preset identity.
    internal const string MainWindowTitle = ProductName + "###mahjong-cn";
    internal const string ProjectUrl = "https://github.com/Kamishiroex/MahjongSolver-CN";
    internal const string UpstreamUrl = "https://github.com/XeldarAlz/FFXIV-AutoMahjongSolver";
}
