using Dalamud.Game;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class RuntimeIdentityTests
{
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void Historical_public_DTOs_keep_original_identity_but_mixed_pairs_are_rejected(bool oldFramework, bool oldStructs, bool allowed)
    {
        var identity = new RuntimeIdentity(RuntimeIdentity.TargetGame, 15, "15.0.3.5",
            oldFramework ? "cac6159a2c76e62a4e1f7bc347454c205808bb04" : RuntimeIdentity.TargetDalamud,
            oldStructs ? "1.0.0+f824354f4a6a2b1cd16cc8fcb670c7a66bf64880" : RuntimeIdentity.TargetStructs,
            "ChineseSimplified", "10.0", null);
        Assert.Equal(allowed, PublicObservationAssembler.CreateAuditedContext(identity, LowerHandProfile.EmjUldSha256) is not null);
        Assert.Null(PublicObservationAssembler.CreateAuditedContext(identity with { Error = "VERSION_DALAMUD" }, LowerHandProfile.EmjUldSha256));
    }

    [Fact]
    public void Audited_September_25_pair_passes_without_disabling_identity_gate() =>
        Assert.Null(RuntimeIdentity.Validate("2026.09.15.0000.0000", 15,
            "a198a02bdce1f3213cad618850ab1b2736307588", "1.0.0+243dc41e4d71f350cd80aa5eba8c75517f3d5154",
            ClientLanguage.ChineseSimplified));

    [Theory]
    [InlineData("game", "VERSION_GAME")]
    [InlineData("api", "VERSION_API")]
    [InlineData("old-framework", "VERSION_DALAMUD")]
    [InlineData("unknown-framework", "VERSION_DALAMUD")]
    [InlineData("old-structs", "VERSION_STRUCTS")]
    [InlineData("language", "VERSION_LANGUAGE")]
    public void Unknown_or_mixed_versions_remain_blocked(string change, string error)
    {
        string? result = RuntimeIdentity.Validate(change == "game" ? "unknown" : RuntimeIdentity.TargetGame,
            change == "api" ? 16 : 15,
            change == "old-framework" ? "cac6159a2c76e62a4e1f7bc347454c205808bb04" :
                change == "unknown-framework" ? null : RuntimeIdentity.TargetDalamud,
            change == "old-structs" ? "1.0.0+f824354f4a6a2b1cd16cc8fcb670c7a66bf64880" : RuntimeIdentity.TargetStructs,
            change == "language" ? ClientLanguage.English : ClientLanguage.ChineseSimplified);
        Assert.StartsWith(error, result);
    }
}
