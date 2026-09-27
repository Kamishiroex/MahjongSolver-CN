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
        if (text.Contains("BETA_ACCESS", StringComparison.Ordinal)) return "测试版验证失效，已暂停，请接管。";
        if (text.Contains("AKOCHAN_PENDING", StringComparison.Ordinal)) return "测试版等待计算或当前操作窗口。";
        if (text.Contains("AKOCHAN_BLOCKED", StringComparison.Ordinal)) return "测试版已暂停；请查看本地技术详情。";
        if (text.Contains("Mortal", StringComparison.OrdinalIgnoreCase) || text.Contains("akochan", StringComparison.OrdinalIgnoreCase)) return "测试版状态已更新；具体原因见本地技术详情。";
        return text.Replace("全局 AI", "测试版").Replace("实验 AI", "测试版").Replace("上游牌效", "标准求解器");
    }
}
