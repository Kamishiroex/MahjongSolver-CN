using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;

namespace Mahjong.Plugin.CN.PublicState;

/// <summary>Current accepted/ippatsu facts from public inventories, never invented mjai events.</summary>
internal sealed class PublicRiichiWindowTracker
{
    private sealed record RiverTile(string Path, VisibleTile Tile, int Order, bool Sideways);
    private sealed record Table(ObservationReference Reference, ImmutableArray<RiverTile>[] Rivers, string Melds);
    private sealed record Before(ObservationReference Reference, ImmutableArray<RiverTile> River, string Melds, bool PositiveSeen=false);
    private sealed record Window(Table Declaration, RiverTile Tile, ObservationReference? BeforeDeclaration,
        ObservationReference? Accepted = null, bool Ended = false, bool? DoubleRiichi = null);
    private readonly Before?[] before = new Before?[4];
    private readonly Window?[] windows = new Window?[4];
    private string? epoch;
    private Guid session;
    private ObservationReference? last;
    private Table? lastTable;

    internal void Clear()
    {
        Array.Clear(before); Array.Clear(windows); epoch = null; session = default; last = null; lastTable = null;
    }

    internal PublicSnapshot Observe(PublicSnapshot snapshot, string? token, PublicObservationContext? context)
    {
        if (!PublicCurrentFacts.Audited(context) || string.IsNullOrWhiteSpace(token) ||
            snapshot.SessionId == Guid.Empty || snapshot.Observation is not { } reference ||
            snapshot.Players.Length != 4 || snapshot.Players.Select(p => (int)p.Position).Order().SequenceEqual([0,1,2,3]) == false)
        { Clear(); return snapshot; }
        if (session != snapshot.SessionId || epoch != token || last is { } prior &&
            (reference.Sequence <= prior.Sequence || reference.ObservedAtUtc <= prior.ObservedAtUtc ||
             reference.ObservedAtUtc - prior.ObservedAtUtc > TimeSpan.FromSeconds(2))) Clear();
        session = snapshot.SessionId; epoch = token; last = reference;
        // Animation does not erase an unchanged public inventory; it never emits a fact.
        // An epoch/time boundary above does erase it, even if the table title repeats.
        string? currentMelds = ReadMelds(snapshot, reference);
        var table = ReadTable(snapshot, reference, currentMelds);
        if (table is not null)
        {
            if (lastTable is not null && !Extends(lastTable,table)) { Array.Clear(before); Array.Clear(windows); }
            lastTable = table;
        }
        var updated = snapshot with { Players = snapshot.Players.Select(player =>
        {
            int id = (int)player.Position;
            if (!Fresh(player.RiichiDeclared, reference) || player.RiichiEstablished.Availability == Availability.Conflict ||
                player.Ippatsu.Availability == Availability.Conflict)
            { before[id] = null; windows[id] = null; return player; }
            if (!player.RiichiDeclared.Value)
            {
                windows[id] = null;
                var river = ReadRiver(player.RiverImages, reference);
                before[id] = river is { } own && own.All(t => !t.Sideways) && currentMelds is not null
                    ? new(reference,own,currentMelds) : null;
                return player.DoubleRiichi.Availability == Availability.Conflict ? player : player with {
                    DoubleRiichi = Field<bool>.Known(false, reference with {
                        Source = "PublicRiichiWindowTracker/not-declared", Evidence = "docs/cn/DOUBLE-RIICHI-CONTINUATION.md"
                    }, SourceKind.Derived) };
            }
            if (Fresh(player.RiichiEstablished, reference) && !player.RiichiEstablished.Value)
            { before[id] = null; windows[id] = null; return player; }
            if (before[id] is { PositiveSeen: false } pending)
                before[id] = reference.ObservedAtUtc - pending.Reference.ObservedAtUtc <= TimeSpan.FromSeconds(2)
                    ? pending with { PositiveSeen=true } : null;
            if (table is null) return player;
            var sideways = table.Rivers[id].Where(t => t.Sideways && t.Path == $"Emj/{117 + 3 * id}/4").ToArray();
            if (sideways.Length != 1) { windows[id] = null; return player; }
            var tile = sideways[0];
            var window = windows[id];
            if (window is not null && (window.Tile != tile || !Extends(window.Declaration, table))) window = null;
            if (window is null)
            {
                var baseline = before[id];
                bool start = baseline is { PositiveSeen: true } && baseline.Reference.Sequence < reference.Sequence &&
                    baseline.Melds == table.Melds &&
                    table.Rivers[id].Take(baseline.River.Length).SequenceEqual(baseline.River) &&
                    table.Rivers[id].Length == baseline.River.Length + 1 && table.Rivers[id][^1] == tile;
                // A complete empty meld table proves no earlier call even after a
                // mid-hand restart: public melds persist. Existing melds, however,
                // cannot tell whether a call preceded or followed this declaration.
                bool? initialDoubleRiichi = tile.Order > 1 ? false : table.Melds.Length == 0 ? true :
                    start && baseline!.Melds.Length > 0 ? false : null;
                window = new(table, tile, start ? baseline!.Reference : null, DoubleRiichi: initialDoubleRiichi);
                before[id] = null;
            }
            bool ownLater = table.Rivers[id].Any(t => t.Order > tile.Order);
            bool progressed = ownLater || Enumerable.Range(0,4).Any(i => i != id &&
                table.Rivers[i].Length > window.Declaration.Rivers[i].Length);
            if (progressed && window.Accepted is null && window.Declaration.Reference.Sequence < reference.Sequence)
                window = window with { Accepted = reference };
            // Calls permanently change a public meld inventory within a hand. No event
            // reconstruction is needed to reject positive ippatsu when one changed.
            if (ownLater || table.Melds != window.Declaration.Melds ||
                Fresh(player.Ippatsu, reference) && !player.Ippatsu.Value)
                window = window with { Ended = true };
            windows[id] = window;
            var evidence = reference with { Source = "PublicRiichiWindowTracker/current-window",
                Evidence = "docs/cn/RIICHI-WINDOW-CONTINUATION.md",
                DerivationInputs = [$"before-declaration:{window.BeforeDeclaration?.Sequence}",
                    $"declaration:{window.Declaration.Reference.Sequence}",
                    $"continuation:{window.Accepted?.Sequence}", $"current:{reference.Sequence}"] };
            if (window.Accepted is not null && player.RiichiEstablished.Availability != Availability.Conflict)
                player = player with { RiichiEstablished = Field<bool>.Known(true, evidence, SourceKind.Derived) };
            if (window.DoubleRiichi is { } doubleRiichi && Fresh(player.RiichiEstablished, reference) &&
                player.RiichiEstablished.Value && player.DoubleRiichi.Availability != Availability.Conflict)
                player = player with { DoubleRiichi = Field<bool>.Known(doubleRiichi, evidence with {
                    Evidence = "docs/cn/DOUBLE-RIICHI-CONTINUATION.md",
                    Source = "PublicRiichiWindowTracker/double-riichi"
                }, SourceKind.Derived) };
            // Unknown baseline can prove current acceptance but never a positive one-shot window.
            if (window.BeforeDeclaration is not null && window.Accepted is not null && player.Ippatsu.Availability != Availability.Conflict)
                player = player with { Ippatsu = Field<bool>.Known(!window.Ended, evidence, SourceKind.Derived) };
            return player;
        }).ToImmutableArray() };
        if (snapshot.Stability != StabilityState.Stable) return snapshot;
        var own = updated.Players.Single(p => p.Position == ScreenPosition.Lower);
        return updated.OurDoubleRiichi.Availability == Availability.Conflict || !Fresh(own.DoubleRiichi, reference)
            ? updated : updated with { OurDoubleRiichi = own.DoubleRiichi };
    }

    private static bool Fresh<T>(Field<T> field, ObservationReference r) => field.IsConfirmed && Current(field, r);
    private static bool Current<T>(Field<T> field, ObservationReference r) => field.Observation is { } o &&
        o.Sequence == r.Sequence && o.ObservedAtUtc == r.ObservedAtUtc;

    private static Table? ReadTable(PublicSnapshot snapshot, ObservationReference reference, string? melds)
    {
        var rivers = new ImmutableArray<RiverTile>[4];
        if (melds is null) return null;
        foreach (var player in snapshot.Players)
        {
            var river = ReadRiver(player.RiverImages,reference);
            if (river is null) return null;
            rivers[(int)player.Position] = river.Value;
        }
        return new(reference, rivers, melds);
    }

    private static ImmutableArray<RiverTile>? ReadRiver(Field<PublicImageInventory> field, ObservationReference reference)
    {
        if (!Inventory(field,reference,out var river) || river.Tiles.Length > 40 ||
            river.Tiles.Any(t => t.DisplayPosition is null || !t.SlotPath.StartsWith("Emj/",StringComparison.Ordinal))) return null;
        var tiles = river.Tiles.OrderBy(t => t.DisplayPosition!.ReadOrder).Select(t =>
            new RiverTile(t.SlotPath,t.Tile,t.DisplayPosition!.ReadOrder,t.DisplayPosition.IsSideways)).ToImmutableArray();
        return tiles.Select(t => t.Order).SequenceEqual(Enumerable.Range(1,tiles.Length)) ? tiles : null;
    }

    private static string? ReadMelds(PublicSnapshot snapshot, ObservationReference reference)
    {
        var melds = new List<string>();
        foreach (var player in snapshot.Players)
        {
            int id = (int)player.Position;
            if (!PublicMeldInventoryProof.TryRead(player.MeldImages, reference, out var meld)) return null;
            melds.AddRange(meld.Groups.Select(g => $"{id}:{g.GroupPath}:{g.ShapeCode}:{g.VisibleSlots}"));
            melds.AddRange(meld.Tiles.Select(t => $"{id}:{t.GroupPath}:{t.SlotPath}:{t.Tile.Id}:{t.Tile.Red}"));
        }
        return string.Join('|', melds.Order(StringComparer.Ordinal));
    }

    private static bool Inventory(Field<PublicImageInventory> field, ObservationReference reference, out PublicImageInventory result)
    {
        result = field.Value!;
        // Image inventory semantics remain Candidate globally. This bounded derivation
        // independently requires the audited profile, complete regions and stable faces.
        return field.Availability == Availability.Known && field.SourceKind is SourceKind.Observed or SourceKind.Derived &&
            Current(field, reference) && result is { Stable: true, RegionReadable: true,
                AllVisibleSlotsDecoded: true, UnknownComponents: 0 } && !result.Tiles.IsDefault &&
            result.Tiles.Length == result.VisibleSlots && result.ObservedEmpty == (result.VisibleSlots == 0) &&
            result.Tiles.All(t => t.Stable) && result.Tiles.Select(t => t.SlotPath).Distinct().Count() == result.Tiles.Length;
    }

    private static bool Extends(Table previous, Table current) => Enumerable.Range(0,4).All(i =>
        current.Rivers[i].Length >= previous.Rivers[i].Length &&
        current.Rivers[i].Take(previous.Rivers[i].Length).SequenceEqual(previous.Rivers[i]));
}
