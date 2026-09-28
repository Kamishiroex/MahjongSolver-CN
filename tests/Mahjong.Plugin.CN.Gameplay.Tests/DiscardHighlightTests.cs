using System.Collections.Immutable;
using Mahjong.Core;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Presentation;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class DiscardHighlightTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-28T00:00:00Z");
    private static (StateSnapshot State, LowerHandReading Reading, HandFaceCandidate[] Faces) Hand(float scale = 1)
    {
        int[] kinds = [0, 1, 2, 4, 4, 4, 9, 10, 11, 18, 19, 20, 27, 33];
        var faces = kinds.Select((kind, i) =>
        {
            uint icon = (uint)(i == 3 ? 76035 : 76001 + kind);
            return new HandFaceCandidate($"Emj/{1340001 + i}/9/4", (100 + 45 * i) * scale, 600 * scale,
                40 * scale, 52 * scale, icon, LowerHandProfile.VerifiedIconStatus,
                LowerHandImageReader.ClientTexturePathHash($"ui/icon/076000/{icon:D6}.tex"));
        }).ToArray();
        var tracker = new LowerHandTracker(); var addon = new AddonProbe("Emj", true, true, true, 50, [], null, faces);
        tracker.Observe(1, addon); var reading = tracker.Observe(2, addon);
        Assert.True(reading.Stable);
        var tiles = kinds.Select(Tile.FromId).ToArray();
        return (StateSnapshot.Empty with { Hand = tiles, AkaDora = 1, AddonStateCode = 30,
            Legal = new(ActionFlags.Discard | ActionFlags.Riichi, tiles, [], [], []) }, reading, faces);
    }
    [Theory][InlineData(.77f)][InlineData(1f)][InlineData(1.37f)][InlineData(2f)]
    public void Uses_observed_geometry_at_arbitrary_scales_and_prefers_nonred_copy(float scale)
    {
        var (state, reading, faces) = Hand(scale);
        var hint = DiscardHighlight.Create(state, ActionChoice.Discard(Tile.FromId(4)), reading, faces, Now, Now, null);
        Assert.NotNull(hint); Assert.Equal(faces[4].X, hint.X); Assert.Equal(faces[4].Width, hint.Width);
        Assert.DoesNotContain("赤", hint.Label);
        var red = DiscardHighlight.Create(state, ActionChoice.Discard(Tile.FromId(4)) with { DiscardRed = true }, reading, faces, Now, Now, null);
        Assert.Equal(faces[3].X, red!.X); Assert.Contains("赤", red.Label);
    }
    [Fact]
    public void Drawn_identity_is_required_and_matching_duplicate_is_selected_by_path()
    {
        var (state, reading, faces) = Hand();
        var choice = ActionChoice.Discard(Tile.FromId(4)) with { DiscardRed = false, DiscardTsumogiri = true };
        Assert.Null(DiscardHighlight.Create(state, choice, reading, faces, Now, Now, null));
        Assert.Equal(faces[5].X, DiscardHighlight.Create(state, choice, reading, faces, Now, Now, faces[5].Path)!.X);
        Assert.Equal(faces[4].X, DiscardHighlight.Create(state, choice with { DiscardTsumogiri = false }, reading, faces, Now, Now, faces[5].Path)!.X);
        Assert.Null(DiscardHighlight.Create(state with { OurRiichi = true }, choice with { DiscardTsumogiri = false }, reading, faces, Now, Now, faces[5].Path));
    }
    [Fact]
    public void Stale_changed_hidden_transition_and_illegal_states_clear_the_hint()
    {
        var (state, reading, faces) = Hand(); var choice = ActionChoice.Discard(Tile.FromId(4));
        Assert.Null(DiscardHighlight.Create(state, choice, reading, faces, Now, Now.AddMilliseconds(351), null));
        Assert.Null(DiscardHighlight.Create(state, choice, reading, faces, Now, Now.AddMilliseconds(-1), null));
        Assert.Null(DiscardHighlight.Create(state, choice, reading with { Stable = false }, faces, Now, Now, null));
        Assert.Null(DiscardHighlight.Create(state, choice, reading, null, Now, Now, null));
        Assert.Null(DiscardHighlight.Create(state with { Hand = state.Hand.Skip(1).ToArray() }, choice, reading, faces, Now, Now, null));
        Assert.Null(DiscardHighlight.Create(state with { Legal = LegalActions.None }, choice, reading, faces, Now, Now, null));
        foreach (var win in new[] { ActionFlags.Ron, ActionFlags.Tsumo })
            Assert.Null(DiscardHighlight.Create(state with { Legal = state.Legal with { Flags = state.Legal.Flags | win } }, choice, reading, faces, Now, Now, null));
        Assert.Null(DiscardHighlight.Create(state with { AddonStateCode = 29 }, choice, reading, faces, Now, Now, null));
        Assert.Null(DiscardHighlight.Create(state, ActionChoice.Pass("pending"), reading, faces, Now, Now, null));
        var changed = faces.ToArray(); changed[4] = changed[4] with { IconId = 76001 };
        Assert.Null(DiscardHighlight.Create(state, choice, reading, changed, Now, Now, null));
    }
}
