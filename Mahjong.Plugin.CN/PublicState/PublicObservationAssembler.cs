using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Mahjong.Cn;
using Mahjong.Cn.PublicState;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.PublicState;

internal sealed record ValidatedPublicMapping(string Path, string Target, string Evidence);
internal sealed record ValidatedDecimalCounterPair(string TensPath, string UnitsPath, string Target, string Evidence);
internal sealed record PublicObservationProfile(string ClientVersion, string UldSha256,
    ImmutableArray<ValidatedPublicMapping> Mappings)
{
    public ImmutableArray<ValidatedDecimalCounterPair> DecimalCounterPairs { get; init; } = [];
    public string? RelativePlayerIdentityEvidence { get; init; }
}
internal sealed record PublicObservationContext(string ClientVersion, string UldSha256,
    PublicObservationProfile? Profile = null);

/// <summary>Managed projection only. Current display inventories never become chronological rivers or game events.</summary>
internal static class PublicObservationAssembler
{
    /// <summary>Only these precise status meanings were compared with this client's ordinary public display.</summary>
    internal static PublicObservationContext? CreateAuditedContext(RuntimeIdentity identity, string? uldSha256)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Error is not null || identity.GameVersion != RuntimeIdentity.TargetGame || identity.Api != 15 ||
            !HasAuditedPublicLayout(identity) ||
            identity.Language != "ChineseSimplified" ||
            !string.Equals(uldSha256, LowerHandProfile.EmjUldSha256, StringComparison.OrdinalIgnoreCase)) return null;
        const string evidence = "docs/cn/LIVE-PUBLIC-STATUS-EVIDENCE.md; CN 2026.09.15 ordinary public display compared on 2026-09-24";
        var mappings = ImmutableArray.CreateBuilder<ValidatedPublicMapping>();
        mappings.Add(new("Emj/22", "RiichiSticks", evidence));
        mappings.Add(new("Emj/23", "Honba", evidence));
        mappings.Add(new("Emj/27", "DoraMode", evidence));
        var directions = new[] { ScreenPosition.Lower, ScreenPosition.Right, ScreenPosition.Upper, ScreenPosition.Left };
        uint[] panels = [38, 40, 42, 44];
        for (int i = 0; i < panels.Length; i++)
        {
            string panelEvidence = evidence + (i < 2
                ? "; lower/right score and wind compared to current public display"
                : "; upper/left fixed equivalent ULD panel and finite glyph mapping; no per-value live visual confirmation");
            mappings.Add(new($"Emj/{panels[i]}/{(i == 0 ? 9 : 10)}", $"Players.{directions[i]}.SeatWind", panelEvidence));
            foreach (int leaf in new[] { 2, 3 })
                mappings.Add(new($"Emj/{panels[i]}/{(i == 0 ? 12 : 13)}/{leaf}", $"Players.{directions[i]}.Score", panelEvidence));
        }
        var profile = new PublicObservationProfile(RuntimeIdentity.TargetGame, LowerHandProfile.EmjUldSha256, mappings.ToImmutable())
        {
            DecimalCounterPairs = [new("Emj/105/2/2", "Emj/105/3/2", "WallRemaining", evidence)],
            RelativePlayerIdentityEvidence = evidence + "; fixed lower/right/upper/left relative identity; requires all four current winds and cyclic consistency",
        };
        return new(identity.GameVersion, LowerHandProfile.EmjUldSha256, profile);
    }

    // This managed projection also replays historical public DTOs. The September 18
    // pair remains valid evidence for the same client/ULD; never rewrite its provenance.
    // Live native reads still require RuntimeIdentity.Validate's current pair and Error=null.
    private static bool HasAuditedPublicLayout(RuntimeIdentity identity) =>
        (identity.DalamudCommit == RuntimeIdentity.TargetDalamud && identity.ClientStructsVersion == RuntimeIdentity.TargetStructs) ||
        (identity.DalamudCommit == "cac6159a2c76e62a4e1f7bc347454c205808bb04" &&
         identity.ClientStructsVersion == "1.0.0+f824354f4a6a2b1cd16cc8fcb670c7a66bf64880");

    internal static PublicSnapshot Assemble(PublicSnapshot source, AddonProbe? addon,
        PublicObservationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var players = source.Players.IsDefault ? ImmutableArray<PlayerPublicState>.Empty : source.Players;
        var result = source with
        {
            StatusCandidates = Field<PublicStatusObservation>.Unknown("本次没有受控公开状态读取。"),
            Players = players.Select(p => p with
            {
                RiverImages = Field<PublicImageInventory>.Unknown("本次牌河图像不可读；未沿用旧库存。"),
                MeldImages = Field<PublicImageInventory>.Unknown("本次副露图像不可读；未沿用旧库存。"),
                OpponentHandAppearance = Field<PublicConcealedAppearance>.Unknown("本次没有对手公开背牌观察。"),
                Score = Fresh(p.Score, source), SeatWind = Fresh(p.SeatWind, source),
                PlayerId = Fresh(p.PlayerId, source),
                RiichiDeclared = Fresh(p.RiichiDeclared, source), RiichiEstablished = Fresh(p.RiichiEstablished, source),
                Ippatsu = Fresh(p.Ippatsu, source),
                DoubleRiichi = Fresh(p.DoubleRiichi, source),
            }).ToImmutableArray(),
            RoundWind = Fresh(source.RoundWind, source), HandNumber = Fresh(source.HandNumber, source),
            Honba = Fresh(source.Honba, source), RiichiSticks = Fresh(source.RiichiSticks, source),
            WallRemaining = Fresh(source.WallRemaining, source), DoraMode = Fresh(source.DoraMode, source),
            OwnDrawKind = Fresh(source.OwnDrawKind, source),
            OurDoubleRiichi = Fresh(source.OurDoubleRiichi, source),
            OurTemporaryFuriten = Fresh(source.OurTemporaryFuriten, source),
            OurRiichiFuriten = Fresh(source.OurRiichiFuriten, source),
            DoraDisplay = Fresh(source.DoraDisplay, source),
            VisibleActionMenu = Field<PublicActionMenuObservation>.Unknown("本次未读取可见动作菜单。"),
            OurPlayerId = Fresh(source.OurPlayerId, source), DealerPlayerId = Fresh(source.DealerPlayerId, source),
            PlayerIdBasis = Fresh(source.PlayerIdBasis, source),
            Rules = source.Rules with { MatchType = Fresh(source.Rules.MatchType, source),
                OpenTanyao = Fresh(source.Rules.OpenTanyao, source) },
        };
        if (source.Observation is not { } boundary || addon is not
            { Name: "Emj", Present: true, Visible: true, Ready: true, Error: null }) return result;

        var imageReference = boundary with { Source = "VisibleUiReader/PublicTableTracker:Emj",
            Evidence = "docs/cn/PUBLIC-TABLE-EVIDENCE.md; fixed public image resources; no chronological inference" };
        result = result with { Players = result.Players.Select(p => p with
        {
            RiverImages = Images(addon, p.Position, "river", imageReference, context),
            MeldImages = Images(addon, p.Position, "meld", imageReference, context),
            OpponentHandAppearance = OpponentAppearance(addon.PublicOpponentHands, p.Position, source.StateRevision, boundary),
        }).ToImmutableArray() };
        result = result with { DoraDisplay = DoraImages(addon, boundary, context) };
        result = PublicCurrentFacts.Apply(result, addon, context);
        result = result with { VisibleActionMenu = ActionMenu(addon.PublicActionMenu, source.StateRevision, boundary) };
        result = RoundTitle(result, addon.RoundTitleResource, context, boundary);

        var candidates = addon.PublicStatusCandidates;
        if (candidates is null) return result;
        if (candidates.Count > PublicStatusReader.MaximumCandidates ||
            candidates.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            return result with { StatusCandidates = new Field<PublicStatusObservation>
            { Availability = Availability.Conflict, Reason = "STATUS_PROJECTION_CONFLICT：状态候选重复或超过上限。", Observation = boundary } };
        var retained = ImmutableArray.CreateBuilder<PublicStatusValue>();
        foreach (var candidate in candidates)
        {
            if (!PublicStatusReader.TryRoute(candidate.Path, out var route) || route.Field != candidate.Field ||
                route.Direction != candidate.Direction) continue;
            bool accepted = candidate.Code == "PUBLIC_STATUS_VALUE_CANDIDATE" && ValidStatus(candidate, route.Grammar);
            retained.Add(new(candidate.Path, candidate.Field, Position(candidate.Direction),
                accepted ? candidate.Number : null, accepted ? candidate.Value : null,
                accepted ? candidate.Code : "STATUS_VALUE_UNAVAILABLE"));
        }
        var statusReference = boundary with { Source = "VisibleUiReader/PublicStatusReader:Emj",
            Evidence = "docs/cn/VISIBLE-FIELDS-EVIDENCE.md; bounded allowlisted parsed values; semantic mapping is Candidate" };
        var values = retained.ToImmutable();
        result = result with { StatusCandidates = Field<PublicStatusObservation>.Known(new(values), statusReference,
            mappingStatus: MappingStatus.Candidate) };
        var valid = values.Where(x => x.Code == "PUBLIC_STATUS_VALUE_CANDIDATE").ToArray();
        result = result with { Players = result.Players.Select(p => p with
        {
            Score = Number(valid.Where(x => x.Field == "PlayerScore" && x.Position == p.Position),
                "Players." + p.Position + ".Score", statusReference, context, p.Score),
            SeatWind = Number(valid.Where(x => x.Field == "SeatWind" && x.Position == p.Position),
                "Players." + p.Position + ".SeatWind", statusReference, context, p.SeatWind),
        }).ToImmutableArray() };
        var round = valid.Where(x => x.Field == "RoundHeader").ToArray();
        result = result with
        {
            HandNumber = ReconcileRound(result.HandNumber, Number(round, "HandNumber", statusReference, context,
                Field<int>.Unknown("本次未读到局数文本。"))),
            RoundWind = ReconcileRound(result.RoundWind, Number(round.Select(x => x with { Number = Wind(x.Value) }), "RoundWind", statusReference, context,
                Field<int>.Unknown("本次未读到场风文本。"))),
        };
        // Unnamed counters do not acquire semantics from their colors or proximity.
        // They can project only through an explicit, version-matched mapping profile.
        foreach (string target in new[] { "Honba", "RiichiSticks" })
        {
            var mapped = valid.Where(x => x.Number is not null && Mapping(x.Path, target, context) is not null &&
                x.Field is "TopRedStickCount" or "TopBlackStickCount").ToArray();
            var field = Number(mapped, target, statusReference, context, Field<int>.Unknown("计数器语义未映射。"));
            if (mapped.Length == 0) continue;
            result = target switch { "Honba" => result with { Honba = field }, "RiichiSticks" => result with { RiichiSticks = field },
                _ => result with { WallRemaining = field } };
        }
        if (MatchingProfile(context) is { } activeProfile && !activeProfile.DecimalCounterPairs.IsDefault)
        {
            var pairs = activeProfile.DecimalCounterPairs.Where(x => x.Target == "WallRemaining" &&
                !string.IsNullOrWhiteSpace(x.Evidence) && x.Evidence.Length <= 2048).ToArray();
            if (pairs.Length == 1)
            {
                var pair = pairs[0];
                var tens = valid.SingleOrDefault(x => x.Path == pair.TensPath && x.Field == "CenterCounterLeft");
                var units = valid.SingleOrDefault(x => x.Path == pair.UnitsPath && x.Field == "CenterCounterRight");
                if (tens?.Number is >= 0 and <= 9 && units?.Number is >= 0 and <= 9)
                {
                    int number = tens.Number.Value * 10 + units.Number.Value;
                    var reference = statusReference with { Evidence = pair.Evidence,
                        DerivationInputs = [pair.TensPath, pair.UnitsPath] };
                    result = result with { WallRemaining = number > 70
                        ? Field<int>.Conflict(number, reference, "十位与个位合成的余牌数越界。")
                        : Field<int>.Known(number, reference, SourceKind.Derived) };
                }
            }
        }
        var dora = valid.Where(x => x.Field == "DoraDisplayLabel").Select(x => (Value: x, Mode: DoraMode(x.Value)))
            .Where(x => x.Mode != DoraDisplayMode.Unknown).ToArray();
        if (dora.Length > 0)
        {
            var reference = Reference(statusReference, dora.Select(x => x.Value), "DoraMode", context, out var mapping);
            result = result with { DoraMode = dora.Select(x => x.Mode).Distinct().Count() != 1
                ? Field<DoraDisplayMode>.Conflict(dora[0].Mode, reference, "宝牌显示模式候选相互矛盾。")
                : Field<DoraDisplayMode>.Known(dora[0].Mode, reference, mappingStatus: mapping) };
        }
        return PlayerIdentities(result, context, boundary);
    }

    private static Field<PublicActionMenuObservation> ActionMenu(PublicActionMenuCandidate? candidate,
        long revision, ObservationReference boundary)
    {
        if (candidate is null) return Field<PublicActionMenuObservation>.Unknown("本次未读取可见动作菜单。");
        var reference = boundary with { Source = "VisibleUiReader/PublicActionMenuReader:Emj/104/3",
            Evidence = "fixed Emj 1052/1030/1029/text4 ownership and finite action labels; menu semantics remain Candidate" };
        if (candidate.Rows is null || candidate.Rows.Count > PublicActionMenuReader.MaximumVisibleRows ||
            candidate.Rows.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != candidate.Rows.Count ||
            candidate.Rows.Any(x => !ValidActionRowPath(x.Path) || !float.IsFinite(x.ScreenY)))
            return new() { Availability = Availability.Conflict, SourceKind = SourceKind.Observed, Observation = reference,
                Reason = "ACTION_MENU_PROJECTION_CONFLICT：菜单行路径、位置或数量无效。" };
        var rows = candidate.Rows.Select(row =>
        {
            PublicMenuAction action = row.Code == "ACTION_MENU_LABEL_CANDIDATE" ? MenuAction(row.Action) : PublicMenuAction.Unknown;
            return new PublicActionMenuOption(row.Path, row.ScreenY, revision, action,
                action != PublicMenuAction.Unknown ? row.Enabled : null,
                action != PublicMenuAction.Unknown ? "ACTION_MENU_LABEL_CANDIDATE" : "ACTION_MENU_LABEL_UNAVAILABLE");
        }).OrderBy(x => x.ScreenY).ToImmutableArray();
        bool complete = candidate.Visible && candidate.AllVisibleRowsDecoded && candidate.Code == "ACTION_MENU_VISIBLE_CANDIDATE" &&
            rows.Length > 0 && rows.All(x => x.Action != PublicMenuAction.Unknown && x.Enabled is not null) &&
            rows.Select(x => x.ScreenY).Distinct().Count() == rows.Length;
        if (!candidate.Visible && rows.Length > 0)
            return new() { Availability = Availability.Conflict, SourceKind = SourceKind.Observed, Observation = reference,
                Reason = "ACTION_MENU_PROJECTION_CONFLICT：不可见菜单仍携带当前行。" };
        string code = !candidate.Visible ? "ACTION_MENU_NOT_VISIBLE" : complete ? "ACTION_MENU_VISIBLE_CANDIDATE" : "ACTION_MENU_PARTIAL_CANDIDATE";
        return Field<PublicActionMenuObservation>.Known(new(candidate.Visible, complete, rows, code), reference,
            mappingStatus: MappingStatus.Candidate);
    }

    private static bool ValidActionRowPath(string? path)
    {
        if (path is null || !path.StartsWith("Emj/104/3/", StringComparison.Ordinal) || path.Length > 28) return false;
        string id = path[10..];
        return uint.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out uint number) && number is > 0 and <= 1000000 &&
            number.ToString(CultureInfo.InvariantCulture) == id;
    }

    private static Field<PublicConcealedAppearance> OpponentAppearance(IReadOnlyList<PublicOpponentHandStatus>? statuses,
        ScreenPosition position, long revision, ObservationReference boundary)
    {
        if (position == ScreenPosition.Lower || statuses is null)
            return Field<PublicConcealedAppearance>.Unknown("本人不使用对手牌背读侧；其他区域本次未读。");
        string direction = position switch { ScreenPosition.Right => "right", ScreenPosition.Upper => "top", _ => "left" };
        var matches = statuses.Where(x => x.ScreenDirection == direction).ToArray();
        var reference = boundary with { Source = "VisibleUiReader/PublicOpponentHandReader:" + direction,
            Evidence = "fixed visible EmjTile back-only slots; no hidden face image/resource access; appearance is not a draw event" };
        if (matches.Length == 0) return Field<PublicConcealedAppearance>.Unknown("本次未枚举该方对手公开背牌。");
        var item = matches[0];
        if (matches.Length != 1 || item.Slots.IsDefault || item.Slots.Length > 16 || item.VisibleSlots != item.Slots.Length ||
            item.UnknownComponents < 0 || item.VerifiedBackCount < 0 || item.VerifiedBackCount > item.VisibleSlots ||
            item.Slots.Select(x => x.SlotPath).Distinct(StringComparer.Ordinal).Count() != item.Slots.Length ||
            item.Slots.Any(x => !PublicOpponentHandReader.TryRoute(x.SlotPath, out var route) ||
                route.Direction != direction || route.SeparateDrawSlot != x.SeparateDrawSlot) ||
            item.Slots.Count(x => x.Code == PublicOpponentHandReader.VerifiedCode) != item.VerifiedBackCount)
            return new() { Availability = Availability.Conflict, Observation = reference, SourceKind = SourceKind.Observed,
                Reason = "OPPONENT_APPEARANCE_CONFLICT：公开背牌槽或计数矛盾。" };
        bool complete = item.CountComplete;
        bool? separate = complete ? item.Slots.Any(x => x.SeparateDrawSlot) : null;
        if (complete && item.SeparateDrawSlotVisible != separate)
            return new() { Availability = Availability.Conflict, Observation = reference, SourceKind = SourceKind.Observed,
                Reason = "OPPONENT_APPEARANCE_CONFLICT：独立显示槽与完整扫描不一致。" };
        var slots = item.Slots.Select(x => new PublicConcealedDisplaySlot(x.SlotPath, revision, x.SeparateDrawSlot,
            x.Code == PublicOpponentHandReader.VerifiedCode, x.Code == PublicOpponentHandReader.VerifiedCode
                ? "OPPONENT_BACK_RESOURCE_VERIFIED" : "OPPONENT_BACK_UNAVAILABLE")).ToImmutableArray();
        return Field<PublicConcealedAppearance>.Known(new(item.VisibleSlots, item.VerifiedBackCount, complete, separate, slots,
            complete ? "OPPONENT_VISIBLE_BACK_COUNT_CANDIDATE" : "OPPONENT_BACK_COUNT_INCOMPLETE"), reference,
            mappingStatus: MappingStatus.Candidate);
    }

    private static PublicMenuAction MenuAction(string? value) => value switch
    {
        "Pon" => PublicMenuAction.Pon, "Chi" => PublicMenuAction.Chi, "Kan" => PublicMenuAction.Kan,
        "Ron" => PublicMenuAction.Ron, "Riichi" => PublicMenuAction.Riichi, "Tsumo" => PublicMenuAction.Tsumo,
        "Kyushukyuhai" => PublicMenuAction.Kyushukyuhai,
        "Pass" => PublicMenuAction.Pass, "Cancel" => PublicMenuAction.Cancel, _ => PublicMenuAction.Unknown,
    };

    private static PublicSnapshot PlayerIdentities(PublicSnapshot source, PublicObservationContext? context,
        ObservationReference boundary)
    {
        var profile = MatchingProfile(context);
        if (profile?.RelativePlayerIdentityEvidence is not { Length: > 0 and <= 2048 } evidence || string.IsNullOrWhiteSpace(evidence) || source.Players.IsDefault ||
            source.Players.Length != 4 || source.Players.Select(x => x.Position).Distinct().Count() != 4 ||
            source.Players.Any(x => !Enum.IsDefined(x.Position) || !x.SeatWind.IsConfirmed)) return source;
        ScreenPosition[] order = [ScreenPosition.Lower, ScreenPosition.Right, ScreenPosition.Upper, ScreenPosition.Left];
        var players = order.Select(position => source.Players.Single(x => x.Position == position)).ToArray();
        var inputs = players.Select(x => "Players." + x.Position + ".SeatWind").ToImmutableArray();
        var reference = boundary with { Source = "PublicObservationAssembler/ScreenRelativePlayerIdentity",
            Evidence = evidence, DerivationInputs = inputs };
        if (players.Any(x => x.SeatWind.Value is < 0 or > 3 || x.SeatWind.Observation!.Sequence != boundary.Sequence ||
                x.SeatWind.Observation.ObservedAtUtc != boundary.ObservedAtUtc) ||
            players.Select(x => x.SeatWind.Value).Distinct().Count() != 4 ||
            players.Where((x, i) => x.SeatWind.Value != (players[0].SeatWind.Value + i) % 4).Any())
        {
            const string error = "PLAYER_WIND_CONFLICT：四家门风与已验证方位顺序矛盾。";
            return source with
            {
                Players = source.Players.Select(p => p with { PlayerId = new Field<int>
                    { Availability = Availability.Conflict, Observation = reference, SourceKind = SourceKind.Derived, Reason = error } }).ToImmutableArray(),
                OurPlayerId = new() { Availability = Availability.Conflict, Observation = reference, SourceKind = SourceKind.Derived, Reason = error },
                DealerPlayerId = new() { Availability = Availability.Conflict, Observation = reference, SourceKind = SourceKind.Derived, Reason = error },
                PlayerIdBasis = new() { Availability = Availability.Conflict, Observation = reference, SourceKind = SourceKind.Derived, Reason = error },
            };
        }
        int dealer = Array.FindIndex(players, p => p.SeatWind.Value == 0);
        return source with
        {
            Players = source.Players.Select(p => p with { PlayerId = Field<int>.Known(Array.IndexOf(order, p.Position), reference,
                SourceKind.Derived) }).ToImmutableArray(),
            OurPlayerId = Field<int>.Known(0, reference, SourceKind.Derived),
            DealerPlayerId = Field<int>.Known(dealer, reference, SourceKind.Derived),
            PlayerIdBasis = Field<PlayerIdentityBasis>.Known(PlayerIdentityBasis.RelativeToLocalPlayer, reference, SourceKind.Derived),
        };
    }

    private static PublicSnapshot RoundTitle(PublicSnapshot source, RoundTitleResourceCandidate? candidate,
        PublicObservationContext? context, ObservationReference boundary)
    {
        if (candidate is null || context?.ClientVersion != RoundTitleCatalog.GameVersion ||
            !string.Equals(context.UldSha256, LowerHandProfile.EmjUldSha256, StringComparison.OrdinalIgnoreCase) ||
            !RoundTitleCatalog.TryDecode(context.ClientVersion, candidate, out var meaning)) return source;
        var reference = boundary with { Source = "VisibleUiReader/RoundTitleCatalog:Emj/19",
            Evidence = "docs/cn/evidence/round-title-resource-catalog.json; " + meaning.Evidence };
        return source with
        {
            RoundWind = Field<int>.Known(meaning.RoundWind, reference),
            HandNumber = Field<int>.Known(meaning.HandNumber, reference),
        };
    }

    private static Field<int> ReconcileRound(Field<int> resource, Field<int> text)
    {
        if (resource.Availability == Availability.Conflict) return resource;
        if (text.Availability == Availability.Conflict) return text;
        if (!resource.HasValue) return text.HasValue ? text : resource;
        if (!text.HasValue) return resource;
        if (resource.Value == text.Value) return resource.IsConfirmed ? resource : text;
        var reference = resource.Observation! with
        {
            Evidence = resource.Observation!.Evidence + "; conflicting source: " + text.Observation!.Evidence,
        };
        return Field<int>.Conflict(resource.Value, reference, "ROUND_TITLE_CONFLICT：同帧局况图像与文本不一致，停止完整决策。");
    }

    private static Field<ImmutableArray<VisibleTile>> DoraImages(AddonProbe addon, ObservationReference boundary,
        PublicObservationContext? context)
    {
        if (addon.PublicDoraCandidates is not { Count: > 0 } candidates)
            return Field<ImmutableArray<VisibleTile>>.Unknown("本次没有完整可读的公开宝牌显示。");
        var reference = boundary with { Source = "VisibleUiReader/PublicDoraReader:Emj",
            Evidence = "docs/cn/LIVE-PUBLIC-STATUS-EVIDENCE.md; docs/cn/PUBLIC-INPUT-GAPS-20260925.md; " +
                "fixed Emj/28..32/2 public display resource identities; display mode is read separately; not a flip event" };
        if (candidates.Count > 5 || candidates.Select(x => x.Slot).Distinct().Count() != candidates.Count ||
            candidates.Select(x => x.Path).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
            return new() { Availability = Availability.Conflict, SourceKind = SourceKind.Observed, Observation = reference,
                Reason = "DORA_PROJECTION_CONFLICT：公开显示槽位重复或超过上限。" };
        var result = ImmutableArray.CreateBuilder<VisibleTile>();
        foreach (var candidate in candidates.OrderBy(x => x.Slot))
        {
            if (candidate.Slot != result.Count || candidate.Path != $"Emj/{28 + candidate.Slot}/2" ||
                candidate.Code != "PUBLIC_DORA_FACE_CANDIDATE" ||
                !LowerTileCatalog.TryDecode(candidate.IconId, candidate.PathHash, out var tile) ||
                candidate.Kind34 != tile.Kind34 || candidate.RedFive != tile.RedFive)
                return Field<ImmutableArray<VisibleTile>>.Unknown("DORA_PROJECTION_INCOMPLETE：公开槽位、资源或牌面不完整；不沿用旧宝牌。");
            result.Add(new(tile.Kind34, tile.RedFive));
        }
        if (result.GroupBy(x => x.Id).Any(g => g.Count() > 4 || g.Count(x => x.Red) > 1))
            return new() { Availability = Availability.Conflict, SourceKind = SourceKind.Observed, Observation = reference,
                Reason = "DORA_PROJECTION_CONFLICT：公开显示中的牌种数量矛盾。" };
        return Field<ImmutableArray<VisibleTile>>.Known(result.ToImmutable(), reference,
            mappingStatus: PublicCurrentFacts.Audited(context) ? MappingStatus.Validated : MappingStatus.Candidate);
    }

    private static Field<PublicImageInventory> Images(AddonProbe addon, ScreenPosition position, string kind,
        ObservationReference reference, PublicObservationContext? context)
    {
        if (addon.PublicTableReading is not { } reading || addon.PublicTableFaces is not { } faces ||
            reading.Tiles.IsDefault || reading.Rejections.IsDefault)
            return Field<PublicImageInventory>.Unknown("本次未取得公开区域图像读取结果。");
        string direction = position switch { ScreenPosition.Lower => "bottom", ScreenPosition.Upper => "top",
            ScreenPosition.Right => "right", _ => "left" };
        string area = kind + "-" + direction;
        var tiles = reading.Tiles.Where(x => x.Area == area).ToArray();
        var statuses = reading.Areas.IsDefault ? [] : reading.Areas.Where(x => x.Area == area).ToArray();
        var rejected = reading.Rejections.Where(x => x.Area == area).Select(x => x.Code).ToImmutableArray();
        if (tiles.Length == 0 && statuses.Length == 0 && rejected.IsEmpty)
            return Field<PublicImageInventory>.Unknown("区域没有读取证据；不能认定为空。");
        bool invalid = statuses.Length > 1 || tiles.Any(x => x.ScreenDirection != direction) ||
            tiles.Select(x => x.SlotPath).Distinct(StringComparer.Ordinal).Count() != tiles.Length ||
            tiles.Any(tile => !ValidTile(tile, faces)) || tiles.GroupBy(x => x.Kind34).Any(g => g.Count() > 4 || g.Count(x => x.RedFive) > 1);
        if (invalid) return new() { Availability = Availability.Conflict, Observation = reference,
            SourceKind = SourceKind.Observed, Reason = "PUBLIC_IMAGE_PROJECTION_CONFLICT：槽位、资源或牌数相互矛盾。" };
        var status = statuses.SingleOrDefault();
        bool readable = status is { ContainerVerified: true, ContainerVisible: true, EnumerationCompleted: true };
        bool complete = readable && status!.UnknownComponents == 0 && rejected.IsEmpty && status.VisibleSlots == tiles.Length;
        bool empty = complete && status!.ObservedEmptyCandidate && tiles.Length == 0;
        var images = tiles.Select(x => new PublicImageTile(new VisibleTile(x.Kind34, x.RedFive), x.SlotPath, x.GroupPath,
            x.X, x.Y, x.Width, x.Height, x.RotationDegrees, x.Mirrored, x.Stable)
        {
            DisplayPosition = x.RiverPosition is { } p ? new(p.DisplayRow, p.VisibleColumn, p.ReadOrder, p.IsSideways) : null,
            Style = x.VisualMark is { } m ? new(m.AddRed, m.AddGreen, m.AddBlue, m.MultiplyRed, m.MultiplyGreen, m.MultiplyBlue,
                m.Style is "normal" or "darkened" or "red-tinted" or "darkened-red-tinted" ? m.Style : "unclassified")
                { ResponseHighlight = m.ResponseHighlight } : null,
            Tsumogiri = kind == "river" && x.Stable
                ? PublicDiscardSemantics.Tsumogiri(x.VisualMark, context, reference)
                : Field<bool>.Unknown("只有稳定牌河图像可解释摸切颜色；副露和动画不适用。"),
            WasClaimed = kind == "river" && x.Stable
                ? PublicDiscardSemantics.WasClaimed(x.VisualMark, context, reference)
                : Field<bool>.Unknown("只有稳定牌河图像可解释被鸣颜色；副露和动画不适用。"),
        }).ToImmutableArray();
        var groups = kind != "meld" || reading.MeldGroups.IsDefault ? ImmutableArray<PublicMeldImageGroup>.Empty : reading.MeldGroups
            .Where(g => g.ScreenDirection == direction && faces.Any(f => f.Area == area && f.GroupPath == g.GroupPath))
            .Select(g => new PublicMeldImageGroup(g.GroupPath, g.VisibleSlots, g.KnownFaces, g.VerifiedBackSlots, g.Stable,
                g.ShapeCode is "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN" or "PUBLIC_PON_THREE_FACE_PATTERN" or
                    "PUBLIC_KAN_FOUR_FACE_PATTERN" or "PUBLIC_CHI_THREE_FACE_PATTERN" ? g.ShapeCode : "PUBLIC_MELD_SHAPE_UNKNOWN",
                g.ShapeCode == "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN" && g.InferredClosedKanKind34 is >= 0 and <= 33 ? g.InferredClosedKanKind34 : null)).ToImmutableArray();
        return Field<PublicImageInventory>.Known(new PublicImageInventory(images, complete && status!.Stable && tiles.All(x => x.Stable),
            readable, complete, empty, status?.VisibleSlots ?? faces.Count(x => x.Area == area), status?.UnknownComponents ?? 0, rejected)
            { Groups = groups },
            reference, mappingStatus: MappingStatus.Candidate);
    }

    private static bool ValidTile(PublicTableTile tile, IReadOnlyList<PublicTableFaceCandidate> faces)
    {
        if (tile.Kind34 is < 0 or > 33 || (tile.RedFive && tile.Kind34 is not (4 or 13 or 22)) ||
            !float.IsFinite(tile.X) || !float.IsFinite(tile.Y) || !float.IsFinite(tile.Width) || tile.Width <= 0 ||
            !float.IsFinite(tile.Height) || tile.Height <= 0 || !float.IsFinite(tile.RotationDegrees)) return false;
        var matching = faces.Where(x => x.SlotPath == tile.SlotPath).ToArray();
        if (matching.Length != 1) return false;
        var face = matching[0];
        return face.Code == PublicTableImageReader.VerifiedResourceCode &&
            face.Area == tile.Area && face.ScreenDirection == tile.ScreenDirection && face.GroupPath == tile.GroupPath &&
            face.X == tile.X && face.Y == tile.Y && face.Width == tile.Width && face.Height == tile.Height &&
            face.RotationDegrees == tile.RotationDegrees && face.Mirrored == tile.Mirrored &&
            face.RiverPosition == tile.RiverPosition && SameRawMark(face.VisualMark, tile.VisualMark) &&
            LowerTileCatalog.TryDecode(face.IconId, face.FacePathHash, out var identity) &&
            identity.Kind34 == tile.Kind34 && identity.RedFive == tile.RedFive;
    }

    private static bool SameRawMark(PublicTileVisualMark? observed, PublicTileVisualMark? interpreted) =>
        // ResponseHighlight is derived by PublicTableTracker, not copied from the raw
        // face. All actually observed color channels and the original label must match.
        (observed is null ? null : observed with { ResponseHighlight = false }) ==
        (interpreted is null ? null : interpreted with { ResponseHighlight = false });

    private static Field<int> Number(IEnumerable<PublicStatusValue> input, string target, ObservationReference observation,
        PublicObservationContext? context, Field<int> fallback)
    {
        var rows = input.Where(x => x.Number is not null).ToArray();
        if (rows.Length == 0) return fallback;
        var reference = Reference(observation, rows, target, context, out var mapping);
        if (rows.Select(x => x.Number).Distinct().Count() != 1)
            return Field<int>.Conflict(rows[0].Number!.Value, reference, "同一字段的当前公开候选值不一致。");
        int number = rows[0].Number!.Value;
        if (target == "WallRemaining" && number is < 0 or > 70)
            return Field<int>.Conflict(number, reference, "剩余牌计数候选越界。");
        return Field<int>.Known(number, reference, mappingStatus: mapping);
    }

    private static ObservationReference Reference(ObservationReference observation, IEnumerable<PublicStatusValue> input,
        string target, PublicObservationContext? context, out MappingStatus status)
    {
        var rows = input.ToArray();
        var mappings = rows.Select(x => Mapping(x.Path, target, context)).ToArray();
        status = mappings.All(x => x is not null) ? MappingStatus.Validated : MappingStatus.Candidate;
        string evidence = status == MappingStatus.Validated ? string.Join("; ", mappings.Select(x => x!.Evidence).Distinct(StringComparer.Ordinal))
            : observation.Evidence ?? "candidate public node mapping";
        return observation with { Evidence = evidence + "; paths=" + string.Join(",", rows.Select(x => x.Path)) };
    }

    private static ValidatedPublicMapping? Mapping(string path, string target, PublicObservationContext? context)
    {
        var profile = MatchingProfile(context);
        if (profile is null || profile.Mappings.IsDefault) return null;
        var matches = profile.Mappings.Where(x => x.Path == path && x.Target == target &&
            !string.IsNullOrWhiteSpace(x.Evidence) && x.Evidence.Length <= 2048).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static PublicObservationProfile? MatchingProfile(PublicObservationContext? context)
    {
        if (context?.Profile is not { } profile || string.IsNullOrWhiteSpace(profile.ClientVersion) ||
            profile.ClientVersion != context.ClientVersion || profile.UldSha256 != LowerHandProfile.EmjUldSha256 ||
            !string.Equals(profile.UldSha256, context.UldSha256, StringComparison.OrdinalIgnoreCase))
            return null;
        return profile;
    }

    private static Field<T> Fresh<T>(Field<T> field, PublicSnapshot snapshot) => field.Observation is { } observation &&
        snapshot.Observation is { } boundary && observation.Sequence == boundary.Sequence && observation.ObservedAtUtc == boundary.ObservedAtUtc
        ? field : Field<T>.Unknown("本次未读取；不沿用其他观察的状态字段。");

    private static bool ValidStatus(PublicStatusCandidate candidate, string grammar)
    {
        string text = grammar == "round" ? candidate.Value + candidate.Number?.ToString(CultureInfo.InvariantCulture) + "局"
            : grammar is "wind" or "dora" or "result" ? candidate.Value ?? ""
            : candidate.Number?.ToString(CultureInfo.InvariantCulture) ?? "";
        return PublicStatusReader.TryParse(grammar, Encoding.UTF8.GetBytes(text), out var number, out var value) &&
            number == candidate.Number && value == candidate.Value;
    }
    private static int? Wind(string? value) => value switch { "东" => 0, "南" => 1, "西" => 2, "北" => 3, _ => null };
    private static ScreenPosition? Position(string? value) => value switch
    { "bottom" => ScreenPosition.Lower, "right" => ScreenPosition.Right, "top" => ScreenPosition.Upper, "left" => ScreenPosition.Left, _ => null };
    private static DoraDisplayMode DoraMode(string? value) => value switch
    {
        "宝牌指示牌" or "宝牌(传统式)" or "宝牌（传统式）" or "宝牌指示牌(传统式)" or "宝牌指示牌（传统式）" => DoraDisplayMode.Indicator,
        "宝牌(多玛式)" or "宝牌（多玛式）" => DoraDisplayMode.ActualDora,
        _ => DoraDisplayMode.Unknown,
    };
}
