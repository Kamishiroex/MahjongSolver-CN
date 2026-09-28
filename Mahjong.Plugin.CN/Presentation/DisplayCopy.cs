namespace Mahjong.Plugin.CN.Presentation;

/// <summary>Presentation only. Exact provenance and errors remain in local technical records.</summary>
internal static class DisplayCopy
{
    internal static string Source(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source == "未知") return "未知";
        if (source.Contains("upstream", StringComparison.OrdinalIgnoreCase) || source.Contains("上游") || source == "标准求解器") return "标准求解器";
        if (source.Contains("Mortal", StringComparison.OrdinalIgnoreCase) || source.Contains("akochan", StringComparison.OrdinalIgnoreCase) || source.Contains("AI") || source.Contains("测试版")) return "测试版";
        return "来源待核对";
    }
    internal static string Summary(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "等待状态更新。";
        if (text.Contains("AUTO_RUNTIME_GATE_CLOSED", StringComparison.Ordinal)) return "自动操作权限或任务状态已改变，已暂停。点击继续会重新核对同一任务。";
        if (text.Contains("GLOBAL_AI_TIMEOUT", StringComparison.Ordinal) || text.Contains("PROCESS_Timeout", StringComparison.Ordinal)) return "本次计算超时，旧结果已作废。恢复时重新读取当前牌桌并计算。";
        if (text.Contains("SNAPSHOT_UNAVAILABLE", StringComparison.Ordinal) || text.Contains("STATE_INCONSISTENT", StringComparison.Ordinal)) return "当前牌面缺失或读数矛盾，已暂停操作并等待重新核对。";
        if (text.Contains("NEXT_HAND_TIMEOUT", StringComparison.Ordinal)) return "等待下一局超时；请查看游戏是否仍在等待其他玩家确认。";
        if (text.Contains("BETA_ACCESS", StringComparison.Ordinal)) return "测试版验证失效，已暂停。请在设置 → 测试版续期，原模型选择已保留。";
        if (text.Contains("AKOCHAN_PENDING", StringComparison.Ordinal)) return "测试版等待计算或当前操作窗口。";
        if (text.Contains("AKOCHAN_BLOCKED", StringComparison.Ordinal)) return "测试版已暂停；请查看本地技术详情。";
        if (text.Contains("Mortal", StringComparison.OrdinalIgnoreCase) || text.Contains("akochan", StringComparison.OrdinalIgnoreCase)) return "测试版状态已更新；具体原因见本地技术详情。";
        return text.Replace("全局 AI", "测试版").Replace("实验 AI", "测试版").Replace("上游牌效", "标准求解器");
    }
}
