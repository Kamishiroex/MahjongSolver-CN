using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.PublicState;

/// <summary>Observed missed winning window, not reconstructed complete pass history.</summary>
internal sealed class PublicFuritenTracker
{
    private sealed record RiverTile(string Slot, VisibleTile Tile);
    private sealed record Table(ImmutableArray<VisibleTile> Hand, string Melds, ImmutableArray<RiverTile>[] Rivers,
        int Wall, bool Waiting, PublicImageInventory OwnMelds, string? AllMelds);
    private sealed record Opportunity(Table Table, ObservationReference Reference, bool? Riichi,
        string Basis = "ron-offered", long? Before = null);
    private Opportunity? offered;
    private Opportunity? missed;
    private Opportunity? permanent;
    private Guid session;
    private string? epoch;
    private ObservationReference? last;
    private bool? lastMenuRon;
    private (Table Table, ObservationReference Reference)? shapeBaseline;
    private Opportunity? shapeOffered;

    internal void Clear()
    { offered = missed = permanent = shapeOffered = null; shapeBaseline = null; session = default; epoch = null; last = null; lastMenuRon=null; }

    internal PublicSnapshot Observe(PublicSnapshot s, string? token, AddonProbe? addon, PublicObservationContext? context)
    {
        if (!PublicCurrentFacts.Audited(context) || string.IsNullOrWhiteSpace(token) || s.SessionId == Guid.Empty ||
            s.Observation is not { } r || addon is not {Name:"Emj",Present:true,Visible:true,Ready:true,Error:null} ||
            s.Players.Length != 4 || !s.Players.Select(p=>(int)p.Position).Order().SequenceEqual([0,1,2,3]))
        { Clear(); return s; }
        if (session != s.SessionId || epoch != token || last is {} old &&
            (r.Sequence <= old.Sequence || r.ObservedAtUtc <= old.ObservedAtUtc ||
             r.ObservedAtUtc-old.ObservedAtUtc > TimeSpan.FromSeconds(2))) Clear();
        session=s.SessionId;epoch=token;last=r;
        if (s.Stability != StabilityState.Stable) return s;
        var own=s.Players.Single(p=>p.Position==ScreenPosition.Lower);
        if (own.RiichiDeclared.Availability==Availability.Conflict || own.RiichiEstablished.Availability==Availability.Conflict)
        { offered=missed=permanent=shapeOffered=null;shapeBaseline=null;return s; }
        bool? riichi=Fresh(own.RiichiEstablished,r) && own.RiichiEstablished.Value ? true :
            Fresh(own.RiichiDeclared,r) && !own.RiichiDeclared.Value ? false : null;
        var table=Read(s,r);
        bool? menu=MenuState(s,addon,r);
        bool rising=menu==true && lastMenuRon==false;
        if(menu is not null)lastMenuRon=menu;
        // Captured old clients retained enabled Ron rows after the game moved on.
        // Only a newly appearing menu or its unchanged bound table is evidence.
        bool ron=menu==true && table is {Waiting:true} && (rising || offered is {} bound && SameTable(bound.Table,table));
        bool drawn=Fresh(s.OwnDrawKind,r) && s.OwnDrawKind.Value is "normal" or "rinshan";
        ObserveShape(table, r, riichi, drawn);
        Field<bool> Fact(bool value,string code,Opportunity? proof=null)=>Field<bool>.Known(value,r with {
            Source="PublicFuritenTracker/"+code, Evidence="docs/cn/PASS-FURITEN-CONTINUATION.md",
            DerivationInputs=proof is null ? [$"current:{r.Sequence}"] :
                proof.Before is { } before ? [$"before:{before}",$"{proof.Basis}:{proof.Reference.Sequence}",$"current:{r.Sequence}"] :
                    [$"{proof.Basis}:{proof.Reference.Sequence}",$"current:{r.Sequence}"]
        },SourceKind.Derived);
        var temporary=Field<bool>.Unknown("未连续确认过和与本人下一次摸牌；不能从无荣和按钮推断。");
        var afterRiichi=riichi==false ? Fact(false,"not-declared") : Field<bool>.Unknown("立直后过和历史尚未确认。");
        if (permanent is not null && riichi==false || ron && (permanent is not null || missed is not null && !drawn && table is not null && SameOwn(missed.Table,table)))
        {
            var conflict=Field<bool>.Conflict(true,r with {Source="PublicFuritenTracker/conflict",Evidence="docs/cn/PASS-FURITEN-CONTINUATION.md"},
                "FURITEN_PUBLIC_CONFLICT：连续过和证据与当前荣和／立直状态矛盾。");
            return s with {OurTemporaryFuriten=conflict,OurRiichiFuriten=conflict};
        }
        if (drawn) {
            if(offered is {Riichi:true} previous && table is {Waiting:false} &&
                SameOwnExceptHand(previous.Table,table) && AddedOne(previous.Table.Hand,table.Hand))permanent=previous;
            missed=null;offered=null;temporary=Fact(false,"own-draw");
        }
        if (ron)
        {
            temporary=Fact(false,"enabled-ron");afterRiichi=Fact(false,"enabled-ron");
            // A current legal Ron proves only the current absence of furiten.
            // The absence of that button later cannot prove a pass by itself.
            if (rising) offered=new(table!,r,riichi);
        }
        else if (offered is {} opportunity && table is not null)
        {
            if (!SameOwn(opportunity.Table,table) || !Extends(opportunity.Table,table) ||
                table.Wall>opportunity.Table.Wall || opportunity.Table.Wall-table.Wall>1)
                offered=null;
            else if (MenuClosed(s,addon,r) && Enumerable.Range(1,3).Sum(i=>table.Rivers[i].Length-opportunity.Table.Rivers[i].Length)==1)
            {
                if (opportunity.Riichi==true) permanent=opportunity;
                else if (opportunity.Riichi==false) missed=opportunity;
                offered=null;
            }
        }
        if (missed is {} pending && table is not null)
        {
            if (!SameOwn(pending.Table,table) || !Extends(pending.Table,table)) missed=null;
            else temporary=Fact(true,"missed-ron-before-own-draw",pending);
        }
        if (permanent is not null && riichi==true) afterRiichi=Fact(true,"missed-ron-after-riichi",permanent);
        return s with {OurTemporaryFuriten=temporary,OurRiichiFuriten=afterRiichi};
    }

    private static bool Fresh<T>(Field<T> f,ObservationReference r)=>f.IsConfirmed && Current(f,r);
    private static bool Current<T>(Field<T> f,ObservationReference r)=>f.Observation is {} o && o.Sequence==r.Sequence && o.ObservedAtUtc==r.ObservedAtUtc;
    private static bool MenuClosed(PublicSnapshot s,AddonProbe addon,ObservationReference r)=>
        Current(s.VisibleActionMenu,r) && s.VisibleActionMenu.Availability==Availability.Known &&
        s.VisibleActionMenu.Value is {Visible:false,Rows.IsEmpty:true,Code:"ACTION_MENU_NOT_VISIBLE"} &&
        addon.PublicActionMenu is {Visible:false};
    private static bool? MenuState(PublicSnapshot s,AddonProbe addon,ObservationReference r)
    {
        if(MenuClosed(s,addon,r))return false;
        if(RonOffered(s,addon,r))return true;
        if(Current(s.VisibleActionMenu,r) && s.VisibleActionMenu.Availability==Availability.Known &&
            s.VisibleActionMenu.SourceKind is SourceKind.Observed or SourceKind.Derived &&
            s.VisibleActionMenu.Value is {Visible:true,AllVisibleRowsDecoded:true} menu &&
            addon.PublicActionMenu is {Visible:true,AllVisibleRowsDecoded:true} raw &&
            raw.ListState is not {IsUpdatePending:true} && raw.ListState is not {IsScrollRefreshPending:true} &&
            menu.Rows.All(x=>x.Action!=PublicMenuAction.Ron))return false;
        return null;
    }
    private static bool RonOffered(PublicSnapshot s,AddonProbe addon,ObservationReference r)=>
        Current(s.VisibleActionMenu,r) && s.VisibleActionMenu.Availability==Availability.Known &&
        s.VisibleActionMenu.SourceKind is SourceKind.Observed or SourceKind.Derived &&
        s.VisibleActionMenu.Value is {Visible:true,AllVisibleRowsDecoded:true,Code:"ACTION_MENU_VISIBLE_CANDIDATE"} menu &&
        menu.Rows.Count(x=>x.Action==PublicMenuAction.Ron && x.Enabled==true && x.Code=="ACTION_MENU_LABEL_CANDIDATE")==1 &&
        menu.Rows.Any(x=>x.Action==PublicMenuAction.Pass && x.Enabled==true) &&
        addon.PublicActionMenu is {Visible:true,AllVisibleRowsDecoded:true} raw &&
        raw.ListState is not {IsUpdatePending:true} && raw.ListState is not {IsScrollRefreshPending:true};

    private static Table? Read(PublicSnapshot s,ObservationReference r)
    {
        if (!Fresh(s.LowerVisibleFaces,r) || !Fresh(s.WallRemaining,r)) return null;
        var rivers=new ImmutableArray<RiverTile>[4];
        foreach(var p in s.Players) {
            if (!Inventory(p.RiverImages,r,out var inv) || inv.Tiles.Any(t=>t.DisplayPosition is null)) return null;
            var tiles=inv.Tiles.OrderBy(t=>t.DisplayPosition!.ReadOrder).ToArray();
            if(!tiles.Select(t=>t.DisplayPosition!.ReadOrder).SequenceEqual(Enumerable.Range(1,tiles.Length)))return null;
            rivers[(int)p.Position]=tiles.Select(t=>new RiverTile(t.SlotPath,t.Tile)).ToImmutableArray();
        }
        var own=s.Players.Single(p=>p.Position==ScreenPosition.Lower);
        if (!PublicMeldInventoryProof.TryRead(own.MeldImages,r,out var melds) ||
            s.LowerVisibleFaces.Value.Length!=13-3*melds.Groups.Length && s.LowerVisibleFaces.Value.Length!=14-3*melds.Groups.Length) return null;
        var all = new List<string>();
        foreach (var p in s.Players.OrderBy(p => p.Position))
        {
            if (!PublicMeldInventoryProof.TryRead(p.MeldImages,r,out var groups)) { all.Clear(); break; }
            all.Add($"{p.Position}:{MeldKey(groups)}");
        }
        return new(s.LowerVisibleFaces.Value.Select(t=>t.Tile).OrderBy(t=>t.Id).ThenBy(t=>t.Red).ToImmutableArray(),
            MeldKey(melds),rivers,s.WallRemaining.Value,
            s.LowerVisibleFaces.Value.Length==13-3*melds.Groups.Length, melds, all.Count==4 ? string.Join(';',all) : null);
    }

    private static string MeldKey(PublicImageInventory melds) => string.Join('|',
        melds.Tiles.Select(t=>$"{t.GroupPath}:{t.SlotPath}:{t.Tile.Id}:{t.Tile.Red}")
            .Concat(melds.Groups.Select(g=>$"{g.GroupPath}:{g.ShapeCode}:{g.VisibleSlots}:{g.VerifiedBackSlots}:{g.InferredClosedKanKind34}")).Order());

    // Covers shape-completing discards even when no yaku/Ron button exists. A new
    // discard only opens a candidate; subsequent play must prove it was passed.
    private void ObserveShape(Table? table, ObservationReference reference, bool? riichi, bool drawn)
    {
        if (table?.AllMelds is null || table.Wall is < 0 or > 70)
        { shapeBaseline = null; shapeOffered = null; return; }
        if (shapeOffered is { } opportunity)
        {
            if (drawn && !table.Waiting && SameOwnExceptHand(opportunity.Table,table) &&
                opportunity.Table.AllMelds == table.AllMelds && AddedOne(opportunity.Table.Hand,table.Hand))
            {
                if (opportunity.Riichi == true) permanent = opportunity;
                shapeOffered = null;
            }
            else if (!SameOwn(opportunity.Table,table) || !Extends(opportunity.Table,table) ||
                opportunity.Table.AllMelds != table.AllMelds ||
                table.Wall > opportunity.Table.Wall || opportunity.Table.Wall - table.Wall > 1)
                shapeOffered = null;
            else if (OneNewOpponentDiscard(opportunity.Table,table) is not null)
            {
                if (opportunity.Riichi == true) permanent = opportunity;
                else if (opportunity.Riichi == false) missed = opportunity;
                shapeOffered = null;
            }
        }
        if (!drawn && table.Waiting && shapeBaseline is { } before &&
            reference.ObservedAtUtc - before.Reference.ObservedAtUtc <= TimeSpan.FromSeconds(2) &&
            SameOwn(before.Table,table) && before.Table.AllMelds == table.AllMelds &&
            table.Wall <= before.Table.Wall && before.Table.Wall - table.Wall <= 1 &&
            OneNewOpponentDiscard(before.Table,table) is { } tile &&
            PublicWinningShape.Completes(table.Hand,table.OwnMelds,tile))
            shapeOffered = new(table,reference,riichi,"shape-discard",before.Reference.Sequence);
        shapeBaseline = (table,reference);
    }

    private static VisibleTile? OneNewOpponentDiscard(Table before, Table after)
    {
        if (!Extends(before,after) || !before.Rivers[0].SequenceEqual(after.Rivers[0])) return null;
        var added = Enumerable.Range(1,3).SelectMany(i => after.Rivers[i].Skip(before.Rivers[i].Length)).ToArray();
        return added.Length == 1 ? added[0].Tile : null;
    }
    private static bool Inventory(Field<PublicImageInventory> f,ObservationReference r,out PublicImageInventory inv)
    {
        inv=f.Value!;
        return f.Availability==Availability.Known && f.SourceKind is SourceKind.Observed or SourceKind.Derived && Current(f,r) &&
            inv is {Stable:true,RegionReadable:true,AllVisibleSlotsDecoded:true,UnknownComponents:0} &&
            !inv.Tiles.IsDefault && !inv.Groups.IsDefault && inv.RejectionCodes.IsEmpty &&
            inv.Tiles.Length==inv.VisibleSlots && inv.ObservedEmpty==(inv.VisibleSlots==0) &&
            inv.Tiles.All(t=>t.Stable) && inv.Tiles.Select(t=>t.SlotPath).Distinct().Count()==inv.Tiles.Length;
    }
    private static bool SameOwnExceptHand(Table a,Table b)=>a.Melds==b.Melds && a.Rivers[0].SequenceEqual(b.Rivers[0]);
    private static bool SameOwn(Table a,Table b)=>a.Hand.SequenceEqual(b.Hand) && SameOwnExceptHand(a,b);
    private static bool SameTable(Table a,Table b)=>SameOwn(a,b) && a.Wall==b.Wall &&
        Enumerable.Range(1,3).All(i=>a.Rivers[i].SequenceEqual(b.Rivers[i]));
    private static bool AddedOne(ImmutableArray<VisibleTile> before,ImmutableArray<VisibleTile> after)
    {var remainder=after.ToList();foreach(var tile in before)if(!remainder.Remove(tile))return false;return remainder.Count==1;}
    private static bool Extends(Table a,Table b)=>Enumerable.Range(0,4).All(i=>b.Rivers[i].Length>=a.Rivers[i].Length &&
        b.Rivers[i].Take(a.Rivers[i].Length).SequenceEqual(a.Rivers[i]));
}
