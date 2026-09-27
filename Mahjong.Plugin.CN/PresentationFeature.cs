using System.Collections.Concurrent;
using Mahjong.Plugin.CN.Presentation;
using Mahjong.Plugin.Dalamud;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private readonly ConcurrentQueue<(int Generation, Action Action)> uiIntents = new();
    private int uiIntentGeneration;
    private PluginUiSnapshot uiSnapshot = PluginUiSnapshot.Empty;
    internal sealed record EngineUiItem(string Id,string Name,bool Installed);
    internal EngineUiItem[] UiEngines {get;private set;}=[];
    internal string UiEngineDirectory {get;private set;}="";
    internal bool UiEngineInstalled {get;private set;}
    private double lastEngineUiPoll;
    private readonly Queue<string> recentUiEvents = new();
    private void NoteUiEvent(string text)
    {
        if(recentUiEvents is null)return;
        recentUiEvents.Enqueue($"{DateTimeOffset.Now:HH:mm:ss}  {DisplayCopy.Summary(text)}");
        while(recentUiEvents.Count>6)recentUiEvents.Dequeue();
    }
    internal PluginUiSnapshot UiSnapshot => Volatile.Read(ref uiSnapshot);
    internal void DispatchUi(Action action) { if (!disposed) uiIntents.Enqueue((Volatile.Read(ref uiIntentGeneration), action)); }
    private void RevokeUiIntents() { Interlocked.Increment(ref uiIntentGeneration); uiIntents?.Clear(); }
    private void DrainUiIntents()
    {
        for (int i = 0; i < 16 && uiIntents.TryDequeue(out var intent); i++)
            try { if (intent.Generation == Volatile.Read(ref uiIntentGeneration)) intent.Action(); }
            catch (Exception ex) { Log.Error(ex, "UI command failed"); Status = "界面操作失败：" + ex.GetType().Name; }
    }
    private void PublishUiSnapshot()
    {
        if(AutomationNow-lastEngineUiPoll>2)
        {
            lastEngineUiPoll=AutomationNow;
            UiEngineDirectory=DefaultGlobalEngineDirectory;
            UiEngineInstalled=SelectedEngineInstalled;
            UiEngines=InstalledEngines.Select(p=>new EngineUiItem(p.Id,p.Name,p.IsInstalled)).ToArray();
        }
        var runtime = PlayRuntime;
        bool active = SelectedSourceAccessValid && (runtime?.Mode!=PlayMode.Automatic || GameOperationsAuthorized) && Identity.Error is null && PendingStopAlert is null &&
            runtime?.Mode is PlayMode.Manual or PlayMode.Automatic;
        var now = DateTimeOffset.UtcNow;
        var hand = active && journalLower is { Stable: true } lower && now - journalLowerUtc < TimeSpan.FromSeconds(2)
            ? lower.Tiles : [];
        Volatile.Write(ref uiSnapshot, new(now, runtimeJournalSequence, runtime?.Mode ?? PlayMode.Off,
            runtime?.IsObservingPaused == true, runtime?.Status ?? Status, DecisionSourceLabel,
            TaskSummary, active ? runtime?.ActiveAggregator?.Latest : null,
            active && !hand.IsDefaultOrEmpty ? runtime?.ActiveAggregator?.LastChoice : null, hand, CurrentRating,
            active && CurrentJournalPublicSnapshot?.Observation is { } observation && now-observation.ObservedAtUtc<TimeSpan.FromSeconds(2)
                ? CurrentJournalPublicSnapshot : null,
            System.Collections.Immutable.ImmutableArray.CreateRange(recentUiEvents), RatingRefreshBusy, RatingRefreshStatus));
    }
}
