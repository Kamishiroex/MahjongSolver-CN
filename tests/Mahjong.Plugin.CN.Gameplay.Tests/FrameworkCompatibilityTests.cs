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
}
