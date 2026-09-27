using System.Collections.Immutable;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Core;
using Mahjong.Policy.Abstractions;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.Experimental;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

public sealed class AkochanGlobalObservationProjectorTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Only_current_pass_furiten_enters_native_input(bool stale)
    {
        var r=stale?Ref with {Sequence=Ref.Sequence-1}:Ref;
        var s=Snapshot() with {OurTemporaryFuriten=Field<bool>.Known(true,r),OurRiichiFuriten=Field<bool>.Known(false,r)};
        var input=Projector(s).Project(Runtime(),"observed-furiten").Snapshot;
        Assert.NotNull(input);Assert.Equal(stale?(bool?)null:true,input.OwnTemporaryFuriten);
        Assert.Equal(stale?(bool?)null:false,input.OwnRiichiFuriten);
        using var json=System.Text.Json.JsonDocument.Parse(Mahjong.Cn.Engines.AkochanGlobalEngine.CanonicalInput(input));
        var wire=json.RootElement.GetProperty("record")[1].GetProperty("mjcn_snapshot");
        Assert.Equal(stale?System.Text.Json.JsonValueKind.Null:System.Text.Json.JsonValueKind.True,wire.GetProperty("own_temporary_furiten").ValueKind);
    }
    [Fact]
    public void Public_furiten_conflict_blocks_projection_with_specific_reason()
    {
        var result=Projector(Snapshot() with {OurTemporaryFuriten=Field<bool>.Conflict(true,Ref,"contradiction")}).Project(Runtime(),"conflict");
        Assert.Null(result.Snapshot);Assert.Equal("AKOCHAN_PUBLIC_FURITEN_CONFLICT",result.Error);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Positive_ippatsu_enters_canonical_native_input_only_when_current(bool stale)
    {
        var context = new PublicObservationContext(RuntimeIdentity.TargetGame,LowerHandProfile.EmjUldSha256,
            new(RuntimeIdentity.TargetGame,LowerHandProfile.EmjUldSha256,[]));
        var tracker=new PublicRiichiWindowTracker();
        PublicSnapshot Step(int seq,bool declare,bool discard,bool continued) {
            var r=Ref with {Sequence=seq,ObservedAtUtc=Ref.ObservedAtUtc.AddMilliseconds((seq-10)*100)};
            var s=Snapshot();
            return s with {Observation=r,Players=s.Players.Select(p=>p with {
                RiichiDeclared=Field<bool>.Known(p.Position==ScreenPosition.Lower && declare,r),
                RiverImages=Field<PublicImageInventory>.Known(p.Position==ScreenPosition.Lower && discard
                    ? Inventory(Image(5,"Emj/117/4") with {DisplayPosition=new(1,1,1,true)})
                    : p.Position==ScreenPosition.Right && continued ? Inventory(Image(6,"Emj/121/4")) : Inventory(),r),
                MeldImages=Field<PublicImageInventory>.Known(Inventory(),r)
            }).ToImmutableArray()};
        }
        tracker.Observe(Step(10,false,false,false),"window",context);
        tracker.Observe(Step(11,true,true,false),"window",context);
        var source=tracker.Observe(Step(12,true,true,true),"window",context);
        Assert.True(source.Players[0].Ippatsu.IsConfirmed);Assert.True(source.Players[0].Ippatsu.Value);
        if(stale) source=source with {Players=source.Players.SetItem(0,source.Players[0] with {
            Ippatsu=source.Players[0].Ippatsu with {Observation=Ref},
            DoubleRiichi=source.Players[0].DoubleRiichi with {Observation=Ref}})};
        var input=Projector(source).Project(Runtime(),"positive-ippatsu").Snapshot;
        Assert.NotNull(input);Assert.Equal(stale ? (bool?)null : true,input.Players[0].Ippatsu);
        Assert.True(input.Players[0].RiichiEstablished);Assert.False(input.HistoryComplete);
        using var json=System.Text.Json.JsonDocument.Parse(Mahjong.Cn.Engines.AkochanGlobalEngine.CanonicalInput(input));
        var wire=json.RootElement.GetProperty("record")[1].GetProperty("mjcn_snapshot").GetProperty("players")[0].GetProperty("ippatsu");
        Assert.Equal(stale ? System.Text.Json.JsonValueKind.Null : System.Text.Json.JsonValueKind.True,wire.ValueKind);
        Assert.Equal(stale ? (bool?)null : true,input.Players[0].DoubleRiichi);
        var doubleWire=json.RootElement.GetProperty("record")[1].GetProperty("mjcn_snapshot").GetProperty("players")[0].GetProperty("double_riichi");
        Assert.Equal(stale ? System.Text.Json.JsonValueKind.Null : System.Text.Json.JsonValueKind.True,doubleWire.ValueKind);
    }

    [Fact]
    public void Current_duty_from_automatic_journal_probe_reaches_native_match_format()
    {
        var context = new PublicObservationContext(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
            new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
        int reads=0;
        var addon=Plugin.AttachCurrentMatchRules(Probe(),()=>{reads++;return new(766,"east",true,"PUBLIC_CURRENT_MAHJONG_DUTY_VERIFIED");});
        var source=PublicCurrentFacts.Apply(Snapshot(),addon,context);
        var input=Projector(source,addon).Project(Runtime(),"actual-duty").Snapshot;
        Assert.Equal(1,reads);Assert.NotNull(input);Assert.Equal(4,input.MatchFirstRound);
        Assert.DoesNotContain(input.Assumptions,a=>a.Contains("暂按东南战"));
    }

    [Theory]
    [InlineData("hidden")][InlineData("not-ready")][InlineData("error")][InlineData("other-addon")]
    public void Invalid_public_table_does_not_read_or_reuse_current_match_rules(string reason)
    {
        var addon=Probe() with {PublicMatchRules=new(766,"east",true,"PUBLIC_CURRENT_MAHJONG_DUTY_VERIFIED")};
        addon=reason switch {"hidden"=>addon with {Visible=false},"not-ready"=>addon with {Ready=false},
            "error"=>addon with {Error="transition"},_=>addon with {Name="Other"}};
        var result=Plugin.AttachCurrentMatchRules(addon,()=>throw new InvalidOperationException("must not read"));
        Assert.Null(result.PublicMatchRules);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Only_fresh_draw_origin_is_forwarded_to_global_engine(bool stale)
    {
        var source=Snapshot() with { OwnDrawKind=Field<string>.Known("normal",
            stale ? Ref with {Sequence=Ref.Sequence-1} : Ref, SourceKind.Derived) };
        var input=Projector(source).Project(Runtime(),"draw-origin").Snapshot;
        Assert.NotNull(input);
        Assert.Equal(stale?null:"normal",input.OwnDrawKind);
        Assert.False(input.HistoryComplete);
    }
    [Fact]
    public void Confirmed_call_after_riichi_reaches_ai_as_cancelled_ippatsu_without_invented_acceptance()
    {
        var context = new PublicObservationContext(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
            new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
        var source = Snapshot() with { Players = Snapshot().Players.Select(p => p with
            { RiichiDeclared = Known(true) }).ToImmutableArray() };
        var tracker = new PublicIppatsuTracker();
        tracker.Observe(source, "segment", null, context);
        var nextReference = Ref with { Sequence = 12, ObservedAtUtc = Epoch.AddMilliseconds(200) };
        source = source with { Observation = nextReference, Players = source.Players.Select(p => p with
            { RiichiDeclared = Field<bool>.Known(true, nextReference) }).ToImmutableArray() };
        var call = new PublicCallEvent("pon", "right", "top", new(2, false), [new(2, false), new(2, false)],
            "Emj/113", "Emj/124/4", 11, 12, "segment", "PUBLIC_CALL_VISIBLE_TRANSITION");
        var updated = tracker.Observe(source, "segment", new([call], []), context);
        var input = Projector(updated).Project(Runtime(), "ippatsu-cancelled").Snapshot;
        Assert.NotNull(input);
        Assert.All(input.Players, p => Assert.Equal(false, p.Ippatsu));
        Assert.False(input.HistoryComplete);
        Assert.All(updated.Players, p => Assert.False(p.RiichiEstablished.IsConfirmed));
        Assert.Contains(input.Assumptions, a => a.Contains("成立时刻未独立确认"));
    }

    [Theory]
    [InlineData("east", 4)]
    [InlineData("hanchan", 0)]
    public void Actual_public_match_type_reaches_engine(string match, int first)
    {
        var input = Projector(Snapshot() with { Rules = new() { MatchType = Known(match) } })
            .Project(Runtime(), "match-test").Snapshot;
        Assert.NotNull(input); Assert.Equal(first, input.MatchFirstRound);
        Assert.Equal(match, input.MatchRules!.MatchType);
        Assert.Equal(8 - first, input.MatchRules.ScheduledHands);
        Assert.Equal("observed-current-duty", input.MatchRules.MatchEvidence);
        Assert.DoesNotContain(input.Assumptions, a => a.Contains("暂按东南战"));
    }

    [Fact]
    public void No_open_tanyao_table_is_explicitly_unsupported_instead_of_wrong_valuation()
    {
        var result = Projector(Snapshot() with { Rules = new() { OpenTanyao = Known(false) } })
            .Project(Runtime(), "rules-test");
        Assert.Null(result.Snapshot); Assert.Equal("AKOCHAN_RULES_UNSUPPORTED_NO_OPEN_TANYAO", result.Error);
    }
    [Theory]
    [InlineData(ActionFlags.Riichi)]
    [InlineData(ActionFlags.Tsumo)]
    [InlineData(ActionFlags.Discard)]
    public void Own_turn_menu_with_post_discard_hand_waits_without_sending_invalid_engine_request(ActionFlags action)
    {
        // Captured 2026-09-24T18:22:08: Riichi|Pass, 13 tiles, discard trigger.
        int[] waitingHand = [0, 1, 2, 4, 4, 6, 7, 8, 15, 16, 17, 18, 18];
        var projector = Projector(Snapshot(waitingHand));
        var state = Runtime(waitingHand) with { Legal = new(action | ActionFlags.Pass, [], [], [], []) };
        var projection = projector.Project(state, "declaration-transition");
        Assert.Null(projection.Snapshot);
        Assert.Equal("AKOCHAN_PUBLIC_TRIGGER_HAND_TRANSITION", projection.Error);
        int engineCalls = 0;
        using var policy = new AkochanGlobalPolicy(_ => projection, _ => { }, _ => { }, (_, _) =>
        { engineCalls++; throw new InvalidOperationException("must not call engine for a transitional hand"); });
        Assert.StartsWith("AKOCHAN_PENDING:", policy.Choose(state).Reasoning);
        Assert.Equal(0, engineCalls);
        // A later valid 14-tile observation is usable without restarting the reader.
        projector.Observe(Snapshot(), Probe(), null, null);
        Assert.NotNull(projector.Project(Runtime(), "next-consistent-turn").Snapshot);
    }

    private static readonly DateTimeOffset Epoch = DateTimeOffset.Parse("2026-09-24T09:00:00Z");
    private static readonly ObservationReference Ref = new(10, Epoch, "public fixture", "explicit constructed public fixture");
    private static readonly int[] Hand = [0, 1, 2, 9, 10, 11, 18, 19, 20, 27, 27, 31, 31, 33];
    private static Field<T> Known<T>(T value) => Field<T>.Known(value, Ref);
    private static PublicImageInventory Inventory(params PublicImageTile[] tiles) =>
        new(tiles.ToImmutableArray(), true, true, true, tiles.Length == 0, tiles.Length, 0, []);
    private static PublicImageTile Image(int id, string path, string group = "", bool called = false, int order = 1) =>
        new(new VisibleTile(id), path, group, 0, 0, 20, 30, 0, false, true)
        { DisplayPosition = new(1, order, order, false), Tsumogiri = Known(true), WasClaimed = Known(called) };
    private static PublicSnapshot Snapshot(int[]? hand = null) => new()
    {
        SessionId = Guid.Parse("fa100000-0000-0000-0000-000000000001"), Observation = Ref,
        Stability = StabilityState.Stable, Synchronization = SynchronizationState.HistoryGap,
        RoundId = Known("round1"), RoundWind = Known(1), HandNumber = Known(2), Honba = Known(1),
        RiichiSticks = Known(2), WallRemaining = Known(43), DealerPlayerId = Known(3),
        DoraMode = Known(DoraDisplayMode.Indicator), DoraDisplay = Known<ImmutableArray<VisibleTile>>([new(7)]),
        LowerVisibleFaces = Known((hand ?? Hand).Select((id, index) => new PublicHandTile(new(id),
            new(index == (hand ?? Hand).Length - 1 ? "Emj/135/9/4" : $"Emj/{140 + index}/9/4", index + 1, 1))).ToImmutableArray()),
        Players = Enumerable.Range(0, 4).Select(id => new PlayerPublicState((ScreenPosition)id)
        { PlayerId = Known(id), SeatWind = Known((id + 1) % 4), Score = Known(25000 + id * 1000),
            RiverImages = Known(Inventory()), MeldImages = Known(Inventory()) }).ToImmutableArray(),
    };
    private static StateSnapshot Runtime(int[]? hand = null) => StateSnapshot.Empty with
    { Hand = (hand ?? Hand).Select(id => new Tile((byte)id)).ToArray(), SeatInfoKnown = true, OurSeat = 1,
        Legal = new(ActionFlags.Discard, [], [], [], []) };
    private static AddonProbe Probe() => new("Emj", true, true, true, 0, [], null, PublicTableFaces: []);
    private static AkochanGlobalObservationProjector Projector(PublicSnapshot snapshot, AddonProbe? addon = null)
    { var projector = new AkochanGlobalObservationProjector(); projector.Observe(snapshot, addon ?? Probe(), null, null); return projector; }
    private static readonly int[] ResponseHand = [0, 1, 2, 9, 10, 11, 12, 12, 20, 27, 27, 31, 31];
    private const string ResponseSlot = "Emj/124/4";
    private static AddonProbe ResponseProbe(bool visible = true, bool decoded = true, bool ponEnabled = true,
        bool passEnabled = true, string rowCode = "ACTION_MENU_LABEL_CANDIDATE") => Probe() with
    {
        PublicActionMenu = new("ACTION_MENU_VISIBLE_CANDIDATE", visible, decoded,
            [new("Emj/104/3/3", 0, "Pon", ponEnabled, rowCode),
                new("Emj/104/3/4", 30, "Pass", passEnabled, "ACTION_MENU_LABEL_CANDIDATE")]),
    };
    private static PublicSnapshot ResponseSnapshot(long sample = 10, int[]? hand = null) => Snapshot(hand ?? ResponseHand) with
    {
        Observation = Ref with { Sequence = sample, ObservedAtUtc = Epoch.AddMilliseconds((sample - 10) * 100) },
        Players = Snapshot().Players.SetItem(2, Snapshot().Players[2] with
        { RiverImages = Known(Inventory(Image(12, ResponseSlot))) }),
    };
    private static PublicRiverObservedEvent Discard(long sample = 9, string direction = "top", int tile = 12,
        string slot = ResponseSlot) => new("DiscardObserved", direction, slot, new(tile, false, "synthetic"),
            false, sample, 1, false);
    private static PublicRiverHistoryObservation ResponseHistory(params PublicRiverObservedEvent[] events) =>
        new("partial", "round1", true, true, 0, [], events.ToImmutableArray(), []);
    private static StateSnapshot ResponseRuntime(int[]? hand = null) => Runtime(hand ?? ResponseHand) with
    {
        // Deliberately wrong tile and source: the observed top-player discard is 12,
        // and the current visible hand has a pair of 12, not this legacy candidate.
        Legal = new(ActionFlags.Pon | ActionFlags.Pass, [],
            [new(MeldKind.Pon, new(31), [new(31), new(31)], 1)], [], []),
    };
    private static AkochanGlobalObservationProjector BoundResponse()
    {
        var projector = new AkochanGlobalObservationProjector();
        projector.Observe(ResponseSnapshot(), ResponseProbe(), ResponseHistory(Discard()), null);
        Assert.Null(projector.Project(ResponseRuntime(), "response").Error);
        return projector;
    }
    private static void AssertNoResponse(AkochanGlobalProjection result)
    {
        Assert.Null(result.Snapshot);
        Assert.Equal("AKOCHAN_PUBLIC_TRIGGER_UNAVAILABLE", result.Error);
        Assert.Null(result.ResponseLegal);
    }

    [Fact]
    public void Feeds_current_public_round_scores_winds_dora_wall_and_exact_hand_despite_history_gap()
    {
        var result = Projector(Snapshot()).Project(Runtime(), "runtime-turn");
        Assert.Null(result.Error); var input = Assert.IsType<Mahjong.Cn.Engines.AkochanGlobalSnapshot>(result.Snapshot);
        Assert.Equal(1, input.RoundWind); Assert.Equal(2, input.HandNumber); Assert.Equal(3, input.DealerPlayerId);
        Assert.Equal(43, input.WallRemaining); Assert.Equal(2, input.RiichiSticks); Assert.Equal(1, input.Honba);
        Assert.Equal(new[] { 25000, 26000, 27000, 28000 }, input.Players.Select(x => x.Score));
        Assert.Equal(Hand, input.Hand.Select(x => x.Id)); Assert.Equal("tsumo", input.Trigger.Type);
        Assert.Equal(33, input.Trigger.Tile!.Value.Id); Assert.False(input.HistoryComplete);
        Assert.Contains(input.Assumptions, x => x.Contains("历史不完整"));
    }

    [Theory]
    [InlineData(0, 8)] [InlineData(27, 30)] [InlineData(31, 33)]
    public void Actual_dora_is_converted_to_indicator_using_observed_mode(int actual, int indicator)
    {
        var result = Projector(Snapshot() with { DoraMode = Known(DoraDisplayMode.ActualDora),
            DoraDisplay = Known<ImmutableArray<VisibleTile>>([new(actual)]) }).Project(Runtime(), "turn");
        Assert.Null(result.Error); Assert.Equal(indicator, Assert.Single(result.Snapshot!.DoraIndicators).Id);
    }

    [Fact]
    public void Unknown_fields_are_assumptions_and_conflicting_fields_block()
    {
        var source = Snapshot() with { Honba = Field<int>.Unknown("notread") };
        var fallback = Projector(source).Project(Runtime(), "turn");
        Assert.Equal(0, fallback.Snapshot!.Honba); Assert.Contains(fallback.Snapshot.Assumptions, x => x.Contains("本场未知"));
        var conflict = Projector(source with { Honba = Field<int>.Conflict(9, Ref, "disagree") }).Project(Runtime(), "turn");
        Assert.Null(conflict.Snapshot); Assert.StartsWith("AKOCHAN_PUBLIC_FIELD_CONFLICT", conflict.Error);
    }

    [Fact]
    public void Called_river_tile_is_not_counted_again_in_public_pon()
    {
        const string group = "Emj/161";
        var meld = Inventory(Image(0, group + "/2/4", group), Image(0, group + "/3/4", group), Image(0, group + "/4/4", group)) with
        { Groups = [new(group, 3, 3, 0, true, "PUBLIC_PON_THREE_FACE_PATTERN", null)] };
        var source = Snapshot();
        source = source with { Players = source.Players.SetItem(1, source.Players[1] with { MeldImages = Known(meld) })
            .SetItem(2, source.Players[2] with { RiverImages = Known(Inventory(Image(0, "Emj/124/4", called: true))) }) };
        var result = Projector(source).Project(Runtime(), "turn");
        Assert.Null(result.Error); Assert.True(Assert.Single(result.Snapshot!.Players[2].River).WasClaimed);
        Assert.Equal("pon", Assert.Single(result.Snapshot.Players[1].Melds).Type);
        Assert.Equal(2, Assert.Single(result.Snapshot.Players[1].Melds).FromPlayerId);
        var ambiguous = source with { Players = source.Players.SetItem(3, source.Players[3] with
        { RiverImages = Known(Inventory(Image(0, "Emj/127/4", called: true))) }) };
        Assert.Null(Assert.Single(Projector(ambiguous).Project(Runtime(), "ambiguous").Snapshot!.Players[1].Melds).FromPlayerId);
        var incomplete = source with { Players = source.Players.SetItem(3, source.Players[3] with
        { RiverImages = Field<PublicImageInventory>.Unknown("unreadable") }) };
        Assert.Null(Assert.Single(Projector(incomplete).Project(Runtime(), "incomplete").Snapshot!.Players[1].Melds).FromPlayerId);
        source = source with { Players = source.Players.SetItem(2, source.Players[2] with
        { RiverImages = Known(Inventory(Image(0, "Emj/124/4"))) }) };
        Assert.Equal("AKOCHAN_PUBLIC_PHYSICAL_TILE_CONFLICT", Projector(source).Project(Runtime(), "turn").Error);
    }

    [Fact]
    public void Same_round_history_preserves_occluded_slots_but_new_round_drops_old_events()
    {
        var source = Snapshot();
        var projector = new AkochanGlobalObservationProjector();
        var history = new PublicRiverHistoryObservation("partial", "round1", true, true, 0,
            [new("right", "Emj/121/4", new(12, false, "四筒"), 1, false, false, false, 4, 5, false, 1)],
            [new("DiscardObserved", "right", "Emj/121/4", new(12, false, "四筒"), false, 4, 1, false)], []);
        projector.Observe(source, Probe(), history, null);
        var first = projector.Project(Runtime(), "turn").Snapshot!;
        Assert.Equal(12, Assert.Single(first.Players[1].River).Tile.Id); Assert.Single(first.KnownEvents);
        Assert.Equal(0, first.KnownEvents[0].RiverIndex);
        projector.Observe(source with { Observation = Ref with { Sequence = 11 }, Honba = Known(2), RoundId = Known("round2") },
            Probe(), history, null);
        var next = projector.Project(Runtime(), "turn2").Snapshot!;
        Assert.Empty(next.Players[1].River); Assert.Empty(next.KnownEvents);
    }

    [Fact]
    public void Clear_removes_all_scene_data_and_hand_mismatch_blocks_instead_of_guessing()
    {
        var projector = Projector(Snapshot());
        Assert.Equal("AKOCHAN_PUBLIC_RUNTIME_HAND_CONFLICT", projector.Project(Runtime(Hand[..13]), "turn").Error);
        projector.Clear(); Assert.Equal("AKOCHAN_PUBLIC_TABLE_UNAVAILABLE", projector.Project(Runtime(), "turn").Error);
    }

    [Fact]
    public void Postcall_discard_does_not_invent_draw_from_sorted_hand()
    {
        var source = Snapshot() with { LowerVisibleFaces = Known(Snapshot().LowerVisibleFaces.Value.Select(x =>
            x with { Slot = x.Slot with { Path = "sorted/" + x.Slot.DisplayPosition } }).ToImmutableArray()) };
        var result = Projector(source).Project(Runtime(), "turn");
        Assert.Null(result.Error); Assert.Equal("discard", result.Snapshot!.Trigger.Type); Assert.Null(result.Snapshot.Trigger.Tile);
    }

    [Fact]
    public void Response_actor_comes_from_public_discard_event_not_legacy_candidate_source()
    {
        var result = BoundResponse().Project(ResponseRuntime(), "response");
        Assert.Null(result.Error); Assert.Equal("dahai", result.Snapshot!.Trigger.Type); Assert.Equal(2, result.Snapshot.Trigger.Actor);
        Assert.Equal(12, result.Snapshot.Trigger.Tile!.Value.Id);
        Assert.Equal(ActionFlags.Pon | ActionFlags.Pass, result.ResponseLegal!.Flags);
        var candidate = Assert.Single(result.ResponseLegal.PonCandidates);
        Assert.Equal(12, candidate.ClaimedTile.Id); Assert.Equal(2, candidate.FromSeat);
        Assert.Equal(new byte[] { 12, 12 }, candidate.HandTiles.Select(x => x.Id));
        Assert.Empty(result.ResponseLegal.ChiCandidates); Assert.Empty(result.ResponseLegal.KanCandidates);
    }

    [Fact]
    public void Bound_response_survives_more_than_twenty_samples_of_same_visible_popup()
    {
        var projector = BoundResponse();
        for (int sample = 11; sample <= 65; sample++)
        {
            projector.Observe(ResponseSnapshot(sample), ResponseProbe(), ResponseHistory(), null);
            var current = projector.Project(ResponseRuntime(), "response");
            Assert.Null(current.Error); Assert.Equal(2, current.Snapshot!.Trigger.Actor);
            Assert.Equal(12, Assert.Single(current.ResponseLegal!.PonCandidates).ClaimedTile.Id);
            Assert.Equal(Epoch.AddMilliseconds((sample - 10) * 100), current.Snapshot.Utc);
        }
    }

    [Fact]
    public void Closing_and_reopening_popup_does_not_reuse_previously_bound_discard()
    {
        var projector = BoundResponse();
        projector.Observe(ResponseSnapshot(11), ResponseProbe(visible: false), ResponseHistory(), null);
        AssertNoResponse(projector.Project(ResponseRuntime(), "closed"));
        projector.Observe(ResponseSnapshot(12), ResponseProbe(), ResponseHistory(), null);
        AssertNoResponse(projector.Project(ResponseRuntime(), "reopened"));
        var next = ResponseSnapshot(13);
        next = next with { Players = next.Players.SetItem(2, next.Players[2] with
        { RiverImages = Known(Inventory(Image(12, ResponseSlot), Image(12, "Emj/1240001/4", order: 2))) }) };
        projector.Observe(next, ResponseProbe(), ResponseHistory(Discard(13, slot: "Emj/1240001/4")), null);
        var projection = projector.Project(ResponseRuntime(), "new-discard");
        Assert.Null(projection.Error);
        Assert.Equal(1, projection.Snapshot!.KnownEvents.Single(e => e.Sequence == 13 && e.Type == "dahai").RiverIndex);
    }

    [Fact]
    public void Latest_own_discard_is_not_skipped_to_bind_an_older_opponent_discard()
    {
        var projector = BoundResponse();
        var source = ResponseSnapshot(11);
        source = source with { Players = source.Players.SetItem(0, source.Players[0] with
        { RiverImages = Known(Inventory(Image(5, "Emj/118/4"))) }) };
        projector.Observe(source, ResponseProbe(), ResponseHistory(Discard(11, "bottom", 5, "Emj/118/4")), null);
        AssertNoResponse(projector.Project(ResponseRuntime(), "new-own-discard"));
    }

    [Fact]
    public void Simultaneous_public_discards_have_no_invented_last_actor()
    {
        var projector = BoundResponse();
        var source = ResponseSnapshot(11);
        source = source with { Players = source.Players.SetItem(1, source.Players[1] with
        { RiverImages = Known(Inventory(Image(5, "Emj/121/4"))) }) };
        projector.Observe(source, ResponseProbe(), ResponseHistory(Discard(11), Discard(11, "right", 5, "Emj/121/4")), null);
        AssertNoResponse(projector.Project(ResponseRuntime(), "ambiguous-event-order"));
    }

    [Theory]
    [InlineData("new-tail")] [InlineData("tile-mismatch")] [InlineData("claimed")]
    [InlineData("claimed-conflict")] [InlineData("unstable")] [InlineData("incomplete")]
    [InlineData("unknown-order")] [InlineData("ambiguous-order")]
    public void Response_requires_current_stable_uncalled_source_tail(string change)
    {
        var projector = BoundResponse();
        var source = ResponseSnapshot(11);
        var tile = Image(12, ResponseSlot);
        var inventory = change switch
        {
            "new-tail" => Inventory(tile, Image(5, "Emj/1240001/4", order: 2)),
            "tile-mismatch" => Inventory(Image(5, ResponseSlot)),
            "claimed" => Inventory(tile with { WasClaimed = Known(true) }),
            "claimed-conflict" => Inventory(tile with { WasClaimed = Field<bool>.Conflict(false, Ref, "contradictory mark") }),
            "unstable" => Inventory(tile with { Stable = false }),
            "unknown-order" => Inventory(tile with { DisplayPosition = null }),
            "ambiguous-order" => Inventory(tile, Image(5, "Emj/1240001/4", order: 1)),
            _ => Inventory(tile) with { AllVisibleSlotsDecoded = false },
        };
        source = source with { Players = source.Players.SetItem(2, source.Players[2] with { RiverImages = Known(inventory) }) };
        projector.Observe(source, ResponseProbe(), ResponseHistory(), null);
        AssertNoResponse(projector.Project(ResponseRuntime(), "source-no-longer-valid"));
    }

    [Theory]
    [InlineData("hidden")] [InlineData("undecoded")] [InlineData("pon-disabled")]
    [InlineData("pass-disabled")] [InlineData("unrecognized-row")]
    public void Unreadable_or_disabled_response_menu_never_returns_action(string state)
    {
        var projector = BoundResponse();
        var probe = state switch
        {
            "hidden" => ResponseProbe(visible: false),
            "undecoded" => ResponseProbe(decoded: false),
            "pon-disabled" => ResponseProbe(ponEnabled: false),
            "pass-disabled" => ResponseProbe(passEnabled: false),
            _ => ResponseProbe(rowCode: "ACTION_MENU_LABEL_UNRECOGNIZED"),
        };
        projector.Observe(ResponseSnapshot(11), probe, ResponseHistory(), null);
        AssertNoResponse(projector.Project(ResponseRuntime(), "menu-unavailable"));
    }

    [Fact]
    public void Brief_undecoded_menu_suspends_output_without_expiring_still_open_popup()
    {
        var projector = BoundResponse();
        projector.Observe(ResponseSnapshot(35), ResponseProbe(decoded: false), ResponseHistory(), null);
        AssertNoResponse(projector.Project(ResponseRuntime(), "unreadable"));
        projector.Observe(ResponseSnapshot(40), ResponseProbe(), ResponseHistory(), null);
        Assert.Null(projector.Project(ResponseRuntime(), "visible-again").Error);
    }

    [Fact]
    public void Old_discard_is_not_promoted_to_fresh_event_but_unique_current_menu_can_be_inferred()
    {
        var projector = new AkochanGlobalObservationProjector();
        projector.Observe(ResponseSnapshot(35), ResponseProbe(), ResponseHistory(Discard(9)), null);
        var result = projector.Project(ResponseRuntime(), "new-popup-old-event");
        Assert.Null(result.Error);
        Assert.Contains(result.Snapshot!.Assumptions, x => x.Contains("唯一匹配推导"));
        Assert.Equal(9, Assert.Single(result.Snapshot.KnownEvents).Sequence);
        Assert.False(result.Snapshot.HistoryComplete);
    }

    [Theory]
    [InlineData("round")] [InlineData("hand")] [InlineData("scene")]
    public void Round_hand_and_scene_boundaries_clear_bound_response(string boundary)
    {
        var projector = BoundResponse();
        int[] hand = ResponseHand;
        PublicSnapshot source = ResponseSnapshot(11);
        if (boundary == "round") source = source with { RoundId = Known("round2"), Honba = Known(2) };
        else if (boundary == "hand")
        {
            hand = (int[])ResponseHand.Clone(); hand[0] = 3;
            source = ResponseSnapshot(11, hand);
        }
        else projector.Observe(null, Probe(), null, null);
        projector.Observe(source, ResponseProbe(), ResponseHistory(), null);
        var result = projector.Project(ResponseRuntime(hand), "boundary");
        if (boundary == "hand") AssertNoResponse(result);
        else
        {
            Assert.Null(result.Error); Assert.Empty(result.Snapshot!.KnownEvents);
            Assert.Contains(result.Snapshot.Assumptions, x => x.Contains("唯一匹配推导"));
            Assert.False(result.Snapshot.HistoryComplete);
        }
    }

    [Fact]
    public void Menu_permission_conflict_blocks_instead_of_using_a_wrong_dispatch_row_index()
    {
        var runtime = ResponseRuntime() with { Legal = new(ActionFlags.Pon | ActionFlags.Chi | ActionFlags.MinKan | ActionFlags.Pass,
            [], [new(MeldKind.Pon, new(31), [new(31), new(31)], 1)],
            [new(MeldKind.Chi, new(11), [new(9), new(10)], 3)], [new(MeldKind.MinKan, new(31), [new(31), new(31), new(31)], 1)]) };
        var result = BoundResponse().Project(runtime, "wrong-legacy-options");
        Assert.Equal("AKOCHAN_PUBLIC_MENU_LEGAL_CONFLICT", result.Error);
        Assert.Null(result.Snapshot); Assert.Null(result.ResponseLegal);
    }

    [Fact]
    public void Incomplete_own_public_melds_block_but_opponent_missing_region_is_explicit_assumption()
    {
        var source = Snapshot();
        source = source with { Players = source.Players.SetItem(1, source.Players[1] with
        { MeldImages = Field<PublicImageInventory>.Unknown("unavailable") }) };
        var opponent = Projector(source).Project(Runtime(), "turn"); Assert.NotNull(opponent.Snapshot);
        Assert.Contains(opponent.Snapshot.Assumptions, x => x.Contains("玩家1副露区域"));
        source = source with { Players = source.Players.SetItem(0, source.Players[0] with
        { MeldImages = Field<PublicImageInventory>.Unknown("unavailable") }) };
        Assert.Equal("AKOCHAN_PUBLIC_OWN_MELD_UNAVAILABLE", Projector(source).Project(Runtime(), "turn").Error);
    }

    [Fact]
    public void Candidate_dora_values_are_forwarded_with_explicit_uncertainty_and_not_dropped()
    {
        var snapshot=Snapshot();
        snapshot=snapshot with { DoraDisplay=snapshot.DoraDisplay with { MappingStatus=MappingStatus.Candidate } };
        var result=Projector(snapshot).Project(Runtime(),"turn");
        Assert.NotNull(result.Snapshot);
        Assert.Single(result.Snapshot.DoraIndicators);
        Assert.Contains(result.Snapshot.Assumptions,x=>x.Contains("宝牌使用当前资源"));
        _=Mahjong.Cn.Engines.AkochanGlobalEngine.CanonicalInput(result.Snapshot);
    }

    [Fact]
    public void UI_stick_and_called_mark_signals_do_not_become_fabricated_native_actions()
    {
        var source=Snapshot(); var projector=new AkochanGlobalObservationProjector();
        var rivers=new PublicRiverHistoryObservation("partial","round1",true,true,0,[],
            [new("CalledMarkObserved","right","Emj/121/4",new(12,false,"四筒"),false,9,1,false)],[]);
        var round=new PublicRoundProgress(Known("round1"),
            [new(PublicRoundSignalKind.RiichiStickObserved,"round1",ScreenPosition.Right,Ref,Ref)],[]);
        projector.Observe(source,Probe(),rivers,null,round);
        var result=projector.Project(Runtime(),"turn");
        Assert.NotNull(result.Snapshot); Assert.Empty(result.Snapshot.KnownEvents);
        _=Mahjong.Cn.Engines.AkochanGlobalEngine.CanonicalInput(result.Snapshot);
    }


    [Theory]
    [InlineData("Chi", 3, 3, ActionFlags.Chi, ActionKind.Chi)]
    [InlineData("Pon", 2, 12, ActionFlags.Pon, ActionKind.Pon)]
    [InlineData("Kan", 1, 12, ActionFlags.MinKan, ActionKind.MinKan)]
    [InlineData("Ron", 2, 12, ActionFlags.Ron, ActionKind.Ron)]
    public void Every_response_kind_accepts_a_real_partial_epoch_event_without_a_confirmed_round(
        string menuAction, int actor, int claim, ActionFlags flag, ActionKind expected)
    {
        int[] hand = (int[])ResponseHand.Clone();
        if (menuAction == "Kan") hand[3] = 12;
        var source = Snapshot(hand) with { RoundId = Field<string>.Unknown("opening missed") };
        const string slot = "observed/source";
        source = source with { Players = source.Players.SetItem(actor, source.Players[actor] with
        { RiverImages = Known(Inventory(Image(claim, slot))) }) };
        var probe = Probe() with { PublicActionMenu = new("ACTION_MENU_VISIBLE_CANDIDATE", true, true,
            [new("menu/0",0,menuAction,true,"ACTION_MENU_LABEL_CANDIDATE"),
             new("menu/1",30,"Pass",true,"ACTION_MENU_LABEL_CANDIDATE")]) };
        string direction = new[] { "bottom", "right", "top", "left" }[actor];
        var history = ResponseHistory(Discard(9,direction,claim,slot)) with { RoundToken = "observed:1" };
        var projector = new AkochanGlobalObservationProjector();
        projector.Observe(source,probe,history,null,trackingEpochToken:"observed:1");
        var runtime = Runtime(hand) with { Legal = new(flag | ActionFlags.Pass,[],[],[],[]) };
        var result = projector.Project(runtime,"partial-response");
        Assert.Null(result.Error);
        Assert.Single(result.Snapshot!.KnownEvents); Assert.False(result.Snapshot.HistoryComplete);
        Assert.Equal(actor,result.Snapshot.Trigger.Actor); Assert.Equal(claim,result.Snapshot.Trigger.Tile!.Value.Id);
        var consumed = menuAction switch
        {
            "Chi" => new Mahjong.Cn.VisibleTile[] { new(1), new(2) },
            "Pon" => [new(12),new(12)], "Kan" => [new(12),new(12),new(12)], _ => [],
        };
        string type = menuAction switch { "Chi"=>"chi", "Pon"=>"pon", "Kan"=>"daiminkan", _=>"hora" };
        var mapped = AkochanGlobalActionMapper.Map(
            [new(type,0,actor,new(claim),consumed.ToImmutableArray(),null)],
            runtime with { Legal = result.ResponseLegal! }, result.Snapshot);
        Assert.Equal(expected,mapped.Kind);
        Assert.StartsWith("AKOCHAN_GLOBAL",mapped.Reasoning);
    }

    [Fact]
    public void Cold_start_chi_uses_unique_current_left_tail_without_creating_history()
    {
        var source = Snapshot(ResponseHand) with { RoundId = Field<string>.Unknown("opening missed") };
        source = source with { Players = source.Players.SetItem(3,source.Players[3] with
        { RiverImages = Known(Inventory(Image(3,"left/current"))) }) };
        var probe = ResponseProbe() with { PublicActionMenu = new("ACTION_MENU_VISIBLE_CANDIDATE",true,true,
            [new("menu/0",0,"Chi",true,"ACTION_MENU_LABEL_CANDIDATE"),
             new("menu/1",30,"Pass",true,"ACTION_MENU_LABEL_CANDIDATE")]) };
        var projector = Projector(source,probe);
        var result = projector.Project(Runtime(ResponseHand) with { Legal = new(ActionFlags.Chi | ActionFlags.Pass,[],[],[],[]) },"recovery");
        Assert.Null(result.Error);
        Assert.Equal(3,result.Snapshot!.Trigger.Actor);
        Assert.Contains(result.Snapshot.Assumptions,x=>x.Contains("唯一匹配推导"));
        Assert.Empty(result.Snapshot.KnownEvents); Assert.False(result.Snapshot.HistoryComplete);
        Assert.NotEmpty(result.ResponseLegal!.ChiCandidates);
    }

    [Fact]
    public void Recovery_does_not_treat_an_old_own_action_as_the_current_response()
    {
        var source = ResponseSnapshot(100) with { RoundId = Field<string>.Unknown("opening missed") };
        var projector = new AkochanGlobalObservationProjector();
        projector.Observe(source,ResponseProbe(),ResponseHistory(Discard(9,"bottom",5,"old/own")),null);
        var result = projector.Project(ResponseRuntime(),"recovery");
        Assert.Null(result.Error); Assert.Equal(2,result.Snapshot!.Trigger.Actor);
        Assert.Contains(result.Snapshot.Assumptions,x=>x.Contains("唯一匹配推导"));
        Assert.Equal(0,Assert.Single(result.Snapshot.KnownEvents).Actor);
    }

    [Fact]
    public void Cold_start_pon_refuses_two_possible_sources_and_incomplete_other_river()
    {
        var source = ResponseSnapshot();
        source = source with { Players = source.Players.SetItem(1,source.Players[1] with
        { RiverImages = Known(Inventory(Image(12,"right/tail"))) }) };
        var ambiguous = Projector(source,ResponseProbe()).Project(ResponseRuntime(),"ambiguous");
        AssertNoResponse(ambiguous); Assert.Contains("2个可能来源",ambiguous.ErrorDetail);
        source = source with { Players = source.Players.SetItem(1,source.Players[1] with
        { RiverImages = Field<PublicImageInventory>.Unknown("region unreadable") }) };
        var incomplete = Projector(source,ResponseProbe()).Project(ResponseRuntime(),"incomplete");
        AssertNoResponse(incomplete); Assert.Contains("未解码",incomplete.ErrorDetail);
    }

    [Fact]
    public void Sort_and_disabled_menu_animation_do_not_destroy_a_current_response()
    {
        var projector = BoundResponse();
        var source = ResponseSnapshot(30);
        source = source with { LowerVisibleFaces = Known(source.LowerVisibleFaces.Value.Reverse()
            .Select((tile,i)=>tile with { Slot = tile.Slot with { Path = $"sorted/{i}" } }).ToImmutableArray()) };
        projector.Observe(source,ResponseProbe(ponEnabled:false,passEnabled:false),ResponseHistory(),null);
        AssertNoResponse(projector.Project(ResponseRuntime(),"disabled"));
        projector.Observe(source with { Observation = Ref with { Sequence=31 } },ResponseProbe(),ResponseHistory(),null);
        var recovered = projector.Project(ResponseRuntime(),"enabled");
        Assert.Null(recovered.Error); Assert.DoesNotContain(recovered.Snapshot!.Assumptions,x=>x.Contains("唯一匹配推导"));
    }

    [Fact]
    public void Changing_partial_observation_epoch_drops_prior_events_and_only_rebuilds_from_current_display()
    {
        var source = ResponseSnapshot() with { RoundId = Field<string>.Unknown("opening missed") };
        var projector = new AkochanGlobalObservationProjector();
        projector.Observe(source,ResponseProbe(),ResponseHistory(Discard()) with { RoundToken="observed:1" },
            null,trackingEpochToken:"observed:1");
        Assert.Single(projector.Project(ResponseRuntime(),"first").Snapshot!.KnownEvents);
        projector.Observe(source with { Observation=Ref with { Sequence=11 } },ResponseProbe(),
            ResponseHistory() with { RoundToken="observed:2" },null,trackingEpochToken:"observed:2");
        var result=projector.Project(ResponseRuntime(),"recovered");
        Assert.Null(result.Error); Assert.Empty(result.Snapshot!.KnownEvents);
        Assert.Contains(result.Snapshot.Assumptions,x=>x.Contains("唯一匹配推导"));
    }

    [Fact]
    public void Same_visible_state_in_a_new_observation_epoch_has_a_new_request_identity()
    {
        var source = ResponseSnapshot() with { RoundId=Field<string>.Unknown("opening missed") };
        var projector = new AkochanGlobalObservationProjector();
        projector.Observe(source,ResponseProbe(),null,null,trackingEpochToken:"observed:1");
        var first=projector.Project(ResponseRuntime(),"unchanged-runtime").Snapshot!;
        projector.Observe(source with { Observation=Ref with { Sequence=11 } },ResponseProbe(),null,null,
            trackingEpochToken:"observed:2");
        var next=projector.Project(ResponseRuntime(),"unchanged-runtime").Snapshot!;
        Assert.NotEqual(first.ContextKey,next.ContextKey);
        Assert.NotEqual(Mahjong.Cn.Engines.AkochanGlobalEngine.ComputeInputSha256(first),
            Mahjong.Cn.Engines.AkochanGlobalEngine.ComputeInputSha256(next));
    }

    [Fact]
    public void Generic_kan_menu_with_four_owned_tiles_is_refined_to_self_kan()
    {
        int[] hand = [4, 4, 4, 4, 0, 1, 2, 9, 10, 11, 18, 19, 20, 33];
        var probe = Probe() with { PublicActionMenu = new("ACTION_MENU_VISIBLE_CANDIDATE", true, true,
            [new("Emj/104/3/3", 0, "Kan", true, "ACTION_MENU_LABEL_CANDIDATE")]) };
        var result = Projector(Snapshot(hand), probe).Project(Runtime(hand) with
        { Legal = new(ActionFlags.MinKan | ActionFlags.Pass, [], [], [], []) }, "turn");
        Assert.Null(result.Error); Assert.True(result.Snapshot!.LegalActions.HasFlag(ActionFlags.AnKan));
        Assert.False(result.Snapshot.LegalActions.HasFlag(ActionFlags.MinKan)); Assert.Equal("tsumo", result.Snapshot.Trigger.Type);
    }
}
