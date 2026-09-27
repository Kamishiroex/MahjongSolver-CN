using System.Collections.Immutable;
using Mahjong.Cn.Events;
using Mahjong.Cn.PublicState;
using Mahjong.Core;
using Xunit;

namespace Mahjong.Cn.Tests;

// Synthetic fixtures exercise logic only; they are not CN layout/identity evidence.
public sealed class TableReconcilerTests
{
    private static readonly Guid Session = Guid.Parse("618b5195-1a82-445b-ae27-c0dc682cd276");
    private static readonly DateTimeOffset Time = DateTimeOffset.Parse("2026-09-24T00:00:00Z");
    private static readonly ObservationReference Source = new(10, Time, "synthetic", "synthetic only");
    private static Field<T> K<T>(T value) => Field<T>.Known(value, Source);
    private static ImmutableArray<VisibleTile> Hand => [new(0), new(1), new(2), new(3), new(4, true), new(6), new(7), new(8), new(9), new(10)];
    private static VisibleMeld Pon => new(MeldKind.Pon, [new(27), new(27), new(27)], 1, 0, new(27));
    private static ImmutableArray<RecoveryRiver> Rivers => Enum.GetValues<ScreenPosition>()
        .Select(p => new RecoveryRiver(p, K<ImmutableArray<VisibleDiscard>>([]))).ToImmutableArray();
    private static MeldRecoveryCheckpoint Checkpoint => new(Session, 10, Time, "synthetic-profile", K(Hand),
        K<ImmutableArray<VisibleMeld>>([Pon]), Rivers) { MeldSeatBasis = RecoverySeatBasis.RelativeToLocalPlayer };
    private static RecoveryTableObservation Current => new(Session, 11, Time.AddSeconds(1), "synthetic-profile", K(Hand),
        Field<ImmutableArray<VisibleMeld>>.Unknown("not visible"), Rivers) { Stable = true, MeldSeatBasis = RecoverySeatBasis.RelativeToLocalPlayer };

    [Fact] public void Continuous_observation_restores_meld_inventory_across_hand_sort_without_claiming_ai_history()
    {
        var current = Current with { OwnHand = K(Hand.Reverse().ToImmutableArray()) };
        var result = TableReconciler.Compare(Checkpoint, current, RecoveryContinuity.ContinuousObservation);
        Assert.True(result.CanRestoreOwnMelds);
        Assert.False(result.HistoryComplete);
        var restored = Assert.Single(result.RestoredOwnMelds);
        Assert.Equal(Pon.Kind, restored.Kind);
        Assert.Equal(Pon.Tiles.ToArray(), restored.Tiles.ToArray());
        Assert.Equal(Pon.FromSeat, restored.FromSeat);
        Assert.Equal(Pon.ClaimedTile, restored.ClaimedTile);
        Assert.Equal(RecoverySeatBasis.RelativeToLocalPlayer, result.MeldSeatBasis);
    }

    [Fact] public void Interrupted_identical_hand_and_rivers_cannot_exclude_missed_added_kan()
    {
        var result = TableReconciler.Compare(Checkpoint, Current, RecoveryContinuity.Interrupted);
        Assert.False(result.CanRestoreOwnMelds);
        Assert.Contains(result.Issues, i => i.Code == "meld-evidence-required");
        Assert.Contains(result.Issues, i => i.Code == "history-gap");
    }

    [Fact] public void Independently_observed_current_melds_can_rebuild_without_invented_round_id()
    {
        var current = Current with { ObservationSessionId = Guid.NewGuid(), VisibleOwnMelds = K<ImmutableArray<VisibleMeld>>([Pon]) };
        var result = TableReconciler.RebuildCurrentState(current);
        Assert.True(result.CanRestoreOwnMelds);
        Assert.False(current.RoundId.HasValue);
        Assert.False(result.HistoryComplete);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public void Unknown_seat_basis_or_self_source_cannot_turn_wind_into_relative_player(bool invalidSource)
    {
        var current = Current with
        {
            VisibleOwnMelds = K<ImmutableArray<VisibleMeld>>([invalidSource ? Pon with { FromSeat = 0 } : Pon]),
            MeldSeatBasis = invalidSource ? RecoverySeatBasis.RelativeToLocalPlayer : RecoverySeatBasis.Unknown,
        };
        Assert.False(TableReconciler.RebuildCurrentState(current).CanRestoreOwnMelds);
    }

    [Fact] public void Red_change_candidate_or_unreferenced_derivation_cannot_supply_trusted_recovery()
    {
        var noRed = Hand.SetItem(4, new(4));
        Assert.False(TableReconciler.Compare(Checkpoint, Current with { OwnHand = K(noRed) }, RecoveryContinuity.ContinuousObservation).CanRestoreOwnMelds);
        var candidate = K(Hand) with { MappingStatus = MappingStatus.Candidate };
        Assert.False(TableReconciler.Compare(Checkpoint, Current with { OwnHand = candidate }, RecoveryContinuity.ContinuousObservation).CanRestoreOwnMelds);
        var derived = K(Hand) with { SourceKind = SourceKind.Derived };
        Assert.False(TableReconciler.Compare(Checkpoint, Current with { OwnHand = derived }, RecoveryContinuity.ContinuousObservation).CanRestoreOwnMelds);
    }

    [Fact] public void New_round_profile_change_animation_and_river_conflict_block_restoration()
    {
        Assert.Equal(ReconciliationStatus.Conflict, TableReconciler.Compare(Checkpoint with { RoundId = K("a") },
            Current with { RoundId = K("b") }, RecoveryContinuity.ContinuousObservation).Status);
        Assert.Equal(ReconciliationStatus.Conflict, TableReconciler.Compare(Checkpoint, Current with { RuntimeFingerprint = "other" }, RecoveryContinuity.ContinuousObservation).Status);
        Assert.False(TableReconciler.Compare(Checkpoint, Current with { Stable = false }, RecoveryContinuity.ContinuousObservation).CanRestoreOwnMelds);
        var oldRiver = Rivers.SetItem(0, new(ScreenPosition.Lower, K<ImmutableArray<VisibleDiscard>>([new(new(4, true), null, null)])));
        var newRiver = Rivers.SetItem(0, new(ScreenPosition.Lower, K<ImmutableArray<VisibleDiscard>>([new(new(4), null, null)])));
        Assert.Equal(ReconciliationStatus.Conflict, TableReconciler.Compare(Checkpoint with { Rivers = oldRiver }, Current with { Rivers = newRiver }, RecoveryContinuity.Interrupted).Status);
    }

    private static ExperimentalRecoveryAnchor Anchor => new(Hand, [25000, 25000, 25000, 25000], [new(18)], [1, 0, 0, 0],
        Enum.GetValues<ScreenPosition>().Select(p => new VisibleAreaFootprint(p,
            p == ScreenPosition.Lower ? [new VisibleTile(31)] : [], true, "synthetic complete area")).ToImmutableArray());
    private static ExperimentalRecoveryEvidence Proof => new(Anchor, Anchor,
        [new(MeldKind.Pon, [27, 27, 27], 0, 1, 0, 27)],
        [new("own/meld/0", [new(27), new(27), new(27)], 3, true, true, "synthetic complete face group")]);

    [Fact] public void Experimental_match_is_explicit_and_does_not_promote_unknown_fields()
    {
        var current = Current with { OwnHand = Field<ImmutableArray<VisibleTile>>.Unknown("managed legacy anchor is separate") };
        var result = ExperimentalTableReconciler.CompareExperimental(Checkpoint, current, Proof);
        Assert.True(result.CanRestoreExperimentalMelds);
        Assert.False(result.CanRestoreOwnMelds);
        Assert.False(result.HistoryComplete);
        Assert.Empty(result.RestoredOwnMelds);
        Assert.Single(result.ExperimentalMelds);
        Assert.False(current.OwnHand.HasValue);
    }

    [Fact] public void Interrupted_progress_allows_changed_hand_and_extended_visible_river_without_inventing_event_order()
    {
        var now = Anchor with
        {
            OwnHand = Hand.SetItem(0, new(11)), RiverCounts = [2, 0, 0, 0],
            TableFootprint = Anchor.TableFootprint.SetItem(0, new(ScreenPosition.Lower, [new(31), new(0)], true, "complete")),
        };
        var proof = Proof with { CurrentAnchor = now, Mode = ExperimentalRecoveryMode.InterruptedProgress };
        var result = ExperimentalTableReconciler.CompareExperimental(Checkpoint, Current, proof);
        Assert.True(result.CanRestoreExperimentalMelds);
        Assert.False(result.HistoryComplete);
        Assert.False(ExperimentalTableReconciler.CompareExperimental(Checkpoint, Current, proof with { Mode = ExperimentalRecoveryMode.Exact }).CanRestoreExperimentalMelds);
    }

    [Theory] [InlineData("added-kan")] [InlineData("hidden-face")] [InlineData("missing-red")]
    [InlineData("duplicate-group")] [InlineData("shortened-river")] [InlineData("missing-footprint")]
    public void Experimental_recovery_rejects_incomplete_or_conflicting_evidence(string failure)
    {
        var proof = Proof;
        proof = failure switch
        {
            "added-kan" => proof with { CurrentMeldGroups = [proof.CurrentMeldGroups[0] with { Faces = [new(27), new(27), new(27), new(27)], VisibleSlots = 4 }] },
            "hidden-face" => proof with { CurrentMeldGroups = [proof.CurrentMeldGroups[0] with { AllVisibleSlotsDecoded = false }] },
            "missing-red" => proof with { CurrentAnchor = Anchor with { OwnHand = Hand.SetItem(4, new(4)) } },
            "duplicate-group" => proof with { CurrentMeldGroups = [proof.CurrentMeldGroups[0], proof.CurrentMeldGroups[0]] },
            "shortened-river" => proof with { CurrentAnchor = Anchor with { RiverCounts = [0, 0, 0, 0] }, Mode = ExperimentalRecoveryMode.InterruptedProgress },
            "missing-footprint" => proof with { CurrentAnchor = Anchor with { TableFootprint = Anchor.TableFootprint.SetItem(0, new(ScreenPosition.Lower, [], false, "missing")) } },
            _ => throw new InvalidOperationException(),
        };
        Assert.False(ExperimentalTableReconciler.CompareExperimental(Checkpoint, Current, proof).CanRestoreExperimentalMelds);
    }

    [Fact] public void Experimental_red_count_stays_aggregate_and_must_match_visible_faces()
    {
        var hand = Hand.SetItem(4, new(5));
        var anchor = Anchor with { OwnHand = hand };
        var proof = new ExperimentalRecoveryEvidence(anchor, anchor,
            [new(MeldKind.Pon, [4, 4, 4], 1, 2, 3, 4)],
            [new("meld/0", [new(4), new(4, true), new(4)], 3, true, true, "complete")]);
        var result = ExperimentalTableReconciler.CompareExperimental(Checkpoint, Current, proof);
        Assert.True(result.CanRestoreExperimentalMelds);
        Assert.Equal(1, result.ExperimentalMelds[0].RedTileCount);
        Assert.Empty(result.RestoredOwnMelds);
        Assert.False(ExperimentalTableReconciler.CompareExperimental(Checkpoint, Current, proof with
        { CurrentMeldGroups = [proof.CurrentMeldGroups[0] with { Faces = [new(4), new(4), new(4)] }] }).CanRestoreExperimentalMelds);
    }

    [Fact] public void Progress_without_prior_river_anchor_does_not_claim_same_round()
    {
        var anchor = Anchor with { RiverCounts = [0, 0, 0, 0], TableFootprint = Anchor.TableFootprint.Select(x => x with { Tiles = [] }).ToImmutableArray() };
        var result = ExperimentalTableReconciler.CompareExperimental(Checkpoint, Current,
            Proof with { CheckpointAnchor = anchor, CurrentAnchor = anchor, Mode = ExperimentalRecoveryMode.InterruptedProgress });
        Assert.False(result.CanRestoreExperimentalMelds);
        Assert.Contains(result.Issues, i => i.Code == "prior-river-anchor-required");
    }
}
