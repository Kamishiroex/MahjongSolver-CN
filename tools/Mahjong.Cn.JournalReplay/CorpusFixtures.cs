using Mahjong.Core;
using Mahjong.Plugin.CN.Journaling;
using Mahjong.Policy.Abstractions;
using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.Engines;

internal static class CorpusFixtures
{
    internal static DecisionCorpusFile Create()
    {
        // Deterministic constructed inputs for exercising the replay tool, not live evidence.
        string[] hands=["123456m234p678s55z","234567m345p456s11z","1123456m234p789s1z"];
        return new(1,"Constructed rule fixtures; no captured player history",new string('0',64),hands.Select((h,i)=>
        {
            var tiles=Tiles.Parse(h);
            var state=StateSnapshot.Empty with {Hand=tiles,Legal=LegalActions.None with {Flags=ActionFlags.Discard,DiscardableTiles=tiles},
                SeatInfoKnown=true,AddonStateCode=30,DoraIndicators=[Tile.FromId(8)]};
            var input=new AkochanGlobalSnapshot(0,0,1,0,0,0,70,tiles.Select(t=>new VisibleTile(t.Id,false)).ToImmutableArray(),[new(8,false)],
                Enumerable.Range(0,4).Select(seat=>new AkochanGlobalPlayer(seat,seat,25000,false,false,null,[],[])).ToImmutableArray(),
                new("tsumo",0,new(tiles[^1].Id,false)),["constructed offline fixture; history unknown"])
                {ContextKey="constructed-corpus-"+i,LegalActions=ActionFlags.Discard,OwnDrawKind="normal",HistoryComplete=false};
            return new CorpusCase(DecisionCorpus.Key(state,input),state,input,new(ActionKind.Pass),"fixture",null,null,null,null,null);
        }).ToArray(),0);
    }
}
