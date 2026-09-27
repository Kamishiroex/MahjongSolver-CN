using Mahjong.Core;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.GameState.Variants;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PassOutcomeObservationTests
{
    // Synthetic hand and meld only; the regression is a dismissed prompt retaining
    // the same addon state and tile counts, not any user's captured tile sequence.
    private static StateSnapshot WaitingHand => StateSnapshot.Empty with
    {
        Hand = Tiles.Parse("123m456p789s1z"),
        OurMelds = [Meld.Pon(Tile.FromId(32), Tile.FromId(32), 1)],
        AddonStateCode = 15,
        Legal = LegalActions.None,
    };

    [Fact]
    public void Pass_observes_closed_prompt_without_changing_addon_state_hand_or_meld_counts()
    {
        var before = WaitingHand with { Legal = new(ActionFlags.Chi | ActionFlags.Pass, [], [], [], []) };
        var after = before with { Legal = LegalActions.None };
        Assert.Equal(before.AddonStateCode, after.AddonStateCode);
        Assert.Equal(before.Hand.Count, after.Hand.Count);
        Assert.Equal(before.OurMelds.Count, after.OurMelds.Count);
        Assert.True(AutoPlayLoop.IsDismissedPassPrompt("pass", before.Legal.Flags, after,
            new CallMenuObservation(15, false, [], [])));
    }

    [Fact]
    public void Unchanged_open_prompt_still_requires_normal_outcome_or_timeout()
    {
        var waiting = WaitingHand with { Legal = new(ActionFlags.Pon | ActionFlags.Pass, [], [], [], []) };
        Assert.False(AutoPlayLoop.IsDismissedPassPrompt("pass", waiting.Legal.Flags, waiting,
            new CallMenuObservation(15, true, ["Pon", "Pass"], [])));
    }

    [Fact]
    public void Temporarily_missing_labels_while_menu_remains_visible_do_not_complete_pass()
    {
        Assert.False(AutoPlayLoop.IsDismissedPassPrompt("pass", ActionFlags.Chi | ActionFlags.Pass,
            WaitingHand, new CallMenuObservation(15, true, [], [])));
    }

    [Theory]
    [InlineData("discard")]
    [InlineData("tsumo")]
    [InlineData("ron")]
    [InlineData("pon")]
    [InlineData("riichi")]
    public void Menu_closure_does_not_relax_other_action_outcome_checks(string label)
    {
        Assert.False(AutoPlayLoop.IsDismissedPassPrompt(label, ActionFlags.Tsumo | ActionFlags.Pass,
            WaitingHand, new CallMenuObservation(15, false, [], [])));
    }

    [Fact]
    public void Missing_snapshot_missing_menu_or_wrong_menu_state_is_not_completion_evidence()
    {
        var flags = ActionFlags.Chi | ActionFlags.Pass;
        Assert.False(AutoPlayLoop.IsDismissedPassPrompt("pass", flags, null,
            new CallMenuObservation(15, false, [], [])));
        Assert.False(AutoPlayLoop.IsDismissedPassPrompt("pass", flags, WaitingHand, null));
        Assert.False(AutoPlayLoop.IsDismissedPassPrompt("pass", flags, WaitingHand,
            new CallMenuObservation(6, false, [], [])));
    }

    [Fact]
    public void Pass_without_an_original_call_offer_cannot_claim_prompt_completion()
    {
        Assert.False(AutoPlayLoop.IsDismissedPassPrompt("pass", ActionFlags.Pass, WaitingHand,
            new CallMenuObservation(15, false, [], [])));
    }

    [Fact]
    public void Remaining_call_permission_rejects_inconsistent_hidden_menu_evidence()
    {
        var waiting = WaitingHand with { Legal = new(ActionFlags.Ron | ActionFlags.Pass, [], [], [], []) };
        Assert.False(AutoPlayLoop.IsDismissedPassPrompt("pass", waiting.Legal.Flags, waiting,
            new CallMenuObservation(15, false, [], [])));
    }
}
