using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class PublicMonitorPresentationTests
{
    private static ObservationReference Synthetic => new(1, DateTimeOffset.UnixEpoch, "synthetic-ui", "synthetic fixture", []);

    [Fact]
    public void Unknown_riichi_never_calls_the_false_formatter()
    {
        bool formatted = false;
        string text = PublicMonitorView.Describe(Field<bool>.Unknown("立直未读取"), value =>
        { formatted = true; return value ? "已立直" : "未立直"; });
        Assert.False(formatted);
        Assert.StartsWith("未知", text);
        Assert.DoesNotContain("未立直", text);
    }

    [Fact]
    public void Unknown_melds_are_not_displayed_as_an_empty_confirmed_collection()
    {
        string text = PublicMonitorView.Describe(Field<ImmutableArray<VisibleMeld>>.Unknown("副露未读取"),
            value => throw new InvalidOperationException("Unknown arrays must not be enumerated."));
        Assert.StartsWith("未知", text);
        Assert.DoesNotContain("无副露", text);
    }

    [Fact]
    public void Unknown_dealer_does_not_use_the_integer_default()
    {
        string text = PublicMonitorView.Describe(new PublicSnapshot().DealerPlayerId,
            value => throw new InvalidOperationException("No dealer value exists."));
        Assert.StartsWith("未知", text);
    }

    [Fact]
    public void Legacy_false_is_only_a_candidate_and_confirmed_false_remains_an_explicit_value()
    {
        var old = Field<bool>.Known(false, Synthetic, SourceKind.LegacyAssumption, MappingStatus.Candidate);
        string candidate = PublicMonitorView.Describe(old, value => value ? "已成立" : "未成立");
        Assert.Contains("候选，未验证", candidate);
        Assert.DoesNotContain("已确认", candidate);
        string known = PublicMonitorView.Describe(Field<bool>.Known(false, Synthetic), value => value ? "已成立" : "未成立");
        Assert.Contains("未成立（已确认", known);
    }

    [Fact]
    public void Missing_value_in_a_known_envelope_is_unknown_and_conflict_does_not_show_a_guess()
    {
        var absent = new Field<int> { Availability = Availability.Known, MappingStatus = MappingStatus.Validated,
            SourceKind = SourceKind.Observed, Observation = Synthetic };
        Assert.StartsWith("未知", PublicMonitorView.Describe(absent, _ => "默认零"));
        Assert.StartsWith("冲突", PublicMonitorView.Describe(Field<int>.Conflict(0, Synthetic, "来源冲突"), _ => "默认零"));
    }
}
