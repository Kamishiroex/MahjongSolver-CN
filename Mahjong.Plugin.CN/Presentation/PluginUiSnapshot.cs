using System.Collections.Immutable;
using Mahjong.Core;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.Dalamud;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.CN.Presentation;

/// <summary>One managed observation for presentation, never a permit to issue game input.</summary>
internal sealed record PluginUiSnapshot(DateTimeOffset CapturedUtc, long ObservationRevision,
    PlayMode Mode, bool Paused, string Status, string Engine, string TaskStatus,
    StateSnapshot? Table, ActionChoice? LastChoice, ImmutableArray<DecodedLowerFace> Hand,
    Mahjong.Cn.Rating.RatingObservation? Rating = null,
    Mahjong.Cn.PublicState.PublicSnapshot? PublicTable = null,
    ImmutableArray<string> RecentEvents = default,
    bool RatingRefreshing = false, string RatingStatus = "")
{
    internal static PluginUiSnapshot Empty { get; } = new(default, 0, PlayMode.Off, false,
        "等待状态更新。", "标准求解器", "任务未启动。", null, null, []);
    internal string ModeLabel => Mode switch
    { PlayMode.Manual => "仅提示 · 玩家操作", PlayMode.Automatic => "测试版自动操作", _ => Paused ? "已暂停" : "未启动 / 已停止" };
}
