using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Mahjong.Cn.PublicState;
using Mahjong.Core;

namespace Mahjong.Cn.Events;

public enum EventProvenance { Observed, Derived, Gap, Checkpoint }
public enum GameEventKind
{
    SnapshotObserved, Draw, Discard, Chi, Pon, OpenKan, ClosedKan, AddedKan,
    RiichiDeclared, RiichiEstablished, Ron, Tsumo, ExhaustiveDraw,
    TableEntered, TableExited, RoundStarted, RoundEnded, Paused, Resumed,
    ActionSubmitted, Gap, Checkpoint,
}

/// <summary>Only public event arguments; no opponent concealed hands or submitted callback results.</summary>
public sealed record GameEventDetails
{
    public ScreenPosition? ActorPosition { get; init; }
    public int? PlayerId { get; init; }
    public VisibleTile? Tile { get; init; }
    public VisibleMeld? Meld { get; init; }
    public string? Action { get; init; }
}

public sealed record ConfirmedGameEvent(GameEventKind Kind, ObservationReference Observation,
    GameEventDetails Details)
{
    public EventProvenance Provenance { get; init; }
    public MappingStatus MappingStatus { get; init; }
    public Field<string> RoundId { get; init; } = Field<string>.Unknown("事件来源没有已确认的牌局 ID。");
}

public sealed record PublicGameEvent(Guid SessionId, long Sequence, DateTimeOffset RecordedUtc,
    GameEventKind Kind, EventProvenance Provenance, ObservationReference Observation)
{
    public int SchemaVersion { get; init; } = 1;
    public Field<string> RoundId { get; init; } = Field<string>.Unknown("牌局 ID 未确认。");
    public PublicSnapshot? Snapshot { get; init; }
    public string? SnapshotDigest { get; init; }
    public GameEventDetails? Details { get; init; }
    public string? Code { get; init; }
}

public sealed record LedgerIssue(string Path, string Code, string Reason);
public sealed record LedgerAppendResult(ImmutableArray<PublicGameEvent> Entries,
    ImmutableArray<LedgerIssue> Issues, bool HasGap, bool Duplicate);
public sealed record LedgerReplayResult(ImmutableArray<PublicGameEvent> Entries, PublicSnapshot? LatestSnapshot,
    ImmutableArray<LedgerIssue> Issues, bool HasGap)
{
    // Contiguous snapshot records do not establish complete chronological mjai history.
    public bool HistoryComplete => false;
}

/// <summary>Bounded single-owner ledger. The caller serializes access and owns durable storage.
/// Observing snapshots never fabricates draws, discards, calls or callback success.</summary>
public sealed class PublicEventLedger
{
    private const int MaxSnapshotBytes = 256 * 1024;
    private readonly List<PublicGameEvent> entries = [];
    private readonly int maxEntries;
    private long sequence;
    private long lastObservationSequence = -1;
    private long lastSnapshotSequence = -1;
    private long lastRevision = -1;
    private string? lastFingerprint;
    private PublicSnapshot? latest;
    private bool started;
    public Guid SessionId { get; }
    public bool HasGap { get; private set; }
    public bool HistoryComplete => false;
    public ImmutableArray<PublicGameEvent> Entries => entries.ToImmutableArray();
    public PublicSnapshot? LatestSnapshot => latest;

    public PublicEventLedger(Guid sessionId, int maxEntries = 4096)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("An observation session ID is required.", nameof(sessionId));
        if (maxEntries is < 4 or > 65536) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        SessionId = sessionId;
        this.maxEntries = maxEntries;
    }

    public LedgerAppendResult Observe(PublicSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SessionId != SessionId) return Failure("session-mismatch", "观察会话不同，旧牌局状态不能沿用。");
        if (!ValidObservation(snapshot.Observation) || snapshot.StateRevision < 0)
            return Failure("observation-invalid", "观察缺少有界来源证据或有效序号。");
        if (snapshot.Observation!.Sequence < lastObservationSequence || snapshot.StateRevision < lastRevision)
            return Failure("observation-out-of-order", "迟到观察不能覆盖当前状态。");
        string fingerprint;
        string digest;
        try { fingerprint = SnapshotHash(snapshot, true); digest = SnapshotHash(snapshot, false); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        { return Failure("snapshot-invalid", "快照结构无效或超过大小限制。"); }
        if (snapshot.Observation.Sequence == lastSnapshotSequence && lastFingerprint != fingerprint)
            return Failure("observation-sequence-reused", "同一观察序号出现不一致内容。");
        bool duplicate = fingerprint == lastFingerprint;
        lastObservationSequence = snapshot.Observation.Sequence;
        lastSnapshotSequence = snapshot.Observation.Sequence;
        lastRevision = snapshot.StateRevision;
        if (duplicate)
        {
            latest = snapshot.Stability == StabilityState.Stable ? snapshot : null;
            return new([], [], HasGap, true);
        }
        var added = ImmutableArray.CreateBuilder<PublicGameEvent>();
        if (!started)
        {
            started = true;
            AddGap("history-start-unknown", snapshot.Observation, added);
        }
        if (snapshot.Synchronization != SynchronizationState.Synchronized && !HasGap)
            AddGap("observation-history-gap", snapshot.Observation, added);
        if (latest?.RoundId.IsConfirmed == true && snapshot.RoundId.IsConfirmed && latest.RoundId.Value != snapshot.RoundId.Value)
            AddGap("round-boundary-observed", snapshot.Observation, added);
        Append(new(SessionId, Next(), snapshot.Observation.ObservedAtUtc, GameEventKind.SnapshotObserved,
            EventProvenance.Observed, snapshot.Observation)
        { RoundId = snapshot.RoundId, Snapshot = snapshot, SnapshotDigest = digest }, added);
        latest = snapshot.Stability == StabilityState.Stable ? snapshot : null;
        lastFingerprint = fingerprint;
        return new(added.ToImmutable(), [], HasGap, false);
    }

    public LedgerAppendResult RecordOccurred(ConfirmedGameEvent observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (!IsOccurredKind(observed.Kind) || !ValidObservation(observed.Observation) ||
            observed.MappingStatus != MappingStatus.Validated ||
            observed.Provenance is not (EventProvenance.Observed or EventProvenance.Derived) ||
            (observed.Provenance == EventProvenance.Derived && observed.Observation.DerivationInputs.Length == 0) ||
            !ValidDetails(observed.Kind, observed.Details))
            return Failure("event-unverified", "已发生事件必须有经验证的直接证据或明确推导输入；操作提交不等于发生。");
        if (observed.Observation.Sequence < lastObservationSequence)
            return Failure("event-out-of-order", "迟到事件无法无歧义插入已有时序。");
        if (entries.Any(e => e.Kind == observed.Kind && e.Observation == observed.Observation &&
            JsonSerializer.Serialize(e.Details) == JsonSerializer.Serialize(observed.Details)))
            return new([], [], HasGap, true);
        lastObservationSequence = observed.Observation.Sequence;
        var added = ImmutableArray.CreateBuilder<PublicGameEvent>();
        Append(new(SessionId, Next(), observed.Observation.ObservedAtUtc, observed.Kind, observed.Provenance, observed.Observation)
        { RoundId = observed.RoundId, Details = observed.Details }, added);
        if (observed.Kind is GameEventKind.TableExited or GameEventKind.RoundEnded or GameEventKind.RoundStarted)
        { latest = null; lastFingerprint = null; }
        return new(added.ToImmutable(), [], HasGap, false);
    }

    public LedgerAppendResult RecordSubmitted(GameEventDetails action, ObservationReference source) =>
        RecordControl(GameEventKind.ActionSubmitted, source, action);

    public LedgerAppendResult RecordControl(GameEventKind kind, ObservationReference source, GameEventDetails? details = null)
    {
        if (kind is not (GameEventKind.Paused or GameEventKind.Resumed or GameEventKind.ActionSubmitted) ||
            !ValidObservation(source) || (details?.Action?.Length ?? 0) > 128)
            return Failure("control-invalid", "控制记录类型或来源无效。");
        var added = ImmutableArray.CreateBuilder<PublicGameEvent>();
        Append(new(SessionId, Next(), source.ObservedAtUtc, kind, EventProvenance.Observed, source) { Details = details }, added);
        // Pausing recommendations while observation continues must not erase meld history.
        return new(added.ToImmutable(), [], HasGap, false);
    }

    public LedgerAppendResult MarkGap(string code, ObservationReference source)
    {
        if (!ValidCode(code) || !ValidObservation(source)) return Failure("gap-invalid", "缺口标记无效。");
        var added = ImmutableArray.CreateBuilder<PublicGameEvent>();
        AddGap(code, source, added);
        latest = null;
        lastFingerprint = null;
        return new(added.ToImmutable(), [], HasGap, false);
    }

    public LedgerAppendResult Checkpoint(ObservationReference source)
    {
        if (!ValidObservation(source) || latest is null) return Failure("checkpoint-unavailable", "尚无当前稳定快照可作为检查点。");
        var added = ImmutableArray.CreateBuilder<PublicGameEvent>();
        Append(new(SessionId, Next(), source.ObservedAtUtc, GameEventKind.Checkpoint, EventProvenance.Checkpoint, source)
        { RoundId = latest.RoundId, Snapshot = latest, SnapshotDigest = SnapshotHash(latest, false) }, added);
        return new(added.ToImmutable(), [], HasGap, false);
    }

    private long Next() => checked(++sequence);
    private void AddGap(string code, ObservationReference source, ImmutableArray<PublicGameEvent>.Builder added)
    {
        HasGap = true;
        Append(new(SessionId, Next(), source.ObservedAtUtc, GameEventKind.Gap, EventProvenance.Gap, source) { Code = code }, added);
    }

    private void Append(PublicGameEvent item, ImmutableArray<PublicGameEvent>.Builder added)
    {
        entries.Add(item);
        added.Add(item);
        if (entries.Count <= maxEntries) return;
        entries.RemoveRange(0, entries.Count - maxEntries + 1);
        HasGap = true;
        var gap = new PublicGameEvent(SessionId, Next(), item.RecordedUtc, GameEventKind.Gap, EventProvenance.Gap, item.Observation)
        { Code = "retained-prefix-truncated" };
        entries.Add(gap);
        added.Add(gap);
    }

    private LedgerAppendResult Failure(string code, string reason)
    {
        HasGap = true;
        latest = null;
        lastFingerprint = null;
        return new([], [new("Ledger", code, reason)], true, false);
    }

    /// <summary>Replays parsed complete records. The IO owner reports a rejected partial final line.
    /// This validates the domain envelope; durable cryptographic chaining belongs to that owner.</summary>
    public static LedgerReplayResult Replay(IEnumerable<PublicGameEvent> source, bool partialTail = false, int maxEntries = 4096)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxEntries is < 4 or > 65536) throw new ArgumentOutOfRangeException(nameof(maxEntries));
        var result = new Queue<PublicGameEvent>();
        var issues = ImmutableArray.CreateBuilder<LedgerIssue>();
        PublicSnapshot? latest = null;
        Guid session = Guid.Empty;
        long previous = 0;
        bool gap = partialTail;
        int examined = 0;
        foreach (var item in source)
        {
            // A pure replay cannot spend unbounded time on a malformed/infinite producer.
            if (++examined > 65536) { issues.Add(new("Entries", "replay-limit", "重放记录数超出上限。")); gap = true; break; }
            if (item is null || item.SchemaVersion != 1 || item.SessionId == Guid.Empty || item.Sequence <= previous ||
                (session != Guid.Empty && session != item.SessionId) || !ValidObservation(item.Observation) || !Enum.IsDefined(item.Kind) ||
                !Enum.IsDefined(item.Provenance))
            { issues.Add(new("Entries", "entry-invalid", "记录版本、会话、顺序或来源不一致。")); gap = true; break; }
            if (item.Sequence != previous + 1) { issues.Add(new("Entries", "sequence-gap", "记录序列缺失；保留缺口。")); gap = true; }
            session = item.SessionId;
            previous = item.Sequence;
            if ((item.Kind == GameEventKind.Gap && (item.Provenance != EventProvenance.Gap || !ValidCode(item.Code ?? ""))) ||
                (item.Kind == GameEventKind.Checkpoint && item.Provenance != EventProvenance.Checkpoint) ||
                (item.Kind == GameEventKind.SnapshotObserved && item.Provenance != EventProvenance.Observed) ||
                (IsOccurredKind(item.Kind) && (!ValidDetails(item.Kind, item.Details) ||
                    item.Provenance is not (EventProvenance.Observed or EventProvenance.Derived))) ||
                (item.Provenance == EventProvenance.Derived && item.Observation.DerivationInputs.Length == 0))
            { issues.Add(new("Entries", "event-shape-invalid", "事件内容或来源种类不一致。")); gap = true; break; }
            if (item.Kind is GameEventKind.SnapshotObserved or GameEventKind.Checkpoint)
            {
                bool valid;
                try { valid = item.Snapshot is { } s && s.SessionId == session && ValidObservation(s.Observation) &&
                    item.SnapshotDigest == SnapshotHash(s, false); }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException) { valid = false; }
                if (!valid) { issues.Add(new("Snapshot", "snapshot-digest-mismatch", "快照缺失、损坏或与摘要不一致。")); gap = true; break; }
                latest = item.Snapshot!.Stability == StabilityState.Stable ? item.Snapshot : null;
            }
            else if (item.Snapshot is not null)
            { issues.Add(new("Snapshot", "unexpected-snapshot", "非观察记录不能携带生效快照。")); gap = true; break; }
            if (item.Kind == GameEventKind.Gap)
            { gap = true; latest = null; }
            if (item.Kind is GameEventKind.TableExited or GameEventKind.RoundEnded or GameEventKind.RoundStarted) latest = null;
            result.Enqueue(item);
            if (result.Count > maxEntries) { result.Dequeue(); gap = true; }
        }
        if (partialTail) issues.Add(new("Tail", "partial-tail", "进程中断留下不完整末条；仅重放已验证完整记录，保留缺口。"));
        return new(result.ToImmutableArray(), latest, issues.ToImmutable(), gap);
    }

    private static bool IsOccurredKind(GameEventKind kind) => kind is >= GameEventKind.Draw and <= GameEventKind.RoundEnded;
    private static bool ValidDetails(GameEventKind kind, GameEventDetails? details)
    {
        if (details is null || details.PlayerId is < 0 or > 3 ||
            (details.ActorPosition is { } p && !Enum.IsDefined(p)) || (details.Action?.Length ?? 0) > 128) return false;
        if (kind is >= GameEventKind.Draw and <= GameEventKind.Tsumo && details.ActorPosition is null && details.PlayerId is null) return false;
        if (details.Tile is { } t && !TableReconciler.ValidTiles([t])) return false;
        if (kind == GameEventKind.Discard && details.Tile is null) return false;
        if (kind is >= GameEventKind.Chi and <= GameEventKind.AddedKan)
        {
            var expected = kind switch { GameEventKind.Chi => MeldKind.Chi, GameEventKind.Pon => MeldKind.Pon,
                GameEventKind.OpenKan => MeldKind.MinKan, GameEventKind.ClosedKan => MeldKind.AnKan, _ => MeldKind.ShouMinKan };
            if (!TableReconciler.ValidMeld(details.Meld) || details.Meld!.Kind != expected) return false;
        }
        return true;
    }
    private static bool ValidObservation(ObservationReference? o) => o is not null && o.Sequence >= 0 &&
        !string.IsNullOrWhiteSpace(o.Source) && o.Source.Length <= 256 && !string.IsNullOrWhiteSpace(o.Evidence) && o.Evidence.Length <= 4096 &&
        o.DerivationInputs.Length <= 64 && o.DerivationInputs.All(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 512);
    private static bool ValidCode(string code) => !string.IsNullOrWhiteSpace(code) && code.Length <= 128 &&
        code.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static string SnapshotHash(PublicSnapshot snapshot, bool omitSampleMetadata)
    {
        byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        if (encoded.Length > MaxSnapshotBytes) throw new ArgumentException("Snapshot exceeds bounded journal record size.");
        using var document = JsonDocument.Parse(encoded);
        if (omitSampleMetadata) return PublicObservationFingerprint.Compute(document.RootElement);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, document.RootElement, omitSampleMetadata);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement node, bool omitSampleMetadata)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in node.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (omitSampleMetadata && property.Name is "Sequence" or "ObservedAtUtc" or "StateRevision") continue;
                writer.WritePropertyName(property.Name);
                WriteCanonical(writer, property.Value, omitSampleMetadata);
            }
            writer.WriteEndObject();
        }
        else if (node.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in node.EnumerateArray()) WriteCanonical(writer, item, omitSampleMetadata); writer.WriteEndArray(); }
        else node.WriteTo(writer);
    }
}
