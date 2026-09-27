using System;
using Dalamud.Plugin.Services;
using Mahjong.Policy.Efficiency;

namespace Mahjong.Plugin.Dalamud.GameState;

public sealed class StateAggregator : IDisposable
{
    private readonly AddonEmjReader reader;
    private readonly IFramework framework;
    private readonly IPolicy? policy;
    private bool disposed;
    private long lastRebuildTicks;
    private int lastContentHash;
    private bool hasContentHash;
    private const long MinTickIntervalTicks = 160_000;

    public StateSnapshot? Latest { get; private set; }

    /// <summary>Scored discards for <see cref="Latest"/>; null off our turn or on scorer throw.</summary>
    public ScoredDiscard[]? LastScored { get; private set; }

    /// <summary>Policy verdict for <see cref="Latest"/>; null when Legal=None or on policy throw.</summary>
    public ActionChoice? LastChoice { get; private set; }

    /// <summary>Scorer exception message, paired with <see cref="LastScored"/>=null.</summary>
    public string? LastScorerError { get; private set; }

    public event Action<StateSnapshot>? Changed;

    public StateAggregator(AddonEmjReader reader, IFramework framework, IPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(framework);
        this.reader = reader;
        this.framework = framework;
        this.policy = policy;

        this.reader.ObservationChanged += OnObservationChanged;
        framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        framework.Update -= OnFrameworkUpdate;
        reader.ObservationChanged -= OnObservationChanged;
        ClearCachedState();
    }

    private void OnObservationChanged(AddonEmjObservation observation)
    {
        if (disposed) return;
        // PreFinalize fires while the native addon may still be addressable. Do not rebuild
        // immediately from that dying object and repopulate the cache just cleared by the event.
        if (!observation.Present || !observation.IsVisible)
        {
            ClearCachedState();
            return;
        }
        Rebuild();
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        if (disposed) return;
        long now = DateTime.UtcNow.Ticks;
        if (now - lastRebuildTicks < MinTickIntervalTicks)
            return;
        lastRebuildTicks = now;
        Rebuild();
    }

    private void Rebuild()
    {
        if (disposed) return;
        // Always call TryBuildSnapshot: it observes MeldTracker and pins ActiveLayout.
        StateSnapshot? next;
        try { next = reader.TryBuildSnapshot(); }
        catch (Exception ex)
        {
            ClearCachedState($"READ_FAILED: {ex.GetType().Name}");
            return;
        }
        ApplySnapshot(next);
    }

    /// <summary>Managed cache boundary, shared by normal reads and isolated regression tests.</summary>
    internal void ApplySnapshot(StateSnapshot? next)
    {
        if (disposed) return;
        if (next is null)
        {
            ClearCachedState();
            return;
        }
        if (next.SchemaVersion != StateSnapshot.CurrentSchemaVersion)
        {
            ClearCachedState($"SCHEMA_MISMATCH: expected {StateSnapshot.CurrentSchemaVersion}, received {next.SchemaVersion}");
            return;
        }

        int hash = ComputeContentHash(next);
        if (hasContentHash && hash == lastContentHash)
        {
            // A background decision or newly stable public tiles can become available
            // without a new game event. Refresh only the policy, never invent Changed.
            if (policy is IRefreshablePolicy { RequiresRefresh: true }) RefreshPolicyCache(next);
            return;
        }

        lastContentHash = hash;
        hasContentHash = true;
        Latest = next;
        RefreshPolicyCache(next);
        Changed?.Invoke(next);
    }

    private void ClearCachedState(string? error = null)
    {
        (policy as IRefreshablePolicy)?.Invalidate();
        Latest = null;
        LastScored = null;
        LastChoice = null;
        LastScorerError = error;
        hasContentHash = false;
        lastContentHash = 0;
    }

    private void RefreshPolicyCache(StateSnapshot snap)
    {
        LastScored = null;
        LastChoice = null;
        LastScorerError = null;

        if (policy is null)
            return;
        if (snap.Legal.Flags == ActionFlags.None)
        {
            (policy as IRefreshablePolicy)?.Invalidate();
            return;
        }

        bool independentPublicState = policy is IIndependentPublicStatePolicy;
        int closed = snap.Hand.Count;
        if (!independentPublicState && closed is > 0 and <= 11 && closed % 3 is 1 or 2 &&
            closed + 3 * snap.OurMelds.Count < 13)
        {
            (policy as IRefreshablePolicy)?.Invalidate();
            // A stopped/reloaded reader cannot reconstruct an already-open meld from
            // the remaining tiles. Do not call either scoring or a call/pass policy
            // with that incomplete history, and never invent the missing meld tiles.
            LastScorerError = $"MELD_HISTORY_MISSING：手牌 {closed} 张、已识别副露 {snap.OurMelds.Count} 组；已暂停建议和输入，继续只读记牌。请用日志与当前公开副露核对恢复；证据不足时等待下一局，不能把缺失副露当成空列表。";
            return;
        }

        if (!independentPublicState && snap.Legal.Can(ActionFlags.Discard))
        {
            try
            { LastScored = DiscardScorer.Score(snap); }
            catch (Exception ex)
            { LastScorerError = ex.Message; }
        }

        try
        { LastChoice = policy.Choose(snap); }
        catch (Exception ex)
        {
            LastChoice = null;
            LastScored = null;
            LastScorerError = $"POLICY_FAILED: {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Content fingerprint; record equality reference-checks list fields and reports false on every fresh snapshot.</summary>
    private static int ComputeContentHash(StateSnapshot snap)
    {
        var h = new HashCode();
        h.Add(snap.SchemaVersion);
        h.Add(snap.WallRemaining);
        h.Add(snap.TurnIndex);
        h.Add((int)snap.Legal.Flags);
        AddTiles(snap.Legal.DiscardableTiles);
        AddCandidates(snap.Legal.PonCandidates);
        AddCandidates(snap.Legal.ChiCandidates);
        AddCandidates(snap.Legal.KanCandidates);
        h.Add(snap.OurRiichi);
        h.Add(snap.OurIppatsu);
        h.Add(snap.OurDoubleRiichi);
        h.Add(snap.OurSeat);
        h.Add(snap.SeatInfoKnown);
        h.Add(snap.RoundWind);
        h.Add(snap.DealerSeat);
        h.Add(snap.Honba);
        h.Add(snap.RiichiSticks);
        h.Add(snap.AkaDora);
        h.Add(snap.AddonStateCode);
        AddTiles(snap.Hand);
        AddMelds(snap.OurMelds);
        AddTiles(snap.DoraIndicators);
        AddTiles(snap.UraDoraIndicators);
        h.Add(snap.Scores.Count);
        foreach (var s in snap.Scores)
            h.Add(s);
        h.Add(snap.Seats.Count);
        foreach (var s in snap.Seats)
        {
            h.Add(s.DiscardCount);
            AddTiles(s.Discards);
            h.Add(s.DiscardIsTedashi.Count);
            foreach (bool tedashi in s.DiscardIsTedashi) h.Add(tedashi);
            AddMelds(s.Melds);
            h.Add(s.Riichi);
            h.Add(s.RiichiDiscardIndex);
            h.Add(s.Ippatsu);
            h.Add(s.IsTenpaiCalled);
        }
        return h.ToHashCode();

        void AddTiles(System.Collections.Generic.IReadOnlyList<Tile> tiles)
        {
            h.Add(tiles.Count);
            foreach (var tile in tiles) h.Add(tile.Id);
        }

        void AddMelds(System.Collections.Generic.IReadOnlyList<Meld> melds)
        {
            h.Add(melds.Count);
            foreach (var meld in melds)
            {
                h.Add((int)meld.Kind);
                h.Add(meld.ClaimedFromSeat);
                h.Add(meld.ClaimedTile?.Id);
                AddTiles(meld.Tiles);
            }
        }

        void AddCandidates(System.Collections.Generic.IReadOnlyList<MeldCandidate> candidates)
        {
            h.Add(candidates.Count);
            foreach (var candidate in candidates)
            {
                h.Add((int)candidate.Kind);
                h.Add(candidate.ClaimedTile.Id);
                h.Add(candidate.FromSeat);
                AddTiles(candidate.HandTiles);
            }
        }
    }
}
