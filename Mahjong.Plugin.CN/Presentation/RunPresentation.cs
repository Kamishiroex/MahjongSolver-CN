using Mahjong.Cn.Tasks;
using Mahjong.Plugin.Dalamud;

namespace Mahjong.Plugin.CN.Presentation;

internal sealed record RunPresentation(string Title, string Detail, bool NeedsAttention = false)
{
    internal static RunPresentation Describe(TaskRunPhase phase, PlayMode mode, bool paused,
        bool preparing, bool recovering, string recovery, string? stop, int? addonState,
        bool tableArmed, string tableStatus, string status)
    {
        if (recovering) return new("正在恢复", recovery + " 暂停可取消恢复。");
        if (stop is not null) return new("需要接管", DisplayCopy.Summary(stop) + " 处理后点击继续；确认通知不会恢复。", true);
        if (phase is TaskRunPhase.Problem or TaskRunPhase.WaitingForData)
            return new("等待处理", DisplayCopy.Summary(status), true);
        if (phase == TaskRunPhase.Paused || paused)
            return new("已暂停", "提醒、出牌与排队均已暂停；模型留在后台，进度保留。点击继续恢复原模式。");
        if (preparing) return new("正在准备模型", "后台校验与首次推理中。自动任务会等模型就绪再报名、确认入桌或开打。");
        if (phase == TaskRunPhase.Completed) return new("本次任务已结束", "下次开始将创建新任务。");
        if (mode is PlayMode.Manual or PlayMode.Automatic)
        {
            if (addonState == 29) return new("等待下一局", "当前处于结算过渡；等待牌桌稳定，不是异常暂停。");
            if (addonState == 25) return new("鸣牌选牌中", "等待当前吃碰杠选牌窗口稳定后继续，不是异常暂停。");
            return new(mode == PlayMode.Automatic ? "自动打牌中" : "手动提示中", DisplayCopy.Summary(status));
        }
        if (tableArmed) return new("自动任务进行中", DisplayCopy.Summary(tableStatus));
        return new("尚未开始", "选择手动提示或自动打牌；关闭窗口不会暂停。");
    }
}
