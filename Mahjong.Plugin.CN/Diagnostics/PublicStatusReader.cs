using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>Only a parsed number or finite enum is retained. Unknown text is never exported.</summary>
internal sealed record PublicStatusCandidate(string Path, string Field, string? Direction,
    int? Number, string? Value, string Code)
{
    public string Source => "CN-emj.uld-fixed-public-status";
    public string MappingStatus => "Candidate";
    public bool SemanticVerified => false;
}

internal sealed record PublicStatusRoute(string Field, string? Direction, int Type, int Width, int Height,
    PublicStatusAncestor[] Chain, string Grammar);
internal sealed record PublicStatusAncestor(uint Id, int Type, bool Optional = false);

/// <summary>
/// Fixed CN 2026.09.15 Emj ULD whitelist. Injected bounded reads only: no native method,
/// unknown arrays, player-name text, chat or opponent hands. A value matching a grammar
/// is still a candidate until its runtime screen meaning is independently checked.
/// </summary>
internal sealed unsafe class PublicStatusReader(Func<nint, int, string, byte[]> bytes,
    Func<nint, string, AtkResNode> node)
{
    internal const int MaximumCandidates = 20;
    internal const int MaximumTextBytes = 64;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly int TextOffset = (int)Marshal.OffsetOf<AtkTextNode>(nameof(AtkTextNode.NodeText));
    private static readonly int CounterOffset = (int)Marshal.OffsetOf<AtkCounterNode>(nameof(AtkCounterNode.NodeText));
    private static readonly int UsedOffset = (int)Marshal.OffsetOf<Utf8String>(nameof(Utf8String.BufUsed));

    internal IReadOnlyList<PublicStatusCandidate> Read(IReadOnlyList<UiNode> visible,
        IReadOnlyDictionary<string, nint> addresses, IReadOnlyDictionary<string, nint>? containers = null)
    {
        var result = new List<PublicStatusCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in visible)
        {
            if (!TryRoute(item.Path, out var route) || !seen.Add(item.Path)) continue;
            if (result.Count >= MaximumCandidates)
                return [new(item.Path, route.Field, route.Direction, null, null, "STATUS_CANDIDATE_LIMIT")];
            var candidate = new PublicStatusCandidate(item.Path, route.Field, route.Direction, null, null, "STATUS_PENDING");
            if (item.Type != route.Type || !addresses.TryGetValue(item.Path, out nint address))
            { result.Add(candidate with { Code = "STATUS_NODE_UNAVAILABLE" }); continue; }
            string? error = CheckAncestry(address, route, addresses);
            if (error is not null) { result.Add(candidate with { Code = error }); continue; }
            // Only pointer/size/used fields of Utf8String are copied, never its inline
            // payload or OriginalTextPointer. Length follows pinned Utf8String.AsSpan.
            var header = bytes(address + (route.Type == 5 ? CounterOffset : TextOffset), UsedOffset + 8, "publicStatus.textHeader");
            if (header.Length != UsedOffset + 8)
            { result.Add(candidate with { Code = "STATUS_TEXT_HEADER_UNAVAILABLE" }); continue; }
            nint buffer = (nint)BinaryPrimitives.ReadInt64LittleEndian(header);
            long size = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
            long used = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(UsedOffset));
            if (buffer == 0 || used is < 1 or > MaximumTextBytes + 1 || size < used || size > 4096)
            { result.Add(candidate with { Code = "STATUS_TEXT_BOUNDS_INVALID" }); continue; }
            var text = bytes(buffer, (int)used, "publicStatus.allowlistedText");
            if (text.Length != used || text[^1] != 0 || text.AsSpan(0, text.Length - 1).Contains((byte)0))
            { result.Add(candidate with { Code = "STATUS_TEXT_TERMINATOR_INVALID" }); continue; }
            if (!TryParse(route.Grammar, text.AsSpan(0, text.Length - 1), out int? number, out string? value))
            { result.Add(candidate with { Code = "STATUS_TEXT_GRAMMAR_REJECTED" }); continue; }
            result.Add(candidate with { Number = number, Value = value, Code = "PUBLIC_STATUS_VALUE_CANDIDATE" });
        }
        return result;
    }

    private string? CheckAncestry(nint address, PublicStatusRoute route, IReadOnlyDictionary<string, nint> addresses)
    {
        var visited = new HashSet<nint>();
        var chain = new List<(nint Address, AtkResNode Node)>();
        while (address != 0 && chain.Count < 16)
        {
            if (!visited.Add(address)) return "STATUS_PARENT_CYCLE";
            var current = node(address, "publicStatus.parent");
            if ((current.NodeFlags & NodeFlags.Visible) == 0 || current.Color.A == 0 || current.IsDrawDisabled ||
                !float.IsFinite(current.ScaleX) || current.ScaleX <= 0 || current.ScaleX != current.ScaleY ||
                !float.IsFinite(current.Rotation) || Math.Abs(current.Rotation) > 0.0001f)
                return "STATUS_PARENT_HIDDEN_OR_TRANSFORMED";
            chain.Add((address, current));
            address = (nint)current.ParentNode;
        }
        if (address != 0) return "STATUS_PARENT_DEPTH_LIMIT";
        if (chain.Count == 0 || chain[0].Node.Width != route.Width || chain[0].Node.Height != route.Height)
            return "STATUS_SIZE_MISMATCH";
        int index = 0;
        foreach (var expected in route.Chain)
        {
            bool matches = index < chain.Count && chain[index].Node.NodeId == expected.Id && (int)chain[index].Node.Type == expected.Type;
            if (!matches && expected.Optional) continue;
            if (!matches) return "STATUS_OWNER_CHAIN_MISMATCH";
            index++;
        }
        if (index != chain.Count || chain[^1].Node.NodeId != 1 || chain[^1].Node.Type != NodeType.Res ||
            !addresses.TryGetValue("Emj/1", out nint addonRoot) || chain[^1].Address != addonRoot)
            return "STATUS_ADDON_OWNER_MISMATCH";
        // The visible path and the pointer chain must identify the same component,
        // not a duplicate node ID in another player panel.
        foreach (var ancestor in chain.Where(x => (int)x.Node.Type >= 1000))
        {
            string? expectedPath = ComponentPath(route, ancestor.Node.NodeId, (int)ancestor.Node.Type);
            if (expectedPath is null || !addresses.TryGetValue(expectedPath, out var component) || component != ancestor.Address)
                return "STATUS_COMPONENT_OWNER_MISMATCH";
        }
        return null;
    }

    private static string? ComponentPath(PublicStatusRoute route, uint id, int type)
    {
        uint panel = route.Direction switch { "bottom" => 38u, "right" => 40u, "top" => 42u, "left" => 44u, _ => 0u };
        if (type is 1025 or 1026) return id == panel ? "Emj/" + panel : null;
        if (type == 1047 && panel != 0) return "Emj/" + panel + "/" + id;
        if (type == 1016 && id == 105) return "Emj/105";
        if (type == 1064 && id is 2 or 3) return "Emj/105/" + id;
        return null;
    }

    internal static bool TryRoute(string path, out PublicStatusRoute route)
    {
        route = null!;
        if (path.Length > 32) return false;
        string[] p = path.Split('/');
        if (p.Length < 2 || p[0] != "Emj") return false;
        static PublicStatusAncestor R(uint id, bool optional = false) => new(id, 1, optional);
        foreach (var (panel, owner, type, direction) in new (uint, uint, int, string)[]
            { (38, 37, 1026, "bottom"), (40, 39, 1025, "right"), (42, 41, 1025, "top"), (44, 43, 1025, "left") })
        {
            bool own = panel == 38;
            uint score = own ? 12u : 13u, scoreOwner = own ? 10u : 11u;
            if (path == $"Emj/{panel}/{score}/2" || path == $"Emj/{panel}/{score}/3")
            {
                uint leaf = path[^1] == '2' ? 2u : 3u;
                route = new("PlayerScore", direction, 3, 108, 20,
                    [new(leaf, 3), R(1, true), new(score, 1047), R(scoreOwner), R(1, true), new(panel, type), R(owner), R(36), R(1)], "score");
                return true;
            }
            uint wind = own ? 9u : 10u, windOwner = own ? 7u : 8u;
            if (path == $"Emj/{panel}/{wind}")
            {
                route = new("SeatWind", direction, 3, 120, 18,
                    [new(wind, 3), R(windOwner), R(1, true), new(panel, type), R(owner), R(36), R(1)], "wind");
                return true;
            }
        }
        if (path is "Emj/22" or "Emj/23")
        {
            uint id = path == "Emj/22" ? 22u : 23u;
            route = new(id == 22 ? "TopRedStickCount" : "TopBlackStickCount", null, 3, 30, 10,
                [new(id, 3), R(21), R(1)], "count"); return true;
        }
        if (path == "Emj/27")
        { route = new("DoraDisplayLabel", null, 3, 320, 16, [new(27, 3), R(26), R(21), R(1)], "dora"); return true; }
        if (path is "Emj/105/2/2" or "Emj/105/3/2")
        {
            uint component = path == "Emj/105/2/2" ? 2u : 3u;
            route = new(component == 2 ? "CenterCounterLeft" : "CenterCounterRight", null, 5, 30, 60,
                [new(2, 5), R(1, true), new(component, 1064), R(1, true), new(105, 1016), R(46), R(1)], "digit"); return true;
        }
        // Header and result candidates have fixed non-player regions. Only narrow
        // round/result enums survive the parser; text containing any name is rejected.
        if (path == "Emj/15")
        { route = new("RoundHeader", null, 3, 300, 48, [new(15, 3), R(14), R(1)], "round"); return true; }
        if (path == "Emj/55")
        { route = new("RoundResultLabel", null, 3, 480, 96, [new(55, 3), R(54), R(46), R(1)], "result"); return true; }
        return false;
    }

    internal static bool TryParse(string grammar, ReadOnlySpan<byte> raw, out int? number, out string? value)
    {
        number = null; value = null;
        if (raw.Length is 0 or > MaximumTextBytes) return false;
        // Reject SeString/control payloads rather than extract arbitrary embedded digits.
        foreach (byte b in raw) if (b < 0x20 || b == 0x7f) return false;
        string text;
        try { text = Utf8.GetString(raw).Trim(' '); }
        catch (DecoderFallbackException) { return false; }
        if (grammar is "score" or "count" or "digit")
        {
            if (grammar == "count" && text.Length > 0 && text[0] is '×' or 'x') text = text[1..].TrimStart(' ');
            bool negative = grammar == "score" && text.StartsWith('-');
            if (negative) text = text[1..];
            if (text.Length == 0 || text.Length > (grammar == "score" ? 7 : grammar == "digit" ? 1 : 2)) return false;
            // Scores may contain correctly grouped ASCII commas; malformed punctuation rejects.
            if (grammar == "score" && text.Contains(','))
            {
                var groups = text.Split(',');
                if (groups.Length != 2 || groups[0].Length is < 1 or > 3 || groups[1].Length != 3) return false;
                text = string.Concat(groups);
            }
            if (!text.All(char.IsAsciiDigit) || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ||
                parsed > (grammar == "score" ? 200000 : grammar == "digit" ? 9 : 99)) return false;
            number = negative ? -parsed : parsed; return true;
        }
        if (grammar == "wind" && text is "东" or "南" or "西" or "北")
        { value = text; number = text switch { "东" => 0, "南" => 1, "西" => 2, _ => 3 }; return true; }
        if (grammar == "dora" && text is "宝牌" or "宝牌指示牌" or "宝牌提示" or
            "宝牌(多玛式)" or "宝牌（多玛式）" or "宝牌(传统式)" or "宝牌（传统式）" or
            "宝牌 (多玛式)" or "宝牌 （多玛式）" or "宝牌 (传统式)" or "宝牌 （传统式）" or
            "宝牌指示牌(传统式)" or "宝牌指示牌（传统式）")
        { value = text; return true; }
        if (grammar == "result" && text is "流局" or "荒牌流局" or "中途流局" or "荣和" or "自摸" or "和牌" or "听牌" or "未听牌")
        { value = text; return true; }
        if (grammar == "round" && text.Length == 3 && text[0] is '东' or '南' or '西' or '北' && text[2] == '局')
        {
            int hand = text[1] switch { '1' or '一' => 1, '2' or '二' => 2, '3' or '三' => 3, '4' or '四' => 4, _ => 0 };
            if (hand == 0) return false;
            number = hand; value = text[0].ToString(); return true;
        }
        return false;
    }
}
