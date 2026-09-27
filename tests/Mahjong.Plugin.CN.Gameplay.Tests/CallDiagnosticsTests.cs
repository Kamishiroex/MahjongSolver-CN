using System.Text;
using System.Text.Json;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.GameState;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Plugin.Dalamud.Tests.Replay;
using Mahjong.Plugin.Dalamud.Tests.Stubs;
using Mahjong.Plugin.Game.Variants;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class CallDiagnosticsTests
{
    [Fact]
    public unsafe void Classic_route_scan_retains_only_normalized_whitelist_labels_within_original_limit()
    {
        var unknownBytes = Encoding.UTF8.GetBytes("private-chat-or-account\0");
        var tsumoBytes = Encoding.UTF8.GetBytes("自摸\0");
        var passBytes = Encoding.UTF8.GetBytes("放弃\0");
        var outsideBytes = Encoding.UTF8.GetBytes("立直\0");
        fixed (byte* unknown = unknownBytes)
        fixed (byte* tsumo = tsumoBytes)
        fixed (byte* pass = passBytes)
        fixed (byte* outside = outsideBytes)
        {
            AtkValue* values = stackalloc AtkValue[21];
            new Span<AtkValue>(values, 21).Clear();
            values[2].Type = AtkValueType.String;
            values[2].String = unknown;
            values[3].Type = AtkValueType.ConstString;
            values[3].String = tsumo;
            values[4].Type = AtkValueType.ManagedString;
            values[4].String = pass;
            values[20].Type = AtkValueType.String;
            values[20].String = outside;
            AtkUnitBase unit = default;
            unit.AtkValues = values;
            unit.AtkValuesCount = 21;

            Assert.True(InputDispatcher.HasClassicButtonLabels(&unit, out var labels));
            Assert.Equal(new[] { "Tsumo", "Pass" }, labels);
            string json = JsonSerializer.Serialize(labels);
            Assert.DoesNotContain("private", json);
            Assert.DoesNotContain("Riichi", json);

            // With only unknown text in the scanned range, classification remains false.
            unit.AtkValuesCount = 3;
            Assert.False(InputDispatcher.HasClassicButtonLabels(&unit, out var noLabels));
            Assert.Empty(noLabels);
        }
    }

    [Fact]
    public void Blocked_and_invalid_call_attempts_are_recorded_without_touching_native_addon()
    {
        var gui = RuntimeModeLifecycleTests.ServiceProxy.Create<IGameGui>((_, _) =>
            throw new InvalidOperationException("must not read game pointers"));
        bool allowed = false;
        var dispatcher = new InputDispatcher(new MahjongAddon(gui, new StubPluginLog()), canOperate: () => allowed);
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, dispatcher.DispatchCallOption(0));
        var first = dispatcher.LastCallDispatch!;
        Assert.Equal(-1, first.StateCode);
        Assert.Equal(0, first.Option);
        Assert.Equal("not-submitted", first.Route);
        Assert.Empty(first.ParentLabels);
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, first.Result);
        Assert.Null(first.NativeReturn);

        allowed = true;
        Assert.Equal(InputDispatcher.DispatchResult.InvalidSlot, dispatcher.DispatchCallOption(-1));
        Assert.Equal(InputDispatcher.DispatchResult.InvalidSlot, dispatcher.LastCallDispatch!.Result);
        Assert.Equal(-1, dispatcher.LastCallDispatch.Option);
        Assert.NotSame(first, dispatcher.LastCallDispatch);
        Assert.Equal(InputDispatcher.DispatchResult.OperationBlocked, first.Result);
        Assert.Equal(0, first.Option);
    }

    [Fact]
    public void Menu_observation_is_filtered_immutable_and_clears_hidden_list_evidence()
    {
        var profile = JsonLayoutProfileLoader.Load(Path.Combine(TestPaths.LayoutsDir, "emj.json"));
        var variant = new BaseEmjVariant(profile, new StubPluginLog(), Path.GetTempPath());
        var memory = new AddonMemoryBuilder(profile).WithScores(25000, 25000, 25000, 25000)
            .WithHand("234m456m678p33567s").Build();
        var labels = new List<string> { "自摸", "放弃", "private-character-name", "自摸！" };
        AtkValueRecord[] values = [AtkValueRecord.OfInt(6), AtkValueRecord.OfString("private-chat"),
            AtkValueRecord.OfString("立直")];
        variant.BuildSnapshotFromMemory(memory, values, new(new MeldTracker(), null), true, labels);
        var observed = variant.LastCallMenu!;
        Assert.True(observed.CallModalVisible);
        Assert.Equal(6, observed.StateCode);
        Assert.Equal(new[] { "Tsumo", "Pass" }, observed.VisibleListLabels);
        Assert.Equal(new[] { "Riichi" }, observed.ParentLabels);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(observed));
        labels.Clear();
        Assert.Equal(2, observed.VisibleListLabels.Length);

        variant.BuildSnapshotFromMemory(memory, values, new(new MeldTracker(), null), false, ["自摸"]);
        Assert.False(variant.LastCallMenu!.CallModalVisible);
        Assert.Empty(variant.LastCallMenu.VisibleListLabels);
        Assert.True(observed.CallModalVisible); // Retained observations cannot mutate after a later read.
    }
}
