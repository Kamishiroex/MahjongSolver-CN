using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Core;
using Mahjong.Engine;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;
using Mahjong.Rules.Rulesets;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PublicShapeFuritenTests
{
    private static readonly PublicObservationContext Context = new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
        new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
    private static readonly int[] OpenHand = [1, 2, 3, 13, 13, 18, 19, 20, 21, 22];
    private static readonly int[] ClosedHand = [0, 1, 2, 10, 11, 12, 13, 13, 18, 19, 20, 21, 22];
    private static PublicImageInventory Melds => new(Enumerable.Range(24, 3).Select(id =>
        new PublicImageTile(new(id), $"Emj/115/{id}", "Emj/115", 0, 0, 20, 30, 0, false, true)).ToImmutableArray(),
        true, true, true, false, 3, 0, []) { Groups = [new("Emj/115", 3, 3, 0, true, "PUBLIC_CHI_THREE_FACE_PATTERN", null)] };

    private static PublicSnapshot Snapshot(int sequence, int phase, bool? riichi = false)
    {
        var s = PublicFuritenTrackerTests.Snapshot(sequence, riichi: riichi);
        var r = s.Observation!;
        var hand = riichi == true ? ClosedHand : OpenHand;
        var players = s.Players.Select(p => p with
        {
            RiverImages = Field<PublicImageInventory>.Known(new([], true, true, true, true, 0, 0, []), r),
        }).ToImmutableArray();
        if (riichi != true) players = players.SetItem(0, players[0] with { MeldImages = Field<PublicImageInventory>.Known(Melds, r) });
        for (int actor = 1; actor <= Math.Min(phase, 2); actor++)
        {
            var tile = new PublicImageTile(new(actor == 1 ? 20 : 23), $"Emj/river{actor}/1", "", 0, 0, 20, 30, 0, false, true)
                { DisplayPosition = new(1, 1, 1, false) };
            players = players.SetItem(actor, players[actor] with
            {
                RiverImages = Field<PublicImageInventory>.Known(new([tile], true, true, true, false, 1, 0, []), r),
            });
        }
        return s with
        {
            Players = players,
            WallRemaining = Field<int>.Known(40 - phase, r),
            LowerVisibleFaces = Field<ImmutableArray<PublicHandTile>>.Known(hand.Select((id, index) =>
                new PublicHandTile(new(id), new($"hand/{index}", index, sequence))).ToImmutableArray(), r),
        };
    }
    private static PublicSnapshot Observe(PublicFuritenTracker tracker, PublicSnapshot s, string token = "shape") =>
        tracker.Observe(s, token, PublicFuritenTrackerTests.Addon(false), Context);

    [Fact]
    public void Official_two_sided_wait_example_has_no_yaku_on_three_but_yaku_on_six()
    {
        // Constructed instance of the official 45s -> 3s/6s example, not a live capture.
        var waiting = Hand.FromTiles(OpenHand.Select(Tile.FromId), [Meld.Chi(Tile.FromId(24), Tile.FromId(24), 3)]);
        var scorer = new Scorer(new DomanRuleSet());
        Assert.Null(scorer.Evaluate(waiting.WithTileAdded(Tile.FromId(20)), new(Tile.FromId(20), WinKind.Ron)));
        Assert.NotNull(scorer.Evaluate(waiting.WithTileAdded(Tile.FromId(23)), new(Tile.FromId(23), WinKind.Ron)));
        Assert.True(PublicWinningShape.Completes(OpenHand.Select(i => new VisibleTile(i)).ToImmutableArray(), Melds, new(20)));
        Assert.True(PublicWinningShape.Completes(OpenHand.Select(i => new VisibleTile(i)).ToImmutableArray(), Melds, new(23)));
    }

    [Fact]
    public void No_ron_button_pass_blocks_later_winning_discard_but_not_current_response_window()
    {
        var t = new PublicFuritenTracker();
        Observe(t, Snapshot(1, 0));
        Assert.False(Observe(t, Snapshot(2, 1)).OurTemporaryFuriten.IsConfirmed);
        Assert.False(Observe(t, Snapshot(3, 1)).OurTemporaryFuriten.IsConfirmed);
        var result = Observe(t, Snapshot(4, 2));
        Assert.True(result.OurTemporaryFuriten.IsConfirmed);
        Assert.True(result.OurTemporaryFuriten.Value);
        Assert.Contains("shape-discard:2", result.OurTemporaryFuriten.Observation!.DerivationInputs);
        Assert.Equal(SynchronizationState.HistoryGap, result.Synchronization);
        Assert.False(result.VisibleActionMenu.Value!.Visible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Shape_based_pass_respects_riichi_and_own_next_draw(bool riichi)
    {
        var t = new PublicFuritenTracker();
        Observe(t, Snapshot(1, 0, riichi)); Observe(t, Snapshot(2, 1, riichi));
        var result = Observe(t, Snapshot(3, 2, riichi));
        Assert.True((riichi ? result.OurRiichiFuriten : result.OurTemporaryFuriten).Value);
        var draw = Snapshot(4, 2, riichi);
        draw = draw with
        {
            OwnDrawKind = Field<string>.Known("normal", draw.Observation!),
            LowerVisibleFaces = draw.LowerVisibleFaces with { Value = draw.LowerVisibleFaces.Value.Add(new(new(30), new("draw", 14, 4))) },
        };
        result = Observe(t, draw);
        Assert.True(result.OurTemporaryFuriten.IsConfirmed); Assert.False(result.OurTemporaryFuriten.Value);
        Assert.Equal(riichi, result.OurRiichiFuriten.Value);
    }

    [Fact]
    public void Riichi_shape_pass_followed_directly_by_own_draw_persists()
    {
        var t = new PublicFuritenTracker(); Observe(t, Snapshot(1, 0, true)); Observe(t, Snapshot(2, 1, true));
        var draw = Snapshot(3, 1, true);
        draw = draw with { OwnDrawKind = Field<string>.Known("normal", draw.Observation!),
            LowerVisibleFaces = draw.LowerVisibleFaces with { Value = draw.LowerVisibleFaces.Value.Add(new(new(30), new("draw", 14, 3))) } };
        Assert.True(Observe(t, draw).OurRiichiFuriten.Value);
    }

    [Fact]
    public void Starting_after_the_discard_does_not_reconstruct_a_pass()
    {
        var t = new PublicFuritenTracker(); Observe(t, Snapshot(1, 1));
        Assert.False(Observe(t, Snapshot(2, 2)).OurTemporaryFuriten.IsConfirmed);
    }

    [Theory]
    [InlineData("two-discards")][InlineData("hand")][InlineData("meld")][InlineData("unknown-meld")]
    [InlineData("stale-river")][InlineData("wall")][InlineData("gap")][InlineData("epoch")]
    [InlineData("unknown-riichi")][InlineData("red-identity")]
    public void Ambiguous_shape_progress_never_becomes_known_furiten(string kind)
    {
        var t = new PublicFuritenTracker(); Observe(t, Snapshot(1, 0, kind == "unknown-riichi" ? null : false));
        var s = Snapshot(2, kind == "two-discards" ? 2 : 1, kind == "unknown-riichi" ? null : false);
        var p = s.Players[0];
        switch (kind)
        {
            case "hand": s = s with { LowerVisibleFaces = s.LowerVisibleFaces with { Value = s.LowerVisibleFaces.Value.SetItem(0, new(new(30), new("hand/0", 0, 2))) } }; break;
            case "meld": p = p with { MeldImages = p.MeldImages with { Value = Melds with { Tiles = Melds.Tiles.SetItem(0, Melds.Tiles[0] with { Tile = new(23) }) } } }; break;
            case "unknown-meld": p = p with { MeldImages = Field<PublicImageInventory>.Unknown("gap") }; break;
            case "stale-river": p = p with { RiverImages = p.RiverImages with { Observation = Snapshot(1, 0).Observation } }; break;
            case "wall": s = s with { WallRemaining = Field<int>.Known(30, s.Observation!) }; break;
            case "gap": s = Snapshot(30, 1); break;
            case "red-identity": s = s with { LowerVisibleFaces = s.LowerVisibleFaces with { Value = s.LowerVisibleFaces.Value.SetItem(3, new(new(13, true), new("hand/3", 3, 2))) } }; break;
        }
        if (kind is "meld" or "unknown-meld" or "stale-river") s = s with { Players = s.Players.SetItem(0, p) };
        Observe(t, s, kind == "epoch" ? "new" : "shape");
        var result = Observe(t, Snapshot(kind == "gap" ? 31 : 3, 2));
        Assert.False(result.OurTemporaryFuriten.IsConfirmed);
        Assert.False(result.OurRiichiFuriten.Value);
    }

    [Theory]
    [InlineData("pairs", 6, true)]
    [InlineData("orphans", 33, true)]
    [InlineData("ordinary", 30, false)]
    [InlineData("fifth", 0, false)]
    public void Shape_checker_handles_special_hands_and_impossible_fifth_copy(string kind, int discard, bool expected)
    {
        int[] hand = kind switch
        {
            "pairs" => [0,0,1,1,2,2,3,3,4,4,5,5,6],
            "orphans" => [0,8,9,17,18,26,27,28,29,30,31,32,33],
            "fifth" => [0,0,0,0,1,2,9,10,11,18,19,20,27],
            _ => ClosedHand,
        };
        Assert.Equal(expected, PublicWinningShape.Completes(hand.Select(i => new VisibleTile(i)).ToImmutableArray(),
            new([],true,true,true,true,0,0,[]), new(discard)));
    }
}
