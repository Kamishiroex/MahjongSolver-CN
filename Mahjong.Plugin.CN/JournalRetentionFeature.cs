using System.Text.Json;
using Dalamud.Game.Addon.Lifecycle;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    internal int JournalKeepMatches { get; private set; } = JournalRetention.DefaultMatches;
    private string journalMaintenanceStatus = "整场记录自动分开保存；历史记录默认最多 50 场 / 1 GiB。";
    internal string JournalMaintenanceStatus => journalMaintenanceStatus;
    private Task<JournalRetention.Result>? journalMaintenance;
    private bool journalMaintenanceRequested;
    private DateTime nextJournalMaintenance;
    private bool journalContinueAfterTable;
    private string JournalSettingsPath => Path.Combine(Interface.GetPluginConfigDirectory(), "journal-settings.json");

    private void LoadJournalSettings()
    {
        try
        {
            if (File.Exists(JournalSettingsPath) && new FileInfo(JournalSettingsPath).Length <= 4096)
            {
                using var json = JsonDocument.Parse(File.ReadAllText(JournalSettingsPath));
                int keep = json.RootElement.GetProperty("KeepMatches").GetInt32();
                if (keep is >= 20 and <= 500) JournalKeepMatches = keep;
            }
        }
        catch (Exception ex) { journalMaintenanceStatus = "日志设置读取失败，使用默认 50 场：" + ex.GetType().Name; }
        ScheduleJournalMaintenance();
    }

    internal void SetJournalKeepMatches(int keep)
    {
        lock (gate)
        {
            if (disposed || keep is < 20 or > 500) return;
            try
            {
                string temp = JournalSettingsPath + ".tmp";
                GameJournal.RejectLinks(temp); GameJournal.RejectLinks(JournalSettingsPath);
                File.WriteAllText(temp, JsonSerializer.Serialize(new { KeepMatches = keep }));
                GameJournal.ReplaceAtomically(temp, JournalSettingsPath);
                JournalKeepMatches = keep;
                ScheduleJournalMaintenance();
            }
            catch (Exception ex) { journalMaintenanceStatus = "日志设置保存失败：" + ex.GetType().Name; }
        }
    }

    private void ScheduleJournalMaintenance()
    {
        if (logsDirectory is null) return;
        if (journalMaintenance is not null) { journalMaintenanceRequested = true; return; }
        int keep = JournalKeepMatches;
        string? current = journalActive ? journal?.DirectoryPath : null;
        string root = logsDirectory;
        journalMaintenance = Task.Run(() => JournalRetention.Maintain(root, keep, current));
        nextJournalMaintenance = DateTime.UtcNow.AddMinutes(1);
    }

    private void PollJournalMaintenance()
    {
        if (journalMaintenance is { IsCompleted: true } done)
        {
            journalMaintenanceStatus = done.IsCompletedSuccessfully
                ? done.Result.Warning ?? $"历史记录 {done.Result.Retained} 份；本次清理 {done.Result.Removed} 份。保留最多 {JournalKeepMatches} 场 / 1 GiB，当前场和最新恢复记录除外。"
                : "日志清理失败，将后台重试：" + done.Exception?.GetBaseException().GetType().Name;
            journalMaintenance = null;
            if (journalMaintenanceRequested) { journalMaintenanceRequested = false; ScheduleJournalMaintenance(); }
        }
        if (DateTime.UtcNow >= nextJournalMaintenance && journalMaintenance is null) ScheduleJournalMaintenance();
    }

    private void OnJournalTableLifecycle(AddonEvent type, string name)
    {
        if (disposed || name != "Emj") return;
        // Main table teardown, not EmjScore (individual hand results), ends a record.
        // Explicit stop/unload clears the continuation flag separately.
        if (type == AddonEvent.PreFinalize && journalActive)
        {
            RecordJournalEvent("table_record_closed", new { Source = "Emj.PreFinalize", Reason = "table_exit" });
            journalContinueAfterTable = true;
            CompleteJournalCore("table_exit");
        }
        else if (type == AddonEvent.PostSetup && journalContinueAfterTable && Identity.Error is null)
        {
            journalContinueAfterTable = false;
            EnsureJournalCore("next_table");
        }
        if(type==AddonEvent.PostSetup) { reviewRatingPending=null; BeginReviewTable(true); }
    }
}
