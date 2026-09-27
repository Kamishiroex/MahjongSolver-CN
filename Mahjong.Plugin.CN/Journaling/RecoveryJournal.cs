using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Core;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.Dalamud.GameState;

namespace Mahjong.Plugin.CN.Journaling;

internal sealed record SavedRecoveryState(int Schema, RuntimeIdentity Identity, string PluginVersion,
    Guid ObservationSessionId, long ObservationSequence, StateSnapshot Snapshot, MeldTrackerCheckpoint Tracker,
    long RuntimeSequence, LowerHandReading? Lower, PublicTableReading? PublicTable,
    bool AutomaticEnabled = false, bool FullHistoryVerified = false, string Mapping = "LegacyAssumption");
internal sealed record RecoveryCandidate(SavedRecoveryState State, DateTimeOffset Utc, string LogName, bool IncompleteTail);
internal sealed record RecoveryLoadResult(ImmutableArray<RecoveryCandidate> Candidates, ImmutableArray<string> Errors);

internal static class RecoveryJournal
{
    internal static async Task<RecoveryLoadResult> LoadAsync(string root, RuntimeIdentity identity,
        string? excludeDirectory = null, CancellationToken cancellation = default)
    {
        var found = ImmutableArray.CreateBuilder<RecoveryCandidate>();
        var errors = ImmutableArray.CreateBuilder<string>();
        try
        {
            foreach (string directory in GameJournal.RecentDirectories(root))
            {
                cancellation.ThrowIfCancellationRequested();
                if (string.Equals(directory, excludeDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                var latest = Path.Combine(directory, "recovery-latest.jsonl");
                var latestLog = File.Exists(latest) ? await GameJournal.ReadAsync(latest, cancellation).ConfigureAwait(false) : null;
                if (latestLog is { IntegrityPassed: true, Lines.Length: > 0 } &&
                    Guid.TryParseExact(Path.GetFileName(directory).Split('-')[^1], "N", out var expectedSession) &&
                    latestLog.Lines[0].Entry.SessionId != expectedSession)
                    latestLog = new([], false, "RECOVERY_SESSION_MISMATCH");
                if (latestLog is { IntegrityPassed: false }) errors.Add(latestLog.Error!);
                // An independently hashed checkpoint is sufficient ONLY as a candidate
                // for live-table reconciliation. It makes no claim about event history.
                // Do not scan an arbitrarily long event stream before reading this file.
                if (latestLog is { IntegrityPassed: true, IncompleteTail: false, Lines.Length: 1 } &&
                    latestLog.Lines[0].Entry.Kind == "recovery_checkpoint")
                {
                    try
                    {
                        var state = latestLog.Lines[0].Entry.Data.Deserialize<SavedRecoveryState>();
                        if (Valid(state, identity))
                        {
                            found.Add(new(state!, latestLog.Lines[0].Entry.Utc, Path.GetFileName(directory), false));
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
                    { errors.Add("RECOVERY_CHECKPOINT_INVALID"); }
                }
                string file = Path.Combine(directory, "events.jsonl");
                var log = await GameJournal.ReadAsync(file, cancellation, checkpointsOnly: true).ConfigureAwait(false);
                if (!log.IntegrityPassed) { errors.Add(log.Error!); continue; }
                // Latest stable state is atomically replaced between periodic historical
                // anchors. It never contains an armed action or upgrades a history gap.
                var candidates = log.Lines.Reverse();
                foreach (var entry in candidates)
                {
                    if (entry.Entry.Kind != "recovery_checkpoint") continue;
                    SavedRecoveryState? candidate;
                    try { candidate = entry.Entry.Data.Deserialize<SavedRecoveryState>(); }
                    catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
                    { errors.Add("RECOVERY_CHECKPOINT_INVALID"); continue; }
                    if (!Valid(candidate, identity)) continue;
                    found.Add(new(candidate!, entry.Entry.Utc, Path.GetFileName(directory), log.IncompleteTail));
                    if (found.Count >= 32) break;
                }
                // Prefer the latest session that actually recorded usable checkpoints.
                if (found.Count > 0) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { errors.Add("RECOVERY_LOG_READ_" + ex.GetType().Name); }
        return new(found.ToImmutable(), errors.Distinct(StringComparer.Ordinal).Take(16).ToImmutableArray());
    }

    internal static bool SameIdentity(RuntimeIdentity a, RuntimeIdentity b) => a.Error is null && b.Error is null &&
        a.GameVersion == b.GameVersion && a.Api == b.Api && a.DalamudCommit == b.DalamudCommit &&
        a.ClientStructsVersion == b.ClientStructsVersion && a.Language == b.Language;

    private static bool Valid(SavedRecoveryState? state, RuntimeIdentity identity) =>
        state is { Schema: 1, AutomaticEnabled: false, FullHistoryVerified: false, Mapping: "LegacyAssumption" } &&
        state.Identity is not null && SameIdentity(state.Identity, identity) && state.ObservationSessionId != Guid.Empty &&
        state.ObservationSequence >= 0 && state.RuntimeSequence >= 0 && ValidSnapshot(state.Snapshot) &&
        state.Tracker is { SchemaVersion: 1, Melds.Length: <= 4, LastHand.Length: <= 14, DiscardCounts.Length: 4 } &&
        state.Tracker.Melds.All(ValidMeld) && state.Tracker.LastHand.All(t => t.Id < 34) &&
        state.Tracker.DiscardCounts.All(x => x is >= 0 and <= 32) && state.Tracker.MeldAkadora is >= 0 and <= 3 &&
        state.Tracker.LastAkadora is >= 0 and <= 3 && state.Tracker.LastObservedWall is >= -1 and <= 70;

    internal static bool ValidSnapshot(StateSnapshot? snapshot) =>
        snapshot is { Hand.Count: <= 14, OurMelds.Count: <= 4, Seats.Count: 4, Scores.Count: 4,
            SchemaVersion: StateSnapshot.CurrentSchemaVersion, OurSeat: >= 0 and <= 3,
            AkaDora: >= 0 and <= 3, WallRemaining: >= -1 and <= 70,
            DoraIndicators.Count: <= 5, UraDoraIndicators.Count: <= 5 } &&
        snapshot.Hand.All(t => t.Id < 34) && snapshot.OurMelds.All(ValidMeld) &&
        snapshot.DoraIndicators.All(t => t.Id < 34) && snapshot.UraDoraIndicators.All(t => t.Id < 34) &&
        snapshot.Scores.All(x => x is >= -1000000 and <= 1000000) &&
        snapshot.Seats.All(s => s is { Discards.Count: <= 32, DiscardIsTedashi.Count: <= 32, Melds.Count: <= 4,
            DiscardCount: >= 0 and <= 32 } && s.Discards.All(t => t.Id < 34) && s.Melds.All(ValidMeld));

    private static bool ValidMeld(Meld meld)
    {
        if (!Enum.IsDefined(meld.Kind) || meld.Tiles is null || meld.Tiles.Length != (meld.IsKan ? 4 : 3) ||
            meld.Tiles.Any(t => t.Id >= 34) || (meld.ClaimedTile is { } claimed && !meld.Tiles.Contains(claimed))) return false;
        if (meld.Kind == MeldKind.AnKan)
        { if (meld.ClaimedFromSeat != -1 || meld.ClaimedTile is not null) return false; }
        // Opponents may have called from local player 0. Own-meld restoration separately
        // requires a relative source 1..3; loading a candidate must not reinterpret seats.
        else if (meld.ClaimedFromSeat is < 0 or > 3) return false;
        if (meld.Kind != MeldKind.Chi) return meld.Tiles.All(t => t.Id == meld.Tiles[0].Id);
        var ids = meld.Tiles.Select(t => (int)t.Id).Order().ToArray();
        return ids[0] < 27 && ids[0] / 9 == ids[2] / 9 && ids[1] == ids[0] + 1 && ids[2] == ids[0] + 2;
    }
}
