namespace Mahjong.Plugin.CN.Diagnostics;

internal sealed record UiNode(string Path, uint Id, int Type, float X, float Y, float Rotation,
    int Width, int Height, int? TextByteLength);
internal sealed record AddonProbe(string Name, bool Present, bool Visible, bool Ready, int AtkValueCount,
    IReadOnlyList<UiNode> VisibleNodes, string? Error, IReadOnlyList<HandFaceCandidate>? LowerHandFaces = null,
    LowerHandReading? LowerHandReading = null, IReadOnlyList<PublicLayoutMetadata>? PublicLayouts = null,
    IReadOnlyList<PublicTableFaceCandidate>? PublicTableFaces = null, PublicTableReading? PublicTableReading = null,
    IReadOnlyList<PublicTableAreaStatus>? PublicTableAreas = null,
    IReadOnlyList<PublicStatusCandidate>? PublicStatusCandidates = null,
    IReadOnlyList<PublicDoraCandidate>? PublicDoraCandidates = null,
    RoundTitleResourceCandidate? RoundTitleResource = null,
    PublicActionMenuCandidate? PublicActionMenu = null,
    IReadOnlyList<PublicOpponentHandStatus>? PublicOpponentHands = null,
    PublicHandInteractionCandidate? PublicHandInteraction = null,
    IReadOnlyList<PublicRiichiCandidate>? PublicRiichiCandidates = null,
    PublicMatchRulesCandidate? PublicMatchRules = null);
internal sealed record PublicMatchRulesCandidate(uint DutyId, string MatchType, bool OpenTanyao, string Code);
internal sealed record DiagnosticFrame(long Sequence, DateTimeOffset Utc, string Marker,
    string Recognition, IReadOnlyList<AddonProbe> Addons);
internal sealed record LifecycleEntry(DateTimeOffset Utc, string Addon, string Event);
internal sealed record UnparsedFields(string Status = "待验证：没有国服麻将字段映射，未知值保持 null",
    object? Hand = null, object? DrawnTile = null, object? Rivers = null, object? Melds = null,
    object? DoraIndicators = null, object? Seat = null, object? Round = null, object? Riichi = null,
    object? Scores = null);
