using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;
using Mahjong.Plugin.CN.PublicState;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Synthetic parsed public observations; no game process, memory addresses or private log.</summary>
public sealed class PublicObservationAssemblerTests
{
    private static PublicSnapshot Base(long sequence = 2) => new()
    {
        SessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"), StateRevision = sequence,
        Observation = new(sequence, DateTimeOffset.UnixEpoch.AddSeconds(sequence), "synthetic-public", "managed fixture"),
        Stability = StabilityState.Stable, Synchronization = SynchronizationState.HistoryGap,
    };
    private static AddonProbe Probe(params PublicStatusCandidate[] values) =>
        new("Emj", true, true, true, 50, [], null, PublicStatusCandidates: values);
    private static PublicStatusCandidate Score(int number, string path = "Emj/38/12/2") =>
        new(path, "PlayerScore", "bottom", number, null, "PUBLIC_STATUS_VALUE_CANDIDATE");
    private static PublicTableFaceCandidate Face() => new("river-bottom", "bottom", "Emj/118/4", "Emj/116", 1021,
        100, 200, 40, 52, 0, false, 76001, 0x9A401A76, PublicTableImageReader.VerifiedResourceCode);
    private static AddonProbe PublicTable(bool empty = false)
    {
        var raw = Probe() with
        {
            PublicTableFaces = empty ? [] : [Face()],
            PublicTableAreas = [new("river-bottom", "PUBLIC_AREA_ENUMERATED", true, true, true, empty, empty ? 0 : 1, 0)],
        };
        var tracker = new PublicTableTracker();
        tracker.Observe(1, raw);
        return raw with { PublicTableReading = tracker.Observe(2, raw) };
    }

    [Fact]
    public void Parsed_scores_winds_and_round_are_candidates_with_current_evidence_not_complete_ai_input()
    {
        var snapshot = PublicObservationAssembler.Assemble(Base(), Probe(Score(25000),
            new("Emj/38/9", "SeatWind", "bottom", 1, "南", "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new("Emj/15", "RoundHeader", null, 2, "东", "PUBLIC_STATUS_VALUE_CANDIDATE")));
        var lower = snapshot.Players.Single(x => x.Position == ScreenPosition.Lower);
        Assert.Equal(25000, lower.Score.Value);
        Assert.Equal(1, lower.SeatWind.Value);
        Assert.Equal(0, snapshot.RoundWind.Value);
        Assert.Equal(2, snapshot.HandNumber.Value);
        Assert.All(new[] { lower.Score, lower.SeatWind, snapshot.RoundWind, snapshot.HandNumber }, value =>
        {
            Assert.Equal(Availability.Known, value.Availability);
            Assert.Equal(MappingStatus.Candidate, value.MappingStatus);
            Assert.Equal(SourceKind.Observed, value.SourceKind);
            Assert.Equal(snapshot.Observation!.Sequence, value.Observation!.Sequence);
            Assert.Contains("Emj/", value.Observation.Evidence, StringComparison.Ordinal);
            Assert.False(value.IsConfirmed);
        });
        Assert.Equal(3, snapshot.StatusCandidates.Value!.Values.Length);
        Assert.False(snapshot.OurPlayerId.HasValue);
        Assert.False(snapshot.DealerPlayerId.HasValue);
        Assert.False(snapshot.RoundId.HasValue);
        Assert.False(ReadinessEvaluator.Evaluate(snapshot, ReadinessProfiles.CompleteDecision).IsReady);
    }

    [Fact]
    public void Unnamed_counters_and_ambiguous_dora_labels_do_not_acquire_guessed_semantics()
    {
        var snapshot = PublicObservationAssembler.Assemble(Base(), Probe(
            new("Emj/22", "TopRedStickCount", null, 2, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new("Emj/23", "TopBlackStickCount", null, 1, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new("Emj/105/2/2", "CenterCounterLeft", null, 6, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new("Emj/27", "DoraDisplayLabel", null, null, "宝牌", "PUBLIC_STATUS_VALUE_CANDIDATE")));
        Assert.Equal(4, snapshot.StatusCandidates.Value!.Values.Length);
        Assert.False(snapshot.Honba.HasValue);
        Assert.False(snapshot.RiichiSticks.HasValue);
        Assert.False(snapshot.WallRemaining.HasValue);
        Assert.False(snapshot.DoraMode.HasValue);
        Assert.False(snapshot.DoraDisplay.HasValue);
    }

    [Fact]
    public void Contradictory_score_nodes_are_conflict_and_neither_is_silently_preferred()
    {
        var snapshot = PublicObservationAssembler.Assemble(Base(), Probe(Score(25000), Score(24000, "Emj/38/12/3")));
        var score = snapshot.Players.Single(x => x.Position == ScreenPosition.Lower).Score;
        Assert.Equal(Availability.Conflict, score.Availability);
        Assert.Contains("Emj/38/12/2", score.Observation!.Evidence, StringComparison.Ordinal);
        Assert.Contains("Emj/38/12/3", score.Observation.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void A_validated_mapping_requires_exact_target_version_resource_and_explicit_evidence()
    {
        const string version = "2026.09.15.0000.0000";
        var profile = new PublicObservationProfile(version, LowerHandProfile.EmjUldSha256,
            [new("Emj/38/12/2", "Players.Lower.Score", "synthetic independently validated score mapping")]);
        var context = new PublicObservationContext(version, LowerHandProfile.EmjUldSha256, profile);
        var confirmed = PublicObservationAssembler.Assemble(Base(), Probe(Score(25000)), context);
        Assert.True(confirmed.Players[0].Score.IsConfirmed);
        foreach (var wrong in new[] { context with { ClientVersion = "different" }, context with { UldSha256 = new string('0', 64) },
            context with { Profile = profile with { Mappings = [new("Emj/38/12/2", "Players.Right.Score", "other target")] } },
            context with { Profile = profile with { Mappings = [new("Emj/38/12/2", "Players.Lower.Score", "")] } } })
            Assert.False(PublicObservationAssembler.Assemble(Base(), Probe(Score(25000)), wrong).Players[0].Score.IsConfirmed);
        Assert.False(ReadinessEvaluator.Evaluate(confirmed, ReadinessProfiles.CompleteDecision).IsReady);
    }

    [Fact]
    public void Public_river_images_roundtrip_without_becoming_chronological_discards_or_melds()
    {
        var snapshot = PublicObservationAssembler.Assemble(Base(), PublicTable());
        var lower = snapshot.Players.Single(x => x.Position == ScreenPosition.Lower);
        Assert.Equal(MappingStatus.Candidate, lower.RiverImages.MappingStatus);
        var inventory = lower.RiverImages.Value!;
        Assert.True(inventory.RegionReadable);
        Assert.True(inventory.AllVisibleSlotsDecoded);
        Assert.True(inventory.Stable);
        Assert.False(inventory.HasChronologicalOrder);
        Assert.False(inventory.HistoryComplete);
        var tile = Assert.Single(inventory.Tiles);
        Assert.Equal("Emj/118/4", tile.SlotPath);
        Assert.Equal("Emj/116", tile.GroupPath);
        Assert.Equal(0, tile.Tile.Id);
        Assert.Equal(100, tile.X);
        Assert.All(snapshot.Players, player => { Assert.False(player.River.HasValue); Assert.False(player.Melds.HasValue); });
        var restored = JsonSerializer.Deserialize<PublicSnapshot>(JsonSerializer.Serialize(snapshot))!;
        Assert.Equal(tile, Assert.Single(restored.Players[0].RiverImages.Value!.Tiles));
        Assert.False(restored.Players[0].RiverImages.IsConfirmed);
    }

    [Fact]
    public void Explicit_empty_image_area_is_distinct_from_unread_other_directions()
    {
        var snapshot = PublicObservationAssembler.Assemble(Base(), PublicTable(empty: true));
        var inventory = snapshot.Players[0].RiverImages.Value!;
        Assert.True(inventory.ObservedEmpty);
        Assert.Empty(inventory.Tiles);
        Assert.True(inventory.Stable);
        Assert.False(snapshot.Players[0].River.HasValue);
        Assert.All(snapshot.Players.Skip(1), player => Assert.False(player.RiverImages.HasValue));
    }

    [Fact]
    public void Tampering_with_decoded_public_face_does_not_bypass_source_resource_recheck()
    {
        var probe = PublicTable();
        var reading = probe.PublicTableReading!;
        var tampered = probe with { PublicTableReading = reading with
            { Tiles = reading.Tiles.SetItem(0, reading.Tiles[0] with { Kind34 = 1 }) } };
        var snapshot = PublicObservationAssembler.Assemble(Base(), tampered);
        Assert.Equal(Availability.Conflict, snapshot.Players[0].RiverImages.Availability);
        Assert.False(snapshot.Players[0].RiverImages.HasValue);
    }

    [Fact]
    public void Stale_fields_and_missing_regions_do_not_survive_a_new_observation()
    {
        var first = PublicObservationAssembler.Assemble(Base(), PublicTable() with { PublicStatusCandidates = [Score(25000)] });
        var next = first with { Observation = Base(3).Observation, StateRevision = 3 };
        var snapshot = PublicObservationAssembler.Assemble(next, Probe());
        Assert.False(snapshot.Players[0].Score.HasValue);
        Assert.False(snapshot.Players[0].RiverImages.HasValue);
        Assert.False(snapshot.Honba.HasValue);
    }

    [Fact]
    public void Unlisted_text_paths_and_invalid_values_do_not_enter_normalized_public_observation()
    {
        var snapshot = PublicObservationAssembler.Assemble(Base(), Probe(
            new("Emj/38/5", "PlayerScore", "bottom", 25000, "PRIVATE_NAME", "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new("Emj/38/9", "SeatWind", "bottom", 0, "PRIVATE_NAME", "PUBLIC_STATUS_VALUE_CANDIDATE")));
        Assert.False(snapshot.Players[0].Score.HasValue);
        Assert.False(snapshot.Players[0].SeatWind.HasValue);
        string json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("PRIVATE_NAME", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Emj/38/5", json, StringComparison.Ordinal);
        Assert.Equal("STATUS_VALUE_UNAVAILABLE", Assert.Single(snapshot.StatusCandidates.Value!.Values).Code);
    }

    [Fact]
    public void Monitor_projects_public_observations_while_lower_hand_is_temporarily_unavailable()
    {
        var monitor = new PublicMonitorSession();
        monitor.Start();
        var raw = PublicTable() with { LowerHandReading = null, PublicStatusCandidates = [Score(25000)] };
        for (int i = 1; i <= 2; i++)
            monitor.Observe(new(i, DateTimeOffset.UnixEpoch.AddSeconds(i), "synthetic", "public only", [raw]));
        var snapshot = monitor.Current!;
        Assert.False(snapshot.LowerVisibleFaces.HasValue);
        Assert.Equal(25000, snapshot.Players[0].Score.Value);
        Assert.True(snapshot.Players[0].RiverImages.Value!.Stable);
        Assert.Equal(SynchronizationState.HistoryGap, snapshot.Synchronization);
        monitor.Observe(new(3, DateTimeOffset.UnixEpoch.AddSeconds(3), "synthetic", "gone", [raw with { Present = false, Visible = false }]));
        Assert.Null(monitor.Current);
    }

    [Theory]
    [InlineData("宝牌（多玛式）", DoraDisplayMode.ActualDora)]
    [InlineData("宝牌指示牌", DoraDisplayMode.Indicator)]
    [InlineData("宝牌", DoraDisplayMode.Unknown)]
    public void Public_dora_faces_preserve_displayed_tiles_and_mode_comes_only_from_explicit_label(string label, DoraDisplayMode expected)
    {
        var probe = Probe(new PublicStatusCandidate("Emj/27", "DoraDisplayLabel", null, null, label, "PUBLIC_STATUS_VALUE_CANDIDATE")) with
        { PublicDoraCandidates = [new("Emj/28/2", 0, 76001, 0x9A401A76, 0, false, "PUBLIC_DORA_FACE_CANDIDATE"),
            new("Emj/29/2", 1, 76002, 0xDDE060A6, 1, false, "PUBLIC_DORA_FACE_CANDIDATE")] };
        var snapshot = PublicObservationAssembler.Assemble(Base(), probe);
        Assert.Equal(new[] { 0, 1 }, snapshot.DoraDisplay.Value.Select(x => x.Id));
        Assert.Equal(MappingStatus.Candidate, snapshot.DoraDisplay.MappingStatus);
        Assert.False(snapshot.DoraDisplay.IsConfirmed);
        if (expected == DoraDisplayMode.Unknown) Assert.False(snapshot.DoraMode.HasValue);
        else
        {
            Assert.Equal(expected, snapshot.DoraMode.Value);
            Assert.Equal(MappingStatus.Candidate, snapshot.DoraMode.MappingStatus);
        }
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("hash")]
    [InlineData("identity")]
    [InlineData("error")]
    public void Partial_or_contradictory_dora_display_is_not_exported_as_a_complete_list(string failure)
    {
        var candidate = new PublicDoraCandidate("Emj/28/2", 0, 76001, 0x9A401A76, 0, false, "PUBLIC_DORA_FACE_CANDIDATE");
        candidate = failure switch
        {
            "gap" => candidate with { Path = "Emj/29/2", Slot = 1 },
            "hash" => candidate with { PathHash = 0 },
            "identity" => candidate with { Kind34 = 1 },
            _ => candidate with { Code = "DORA_OWNER_UNVERIFIED" },
        };
        var snapshot = PublicObservationAssembler.Assemble(Base(), Probe() with { PublicDoraCandidates = [candidate] });
        Assert.False(snapshot.DoraDisplay.HasValue);
        Assert.Contains("DORA_PROJECTION_INCOMPLETE", snapshot.DoraDisplay.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Independently_validated_decimal_pair_uses_both_digits_and_keeps_derivation_evidence()
    {
        const string version = "2026.09.15.0000.0000";
        var profile = new PublicObservationProfile(version, LowerHandProfile.EmjUldSha256, [])
        {
            DecimalCounterPairs = [new("Emj/105/2/2", "Emj/105/3/2", "WallRemaining", "synthetic verified decimal counter")],
        };
        var context = new PublicObservationContext(version, LowerHandProfile.EmjUldSha256, profile);
        var probe = Probe(new("Emj/105/2/2", "CenterCounterLeft", null, 6, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new("Emj/105/3/2", "CenterCounterRight", null, 0, null, "PUBLIC_STATUS_VALUE_CANDIDATE"));
        Assert.False(PublicObservationAssembler.Assemble(Base(), probe).WallRemaining.HasValue);
        var snapshot = PublicObservationAssembler.Assemble(Base(), probe, context);
        Assert.Equal(60, snapshot.WallRemaining.Value);
        Assert.True(snapshot.WallRemaining.IsConfirmed);
        Assert.Equal(SourceKind.Derived, snapshot.WallRemaining.SourceKind);
        Assert.Equal(2, snapshot.WallRemaining.Observation!.DerivationInputs.Length);
        Assert.False(PublicObservationAssembler.Assemble(Base(), Probe(probe.PublicStatusCandidates![0]), context).WallRemaining.HasValue);
        Assert.False(PublicObservationAssembler.Assemble(Base(), probe, context with { ClientVersion = "different" }).WallRemaining.HasValue);
    }

    private static RuntimeIdentity Identity() => new(RuntimeIdentity.TargetGame, 15, "15.0.3.5", RuntimeIdentity.TargetDalamud,
        RuntimeIdentity.TargetStructs, "ChineseSimplified", "10.0", null);
    private static RoundTitleResourceCandidate RoundTitleImage() =>
        new("Emj/19", "ROUND_TITLE_RESOURCE_CANDIDATE", 0, 1, 0, 0, 0, 640, 80, 0, 1, 121452, 0x14A042F1);

    [Fact]
    public void Audited_context_requires_full_runtime_identity_and_fixed_uld()
    {
        var identity = Identity();
        Assert.NotNull(PublicObservationAssembler.CreateAuditedContext(identity, LowerHandProfile.EmjUldSha256));
        Assert.Null(PublicObservationAssembler.CreateAuditedContext(identity, null));
        Assert.Null(PublicObservationAssembler.CreateAuditedContext(identity, new string('0', 64)));
        foreach (var invalid in new[] { identity with { GameVersion = "different" }, identity with { Api = 14 },
            identity with { DalamudCommit = "different" }, identity with { ClientStructsVersion = "different" },
            identity with { Language = "Japanese" }, identity with { Error = "VERSION_GAME" } })
            Assert.Null(PublicObservationAssembler.CreateAuditedContext(invalid, LowerHandProfile.EmjUldSha256));
    }

    [Fact]
    public void Pinned_title_resource_confirms_round_only_with_matching_context()
    {
        var context = PublicObservationAssembler.CreateAuditedContext(Identity(), LowerHandProfile.EmjUldSha256)!;
        var probe = Probe() with { RoundTitleResource = RoundTitleImage() };
        var snapshot = PublicObservationAssembler.Assemble(Base(), probe, context);
        Assert.Equal(0, snapshot.RoundWind.Value);
        Assert.Equal(2, snapshot.HandNumber.Value);
        Assert.True(snapshot.RoundWind.IsConfirmed);
        Assert.True(snapshot.HandNumber.IsConfirmed);
        Assert.Contains("RoundTitleCatalog", snapshot.RoundWind.Observation!.Source, StringComparison.Ordinal);
        Assert.False(snapshot.RoundId.HasValue);
        Assert.False(snapshot.DealerPlayerId.HasValue);
        Assert.False(PublicObservationAssembler.Assemble(Base(), probe).RoundWind.HasValue);
        Assert.False(PublicObservationAssembler.Assemble(Base(), probe, context with { ClientVersion = "different" }).RoundWind.HasValue);
        Assert.False(PublicObservationAssembler.Assemble(Base(), probe, context with { UldSha256 = new string('0', 64) }).RoundWind.HasValue);
        Assert.False(PublicObservationAssembler.Assemble(Base(), probe with { RoundTitleResource = RoundTitleImage() with
            { TexturePathHash = 0 } }, context).RoundWind.HasValue);
    }

    [Fact]
    public void Conflicting_round_text_does_not_silently_override_or_get_hidden_by_the_resource()
    {
        var context = PublicObservationAssembler.CreateAuditedContext(Identity(), LowerHandProfile.EmjUldSha256)!;
        var probe = Probe(new PublicStatusCandidate("Emj/15", "RoundHeader", null, 2, "南", "PUBLIC_STATUS_VALUE_CANDIDATE")) with
            { RoundTitleResource = RoundTitleImage() };
        var snapshot = PublicObservationAssembler.Assemble(Base(), probe, context);
        Assert.Equal(Availability.Conflict, snapshot.RoundWind.Availability);
        Assert.Contains("ROUND_TITLE_CONFLICT", snapshot.RoundWind.Reason, StringComparison.Ordinal);
        Assert.True(snapshot.HandNumber.IsConfirmed);
        Assert.False(ReadinessEvaluator.Evaluate(snapshot, ReadinessProfiles.CompleteDecision).IsReady);
    }

    [Fact]
    public void Audited_profile_maps_sticks_decimal_wall_mode_and_present_panels_without_filling_missing_players()
    {
        var context = PublicObservationAssembler.CreateAuditedContext(Identity(), LowerHandProfile.EmjUldSha256)!;
        var probe = Probe(Score(25000),
            new("Emj/38/9", "SeatWind", "bottom", 0, "东", "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new("Emj/22", "TopRedStickCount", null, 1, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new("Emj/23", "TopBlackStickCount", null, 2, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new("Emj/105/2/2", "CenterCounterLeft", null, 5, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new("Emj/105/3/2", "CenterCounterRight", null, 7, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new("Emj/27", "DoraDisplayLabel", null, null, "宝牌（多玛式）", "PUBLIC_STATUS_VALUE_CANDIDATE"));
        var snapshot = PublicObservationAssembler.Assemble(Base(), probe, context);
        Assert.Equal(1, snapshot.RiichiSticks.Value);
        Assert.Equal(2, snapshot.Honba.Value);
        Assert.Equal(57, snapshot.WallRemaining.Value);
        Assert.All(new[] { snapshot.RiichiSticks, snapshot.Honba, snapshot.WallRemaining }, x => Assert.True(x.IsConfirmed));
        Assert.True(snapshot.DoraMode.IsConfirmed);
        Assert.Equal(DoraDisplayMode.ActualDora, snapshot.DoraMode.Value);
        Assert.True(snapshot.Players[0].Score.IsConfirmed);
        Assert.True(snapshot.Players[0].SeatWind.IsConfirmed);
        Assert.False(snapshot.DealerPlayerId.HasValue);
        Assert.False(snapshot.OurPlayerId.HasValue);
    }

    [Fact]
    public void Monitor_stop_discards_the_verified_context_before_a_default_restart()
    {
        var context = PublicObservationAssembler.CreateAuditedContext(Identity(), LowerHandProfile.EmjUldSha256)!;
        var probe = Probe() with { RoundTitleResource = RoundTitleImage() };
        var monitor = new PublicMonitorSession();
        monitor.Start(context);
        monitor.Observe(new(1, DateTimeOffset.UnixEpoch.AddSeconds(1), "synthetic", "title", [probe]));
        Assert.True(monitor.Current!.RoundWind.IsConfirmed);
        monitor.Stop("fixture");
        monitor.Start();
        monitor.Observe(new(1, DateTimeOffset.UnixEpoch.AddSeconds(1), "synthetic", "title", [probe]));
        Assert.False(monitor.Current!.RoundWind.HasValue);
    }

    [Fact]
    public void Action_menu_keeps_current_rows_enabled_state_and_generic_kan_without_inventing_legal_actions()
    {
        var menu = new PublicActionMenuCandidate("ACTION_MENU_VISIBLE_CANDIDATE", true, true,
            [new("Emj/104/3/5", 300, "Pass", true, "ACTION_MENU_LABEL_CANDIDATE"),
             new("Emj/104/3/4", 200, "Kan", false, "ACTION_MENU_LABEL_CANDIDATE"),
             new("Emj/104/3/3", 100, "Tsumo", true, "ACTION_MENU_LABEL_CANDIDATE")]);
        var snapshot = PublicObservationAssembler.Assemble(Base(7), Probe() with { PublicActionMenu = menu });
        var field = snapshot.VisibleActionMenu;
        Assert.False(field.IsConfirmed);
        Assert.Equal(MappingStatus.Candidate, field.MappingStatus);
        Assert.True(field.Value!.Visible);
        Assert.True(field.Value.AllVisibleRowsDecoded);
        Assert.False(field.Value.CompleteLegalActions);
        Assert.False(field.Value.ActionOccurred);
        Assert.Equal(new[] { PublicMenuAction.Tsumo, PublicMenuAction.Kan, PublicMenuAction.Pass }, field.Value.Rows.Select(x => x.Action));
        Assert.False(field.Value.Rows[1].Enabled);
        Assert.All(field.Value.Rows, row => Assert.Equal(7, row.StateRevision));
        Assert.False(snapshot.LegalActions.HasValue);
        Assert.False(snapshot.DiscardableSlots.HasValue);
        Assert.False(snapshot.DecisionWindowId.HasValue);
    }

    [Fact]
    public void Missing_menu_does_not_imply_pass_none_or_old_actions()
    {
        var menu = new PublicActionMenuCandidate("ACTION_MENU_NOT_VISIBLE", false, false, []);
        var snapshot = PublicObservationAssembler.Assemble(Base(), Probe() with { PublicActionMenu = menu });
        Assert.False(snapshot.VisibleActionMenu.Value!.Visible);
        Assert.Empty(snapshot.VisibleActionMenu.Value.Rows);
        Assert.False(snapshot.LegalActions.HasValue);
        var next = PublicObservationAssembler.Assemble(snapshot with { Observation = Base(3).Observation, StateRevision = 3 }, Probe());
        Assert.False(next.VisibleActionMenu.HasValue);
        Assert.False(next.LegalActions.HasValue);
    }

    [Fact]
    public void Unknown_action_text_is_not_exported_and_duplicate_renderer_paths_are_conflict()
    {
        var row = new PublicActionMenuRow("Emj/104/3/3", 100, "PRIVATE_TEXT", true, "ACTION_MENU_LABEL_CANDIDATE");
        var probe = Probe() with { PublicActionMenu = new("ACTION_MENU_VISIBLE_CANDIDATE", true, true, [row]) };
        var snapshot = PublicObservationAssembler.Assemble(Base(), probe);
        Assert.False(snapshot.VisibleActionMenu.Value!.AllVisibleRowsDecoded);
        Assert.Equal(PublicMenuAction.Unknown, Assert.Single(snapshot.VisibleActionMenu.Value.Rows).Action);
        Assert.DoesNotContain("PRIVATE_TEXT", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        var duplicate = PublicObservationAssembler.Assemble(Base(), probe with
            { PublicActionMenu = probe.PublicActionMenu! with { Rows = [row, row] } });
        Assert.Equal(Availability.Conflict, duplicate.VisibleActionMenu.Availability);
    }

    private static PublicStatusCandidate[] PlayerPanels(params int[] winds)
    {
        string[] directions = ["bottom", "right", "top", "left"];
        uint[] panels = [38, 40, 42, 44];
        string[] names = ["东", "南", "西", "北"];
        return Enumerable.Range(0, 4).SelectMany(i => new[]
        {
            new PublicStatusCandidate($"Emj/{panels[i]}/{(i == 0 ? 9 : 10)}", "SeatWind", directions[i], winds[i], names[winds[i]], "PUBLIC_STATUS_VALUE_CANDIDATE"),
            new PublicStatusCandidate($"Emj/{panels[i]}/{(i == 0 ? 12 : 13)}/2", "PlayerScore", directions[i], 25000 + i * 1000, null, "PUBLIC_STATUS_VALUE_CANDIDATE"),
        }).ToArray();
    }

    private static PublicObservationContext VerifiedPlayerPanelContext()
    {
        var original = PublicObservationAssembler.CreateAuditedContext(Identity(), LowerHandProfile.EmjUldSha256)!;
        ScreenPosition[] positions = [ScreenPosition.Lower, ScreenPosition.Right, ScreenPosition.Upper, ScreenPosition.Left];
        uint[] panels = [38, 40, 42, 44];
        var mappings = Enumerable.Range(0, 4).SelectMany(i => new[]
        {
            new ValidatedPublicMapping($"Emj/{panels[i]}/{(i == 0 ? 9 : 10)}", $"Players.{positions[i]}.SeatWind", "synthetic audited panel wind"),
            new ValidatedPublicMapping($"Emj/{panels[i]}/{(i == 0 ? 12 : 13)}/2", $"Players.{positions[i]}.Score", "synthetic audited panel score"),
        }).ToImmutableArray();
        return original with { Profile = original.Profile! with { Mappings = mappings,
            RelativePlayerIdentityEvidence = "synthetic lower/right/upper/left fixed relative player identity mapping" } };
    }

    [Fact]
    public void Audited_player_panel_mapping_is_opt_in_and_distinguishes_static_evidence_from_live_value_comparison()
    {
        var production = PublicObservationAssembler.CreateAuditedContext(Identity(), LowerHandProfile.EmjUldSha256)!;
        var probe = Probe(PlayerPanels(3, 0, 1, 2));
        var snapshot = PublicObservationAssembler.Assemble(Base(), probe);
        Assert.All(snapshot.Players, player => { Assert.False(player.SeatWind.IsConfirmed); Assert.False(player.Score.IsConfirmed); Assert.False(player.PlayerId.HasValue); });
        Assert.False(snapshot.DealerPlayerId.HasValue);
        Assert.False(snapshot.OurPlayerId.HasValue);
        var audited = PublicObservationAssembler.Assemble(Base(), probe, production);
        Assert.All(audited.Players, player => { Assert.True(player.SeatWind.IsConfirmed); Assert.True(player.Score.IsConfirmed); });
        Assert.Equal(1, audited.DealerPlayerId.Value);
        Assert.Contains("compared to current public display", audited.Players[0].Score.Observation!.Evidence, StringComparison.Ordinal);
        Assert.Contains("no per-value live visual confirmation", audited.Players[2].Score.Observation!.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Verified_winds_map_screen_relative_ids_and_dealer_without_equating_our_id_with_east()
    {
        var context = VerifiedPlayerPanelContext();
        var snapshot = PublicObservationAssembler.Assemble(Base(), Probe(PlayerPanels(3, 0, 1, 2)), context);
        Assert.Equal(0, snapshot.OurPlayerId.Value);
        Assert.True(snapshot.OurPlayerId.IsConfirmed);
        Assert.Equal(1, snapshot.DealerPlayerId.Value);
        Assert.Equal(PlayerIdentityBasis.RelativeToLocalPlayer, snapshot.PlayerIdBasis.Value);
        Assert.Equal(3, snapshot.Players.Single(x => x.Position == ScreenPosition.Lower).SeatWind.Value);
        Assert.Equal(new[] { 0, 1, 2, 3 }, snapshot.Players.Select(x => x.PlayerId.Value));
        Assert.Equal(new[] { 25000, 26000, 27000, 28000 }, snapshot.Players.Select(x => x.Score.Value));
        Assert.False(snapshot.RoundWind.HasValue);
        Assert.False(snapshot.RoundId.HasValue);
        Assert.Equal(4, snapshot.DealerPlayerId.Observation!.DerivationInputs.Length);
        var next = PublicObservationAssembler.Assemble(Base(3), Probe(PlayerPanels(0, 1, 2, 3)), context);
        Assert.Equal(snapshot.OurPlayerId.Value, next.OurPlayerId.Value);
        Assert.Equal(0, next.DealerPlayerId.Value);
    }

    [Theory]
    [InlineData(3, 0, 0, 2)]
    [InlineData(3, 2, 1, 0)]
    public void Contradictory_verified_winds_are_conflict_instead_of_relabeling_players(int lower, int right, int upper, int left)
    {
        var snapshot = PublicObservationAssembler.Assemble(Base(), Probe(PlayerPanels(lower, right, upper, left)), VerifiedPlayerPanelContext());
        Assert.Equal(Availability.Conflict, snapshot.DealerPlayerId.Availability);
        Assert.Equal(Availability.Conflict, snapshot.OurPlayerId.Availability);
        Assert.All(snapshot.Players, p => Assert.Equal(Availability.Conflict, p.PlayerId.Availability));
    }

    [Fact]
    public void Missing_wind_on_new_observation_does_not_reuse_old_dealer_or_relative_ids()
    {
        var context = VerifiedPlayerPanelContext();
        var first = PublicObservationAssembler.Assemble(Base(), Probe(PlayerPanels(3, 0, 1, 2)), context);
        var newBase = first with { Observation = Base(3).Observation, StateRevision = 3 };
        var missing = PlayerPanels(3, 0, 1, 2).Where(x => x.Path != "Emj/42/10").ToArray();
        var next = PublicObservationAssembler.Assemble(newBase, Probe(missing), context);
        Assert.False(next.DealerPlayerId.HasValue);
        Assert.False(next.OurPlayerId.HasValue);
        Assert.False(next.PlayerIdBasis.HasValue);
        Assert.All(next.Players, p => Assert.False(p.PlayerId.HasValue));
    }

    private static PublicOpponentHandStatus OpponentBacks(bool separate = false)
    {
        var slots = Enumerable.Range(1, 13).Select(i => new PublicOpponentBackSlot($"Emj/{1380000 + i}/4", false,
            PublicOpponentHandReader.VerifiedCode)).ToImmutableArray();
        if (separate) slots = slots.Add(new("Emj/139/4", true, PublicOpponentHandReader.VerifiedCode));
        return new("right", true, true, true, slots.Length, slots.Length, separate,
            "OPPONENT_VISIBLE_BACK_COUNT_CANDIDATE", slots);
    }

    [Theory]
    [InlineData(false, 13)]
    [InlineData(true, 14)]
    public void Opponent_appearance_contains_only_verified_public_back_slots_and_never_draw_events(bool separate, int count)
    {
        var snapshot = PublicObservationAssembler.Assemble(Base(), Probe() with { PublicOpponentHands = [OpponentBacks(separate)] });
        var field = snapshot.Players.Single(x => x.Position == ScreenPosition.Right).OpponentHandAppearance;
        var appearance = field.Value!;
        Assert.Equal(count, appearance.VerifiedBackCount);
        Assert.Equal(count, appearance.VisibleSlots);
        Assert.True(appearance.CountComplete);
        Assert.Equal(separate, appearance.SeparateDrawSlotVisible);
        Assert.False(appearance.IsDrawEvent);
        Assert.False(appearance.HistoryComplete);
        Assert.False(field.IsConfirmed);
        Assert.All(appearance.Slots, slot => Assert.Equal(snapshot.StateRevision, slot.StateRevision));
        Assert.False(snapshot.TurnPlayerId.HasValue);
        string json = JsonSerializer.Serialize(appearance);
        Assert.DoesNotContain("Kind34", json, StringComparison.Ordinal);
        Assert.DoesNotContain("IconId", json, StringComparison.Ordinal);
        Assert.False(snapshot.Players[0].OpponentHandAppearance.HasValue);
    }

    [Fact]
    public void Incomplete_opponent_back_read_cannot_establish_absence_of_a_separate_slot()
    {
        var backs = OpponentBacks(true);
        var last = backs.Slots[^1] with { Code = "OPPONENT_BACK_NOT_VISIBLE" };
        backs = backs with { VerifiedBackCount = 13, SeparateDrawSlotVisible = null, Slots = backs.Slots.SetItem(13, last) };
        var snapshot = PublicObservationAssembler.Assemble(Base(), Probe() with { PublicOpponentHands = [backs] });
        var appearance = snapshot.Players[1].OpponentHandAppearance.Value!;
        Assert.False(appearance.CountComplete);
        Assert.Null(appearance.SeparateDrawSlotVisible);
        Assert.False(appearance.Slots[^1].BackVerified);
    }

    [Fact]
    public void A_cross_direction_back_slot_or_inconsistent_count_is_conflict()
    {
        var backs = OpponentBacks();
        foreach (var invalid in new[] { backs with { VerifiedBackCount = 12 }, backs with { Slots = backs.Slots.SetItem(0,
            new("Emj/141/4", false, PublicOpponentHandReader.VerifiedCode)) } })
        {
            var snapshot = PublicObservationAssembler.Assemble(Base(), Probe() with { PublicOpponentHands = [invalid] });
            Assert.Equal(Availability.Conflict, snapshot.Players[1].OpponentHandAppearance.Availability);
        }
    }

    [Fact]
    public void River_display_order_and_tint_are_preserved_without_becoming_tsumogiri_or_called_semantics()
    {
        var raw = PublicTable();
        var face = Face() with { RiverPosition = new(1, 2, 2, true), VisualMark = new(0, -75, -75, 100, 100, 100, "red-tinted") };
        raw = raw with { PublicTableFaces = [face] };
        var tracker = new PublicTableTracker();
        tracker.Observe(1, raw);
        raw = raw with { PublicTableReading = tracker.Observe(2, raw) };
        var snapshot = PublicObservationAssembler.Assemble(Base(), raw);
        var tile = Assert.Single(snapshot.Players[0].RiverImages.Value!.Tiles);
        Assert.Equal(2, tile.DisplayPosition!.ReadOrder);
        Assert.True(tile.DisplayPosition.IsSideways);
        Assert.False(tile.DisplayPosition.IsEventOrder);
        Assert.Equal("red-tinted", tile.Style!.Name);
        Assert.False(tile.Style.CalledTileMeaningVerified);
        Assert.False(tile.Style.TsumogiriMeaningVerified);
        Assert.False(snapshot.Players[0].River.HasValue);
    }

    [Fact]
    public void Response_highlight_projection_keeps_raw_zero_color_and_explicit_unknown_semantics()
    {
        var face = Face() with { VisualMark = new(23, 23, 23, 100, 100, 100, "unclassified") };
        var raw = PublicTable() with
        {
            PublicTableFaces = [face],
            PublicActionMenu = new("ACTION_MENU_VISIBLE_CANDIDATE", true, true,
                [new("Emj/104/3/2", 100, "Chi", true, "ACTION_MENU_LABEL_CANDIDATE", 0)],
                new(1, 1, 0, 5, false, false, true)),
        };
        var tracker = new PublicTableTracker();
        tracker.Observe(1, raw); tracker.Observe(2, raw);
        raw = raw with { PublicTableFaces = [face with { VisualMark = new(0, 0, 0, 100, 100, 100, "normal") }] };
        raw = raw with { PublicTableReading = tracker.Observe(3, raw) };
        var context = new PublicObservationContext(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256,
            new(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, []));
        var snapshot = PublicObservationAssembler.Assemble(Base(3), raw, context);
        Assert.Equal(Availability.Known, snapshot.Players[0].RiverImages.Availability);
        var inventory = Assert.IsType<PublicImageInventory>(snapshot.Players[0].RiverImages.Value);
        var tile = Assert.Single(inventory.Tiles);
        Assert.True(tile.Stable);
        Assert.True(tile.Style!.ResponseHighlight);
        Assert.Equal("normal", tile.Style.Name);
        Assert.Equal(0, tile.Style.AddRed);
        Assert.False(tile.Tsumogiri.HasValue); Assert.False(tile.WasClaimed.HasValue);
        Assert.Contains("高亮", tile.Tsumogiri.Reason);
        var zeroFace = Assert.Single(raw.PublicTableFaces!);
        Assert.False(zeroFace.VisualMark!.ResponseHighlight); // Derived tag is absent in the raw observation.
        foreach (var changedMark in new[] { zeroFace.VisualMark with { AddRed = 1 }, zeroFace.VisualMark with { Style = "unclassified" } })
        {
            var mismatch = PublicObservationAssembler.Assemble(Base(3), raw with
                { PublicTableFaces = [zeroFace with { VisualMark = changedMark }] }, context);
            Assert.Equal(Availability.Conflict, mismatch.Players[0].RiverImages.Availability);
        }
    }
}
