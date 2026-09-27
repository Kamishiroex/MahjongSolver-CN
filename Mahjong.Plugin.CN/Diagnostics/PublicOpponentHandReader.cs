using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Diagnostics;

internal sealed record PublicOpponentBackSlot(string SlotPath, bool SeparateDrawSlot, string Code);
internal sealed record PublicOpponentHandStatus(string ScreenDirection, bool ContainerVerified, bool ContainerVisible,
    bool EnumerationCompleted, int VisibleSlots, int VerifiedBackCount, bool? SeparateDrawSlotVisible, string Code,
    ImmutableArray<PublicOpponentBackSlot> Slots, int UnknownComponents = 0)
{
    public string Quality => "Candidate";
    public bool CountComplete => ContainerVerified && ContainerVisible && EnumerationCompleted && UnknownComponents == 0 &&
        VisibleSlots is >= 1 and <= 14 && VerifiedBackCount == VisibleSlots;
}
internal sealed record PublicOpponentBackRoute(string Direction, uint Owner, string RootPath, int Template,
    bool SeparateDrawSlot, ushort Part, ushort U, ushort V, ushort Width, ushort Height);

/// <summary>
/// Reads only the fixed visible side/back Image 4. Image 3 is the opponent's dynamic face and is NEVER
/// inspected as an image, even when a result screen reveals it. Counts are current public appearances,
/// not a reconstructed draw event or private hand inventory.
/// </summary>
internal sealed unsafe class PublicOpponentHandReader(Func<nint, int, string, byte[]> bytes,
    Func<nint, string, AtkResNode> node)
{
    internal const string VerifiedCode = "OPPONENT_BACK_RESOURCE_VERIFIED";
    internal IReadOnlyList<PublicOpponentHandStatus> Read(IReadOnlyList<UiNode> visible,
        IReadOnlyDictionary<string, nint> addresses, IReadOnlyDictionary<string, nint> containers)
    {
        var output = new List<PublicOpponentHandStatus>();
        foreach (var (side, ownerId) in new (string, uint)[] { ("right", 137), ("top", 140), ("left", 143) })
        {
            if (!containers.TryGetValue("Emj/" + ownerId, out var owner) ||
                !containers.TryGetValue("Emj/46", out var fortySix) || !containers.TryGetValue("Emj/1", out var outer) ||
                !Res(node(owner, "opponent.container"), ownerId) || (nint)node(owner, "opponent.container").ParentNode != fortySix ||
                !Res(node(fortySix, "opponent.container"), 46) || (nint)node(fortySix, "opponent.container").ParentNode != outer ||
                !Res(node(outer, "opponent.container"), 1) || node(outer, "opponent.container").ParentNode != null)
            { output.Add(new(side, false, false, false, 0, 0, null, "OPPONENT_CONTAINER_UNAVAILABLE", [])); continue; }
            bool ownerVisible = addresses.ContainsKey("Emj/" + ownerId);
            var slots = ImmutableArray.CreateBuilder<PublicOpponentBackSlot>();
            var bounds = new Dictionary<string, PublicTableProjection>(StringComparer.Ordinal);
            int unknown = 0;
            foreach (var component in visible.Where(x => x.Type >= 1000 && x.Path.Count(c => c == '/') == 1))
            {
                if ((nint)node(addresses[component.Path], "opponent.component").ParentNode != owner) continue;
                if (!TryRoute(component.Path + "/4", out var route) || route.Owner != ownerId || component.Type != route.Template)
                { unknown++; continue; }
                if (slots.Count >= 16) { unknown++; continue; }
                string shellPath = component.Path + "/4";
                string code;
                if (!addresses.TryGetValue(shellPath, out var shell))
                {
                    // ULD keeps the component allocated after its common wrapper is hidden. A
                    // vacant wrapper is not a fourteenth visible back. Never inspect Image 3.
                    if (IsInactiveWrapper(route, addresses, containers)) continue;
                    code = "OPPONENT_BACK_NOT_VISIBLE";
                }
                else if (!CheckLayout(shell, route, addresses, out var box)) code = "OPPONENT_BACK_LAYOUT_UNVERIFIED";
                else { bounds[shellPath] = box; code = ReadSelectedBack(shell, route); }
                slots.Add(new(shellPath, route.SeparateDrawSlot, code));
            }
            for (int i = 0; i < slots.Count; i++)
                if (bounds.TryGetValue(slots[i].SlotPath, out var box) &&
                    bounds.Any(x => x.Key != slots[i].SlotPath && PublicTableGeometry.Overlaps(box, x.Value)))
                    slots[i] = slots[i] with { Code = "OPPONENT_BACK_OVERLAP" };
            int known = slots.Count(x => x.Code == VerifiedCode);
            bool complete = ownerVisible && unknown == 0 && slots.Count is >= 1 and <= 14 && known == slots.Count;
            bool? draw = !complete ? null : slots.Any(x => x.SeparateDrawSlot);
            string status = complete ? "OPPONENT_VISIBLE_BACK_COUNT_CANDIDATE" : !ownerVisible ? "OPPONENT_CONTAINER_NOT_VISIBLE"
                : unknown > 0 ? "OPPONENT_COMPONENT_UNSUPPORTED" : "OPPONENT_BACK_COUNT_INCOMPLETE";
            output.Add(new(side, true, ownerVisible, true, slots.Count, known, draw, status,
                slots.OrderBy(x => x.SlotPath, StringComparer.Ordinal).ToImmutableArray(), unknown));
        }
        return output;
    }

    internal static bool TryRoute(string path, out PublicOpponentBackRoute route)
    {
        route = null!;
        var parts = path.Split('/');
        if (parts.Length != 3 || parts[0] != "Emj" || parts[2] != "4" || parts[1].Length > 8 ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ||
            id.ToString(CultureInfo.InvariantCulture) != parts[1]) return false;
        uint family = id < 10000 ? id : id / 10000;
        if (id >= 10000 && (id % 10000 is 0 or > 256 || family is not (138 or 141 or 144))) return false;
        (string side, int owner, int template, int part, int u, int v, int width, int height) = family switch
        {
            138 or 139 => ("right", 137, 1058, 3, 103, 116, 33, 27),
            141 or 142 => ("top", 140, 1059, 9, 70, 0, 26, 35),
            144 or 145 => ("left", 143, 1057, 2, 0, 144, 33, 27),
            _ => ("", 0, 0, 0, 0, 0, 0, 0),
        };
        if (owner == 0) return false;
        route = new(side, (uint)owner, "Emj/" + parts[1], template, family is 139 or 142 or 145,
            (ushort)part, (ushort)u, (ushort)v, (ushort)width, (ushort)height);
        return true;
    }

    internal static bool RetainWrapper(string managerPath, uint id, int type) =>
        type == 1 && id is 1 or 2 && TryRoute(managerPath + "/4", out _);

    private bool IsInactiveWrapper(PublicOpponentBackRoute route, IReadOnlyDictionary<string, nint> addresses,
        IReadOnlyDictionary<string, nint> containers)
    {
        if (!containers.TryGetValue(route.RootPath + "/2", out var wrapper) ||
            !addresses.TryGetValue(route.RootPath, out var root) ||
            !addresses.TryGetValue("Emj/" + route.Owner, out var owner) ||
            !addresses.TryGetValue("Emj/46", out var fortySix) || !addresses.TryGetValue("Emj/1", out var outer)) return false;
        var two = node(wrapper, "opponent.inactiveWrapper");
        if (!Res(two, 2)) return false;
        nint parent = (nint)two.ParentNode;
        if (parent != root)
        {
            if (!containers.TryGetValue(route.RootPath + "/1", out var one) || parent != one) return false;
            var oneNode = node(one, "opponent.inactiveWrapper");
            if (!Res(oneNode, 1) || (nint)oneNode.ParentNode != root) return false;
        }
        var rootNode = node(root, "opponent.inactiveRoot");
        if ((int)rootNode.Type != route.Template || (nint)rootNode.ParentNode != owner ||
            !Res(node(owner, "opponent.inactiveOwner"), route.Owner) || (nint)node(owner, "opponent.inactiveOwner").ParentNode != fortySix ||
            !Res(node(fortySix, "opponent.inactiveOwner"), 46) || (nint)node(fortySix, "opponent.inactiveOwner").ParentNode != outer ||
            !Res(node(outer, "opponent.inactiveOwner"), 1) || node(outer, "opponent.inactiveOwner").ParentNode != null) return false;
        return (two.NodeFlags & NodeFlags.Visible) == 0 || two.Color.A == 0 || two.IsDrawDisabled;
    }

    internal static bool IsFixedSettledWrapper(int template, float x, float y) => template switch
    {
        // Fixed ULD timelines 210/214/218, last two keyframes of ranges 31..40/41..50.
        1057 => x == 0 && y is 0 or -27 or -32,
        1058 => x == 0 && y is 0 or 27 or 32,
        1059 => y == 0 && x is 0 or 26 or 32,
        _ => false,
    };

    private bool CheckLayout(nint address, PublicOpponentBackRoute route, IReadOnlyDictionary<string, nint> addresses,
        out PublicTableProjection bounds)
    {
        bounds = default;
        var ns = new List<AtkResNode>(); var actual = new List<nint>();
        while (address != 0)
        {
            if (actual.Count >= 9 || actual.Contains(address)) return false;
            var n = node(address, "opponent.parent");
            if ((n.NodeFlags & NodeFlags.Visible) == 0 || n.Color.A == 0 || n.IsDrawDisabled ||
                !PublicTableGeometry.Near(n.Rotation, 0) || n.ScaleX <= 0 || n.ScaleX != n.ScaleY) return false;
            actual.Add(address); ns.Add(n); address = (nint)n.ParentNode;
        }
        if (!addresses.TryGetValue(route.RootPath, out var root)) return false;
        int index = actual.IndexOf(root);
        if (index is not (2 or 3) || index + 4 != ns.Count || !Res(ns[1], 2) || index == 3 && !Res(ns[2], 1) ||
            (int)ns[index].Type != route.Template || !Res(ns[index + 1], route.Owner) ||
            !Res(ns[index + 2], 46) || !Res(ns[index + 3], 1) ||
            !addresses.TryGetValue("Emj/" + route.Owner, out var owner) || owner != actual[index + 1] ||
            !addresses.TryGetValue("Emj/46", out var fortySix) || fortySix != actual[index + 2] ||
            !addresses.TryGetValue("Emj/1", out var outer) || outer != actual[index + 3]) return false;
        var shell = ns[0];
        if (shell.Type != NodeType.Image || shell.NodeId != 4 || shell.Width != route.Width || shell.Height != route.Height ||
            shell.X != 0 || shell.Y != 0 || shell.ScaleX != 1 ||
            !IsFixedSettledWrapper(route.Template, ns[1].X, ns[1].Y) || ns[1].ScaleX != 1) return false;
        var transforms = ns.Select(n => new LayoutTransform(n.NodeId, (int)n.Type, n.ScaleX, n.ScaleY, n.Rotation,
            n.OriginX, n.OriginY, n.Transform.M11, n.Transform.M12, n.Transform.M21, n.Transform.M22, n.Width, n.Height, n.X, n.Y)).ToArray();
        return PublicTableGeometry.TryProject(transforms, out bounds) && float.IsFinite(shell.ScreenX) && float.IsFinite(shell.ScreenY) &&
            Math.Abs(bounds.OriginX - shell.ScreenX) <= 0.5f && Math.Abs(bounds.OriginY - shell.ScreenY) <= 0.5f;
    }

    private string ReadSelectedBack(nint address, PublicOpponentBackRoute route)
    {
        var image = Read<AtkImageNode>(address, "opponent.back.image");
        if (image.PartId != route.Part || (image.Flags & (ImageNodeFlags.FlipH | ImageNodeFlags.FlipV)) != 0 || image.PartsList == null)
            return "OPPONENT_BACK_POSE_UNVERIFIED";
        var list = Read<AtkUldPartsList>((nint)image.PartsList, "opponent.back.parts");
        if (list.Id != 18 || list.PartCount != 23 || list.Parts == null) return "OPPONENT_BACK_PARTS_MISMATCH";
        var part = Read<AtkUldPart>((nint)list.Parts + route.Part * sizeof(AtkUldPart), "opponent.back.selectedPart");
        if (part.U != route.U || part.V != route.V || part.Width != route.Width || part.Height != route.Height || part.UldAsset == null)
            return "OPPONENT_BACK_RECT_MISMATCH";
        var asset = Read<AtkUldAsset>((nint)part.UldAsset, "opponent.back.asset");
        if (asset.Id != 21 || asset.AtkTexture.TextureType != TextureType.Resource || asset.AtkTexture.Resource == null)
            return "OPPONENT_BACK_ASSET_MISMATCH";
        uint hash = Read<uint>((nint)asset.AtkTexture.Resource + (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash)), "opponent.back.hash");
        return hash == LowerHandImageReader.ClientTexturePathHash("ui/uld/EmjTile.tex") ||
            hash == LowerHandImageReader.ClientTexturePathHash("ui/uld/EmjTile_hr1.tex") ? VerifiedCode : "OPPONENT_BACK_HASH_MISMATCH";
    }
    private static bool Res(AtkResNode n, uint id) => n.Type == NodeType.Res && n.NodeId == id;
    private T Read<T>(nint address, string field) where T : unmanaged => MemoryMarshal.Read<T>(bytes(address, sizeof(T), field));
}
