using Mahjong.Core;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Tests.Replay;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game.Variants;
using Xunit;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>
/// These are synthetic upstream-profile buffers, NOT CN live captures. Replay compatibility
/// certifies managed behavior only and does not certify any Chinese-client offset or callback.
/// </summary>
public sealed class CnExperimentalReadContractTests
{
    [Theory]
    [InlineData("emj.json")]
    [InlineData("emj_l.json")]
    public void Placeholder_seat_and_round_indices_are_never_advertised_as_known(string file)
    {
        var profile = JsonLayoutProfileLoader.Load(Path.Combine(TestPaths.LayoutsDir, file));
        var memory = new AddonMemoryBuilder(profile).WithScores(25000, 25000, 25000, 25000)
            .WithHand("1234m456p789s1234z").Build();
        var variant = new BaseEmjVariant(profile, new StubPluginLog(), Path.GetTempPath());
        var values = new AtkValueRecord[profile.AtkValues.StateCode + 1];
        values[profile.AtkValues.StateCode] = AtkValueRecord.OfInt(profile.StateCodes.OurTurnDiscard);
        var result = variant.BuildSnapshotFromMemory(memory, values, new(new MeldTracker(), null), false);
        Assert.NotNull(result);
        Assert.False(result.SeatInfoKnown);
        Assert.Equal(ActionFlags.Discard, result.Legal.Flags);
        Assert.Empty(result.Seats[0].Discards); // Profiles have no discard-array offset; this is an upstream limitation.
    }
}
