using Dalamud.Game;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class RuntimeIdentityTests
{
    private static readonly (string Framework, string Structs)[] HistoricalPairs =
    [
        ("cac6159a2c76e62a4e1f7bc347454c205808bb04", "1.0.0+f824354f4a6a2b1cd16cc8fcb670c7a66bf64880"),
        ("a198a02bdce1f3213cad618850ab1b2736307588", "1.0.0+243dc41e4d71f350cd80aa5eba8c75517f3d5154"),
        (RuntimeIdentity.TargetDalamud, RuntimeIdentity.TargetStructs),
    ];

    [Fact]
    public void Historical_public_DTOs_keep_original_identity_but_mixed_pairs_are_rejected()
    {
        for (int framework = 0; framework < HistoricalPairs.Length; framework++)
        for (int structs = 0; structs < HistoricalPairs.Length; structs++)
        {
            var identity = Identity(HistoricalPairs[framework].Framework, HistoricalPairs[structs].Structs);
            Assert.Equal(framework == structs,
                PublicObservationAssembler.CreateAuditedContext(identity, LowerHandProfile.EmjUldSha256) is not null);
            Assert.Null(PublicObservationAssembler.CreateAuditedContext(identity with { Error = "VERSION_CONTRACT" }, LowerHandProfile.EmjUldSha256));
        }
    }

    [Theory]
    [InlineData("15.0.3.6")]
    [InlineData("15.0.3.7")]
    [InlineData("15.1.0.0")]
    public void Same_API_patch_with_matching_contract_does_not_require_a_commit_allowlist(string version)
    {
        Assert.Null(RuntimeIdentity.Validate(RuntimeIdentity.TargetGame, 15, new string('a', 40),
            "1.0.0+" + new string('b', 40), ClientLanguage.ChineseSimplified, version, null));
    }

    [Fact]
    public void Compatible_patch_uses_existing_public_mapping_without_rewriting_provenance()
    {
        var identity = Identity(new string('a', 40), "1.0.0+" + new string('b', 40));
        Assert.Null(PublicObservationAssembler.CreateAuditedContext(identity, LowerHandProfile.EmjUldSha256));
        identity = identity with { CompatibilityProfile = FrameworkCompatibility.Profile };
        Assert.NotNull(PublicObservationAssembler.CreateAuditedContext(identity, LowerHandProfile.EmjUldSha256));
        Assert.Equal(new string('a', 40), identity.DalamudCommit);
        Assert.Null(PublicObservationAssembler.CreateAuditedContext(identity with { Error = "VERSION_CONTRACT" }, LowerHandProfile.EmjUldSha256));
        Assert.Null(PublicObservationAssembler.CreateAuditedContext(identity, new string('0', 64)));
    }

    [Theory]
    [InlineData("game", "VERSION_GAME")]
    [InlineData("api", "VERSION_API")]
    [InlineData("runtime", "VERSION_RUNTIME")]
    [InlineData("old-framework", "VERSION_DALAMUD")]
    [InlineData("new-api-version", "VERSION_DALAMUD")]
    [InlineData("unknown-framework", "VERSION_DALAMUD")]
    [InlineData("unknown-structs", "VERSION_STRUCTS")]
    [InlineData("layout", "VERSION_CONTRACT")]
    [InlineData("language", "VERSION_LANGUAGE")]
    public void Actual_incompatibility_remains_blocked(string change, string error)
    {
        var result = RuntimeIdentity.Validate(change == "game" ? "unknown" : RuntimeIdentity.TargetGame,
            change == "api" ? 16 : 15,
            change == "unknown-framework" ? null : RuntimeIdentity.TargetDalamud,
            change == "unknown-structs" ? "unknown" : RuntimeIdentity.TargetStructs,
            change == "language" ? ClientLanguage.English : ClientLanguage.ChineseSimplified,
            change == "old-framework" ? "15.0.3.5" : change == "new-api-version" ? "16.0.0.0" : RuntimeIdentity.MinimumDalamud,
            change == "layout" ? "VERSION_CONTRACT：changed field" : null,
            change == "runtime" ? 11 : 10);
        Assert.StartsWith(error, result);
    }

    private static RuntimeIdentity Identity(string framework, string structs) =>
        new(RuntimeIdentity.TargetGame, 15, RuntimeIdentity.MinimumDalamud, framework, structs, "ChineseSimplified", "10.0", null);
}
