using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.PublicState;

/// <summary>Proves the current own draw's origin from bounded public pre/post inventories.</summary>
internal sealed class PublicDrawKindTracker
{
    private sealed record Sample(ObservationReference Reference, int Wall, ImmutableArray<VisibleTile> Hand,
        ImmutableArray<PublicMeldImageGroup> Groups, string Key);
    private readonly List<Sample> history = [];
    private readonly List<PublicCallEvent> kans = [];
    private string? epoch, normalBaseline, boundKey;
    private Guid session;
    private ObservationReference? last;
    private Field<string>? bound;

    internal void Clear()
    {
        history.Clear(); kans.Clear(); epoch = normalBaseline = boundKey = null;
        session = default; last = null; bound = null;
    }

    internal PublicSnapshot Observe(PublicSnapshot snapshot, string? token, PublicCallTrackingResult? calls,
        PublicOwnHandTransitionObservation? own, PublicObservationContext? context)
    {
        snapshot = snapshot with { OwnDrawKind = Field<string>.Unknown("当前摸牌来源尚无连续公开证据。") };
        if (!PublicCurrentFacts.Audited(context) || string.IsNullOrWhiteSpace(token) ||
            snapshot.SessionId == Guid.Empty || snapshot.Observation is not { } reference)
        { Clear(); return snapshot; }
        if (epoch != token || session != snapshot.SessionId || last is { } previous &&
            (reference.Sequence <= previous.Sequence || reference.ObservedAtUtc <= previous.ObservedAtUtc ||
             reference.ObservedAtUtc - previous.ObservedAtUtc > TimeSpan.FromSeconds(2))) Clear();
        epoch = token; session = snapshot.SessionId; last = reference;
        history.RemoveAll(s => reference.ObservedAtUtc - s.Reference.ObservedAtUtc > TimeSpan.FromSeconds(2));
        if (calls is not null)
            foreach (var call in calls.Events.Where(c => c.RoundToken == token && c.CallerDirection == "bottom" &&
                c.FirstObservedSequence < c.ConfirmedSequence && c.ConfirmedSequence <= reference.Sequence && VerifiedKan(c)))
                if (!kans.Any(k => k.GroupPath == call.GroupPath && k.FirstObservedSequence == call.FirstObservedSequence))
                    kans.Add(call);
        kans.RemoveAll(c => history.Count == 0 || c.FirstObservedSequence <= history[0].Reference.Sequence);
        if (kans.Count > 16) { Clear(); return snapshot; }
        var player = snapshot.Players.SingleOrDefault(p => p.Position == ScreenPosition.Lower);
        if (snapshot.Stability != StabilityState.Stable || !Fresh(snapshot.LowerVisibleFaces, reference) ||
            !Fresh(snapshot.WallRemaining, reference) || snapshot.WallRemaining.Value is < 0 or > 70 ||
            player is null || !PublicMeldInventoryProof.TryRead(player.MeldImages, reference, out var melds))
        { bound = null; boundKey = null; return snapshot; }
        var hand = snapshot.LowerVisibleFaces.Value.Select(t => t.Tile).ToImmutableArray();
        string groupKey = Groups(melds.Groups);
        string key = $"{snapshot.WallRemaining.Value}:{groupKey}:{Tiles(hand)}";
        var current = new Sample(reference, snapshot.WallRemaining.Value, hand, melds.Groups, key);
        var separate = snapshot.LowerVisibleFaces.Value.Where(t => t.Slot.Path == "Emj/135/9/4").ToArray();
        if (history.LastOrDefault() is { } latest && Groups(latest.Groups) != groupKey) normalBaseline = null;
        if (own?.Transition is { Kind: "DiscardCandidate" } discard && discard.AfterSample == reference.Sequence &&
            own.Code == "OWN_UNIQUE_INVENTORY_DELTA") normalBaseline = groupKey;
        Field<string>? result = null;
        if (hand.Length == 14 - 3 * melds.Groups.Length && separate.Length == 1)
        {
            var tile = separate[0].Tile;
            if (boundKey == key && bound is not null) result = bound with
            { Observation = bound.Observation! with { Sequence = reference.Sequence, ObservedAtUtc = reference.ObservedAtUtc } };
            else
            {
                foreach (var call in kans.OrderByDescending(c => c.FirstObservedSequence))
                {
                    var before = history.LastOrDefault(s => s.Reference.Sequence < call.FirstObservedSequence &&
                        s.Wall - current.Wall == 1 && MatchesKan(s, current, call, tile));
                    if (before is null) continue;
                    result = Fact("rinshan", reference, before.Reference.Sequence, call.ConfirmedSequence); break;
                }
                if (result is null && own is { Code: "OWN_UNIQUE_INVENTORY_DELTA", Transition: { Kind: "DrawCandidate" } draw } &&
                    draw.AfterSample == reference.Sequence && draw.Tile.Kind34 == tile.Id && draw.Tile.RedFive == tile.Red &&
                    (melds.Groups.All(g => g.VisibleSlots == 3) || normalBaseline == groupKey) &&
                    history.Any(s => s.Reference.Sequence == draw.BeforeSample && s.Wall - current.Wall == 1 &&
                        Groups(s.Groups) == groupKey && s.Hand.Length + 1 == hand.Length && Same(s.Hand.Add(tile), hand)))
                    result = Fact("normal", reference, draw.BeforeSample, draw.AfterSample);
            }
        }
        boundKey = result is null ? null : key; bound = result;
        history.Add(current); if (history.Count > 32) history.RemoveAt(0);
        return result is null ? snapshot : snapshot with { OwnDrawKind = result };
    }

    private static bool MatchesKan(Sample before, Sample after, PublicCallEvent call, VisibleTile drawn)
    {
        var old = before.Groups.SingleOrDefault(g => g.GroupPath == call.GroupPath);
        var next = after.Groups.SingleOrDefault(g => g.GroupPath == call.GroupPath);
        if (next is not { VisibleSlots: 4 } ||
            Groups(before.Groups.Where(g => g.GroupPath != call.GroupPath)) != Groups(after.Groups.Where(g => g.GroupPath != call.GroupPath))) return false;
        if (call.Kind == "kakan" ? old?.ShapeCode != "PUBLIC_PON_THREE_FACE_PATTERN" : old is not null) return false;
        if (next.ShapeCode != (call.Kind == "ankan" ? "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN" : "PUBLIC_KAN_FOUR_FACE_PATTERN")) return false;
        if (before.Hand.Length != (call.Kind == "daiminkan" ? 13 : 14) - 3 * before.Groups.Length) return false;
        var consumed = call.Kind == "kakan" ? call.AddedTile is { } added ? new[] { added } : [] : call.Consumed.ToArray();
        if (consumed.Length != (call.Kind == "ankan" ? 4 : call.Kind == "daiminkan" ? 3 : 1)) return false;
        var remaining = before.Hand.ToList();
        foreach (var t in consumed) if (!remaining.Remove(new(t.Kind34, t.RedFive))) return false;
        remaining.Add(drawn);
        return Same(remaining, after.Hand);
    }
    private static bool VerifiedKan(PublicCallEvent c) => c.Kind switch
    {
        "ankan" => c.Code == "PUBLIC_ANKAN_TWO_FACE_DERIVED",
        "daiminkan" => c.Code == "PUBLIC_CALL_VISIBLE_TRANSITION",
        "kakan" => c.Code == "PUBLIC_KAKAN_VISIBLE_TRANSITION", _ => false,
    };
    private static bool Fresh<T>(Field<T> field, ObservationReference r) => field.IsConfirmed &&
        field.Observation!.Sequence == r.Sequence && field.Observation.ObservedAtUtc == r.ObservedAtUtc;
    private static string Groups(IEnumerable<PublicMeldImageGroup> groups) => string.Join("|", groups.OrderBy(g => g.GroupPath)
        .Select(g => $"{g.GroupPath}:{g.ShapeCode}:{g.VisibleSlots}:{g.InferredClosedKanKind34}"));
    private static string Tiles(IEnumerable<VisibleTile> tiles) => string.Join(',', tiles.Select(t => $"{t.Id}:{t.Red}").Order());
    private static bool Same(IEnumerable<VisibleTile> a, IEnumerable<VisibleTile> b) => Tiles(a) == Tiles(b);
    private static Field<string> Fact(string kind, ObservationReference r, long before, long after) => Field<string>.Known(kind,
        r with { Source = "PublicDrawKindTracker/public-inventory-transition", Evidence = "docs/cn/GLOBAL-WIN-CONTEXT.md",
            DerivationInputs = [$"before:{before}", $"confirmed:{after}"] }, SourceKind.Derived);
}
