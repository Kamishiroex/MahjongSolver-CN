using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Game.Addon.Lifecycle;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Journaling;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class JournalMatchBoundaryTests
{
    [Fact]
    public async Task Individual_hand_results_do_not_close_record_but_main_table_exit_does_exactly_once()
    {
        string root = Path.Combine(Path.GetTempPath(), "mjcn-boundary-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new GameJournal(root);
            journal.Event("session_started", new { Synthetic = true }); await journal.Completion;
            var plugin = (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));
            void Set(string name, object value) => typeof(Plugin).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(plugin, value);
            Set("gate", new object()); Set("journal", journal); Set("journalActive", true);
            Set("journalLowerTracker", new LowerHandTracker()); Set("journalTableTracker", new PublicTableTracker());
            var method = typeof(Plugin).GetMethod("OnJournalTableLifecycle", BindingFlags.NonPublic | BindingFlags.Instance)!;
            bool Active() => (bool)typeof(Plugin).GetField("journalActive", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(plugin)!;
            for (int i = 0; i < 8; i++)
            {
                method.Invoke(plugin, [AddonEvent.PreFinalize, "EmjScore"]);
                method.Invoke(plugin, [AddonEvent.PostSetup, "EmjScore"]);
                Assert.True(Active());
            }
            method.Invoke(plugin, [AddonEvent.PreFinalize, "Emj"]);
            method.Invoke(plugin, [AddonEvent.PreFinalize, "Emj"]);
            await journal.Completion;
            Assert.False(Active());
            var replay = await GameJournal.ReadAsync(Path.Combine(journal.DirectoryPath, "events.jsonl"));
            Assert.True(replay.IntegrityPassed, replay.Error);
            Assert.Single(replay.Lines.Where(x => x.Entry.Kind == "table_record_closed"));
            Assert.Single(replay.Lines.Where(x => x.Entry.Kind == "session_stopped"));
            Assert.True(File.Exists(Path.Combine(journal.DirectoryPath, "record-closed.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
