using System.Runtime.InteropServices;
using Dalamud.Interface.ImGuiNotification;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.Dalamud;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private readonly GameplayStopAlert stopAlert = new();
    internal GameplayStopInfo? PendingStopAlert => stopAlert?.Pending;
    internal void AcknowledgeStopAlert() => stopAlert?.Acknowledge();

    private void AlertUnexpectedStop(GameplayStopInfo stop)
    {
        Status = stop.Reason;
        stopAlert?.Publish(stop, disposed,
            Open,
            () => Notifications.AddNotification(new Notification
            {
                Title = Brand.ProductName + (stop.Reason.StartsWith("BETA_ACCESS_EXPIRED", StringComparison.Ordinal)
                    ? "：测试版已暂停，请接管！" : "：自动打牌已停止，请接管！"),
                MinimizedText = Brand.ProductName + "：功能已暂停",
                Content = Presentation.DisplayCopy.Summary(stop.Reason) + "\n请处理当前牌桌；确认提示不会自动恢复打牌。",
                Type = NotificationType.Error,
                Minimized = false,
                RespectUiHidden = false,
                InitialDuration = TimeSpan.FromSeconds(30),
            }),
            () => MessageBeep(0x30));
    }

    // OS-local alert only: no game callback and no chat message is sent.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);
}
