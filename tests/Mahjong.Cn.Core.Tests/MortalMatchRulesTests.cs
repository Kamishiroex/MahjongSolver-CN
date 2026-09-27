using System.Text.Json;
using System.Text.Json.Nodes;
using Mahjong.Cn.Engines;
using Xunit;

namespace Mahjong.Cn.Core.Tests;

public sealed class MortalMatchRulesTests
{
    [Fact]
    public void Response_identity_includes_rule_evidence_and_repeated_discard_slot()
    {
        var input = Snapshot();
        var hash = AkochanGlobalEngine.ComputeInputSha256(input);
        Assert.NotEqual(hash, AkochanGlobalEngine.ComputeInputSha256(input with
            { MatchRules = input.MatchRules! with { MatchEvidence = "unknown" } }));
        var e = new AkochanGlobalEvent(1, "dahai", 1, null, new(27), []) { RiverIndex = 0 };
        Assert.NotEqual(AkochanGlobalEngine.ComputeInputSha256(input with { KnownEvents = [e] }),
            AkochanGlobalEngine.ComputeInputSha256(input with { KnownEvents = [e with { RiverIndex = 1 }] }));
    }
    [Theory]
    [InlineData("east", 4)]
    [InlineData("hanchan", 8)]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void Only_observed_supported_match_types_confirm_length(string? match, int? hands)
    {
        var rules = DomanMatchRules.FromObservedMatch(match, null);
        Assert.Equal(hands, rules.ScheduledHands);
        Assert.Equal(hands is null ? null : match, rules.MatchType);
        Assert.Equal(hands is null ? "unknown" : "observed-current-duty", rules.MatchEvidence);
        Assert.Null(rules.TimeLimitFinalHand);
        Assert.Null(rules.OpenTanyao);
    }

    private static AkochanGlobalSnapshot Snapshot(int first = 4, int hand = 4)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "mortal-pon-20260926.json")));
        return doc.RootElement.GetProperty("ActualPublicInput").Deserialize<AkochanGlobalSnapshot>()! with
        {
            RoundWind = 0, HandNumber = hand, MatchFirstRound = first,
            MatchRules = DomanMatchRules.FromObservedMatch(first == 4 ? "east" : "hanchan", true),
        };
    }

    private static JsonObject Applied(AkochanGlobalSnapshot input)
    {
        int initial = (input.HandNumber - 1 + 4 - input.DealerPlayerId) % 4;
        int rank = input.Players.Count(p => p.Score > input.Players[0].Score ||
            p.Score == input.Players[0].Score && (p.PlayerId + initial) % 4 < initial);
        return JsonSerializer.SerializeToNode(new
        {
            feature_schema = 2, round_wind = input.RoundWind, rank,
            all_last = input.MatchFirstRound + input.RoundWind * 4 + input.HandNumber >= 8,
            match_context = new { actual_round_wind = input.RoundWind, actual_hand_number = input.HandNumber,
                progress = (input.MatchFirstRound + input.RoundWind * 4 + input.HandNumber - 1) / 7.0,
                progress_row = 27, confirmed_length = true,
                match_type = input.MatchRules!.MatchType, scheduled_hands = input.MatchRules.ScheduledHands },
        })!.AsObject();
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(0, 4)] [InlineData(4, 1)] [InlineData(4, 4)]
    public void Confirms_real_wind_schedule_progress_and_initial_seat_rank(int first, int hand)
    {
        var input = Snapshot(first, hand);
        MortalGlobalEngine.ValidateAppliedContext(JsonSerializer.SerializeToElement(Applied(input)), input);
    }

    [Theory]
    [InlineData("all_last")] [InlineData("round_wind")] [InlineData("rank")]
    [InlineData("feature_schema")] [InlineData("progress")] [InlineData("confirmed_length")]
    [InlineData("scheduled_hands")]
    public void Rejects_backend_that_ignores_or_misapplies_rules(string field)
    {
        var input = Snapshot();
        var applied = Applied(input);
        switch (field)
        {
            case "all_last": applied[field] = false; break;
            case "progress": applied["match_context"]![field] = 0; break;
            case "confirmed_length": applied["match_context"]![field] = false; break;
            case "scheduled_hands": applied["match_context"]![field] = 8; break;
            default: applied[field] = 99; break;
        }
        var ex = Assert.Throws<AkochanException>(() =>
            MortalGlobalEngine.ValidateAppliedContext(JsonSerializer.SerializeToElement(applied), input));
        Assert.Equal("MORTAL_APPLIED_RULES_MISMATCH", ex.Code);
    }
}
