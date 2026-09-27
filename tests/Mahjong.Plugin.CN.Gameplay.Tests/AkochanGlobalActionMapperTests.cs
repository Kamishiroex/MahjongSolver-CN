using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.Engines;
using Mahjong.Core;
using Mahjong.Plugin.CN.Experimental;
using Mahjong.Plugin.Dalamud.Actions;
using Mahjong.Plugin.Dalamud.GameState.Variants;
using Mahjong.Policy.Abstractions;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class AkochanGlobalActionMapperTests
{
    [Theory]
    [InlineData("valid")][InlineData("missing-menu")][InlineData("ron")][InlineData("tsumo")]
    [InlineData("discarded")][InlineData("late")][InlineData("eight-kinds")][InlineData("response")]
    public void Nine_terminals_requires_current_menu_first_turn_and_never_overrides_win(string change)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "kyushu-protocol-20260925.json")));
        var s = System.Text.Json.JsonSerializer.Deserialize<AkochanGlobalSnapshot>(doc.RootElement.GetProperty("ActualPublicInput"))!;
        var flags = ActionFlags.Kyushukyuhai | ActionFlags.Discard | ActionFlags.Pass;
        if (change == "missing-menu") flags &= ~ActionFlags.Kyushukyuhai;
        if (change == "ron") flags |= ActionFlags.Ron;
        if (change == "tsumo") flags |= ActionFlags.Tsumo;
        if (change == "discarded") s = s with { Players = s.Players.SetItem(0,s.Players[0] with { River = [new(V(7),false,false)] }) };
        if (change == "late") s = s with { Players = s.Players.SetItem(1,s.Players[1] with { River = [new(V(7),false,false),new(V(14),false,false)] }) };
        if (change == "eight-kinds") s = s with { Hand = s.Hand.SetItem(0,V(1)) };
        if (change == "response") s = s with { Trigger = new("dahai",1,V(7)) };
        var result = AkochanGlobalActionMapper.Map([Move("kyushukyuhai")],State(s,flags),s);
        if (change == "valid") Assert.Equal(ActionKind.Kyushukyuhai,result.Kind);
        else Block(result,"KYUSHUKYUHAI_NOT_OFFERED_OR_INVALID");
    }
    private static VisibleTile V(int id, bool red = false) => new(id, red);
    private static Tile T(int id) => Tile.FromId(id);
    private static AkochanMove Move(string type, int? target = null, VisibleTile? tile = null,
        VisibleTile[]? consumed = null, bool? tsumogiri = null, int? actor = 0) =>
        new(type, actor, target, tile, (consumed ?? []).ToImmutableArray(), tsumogiri);
    private static AkochanGlobalSnapshot Snapshot(VisibleTile[] hand, string trigger = "tsumo", int actor = 0,
        VisibleTile? tile = null) => new(0, 0, 1, 0, 0, 0, 65, hand.ToImmutableArray(), [V(15)],
        Enumerable.Range(0, 4).Select(i => new AkochanGlobalPlayer(i, (i + 2) % 4, 25000, false, false, null, [], [])).ToImmutableArray(),
        new(trigger, actor, tile), []);
    private static StateSnapshot State(AkochanGlobalSnapshot s, ActionFlags flags, MeldCandidate[]? candidates = null) =>
        StateSnapshot.Empty with
        {
            OurSeat = 2, Hand = s.Hand.Select(t => T(t.Id)).ToArray(),
            Legal = new(flags, [], (candidates ?? []).Where(c => c.Kind == MeldKind.Pon).ToArray(),
                (candidates ?? []).Where(c => c.Kind == MeldKind.Chi).ToArray(),
                (candidates ?? []).Where(c => c.Kind is MeldKind.AnKan or MeldKind.MinKan or MeldKind.ShouMinKan).ToArray()),
        };
    private static void Block(ActionChoice result, string code)
    {
        Assert.Equal(ActionKind.Pass, result.Kind);
        Assert.Equal("AKOCHAN_BLOCKED: " + code, result.Reasoning);
    }

    [Fact]
    public void Discard_preserves_red_and_drawn_identity_for_mixed_fives()
    {
        var s = Snapshot([V(4), V(4, true)], tile: V(4, true));
        var result = AkochanGlobalActionMapper.Map([Move("dahai", tile: V(4, true), tsumogiri: true)], State(s, ActionFlags.Discard), s);
        Assert.Equal(ActionKind.Discard, result.Kind);
        Assert.Equal(T(4), result.DiscardTile);
        Assert.True(result.DiscardRed);
        Assert.True(result.DiscardTsumogiri);
    }

    [Fact]
    public void After_call_discard_does_not_treat_slot_13_as_a_new_draw()
    {
        var s = Snapshot([V(1)], trigger: "discard");
        var result = AkochanGlobalActionMapper.Map([Move("dahai", tile: V(1), tsumogiri: false)], State(s, ActionFlags.Discard), s);
        Assert.Equal(ActionKind.Discard, result.Kind);
        Assert.Null(result.DiscardTsumogiri);
    }

    [Fact]
    public void Riichi_batch_carries_same_exact_discard()
    {
        var s = Snapshot([V(4, true), V(5)], tile: V(5));
        var result = AkochanGlobalActionMapper.Map([Move("reach"), Move("dahai", tile: V(4, true), tsumogiri: false)], State(s, ActionFlags.Riichi), s);
        Assert.Equal(ActionKind.Riichi, result.Kind);
        Assert.True(result.DiscardRed);
        Assert.False(result.DiscardTsumogiri);
    }

    [Theory]
    [InlineData("tsumo", 0, 0, ActionFlags.Tsumo, ActionKind.Tsumo)]
    [InlineData("dahai", 2, 2, ActionFlags.Ron, ActionKind.Ron)]
    [InlineData("kakan", 3, 3, ActionFlags.Ron, ActionKind.Ron)]
    public void Win_requires_current_offered_action_and_matching_target(string trigger, int actor, int target, ActionFlags flags, ActionKind expected)
    {
        var s = Snapshot([V(1)], trigger, actor, V(2));
        Assert.Equal(expected, AkochanGlobalActionMapper.Map([Move("hora", target)], State(s, flags), s).Kind);
        Block(AkochanGlobalActionMapper.Map([Move("hora", (target + 1) % 4)], State(s, flags), s), "HORA_TARGET_OR_LEGAL_MISMATCH");
    }

    [Fact]
    public void None_is_actual_pass_only_when_offered()
    {
        var s = Snapshot([V(1)]);
        var result = AkochanGlobalActionMapper.Map([Move("none", actor: null)], State(s, ActionFlags.Pass), s);
        Assert.Equal(ActionKind.Pass, result.Kind);
        Assert.StartsWith("AKOCHAN_GLOBAL:", result.Reasoning);
        Block(AkochanGlobalActionMapper.Map([Move("none")], State(s, ActionFlags.Discard), s), "PASS_NOT_OFFERED");
    }

    [Theory]
    [InlineData("chi", ActionFlags.Chi, MeldKind.Chi, ActionKind.Chi)]
    [InlineData("pon", ActionFlags.Pon, MeldKind.Pon, ActionKind.Pon)]
    [InlineData("daiminkan", ActionFlags.MinKan, MeldKind.MinKan, ActionKind.MinKan)]
    public void Call_matches_current_tile_shape_and_real_source_instead_of_legacy_placeholder(string type, ActionFlags flags, MeldKind kind, ActionKind expected)
    {
        VisibleTile[] consumed = type == "chi" ? [V(2), V(3)] : type == "pon" ? [V(1), V(1)] : [V(1), V(1), V(1)];
        var s = Snapshot(consumed, "dahai", 3, V(1));
        var candidate = new MeldCandidate(kind, T(1), consumed.Select(t => T(t.Id)).ToArray(), 1);
        var result = AkochanGlobalActionMapper.Map([Move(type, 3, V(1), consumed)], State(s, flags, [candidate]), s);
        Assert.Equal(expected, result.Kind);
        Assert.Equal(3, result.Call!.Value.FromSeat);
        Block(AkochanGlobalActionMapper.Map([Move(type, 2, V(1), consumed)], State(s, flags, [candidate]), s), "CALL_TARGET_MISMATCH");
    }

    [Fact]
    public void Native_chi_batch_selects_only_call_then_recomputes_discard_after_acknowledgement()
    {
        var s = Snapshot([V(2), V(3), V(8)], "dahai", 3, V(1));
        var state = State(s, ActionFlags.Chi, [new(MeldKind.Chi, T(1), [T(2), T(3)], 3)]);
        var result = AkochanGlobalActionMapper.Map([Move("chi", 3, V(1), [V(2), V(3)]), Move("dahai", tile: V(8))], state, s);
        Assert.Equal(ActionKind.Chi, result.Kind);
        Assert.Null(result.DiscardTile);
    }

    [Fact]
    public void Kan_checks_current_visible_inventory_and_existing_pon()
    {
        var s = Snapshot([V(4), V(4), V(4), V(4, true)], tile: V(4));
        var ankan = AkochanGlobalActionMapper.Map([Move("ankan", consumed: s.Hand.ToArray())], State(s, ActionFlags.AnKan), s);
        Assert.Equal(ActionKind.AnKan, ankan.Kind);
        Assert.Equal(-1, ankan.Call!.Value.FromSeat);
        var pon = new AkochanGlobalMeld("pon", 3, V(4), [V(4), V(4), V(4, true)]);
        var added = Snapshot([V(4)], tile: V(4));
        added = added with { Players = added.Players.SetItem(0, added.Players[0] with { Melds = [pon] }) };
        var kakan = Move("kakan", tile: V(4), consumed: pon.Tiles.ToArray());
        Assert.Equal(ActionKind.ShouMinKan, AkochanGlobalActionMapper.Map([kakan], State(added, ActionFlags.ShouMinKan), added).Kind);
        Block(AkochanGlobalActionMapper.Map([kakan with { Consumed = [V(4), V(4), V(4)] }], State(added, ActionFlags.ShouMinKan), added), "KAKAN_NOT_EXISTING_PON");
    }

    [Fact]
    public void Mixed_red_call_is_refused_when_existing_ui_cannot_select_physical_consumption()
    {
        var s = Snapshot([V(4), V(4), V(4, true)], "dahai", 2, V(4));
        var state = State(s, ActionFlags.Pon, [new(MeldKind.Pon, T(4), [T(4), T(4)], 1)]);
        Block(AkochanGlobalActionMapper.Map([Move("pon", 2, V(4), [V(4), V(4)])], state, s), "CALL_RED_VARIANT_UNRESOLVED");
    }

    [Fact]
    public void Unknown_or_stale_decision_cannot_become_game_input()
    {
        var s = Snapshot([V(1)], tile: V(1));
        var state = State(s, ActionFlags.Discard);
        Block(AkochanGlobalActionMapper.Map([], state, s), "EMPTY_MOVES");
        Block(AkochanGlobalActionMapper.Map([Move("dahai", tile: V(1), actor: 1)], state, s), "ACTOR_MISMATCH");
        Block(AkochanGlobalActionMapper.Map([Move("dahai", tile: V(2))], state, s), "DISCARD_NOT_LEGAL");
        Block(AkochanGlobalActionMapper.Map([Move("dahai", tile: V(1))], state with { Hand = [T(2)] }, s), "HAND_OR_PLAYER_MISMATCH");
        Block(AkochanGlobalActionMapper.Map([Move("future-action")], state, s), "CALL_NOT_OFFERED");
    }

    [Fact]
    public void Physical_slot_selection_never_substitutes_red_or_drawn_identity()
    {
        const int textureBase = 100;
        var raw = new int[14]; raw[2] = textureBase + 4; raw[13] = textureBase + 34;
        Assert.Equal(2, HandArrayDecoder.FindAddonSlot(raw, textureBase, 4, false, false));
        Assert.Equal(13, HandArrayDecoder.FindAddonSlot(raw, textureBase, 4, true, true));
        Assert.Equal(-1, HandArrayDecoder.FindAddonSlot(raw, textureBase, 4, false, true));
        Assert.Equal(-1, HandArrayDecoder.FindAddonSlot(raw, textureBase, 4, true, false));
        Assert.Equal(13, HandArrayDecoder.FindAddonSlot(raw, textureBase, 4, true, null));
        Assert.Equal(13, HandArrayDecoder.FindAddonSlot(raw, textureBase, 4));
    }

    [Fact]
    public void Chi_modal_uses_ai_selected_combination_even_if_it_is_not_first()
    {
        var selected = new MeldCandidate(MeldKind.Chi, T(3), [T(4), T(5)], 3);
        Assert.Equal(1, AutoPlayLoop.MatchSelectedChiVariantIndex([[1, 2, 3], [5, 3, 4], [2, 3, 4]], selected));
        Assert.Equal(-1, AutoPlayLoop.MatchSelectedChiVariantIndex([[1, 2, 3]], selected));
        Assert.Equal(-1, AutoPlayLoop.MatchSelectedChiVariantIndex([[3, 4, 5], [5, 4, 3]], selected));
    }
}
