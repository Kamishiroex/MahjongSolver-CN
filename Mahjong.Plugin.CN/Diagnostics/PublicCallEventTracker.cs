using System.Collections.Immutable;

namespace Mahjong.Plugin.CN.Diagnostics;

internal readonly record struct PublicCallTile(int Kind34, bool RedFive);
internal sealed record PublicCallEvent(string Kind, string CallerDirection, string? FromDirection,
    PublicCallTile? ClaimedTile, ImmutableArray<PublicCallTile> Consumed, string GroupPath, string? SourceRiverSlot,
    long FirstObservedSequence, long ConfirmedSequence, string RoundToken, string Code, PublicCallTile? AddedTile = null)
{
    public bool HistoryComplete => false;
    public string Evidence => "cn-public-call-transitions-20260924";
}
internal sealed record PublicCallIssue(string Code, long Sequence, string? SlotPath = null);
internal sealed record PublicCallTrackingResult(ImmutableArray<PublicCallEvent> Events, ImmutableArray<PublicCallIssue> Issues);

/// <summary>
/// Matches a NEW called-river tint to a NEW complete public meld, confirmed by an independent sample.
/// It does not infer calls from a final arrangement or assign absolute seat/player IDs. Previously
/// emitted events survive later resource rejection; current snapshot readiness remains the caller's job.
/// </summary>
internal sealed class PublicCallEventTracker
{
    private const double MaxGapSeconds = 2, MatchSeconds = 3;
    private sealed record River(PublicCallTile Tile, string Style, string Geometry);
    private sealed record Claim(PublicCallTile Tile, string Side, string Slot, long Sequence, DateTimeOffset Utc);
    private sealed record Group(string Path, string Side, string Shape, ImmutableArray<(string Path, PublicCallTile Tile)> Faces,
        int Backs, int Slots, string Signature, bool Complete);
    private sealed class Pending(Group first, long sequence, DateTimeOffset utc, Group? old = null)
    {
        internal Group Current = first;
        internal readonly Group? Old = old;
        internal readonly long Sequence = sequence;
        internal readonly DateTimeOffset Utc = utc;
        internal int Consecutive = 1;
    }
    private readonly Dictionary<string, River> rivers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Group> groups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Claim> claims = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PublicCallEvent> priorCalls = new(StringComparer.Ordinal);
    private string? round;
    private long lastSequence = -1;
    private DateTimeOffset lastUtc;
    private bool baseline = true;

    internal PublicCallTrackingResult Observe(long sequence, DateTimeOffset utc, string? roundToken, AddonProbe? addon,
        string? clientVersion = null, string? uldSha256 = null)
    {
        var events = ImmutableArray.CreateBuilder<PublicCallEvent>();
        var issues = ImmutableArray.CreateBuilder<PublicCallIssue>();
        PublicCallTrackingResult Rebaseline(string code)
        {
            ResetCurrent(); lastSequence = sequence; lastUtc = utc;
            issues.Add(new(code, sequence)); return new(events.ToImmutable(), issues.ToImmutable());
        }
        if (clientVersion != RoundTitleCatalog.GameVersion ||
            !string.Equals(uldSha256, LowerHandProfile.EmjUldSha256, StringComparison.OrdinalIgnoreCase))
            return Rebaseline("CALL_PROFILE_UNVERIFIED");
        if (string.IsNullOrWhiteSpace(roundToken) || roundToken.Length > 128) return Rebaseline("CALL_ROUND_UNKNOWN");
        if (sequence <= lastSequence || lastSequence >= 0 && utc <= lastUtc) return Rebaseline("CALL_SEQUENCE_INVALID");
        if (addon is not { Name: "Emj", Present: true, Visible: true, Ready: true, Error: null,
                PublicTableFaces: not null, PublicTableReading: not null } ||
            addon.PublicTableFaces.Count > PublicTableImageReader.MaximumCandidates ||
            addon.PublicTableReading.Tiles.IsDefault || addon.PublicTableReading.MeldGroups.IsDefault)
            return Rebaseline("CALL_OBSERVATION_GAP");
        if (lastSequence >= 0 && (utc - lastUtc).TotalSeconds > MaxGapSeconds)
        { ResetCurrent(); issues.Add(new("CALL_OBSERVATION_GAP", sequence)); }
        lastSequence = sequence; lastUtc = utc;
        if (round != roundToken) { ResetCurrent(); round = roundToken; }
        var faces = addon.PublicTableFaces;
        if (faces.Select(x => x.SlotPath).Distinct(StringComparer.Ordinal).Count() != faces.Count)
            return Rebaseline("CALL_SLOT_CONFLICT");
        if (rivers.Count > 160 || groups.Count > 16 || pending.Count > 16 || claims.Count > 16)
            return Rebaseline("CALL_STATE_LIMIT");

        foreach (var face in faces.Where(x => x.Area.StartsWith("river-", StringComparison.Ordinal)))
        {
            if (!PublicTableImageReader.TryRoute(face.SlotPath, out var route) || route.Area != face.Area ||
                route.Direction != face.ScreenDirection || route.DisplayType != face.Template) continue;
            string geometry = FormattableString.Invariant($"{face.Template}:{face.X:R}:{face.Y:R}:{face.Width:R}:{face.Height:R}");
            rivers.TryGetValue(face.SlotPath, out var previous);
            PublicCallTile tile;
            if (Decode(face, out tile))
            {
                if (previous is not null && previous.Tile != tile)
                {
                    issues.Add(new("CALL_RIVER_SLOT_CHANGED", sequence, face.SlotPath));
                    claims.Remove(face.SlotPath); previous = null;
                }
            }
            else if (previous is not null && previous.Geometry == geometry) tile = previous.Tile;
            else continue;
            string style = CanonicalMark(face.VisualMark);
            if (style.Length == 0)
            {
                // Preserve a newly observed public river identity through its flash. This is not
                // evidence of hand/draw discard; the later called tint must still match a new meld.
                rivers[face.SlotPath] = new(tile, previous?.Style ?? "", geometry); continue;
            }
            if (!baseline && previous is not null && !Called(previous.Style) && Called(style))
                claims[face.SlotPath] = new(tile, route.Direction, face.SlotPath, sequence, utc);
            rivers[face.SlotPath] = new(tile, style, geometry);
        }

        var current = BuildGroups(addon);
        foreach (var g in current)
        {
            if (!groups.TryGetValue(g.Path, out var old))
            {
                groups[g.Path] = g;
                if (!baseline) pending[g.Path] = new(g, sequence, utc);
                continue;
            }
            if (pending.TryGetValue(g.Path, out var waiting))
            {
                waiting.Consecutive = waiting.Current.Signature == g.Signature ? waiting.Consecutive + 1 : 1;
                waiting.Current = g;
            }
            else if (!baseline && old.Complete && old.Shape == "PUBLIC_PON_THREE_FACE_PATTERN" &&
                g.Complete && g.Shape == "PUBLIC_KAN_FOUR_FACE_PATTERN" && ExtendsPon(old, g))
                pending[g.Path] = new(g, sequence, utc, old);
            groups[g.Path] = g;
        }
        foreach (var key in pending.Keys.ToArray())
        {
            var p = pending[key];
            if (!current.Any(x => x.Path == key))
            { issues.Add(new("CALL_GROUP_DISAPPEARED", sequence, key)); pending.Remove(key); continue; }
            if ((utc - p.Utc).TotalSeconds > MatchSeconds)
            { issues.Add(new(p.Current.Complete ? "CALL_SOURCE_UNMATCHED" : "CALL_MELD_INCOMPLETE", sequence, key)); pending.Remove(key); continue; }
            if (!p.Current.Complete || p.Consecutive < 2) continue;
            var g = p.Current;
            if (p.Old is { } pon)
            {
                if (!ExtendsPon(pon, g)) { issues.Add(new("CALL_KAKAN_CONFLICT", sequence, key)); pending.Remove(key); continue; }
                var added = g.Faces.Single(x => !pon.Faces.Any(y => y.Path == x.Path));
                priorCalls.TryGetValue(key, out var prior);
                var ev = new PublicCallEvent("kakan", g.Side, prior?.FromDirection, prior?.ClaimedTile,
                    pon.Faces.Select(x => x.Tile).ToImmutableArray(), key, prior?.SourceRiverSlot,
                    p.Sequence, sequence, roundToken, "PUBLIC_KAKAN_VISIBLE_TRANSITION", added.Tile);
                events.Add(ev); priorCalls[key] = ev; pending.Remove(key); continue;
            }
            if (g.Shape == "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN")
            {
                int kind = g.Faces[0].Tile.Kind34;
                if (kind is 4 or 13 or 22)
                    issues.Add(new("CALL_ANKAN_RED_IDENTITY_UNKNOWN", sequence, key));
                else events.Add(new("ankan", g.Side, null, null,
                    Enumerable.Repeat(new PublicCallTile(kind, false), 4).ToImmutableArray(), key, null,
                    p.Sequence, sequence, roundToken, "PUBLIC_ANKAN_TWO_FACE_DERIVED"));
                pending.Remove(key); continue;
            }
            var turned = g.Faces.Where(x => x.Path == key + (g.Side == "bottom" ? "/4/9/4" : "/4/4")).ToArray();
            if (turned.Length != 1) { issues.Add(new("CALL_CLAIMED_SLOT_UNKNOWN", sequence, key)); pending.Remove(key); continue; }
            var matches = claims.Values.Where(c => c.Side != g.Side && c.Tile == turned[0].Tile &&
                Math.Abs((c.Utc - p.Utc).TotalSeconds) <= MatchSeconds).ToArray();
            if (matches.Length == 0) continue;
            if (matches.Length > 1) { issues.Add(new("CALL_SOURCE_AMBIGUOUS", sequence, key)); pending.Remove(key); continue; }
            var claim = matches[0];
            string kindName = g.Shape switch { "PUBLIC_CHI_THREE_FACE_PATTERN" => "chi",
                "PUBLIC_PON_THREE_FACE_PATTERN" => "pon", _ => "daiminkan" };
            string[] order = ["bottom", "right", "top", "left"];
            if (kindName == "chi" && (Array.IndexOf(order, claim.Side) + 1) % 4 != Array.IndexOf(order, g.Side))
            { issues.Add(new("CALL_CHI_SOURCE_CONFLICT", sequence, key)); pending.Remove(key); continue; }
            var call = new PublicCallEvent(kindName, g.Side, claim.Side, claim.Tile,
                g.Faces.Where(x => x.Path != turned[0].Path).Select(x => x.Tile).ToImmutableArray(), key, claim.Slot,
                Math.Min(p.Sequence, claim.Sequence), sequence, roundToken, "PUBLIC_CALL_VISIBLE_TRANSITION");
            events.Add(call); priorCalls[key] = call; claims.Remove(claim.Slot); pending.Remove(key);
        }
        foreach (var expired in claims.Where(x => (utc - x.Value.Utc).TotalSeconds > MatchSeconds).Select(x => x.Key).ToArray())
        { issues.Add(new("CALL_MARK_WITHOUT_COMPLETE_MELD", sequence, expired)); claims.Remove(expired); }
        baseline = false;
        return new(events.ToImmutable(), issues.ToImmutable());
    }

    internal void Clear() { ResetCurrent(); round = null; lastSequence = -1; lastUtc = default; }
    private void ResetCurrent() { baseline = true; rivers.Clear(); groups.Clear(); pending.Clear(); claims.Clear(); priorCalls.Clear(); }

    private static bool Decode(PublicTableFaceCandidate face, out PublicCallTile tile)
    {
        tile = default;
        if (face.Code != PublicTableImageReader.VerifiedResourceCode ||
            !LowerTileCatalog.TryDecode(face.IconId, face.FacePathHash, out var t)) return false;
        tile = new(t.Kind34, t.RedFive); return true;
    }
    private static bool Called(string style) => style is "red-tinted" or "darkened-red-tinted";
    private static string CanonicalMark(PublicTileVisualMark? mark)
    {
        if (mark is null || mark.MultiplyRed != 100 || mark.MultiplyGreen != 100 || mark.MultiplyBlue != 100) return "";
        return (mark.AddRed, mark.AddGreen, mark.AddBlue) switch
        {
            (0, 0, 0) => "normal", (-75, -75, -75) => "darkened",
            (0, -75, -75) => "red-tinted", (-75, -150, -150) => "darkened-red-tinted", _ => "",
        };
    }
    private static bool ExtendsPon(Group pon, Group kan) => pon.Faces.Length == 3 && kan.Faces.Length == 4 &&
        kan.Faces.All(x => x.Tile.Kind34 == pon.Faces[0].Tile.Kind34) &&
        pon.Faces.All(x => kan.Faces.Any(y => x == y)) &&
        kan.Faces.Single(x => !pon.Faces.Any(y => y.Path == x.Path)).Path == kan.Path +
            (kan.Side == "bottom" ? "/5/9/4" : "/5/4");

    private static Group[] BuildGroups(AddonProbe addon)
    {
        var result = new List<Group>();
        foreach (var reported in addon.PublicTableReading!.MeldGroups)
        {
            var candidates = addon.PublicTableFaces!.Where(x => x.GroupPath == reported.GroupPath &&
                x.Area == "meld-" + reported.ScreenDirection).OrderBy(x => x.SlotPath, StringComparer.Ordinal).ToArray();
            var decoded = ImmutableArray.CreateBuilder<(string Path, PublicCallTile Tile)>();
            int backs = 0;
            bool valid = candidates.Length is 3 or 4;
            foreach (var f in candidates)
            {
                if (!PublicTableImageReader.TryRoute(f.SlotPath, out var route) || route.RootPath != reported.GroupPath ||
                    route.Direction != reported.ScreenDirection || route.DisplayType != f.Template) { valid = false; continue; }
                if (Decode(f, out var tile)) decoded.Add((f.SlotPath, tile));
                else if (f.Code == PublicTableImageReader.VerifiedBackCode && f.IconId is null && f.FacePathHash is null) backs++;
                else valid = false;
            }
            if (decoded.Count == 0 || decoded.Count + backs != candidates.Length) valid = false;
            int[] kinds = decoded.Select(x => x.Tile.Kind34).Order().ToArray();
            string shape = valid && candidates.Length == 4 && backs == 2 && kinds.Length == 2 && kinds[0] == kinds[1]
                ? "PUBLIC_CLOSED_KAN_TWO_FACE_PATTERN" : valid && backs == 0 && kinds.Distinct().Count() == 1
                ? candidates.Length == 3 ? "PUBLIC_PON_THREE_FACE_PATTERN" : "PUBLIC_KAN_FOUR_FACE_PATTERN"
                : valid && backs == 0 && kinds.Length == 3 && kinds[2] < 27 && kinds[0] / 9 == kinds[2] / 9 &&
                    kinds[1] == kinds[0] + 1 && kinds[2] == kinds[0] + 2 ? "PUBLIC_CHI_THREE_FACE_PATTERN" : "unknown";
            valid &= shape != "unknown" && decoded.Count(x => x.Tile.RedFive) <= 1;
            string signature = string.Join(";", candidates.Select(x => FormattableString.Invariant(
                $"{x.SlotPath}:{x.Code}:{x.IconId}:{x.FacePathHash}:{x.X:R}:{x.Y:R}:{x.Width:R}:{x.Height:R}:{x.RotationDegrees:R}")));
            result.Add(new(reported.GroupPath, reported.ScreenDirection, shape, decoded.ToImmutable(), backs, candidates.Length, signature, valid));
        }
        return result.ToArray();
    }
}
