using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PublicCurrentFactsTests
{
    [Fact]
    public void Live_public_header_excerpt_preserves_true_false_and_unknown_acceptance()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "current-riichi-20260925.json")));
        var addon = Probe() with { PublicRiichiCandidates = fixture.RootElement.GetProperty("Riichi").Deserialize<PublicRiichiCandidate[]>() };
        var result = PublicCurrentFacts.Apply(Snapshot(), addon, Context);
        Assert.True(result.Players[0].RiichiDeclared.Value);
        Assert.False(result.Players[0].RiichiEstablished.IsConfirmed);
        Assert.All(result.Players.Skip(1), p => { Assert.True(p.RiichiDeclared.IsConfirmed);
            Assert.False(p.RiichiDeclared.Value); Assert.True(p.Ippatsu.IsConfirmed); Assert.False(p.Ippatsu.Value); });
    }

    [Fact]
    public void Stale_river_does_not_recover_acceptance_in_a_new_observation()
    {
        var snapshot = Snapshot(Image(3, true), Image(4, false)) with { Observation = Ref with { Sequence = 11 } };
        Assert.False(PublicCurrentFacts.Apply(snapshot, Probe(true), Context).Players[0].RiichiEstablished.IsConfirmed);
    }
    [Fact]
    public void Real_20260925_public_snapshot_recovers_expired_ippatsu_but_not_fresh_acceptance()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "current-facts-20260925.json")));
        foreach (var item in fixture.RootElement.GetProperty("Cases").EnumerateArray())
        {
            var snapshot = item.GetProperty("Snapshot").Deserialize<PublicSnapshot>()!;
            var sticks = item.GetProperty("Riichi").Deserialize<PublicRiichiCandidate[]>()!;
            var addon = Probe() with { PublicRiichiCandidates = sticks };
            var player = PublicCurrentFacts.Apply(snapshot, addon, Context).Players[item.GetProperty("Actor").GetInt32()];
            Assert.True(player.RiichiDeclared.IsConfirmed); Assert.True(player.RiichiDeclared.Value);
            if (item.GetProperty("Case").GetString() == "expired")
            { Assert.True(player.RiichiEstablished.IsConfirmed); Assert.True(player.RiichiEstablished.Value);
                Assert.True(player.Ippatsu.IsConfirmed); Assert.False(player.Ippatsu.Value); }
            else { Assert.False(player.RiichiEstablished.IsConfirmed); Assert.False(player.Ippatsu.IsConfirmed); }
            // Same capture's actual resource identities, not hand-authored tile codes.
            var assembled = PublicObservationAssembler.Assemble(new() { Observation = snapshot.Observation },
                addon with { PublicDoraCandidates = item.GetProperty("Dora").Deserialize<PublicDoraCandidate[]>() }, Context);
            Assert.True(assembled.DoraDisplay.IsConfirmed);
            Assert.False(assembled.DoraMode.HasValue); // No mode text was supplied by this fixture.
        }
    }
    private static readonly ObservationReference Ref = new(10, DateTimeOffset.UnixEpoch, "fixture", "constructed, not live");
    private static readonly PublicObservationContext Context = new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
        new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
    private static PublicSnapshot Snapshot(params PublicImageTile[] river) => new()
    {
        Observation = Ref,
        Players = Enumerable.Range(0, 4).Select(i => new PlayerPublicState((ScreenPosition)i)
        { RiverImages = Field<PublicImageInventory>.Known(new(i == 0 ? river.ToImmutableArray() : [], true, true, true,
            river.Length == 0, river.Length, 0, []), Ref) }).ToImmutableArray(),
    };
    private static PublicImageTile Image(int order, bool sideways) => new(new VisibleTile(5),
        sideways ? "Emj/117/4" : "Emj/1180001/4", "", 0, 0, 20, 30, 0, false, true)
        { DisplayPosition = new(1, order, order, sideways) };
    private static AddonProbe Probe(bool? own = false) => new("Emj", true, true, true, 0, [], null,
        PublicRiichiCandidates: new[] { "bottom", "right", "top", "left" }.Select((d, i) =>
            new PublicRiichiCandidate(d, $"Emj/{100 + i}/2", i == 0 ? own : false,
                i == 0 && own == true ? "PUBLIC_RIICHI_STICK_CANDIDATE" :
                i == 0 && own is null ? "RIICHI_STICK_VISIBILITY_UNOBSERVED" : "PUBLIC_RIICHI_STICK_HIDDEN_OWNER")).ToArray());

    [Theory]
    [InlineData(643u, "hanchan", true)]
    [InlineData(644u, "hanchan", true)]
    [InlineData(645u, "hanchan", true)]
    [InlineData(650u, "hanchan", false)]
    [InlineData(766u, "east", true)]
    [InlineData(767u, "east", true)]
    [InlineData(768u, "east", true)]
    [InlineData(769u, "east", false)]
    public void Actual_current_duty_maps_match_and_open_tanyao(uint id, string type, bool open)
    {
        var s = PublicCurrentFacts.Apply(Snapshot(), Probe() with
        { PublicMatchRules = new(id, type, open, "PUBLIC_CURRENT_MAHJONG_DUTY_VERIFIED") }, Context);
        Assert.True(s.Rules.MatchType.IsConfirmed); Assert.Equal(type, s.Rules.MatchType.Value);
        Assert.Equal(open, s.Rules.OpenTanyao.Value);
        Assert.False(s.Rules.RedFivesEnabled.IsConfirmed); Assert.False(s.Rules.EndCondition.IsConfirmed);
    }

    [Fact]
    public void Missing_or_contradictory_duty_does_not_become_default_match()
    {
        Assert.False(PublicCurrentFacts.Apply(Snapshot(), Probe(), Context).Rules.MatchType.HasValue);
        Assert.False(PublicCurrentFacts.Apply(Snapshot(), Probe() with
        { PublicMatchRules = new(766, "hanchan", true, "PUBLIC_CURRENT_MAHJONG_DUTY_VERIFIED") }, Context).Rules.MatchType.HasValue);
    }

    [Fact]
    public void Hidden_owner_means_no_declared_riichi_and_no_ippatsu_but_absence_remains_unknown()
    {
        var p = PublicCurrentFacts.Apply(Snapshot(), Probe(), Context).Players[0];
        Assert.True(p.RiichiDeclared.IsConfirmed); Assert.False(p.RiichiDeclared.Value);
        Assert.True(p.RiichiEstablished.IsConfirmed); Assert.False(p.RiichiEstablished.Value);
        Assert.True(p.Ippatsu.IsConfirmed); Assert.False(p.Ippatsu.Value);
        Assert.False(PublicCurrentFacts.Apply(Snapshot(), Probe(null), Context).Players[0].RiichiDeclared.HasValue);
        Assert.False(PublicCurrentFacts.Apply(Snapshot(), Probe(), null).Players[0].RiichiDeclared.HasValue);
    }

    [Fact]
    public void Later_own_discard_recovers_accepted_and_expired_ippatsu_from_current_river()
    {
        var p = PublicCurrentFacts.Apply(Snapshot(Image(3, true), Image(4, false)), Probe(true), Context).Players[0];
        Assert.True(p.RiichiEstablished.IsConfirmed); Assert.True(p.RiichiEstablished.Value);
        Assert.True(p.Ippatsu.IsConfirmed); Assert.False(p.Ippatsu.Value);
        Assert.Equal(Ref, p.RiverImages.Observation);
    }

    [Fact]
    public void Stick_and_declaration_alone_or_earlier_discard_do_not_prove_acceptance()
    {
        var p = PublicCurrentFacts.Apply(Snapshot(Image(3, true), Image(2, false)), Probe(true), Context).Players[0];
        Assert.True(p.RiichiDeclared.Value); Assert.False(p.RiichiEstablished.HasValue); Assert.False(p.Ippatsu.HasValue);
    }

    [Fact]
    public void Hidden_stick_and_current_declaration_discard_conflict()
    {
        var p = PublicCurrentFacts.Apply(Snapshot(Image(3, true)), Probe(), Context).Players[0];
        Assert.Equal(Availability.Conflict, p.RiichiDeclared.Availability);
    }
}
