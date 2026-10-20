using System.Runtime.InteropServices;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class FrameworkCompatibilityTests
{
    [Fact]
    public void Installed_framework_satisfies_shipped_contract_without_native_calls() =>
        Assert.Null(FrameworkCompatibility.CheckLoaded());

    [Fact]
    public void Every_compiled_framework_reference_is_covered_by_reviewed_contract()
    {
        var required = FrameworkCompatibility.CaptureRequired(typeof(Plugin).Assembly);
        var shipped = FrameworkCompatibility.ReadContract();
        Assert.NotEmpty(required.Types);
        Assert.Null(FrameworkCompatibility.Validate(required, entry => shipped.Types
            .SingleOrDefault(t => t.Assembly == entry.Assembly && t.Name == entry.Name)?.Required));
    }

    [Theory]
    [InlineData("layout:Explicit:568:0")]
    [InlineData("field:RootNode:pointer:False:200")]
    [InlineData("method:QueueDuties")]
    [InlineData("attribute:FireCallback:signature")]
    [InlineData("constant:MouseClick:9")]
    public void Changed_required_layout_signature_or_enum_blocks_reading(string required)
    {
        var contract = Contract(required);
        Assert.StartsWith("VERSION_CONTRACT", FrameworkCompatibility.Validate(contract, _ => [required + "-changed"]));
        Assert.StartsWith("VERSION_CONTRACT", FrameworkCompatibility.Validate(contract, _ => null));
    }

    [Fact]
    public void Additive_framework_changes_do_not_require_a_plugin_update() =>
        Assert.Null(FrameworkCompatibility.Validate(Contract("required"), _ => ["added-method", "required", "added-field"]));

    [Fact]
    public void Missing_or_unknown_contract_fails_closed()
    {
        Assert.NotNull(FrameworkCompatibility.Validate(new(1, FrameworkCompatibility.Profile, []), _ => []));
        Assert.NotNull(FrameworkCompatibility.Validate(Contract("required") with { Schema = 2 }, _ => ["required"]));
    }

    private static FrameworkCompatibility.Contract Contract(string required) =>
        new(1, FrameworkCompatibility.Profile, [new("synthetic", "Fixture", [required])]);

    [Fact]
    public void Explicit_private_placeholder_rename_does_not_change_required_ABI()
    {
        var before = FrameworkCompatibility.DescribeLayout(typeof(ExplicitBefore));
        var after = FrameworkCompatibility.DescribeLayout(typeof(ExplicitAfter));
        Assert.Contains("field:Used:System.Int32:False:0", before);
        Assert.DoesNotContain(before, line => line.Contains("PrivatePlaceholder", StringComparison.Ordinal));
        Assert.True(before.ToHashSet(StringComparer.Ordinal).SetEquals(after));
        Assert.False(before.ToHashSet(StringComparer.Ordinal).SetEquals(FrameworkCompatibility.DescribeLayout(typeof(ExplicitMoved))));
    }

    [Fact]
    public void Sequential_private_field_order_is_still_a_layout_dependency()
    {
        var layout = FrameworkCompatibility.DescribeLayout(typeof(SequentialLayout));
        Assert.Contains("sequential-fields:PrivatePadding,Used", layout);
        Assert.Contains(layout, line => line.StartsWith("field:PrivatePadding:", StringComparison.Ordinal));
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct ExplicitBefore
    {
        [FieldOffset(0)] public int Used;
        [FieldOffset(4)] private int PrivatePlaceholder;
        public ExplicitBefore() { Used = PrivatePlaceholder = 0; }
        public int ReadPadding() => PrivatePlaceholder;
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct ExplicitAfter
    {
        [FieldOffset(0)] public int Used;
        [FieldOffset(4)] private int RenamedFlagStorage;
        public ExplicitAfter() { Used = RenamedFlagStorage = 0; }
        public int ReadPadding() => RenamedFlagStorage;
    }

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct ExplicitMoved
    {
        [FieldOffset(4)] public int Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SequentialLayout
    {
        private int PrivatePadding;
        public int Used;
        public SequentialLayout() { Used = PrivatePadding = 0; }
        public int ReadPadding() => PrivatePadding;
    }
}
