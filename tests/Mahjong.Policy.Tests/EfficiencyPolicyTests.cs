using Mahjong.Engine;
using Mahjong.Policy.Efficiency;
using Mahjong.Rules.Rulesets;
using Xunit;

namespace Mahjong.Policy.Tests;

public class EfficiencyPolicyTests
{
    private static readonly EfficiencyPolicy Policy = new();

    [Fact]
    public void Tsumo_accepted_when_hand_clears_min_han()
    {
        var s = Snapshots.Closed14("123m456p789s11123p", ActionFlags.Tsumo | ActionFlags.Discard);
        var choice = Policy.Choose(s);
        Assert.Equal(ActionKind.Tsumo, choice.Kind);
    }

    [Fact]
    public void Doman_accepts_closed_one_han_tsumo_without_riichi_or_dora()
    {
        // Menzen tsumo itself is a one-han yaku. This former regression incorrectly
        // called the hand yakuless and locked Doman to a two-han requirement.
        var doman = new EfficiencyPolicy(new DomanRuleSet());
        var s = Snapshots.Closed14("22666p123345s222z", ActionFlags.Tsumo | ActionFlags.Discard);
        var choice = doman.Choose(s);
        Assert.Equal(ActionKind.Tsumo, choice.Kind);
        Assert.Contains("1 han", choice.Reasoning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Doman_rejects_complete_open_no_yaku_hand_even_with_dora(bool hasDora)
    {
        var state = StateSnapshot.Empty with
        {
            // Same complete shape, but 123s is now open: menzen tsumo no longer applies.
            Hand = Tiles.Parse("22666p345s222z"),
            OurMelds = [Meld.Chi(Tile.FromId(18), Tile.FromId(18), 3)],
            OurSeat = 0,
            RoundWind = 0,
            DoraIndicators = hasDora ? [Tile.FromId(13)] : [], // 5p -> three 6p dora.
            Legal = new(ActionFlags.Tsumo | ActionFlags.Discard, [], [], [], []),
        };
        var choice = new EfficiencyPolicy(new DomanRuleSet()).Choose(state);
        Assert.Equal(ActionKind.Discard, choice.Kind);
    }

    [Theory]
    [InlineData(MeldKind.AnKan)]
    [InlineData(MeldKind.MinKan)]
    [InlineData(MeldKind.ShouMinKan)]
    public void Kan_after_replacement_draw_can_discard_with_eleven_closed_tiles(MeldKind kind)
    {
        var state = KanSnapshot(kind, "123m456p789s12z");
        Assert.Equal(15, state.Hand.Count + state.OurMelds[0].TileCount);
        Assert.Equal(14, Hand.FromTiles(state.Hand, state.OurMelds).TotalShantenTileCount);
        var choice = new EfficiencyPolicy(new DomanRuleSet()).Choose(state);
        Assert.Equal(ActionKind.Discard, choice.Kind);
        Assert.Contains(choice.DiscardTile!.Value, state.Hand);
        Assert.NotEmpty(DiscardScorer.Score(state));
    }

    [Theory]
    [InlineData(MeldKind.AnKan)]
    [InlineData(MeldKind.MinKan)]
    [InlineData(MeldKind.ShouMinKan)]
    public void Kan_before_replacement_draw_rejects_premature_discard_with_ten_closed_tiles(MeldKind kind)
    {
        // The physical tile count is 14, but the replacement tile has not arrived.
        var state = KanSnapshot(kind, "123m456p789s1z");
        Assert.Equal(14, state.Hand.Count + state.OurMelds[0].TileCount);
        var choice = new EfficiencyPolicy(new DomanRuleSet()).Choose(state);
        Assert.Equal(ActionKind.Pass, choice.Kind);
        Assert.StartsWith("hand state out of sync", choice.Reasoning);
        Assert.Throws<ArgumentException>(() => DiscardScorer.Score(state));
    }

    private static StateSnapshot KanSnapshot(MeldKind kind, string closedTiles)
    {
        var tile = Tile.FromId(31);
        var meld = kind switch
        {
            MeldKind.AnKan => Meld.AnKan(tile),
            MeldKind.MinKan => Meld.MinKan(tile, tile, 1),
            MeldKind.ShouMinKan => Meld.ShouMinKan(tile, tile, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return StateSnapshot.Empty with
        {
            Hand = Tiles.Parse(closedTiles),
            OurMelds = [meld],
            Legal = new(ActionFlags.Discard, [], [], [], []),
        };
    }

    [Fact]
    public void Ron_legal_returns_ron()
    {
        var s = Snapshots.Closed14("123m456p789s11123p", ActionFlags.Ron);
        var choice = Policy.Choose(s);
        Assert.Equal(ActionKind.Ron, choice.Kind);
    }

    [Fact]
    public void Pon_opportunity_passes_for_now()
    {
        var s = Snapshots.Closed14("123m456p789s234s55m", ActionFlags.Pon | ActionFlags.Pass);
        var choice = Policy.Choose(s);
        Assert.Equal(ActionKind.Pass, choice.Kind);
    }

    [Fact]
    public void Best_discard_does_not_regress_shanten()
    {
        var s = Snapshots.Closed14("123m456p789s234s5z6z");
        var choice = Policy.Choose(s);

        Assert.Equal(ActionKind.Discard, choice.Kind);
        Assert.NotNull(choice.DiscardTile);

        var chosen = choice.DiscardTile!.Value;
        Assert.True(chosen.Id is 31 or 32,
            $"expected 5z (31) or 6z (32), got {chosen} (id {chosen.Id}). Reasoning: {choice.Reasoning}");
    }

    [Fact]
    public void Discard_scorer_sorts_options_best_first()
    {
        var s = Snapshots.Closed14("123m456p789s234s5z6z");
        var scored = DiscardScorer.Score(s);
        Assert.NotEmpty(scored);
        for (int i = 1; i < scored.Length; i++)
            Assert.True(scored[i - 1].Score >= scored[i].Score,
                $"scored array not sorted: [{i - 1}]={scored[i - 1].Score} < [{i}]={scored[i].Score}");
    }

    [Fact]
    public void Dora_increases_score_of_cuts_that_retain_dora_tiles()
    {
        var noDora = Snapshots.Closed14("123m456p789s234s1m5m");
        var withDora = noDora with { DoraIndicators = [Tiles.Parse("4m")[0]] };

        double cut1mNoDora = DiscardScorer.Score(noDora).First(x => x.Discard.Id == 0).Score;
        double cut1mWithDora = DiscardScorer.Score(withDora).First(x => x.Discard.Id == 0).Score;

        Assert.True(cut1mWithDora > cut1mNoDora,
            $"dora indicator should bump the score of a cut that retains a dora tile. " +
            $"noDora={cut1mNoDora} withDora={cut1mWithDora}");

        var cut1m = DiscardScorer.Score(withDora).First(x => x.Discard.Id == 0);
        Assert.Equal(1, cut1m.DoraRetained);
    }

    [Fact]
    public void Isolated_honor_scores_higher_to_discard_than_connected_terminal()
    {
        var s = Snapshots.Closed14("123m456p789s234s5z9p");
        var scored = DiscardScorer.Score(s);
        Assert.NotEmpty(scored);
        Assert.Equal(ActionKind.Discard, Policy.Choose(s).Kind);
    }

    [Fact]
    public void Rejects_non_14_tile_hands()
    {
        var s = Snapshots.Closed14("123m456p789s234s55z");
        var thirteenTile = s with { Hand = Tiles.Parse("123m456p789s234s5z") };
        Assert.Throws<ArgumentException>(() => DiscardScorer.Score(thirteenTile));
    }

    /// <summary>State-6 SelfDeclareList popup (hand=14, Kan + Discard both legal). Declining the kan must fall through to discard scoring — returning Pass softlocks the addon (no Pass button on the list widget). Reproduces 2026-05-26 log capture: hand 223679m 88p 118888s, addon offered MinKan flag for AnKan on 8s.</summary>
    [Fact]
    public void Declined_kan_at_state_6_returns_discard_not_pass()
    {
        var hand = Tiles.Parse("223679m88p118888s");
        var seats = new SeatView[4];
        for (int i = 0; i < 4; i++)
            seats[i] = new SeatView([], [], [], false, -1, false, false);

        var kanCand = new MeldCandidate(
            MeldKind.AnKan,
            Tile.FromId(25),
            [Tile.FromId(25), Tile.FromId(25), Tile.FromId(25)],
            FromSeat: 0);

        var s = StateSnapshot.Empty with
        {
            Hand = hand,
            Seats = seats,
            Legal = new LegalActions(
                Flags: ActionFlags.Discard | ActionFlags.MinKan | ActionFlags.Pass,
                DiscardableTiles: [],
                PonCandidates: [],
                ChiCandidates: [],
                KanCandidates: [kanCand]),
        };

        var choice = Policy.Choose(s);
        Assert.NotEqual(ActionKind.Pass, choice.Kind);
        Assert.True(choice.Kind is ActionKind.Discard or ActionKind.AnKan,
            $"expected Discard or AnKan, got {choice.Kind} (reason: {choice.Reasoning})");
    }
}
