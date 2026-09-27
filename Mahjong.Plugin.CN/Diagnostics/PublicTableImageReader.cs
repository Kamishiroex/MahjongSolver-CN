using System.Globalization;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Diagnostics;

internal sealed record PublicTableRoute(string Area, string Direction, string RootPath, int RootType,
    uint Owner, float RootRotation, string TilePath, int TileType, float TileRotation, float TileScaleX,
    float TileScaleY, string DisplayPath, int DisplayType);

/// <summary>
/// Only fixed public river/meld templates, current selected parts and scalar resource identities.
/// Actual ancestry and transformed geometry precede shell access; verified front shell precedes face access.
/// No hidden hands, text, AtkValues, native calls or chronological interpretation.
/// </summary>
internal sealed unsafe class PublicTableImageReader(Func<nint, int, string, byte[]> bytes,
    Func<nint, string, AtkResNode> node)
{
    internal const int MaximumCandidates = 160;
    internal const string VerifiedResourceCode = "PUBLIC_FACE_RESOURCE_VERIFIED";
    internal const string VerifiedBackCode = "PUBLIC_BACK_SHELL_RESOURCE_VERIFIED";
    internal IReadOnlyList<PublicTableAreaStatus> Areas { get; private set; } = [];
    private sealed record Pending(PublicTableFaceCandidate Candidate, PublicTableRoute Route,
        nint Face, nint Shell, PublicTableProjection FaceBounds, PublicTableProjection ShellBounds);

    internal IReadOnlyList<PublicTableFaceCandidate> Read(IReadOnlyList<UiNode> visible,
        IReadOnlyDictionary<string, nint> addresses, IReadOnlyDictionary<string, nint>? containers = null)
    {
        Areas = [];
        var result = new List<PublicTableFaceCandidate>();
        var pending = new List<Pending>();
        var riverAnchors = new Dictionary<string, PublicRiverAnchor>(StringComparer.Ordinal);
        foreach (var face in visible)
        {
            if (face.Type != 2 || !TryRoute(face.Path, out var route)) continue;
            var candidate = new PublicTableFaceCandidate(route.Area, route.Direction, face.Path, route.RootPath,
                route.DisplayType, face.X, face.Y, 0, 0, 0, false, null, null, "PUBLIC_LAYOUT_PENDING");
            if (pending.Count + result.Count >= MaximumCandidates)
                return [candidate with { Code = "PUBLIC_CANDIDATE_LIMIT" }];
            string shellPath = face.Path[..^1] + "5";
            if (!addresses.TryGetValue(shellPath, out var shellAddress))
            { result.Add(candidate with { Code = "PUBLIC_FRONT_SHELL_NOT_VISIBLE" }); continue; }
            nint faceAddress = addresses[face.Path];
            string? error = CheckLayout(faceAddress, shellAddress, route, addresses, out var fp, out var sp);
            if (error is not null) { result.Add(candidate with { Code = error }); continue; }
            candidate = candidate with { X = fp.X, Y = fp.Y, Width = fp.Width, Height = fp.Height,
                RotationDegrees = fp.RotationDegrees, Mirrored = fp.Mirrored,
                VisualMark = ReadVisualMark(node((nint)node(faceAddress, "publicTable.markParent").ParentNode, "publicTable.mark")) };
            var rootNode = node(addresses[route.RootPath], "publicTable.riverAnchor");
            if (route.Area.StartsWith("river-", StringComparison.Ordinal) && Scale(rootNode, 1, 1) &&
                rootNode.OriginX == 0 && rootNode.OriginY == 0 &&
                PublicRiverSpatialMapper.TryAnchor(route, rootNode.X, rootNode.Y, out var anchor))
                riverAnchors[face.Path] = anchor;
            pending.Add(new(candidate, route, faceAddress, shellAddress, fp, sp));
        }
        // Include opaque backs in the occlusion guard. They have no face node and no resource is read.
        var opaque = new List<PublicTableProjection>();
        foreach (var shell in visible)
        {
            if (shell.Type != 2 || !shell.Path.EndsWith("/5", StringComparison.Ordinal)) continue;
            string facePath = shell.Path[..^1] + "4";
            if (addresses.ContainsKey(facePath) || !TryRoute(facePath, out var route)) continue;
            var candidate = new PublicTableFaceCandidate(route.Area, route.Direction, facePath, route.RootPath, route.DisplayType,
                shell.X, shell.Y, 0, 0, 0, false, null, null, "PUBLIC_FACE_NOT_VISIBLE");
            // Public kan backs are display evidence, not permission to inspect their hidden face node.
            if (route.Area.StartsWith("meld-", StringComparison.Ordinal) &&
                CheckBackLayout(addresses[shell.Path], route, addresses, out var backBounds))
                candidate = ReadBackShell(candidate with { X = backBounds.X, Y = backBounds.Y,
                    Width = backBounds.Width, Height = backBounds.Height, RotationDegrees = backBounds.RotationDegrees,
                    Mirrored = backBounds.Mirrored }, route.DisplayType, addresses[shell.Path]);
            result.Add(candidate);
            if (result.Count + pending.Count > MaximumCandidates)
                return [result[^1] with { Code = "PUBLIC_CANDIDATE_LIMIT" }];
            if (Collect(addresses[shell.Path], out var ns, out _) &&
                PublicTableGeometry.TryProject(ns.Select(Transform).ToArray(), out var bounds)) opaque.Add(bounds);
        }
        // Visible meld components with absent images are unknown slots, not proof of a three-tile pon.
        // This catches a new fourth/stacked/back slot before a consumer compares a previous checkpoint.
        foreach (var group in visible.Where(x => x.Type is >= 1060 and <= 1063 && x.Path.Count(c => c == '/') == 1))
        {
            bool found = false;
            for (int slot = 2; slot <= 5; slot++)
            {
                string tilePath = group.Path + "/" + slot;
                string facePath = tilePath + (group.Type == 1060 ? "/9/4" : "/4");
                if (!TryRoute(facePath, out var route) || route.RootType != group.Type || !addresses.ContainsKey(tilePath)) continue;
                found = true;
                if (pending.Any(x => x.Candidate.SlotPath == facePath) || result.Any(x => x.SlotPath == facePath)) continue;
                if (containers is not null && InactiveMeldVisual(route, addresses, containers)) continue;
                result.Add(new(route.Area, route.Direction, facePath, route.RootPath, route.DisplayType,
                    group.X, group.Y, 0, 0, 0, false, null, null, "PUBLIC_MELD_SLOT_IMAGES_UNAVAILABLE"));
            }
            if (!found && TryRoute(group.Path + (group.Type == 1060 ? "/2/9/4" : "/2/4"), out var empty))
                result.Add(new(empty.Area, empty.Direction, group.Path + "/?", group.Path, group.Type,
                    group.X, group.Y, 0, 0, 0, false, null, null, "PUBLIC_MELD_GROUP_IMAGES_UNAVAILABLE"));
            if (result.Count + pending.Count > MaximumCandidates)
                return [result[^1] with { Code = "PUBLIC_CANDIDATE_LIMIT" }];
        }
        for (int i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            bool overlaps = opaque.Any(b => PublicTableGeometry.Overlaps(p.FaceBounds, b) &&
                !AuditedSmallTileFringe(p.Candidate.Template, p.FaceBounds, p.ShellBounds, b));
            for (int j = 0; j < pending.Count && !overlaps; j++)
                if (i != j && PublicTableGeometry.Overlaps(p.FaceBounds, pending[j].ShellBounds) &&
                    !AuditedSmallTileFringe(p.Candidate.Template, p.FaceBounds, p.ShellBounds, pending[j].ShellBounds) &&
                    !AuditedRightMeldEdge(p, pending[j], addresses)) overlaps = true;
            result.Add(overlaps ? p.Candidate with { Code = "PUBLIC_OCCLUDED_OR_TRANSITION" }
                : ReadSelectedResources(p.Candidate, p.Route.DisplayType, p.Face, p.Shell));
        }
        result = PublicRiverSpatialMapper.Assign(result, riverAnchors);
        Areas = ReadAreas(visible, addresses, containers ?? addresses, result);
        return result.OrderBy(x => x.SlotPath, StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<PublicTableAreaStatus> ReadAreas(IReadOnlyList<UiNode> visible,
        IReadOnlyDictionary<string, nint> addresses, IReadOnlyDictionary<string, nint> containers,
        IReadOnlyList<PublicTableFaceCandidate> candidates)
    {
        var areas = new List<PublicTableAreaStatus>();
        foreach (var (id, area, family, template) in new (uint, string, uint, int)[]
        {
            (116, "river-bottom", 0, 0), (119, "river-right", 0, 0), (122, "river-top", 0, 0), (125, "river-left", 0, 0),
            (111, "meld-bottom", 112, 1060), (111, "meld-right", 113, 1062),
            (111, "meld-top", 114, 1063), (111, "meld-left", 115, 1061),
        })
        {
            bool valid = containers.TryGetValue("Emj/" + id, out var ownerAddress) &&
                containers.TryGetValue("Emj/46", out var fortySixAddress) && containers.TryGetValue("Emj/1", out var outerAddress) &&
                Res(node(ownerAddress, "publicTable.area"), id) &&
                (nint)node(ownerAddress, "publicTable.area").ParentNode == fortySixAddress &&
                Res(node(fortySixAddress, "publicTable.area"), 46) &&
                (nint)node(fortySixAddress, "publicTable.area").ParentNode == outerAddress &&
                Res(node(outerAddress, "publicTable.area"), 1) && node(outerAddress, "publicTable.area").ParentNode == null;
            if (!valid) { areas.Add(new(area, "PUBLIC_AREA_OWNER_UNAVAILABLE", false, false, false, false, 0, 0)); continue; }
            var container = node(ownerAddress, "publicTable.area");
            var regionChain = new[] { container, node((nint)container.ParentNode, "publicTable.area"),
                node((nint)node((nint)container.ParentNode, "publicTable.area").ParentNode, "publicTable.area") };
            var regionTransforms = regionChain.Select(Transform).ToArray();
            // Reuse only the numerical projection; the actual Res ancestry was established above.
            // A synthetic type tag does not perform or authorize any Image-node memory read.
            regionTransforms[0] = regionTransforms[0] with { Type = 2 };
            if (regionChain.Any(n => !Rotation(n, 0) || n.ScaleX <= 0 || n.ScaleX != n.ScaleY) ||
                !PublicTableGeometry.TryProject(regionTransforms, out var regionBounds) ||
                !float.IsFinite(container.ScreenX) || !float.IsFinite(container.ScreenY) ||
                Math.Abs(regionBounds.OriginX - container.ScreenX) > 0.5f || Math.Abs(regionBounds.OriginY - container.ScreenY) > 0.5f)
            { areas.Add(new(area, "PUBLIC_AREA_GEOMETRY_UNVERIFIED", false, false, false, false, 0, 0)); continue; }
            var rows = candidates.Where(x => x.Area == area).ToArray();
            int unknown = 0, visibleGroups = 0, verifiedRoots = 0;
            if (family != 0)
            {
                // All four directions share Res 111. A direction is declared by its fixed base
                // component, never inferred merely from absence of visible image descendants.
                bool declared = false;
                foreach (var entry in containers)
                {
                    if (!TryMeldRoot(entry.Key, out uint rootId, out uint rootFamily) || rootFamily != family) continue;
                    var rootNode = node(entry.Value, "publicTable.meldRegionRoot");
                    bool rootValid = rootNode.NodeId == rootId && (int)rootNode.Type == template &&
                        (nint)rootNode.ParentNode == ownerAddress;
                    if (!rootValid) { unknown++; continue; }
                    verifiedRoots++;
                    if (rootId == family) declared = true;
                }
                if (!declared)
                { areas.Add(new(area, "PUBLIC_AREA_REGION_UNAVAILABLE", false, addresses.ContainsKey("Emj/111"), false,
                    false, 0, unknown, VerifiedRegionRoots: verifiedRoots)); continue; }
            }
            foreach (var component in visible.Where(x => x.Type >= 1000 && x.Path.Count(c => c == '/') == 1))
            {
                if ((nint)node(addresses[component.Path], "publicTable.areaChild").ParentNode != ownerAddress) continue;
                if (family != 0)
                {
                    if (!TryMeldRoot(component.Path, out _, out uint rootFamily)) { unknown++; continue; }
                    if (rootFamily != family) continue;
                    visibleGroups++;
                    if (component.Type != template || !rows.Any(x => x.GroupPath == component.Path)) unknown++;
                }
                else if (!TryRoute(component.Path + "/4", out var route) || route.Area != area || component.Type != route.RootType ||
                    !rows.Any(x => x.GroupPath == component.Path)) unknown++;
            }
            bool isVisible = addresses.ContainsKey("Emj/" + id);
            // Hidden shared owner is unknown; only its visible, fully enumerated display can be empty.
            bool empty = isVisible && rows.Length == 0 && visibleGroups == 0 && unknown == 0;
            string code = !isVisible ? "PUBLIC_AREA_NOT_VISIBLE" : unknown > 0 ? "PUBLIC_AREA_COMPONENT_UNSUPPORTED" :
                rows.Any(x => x.Code is not (VerifiedResourceCode or VerifiedBackCode))
                ? "PUBLIC_AREA_PARTIAL_CANDIDATE" : empty ? "PUBLIC_AREA_EMPTY_CANDIDATE" : "PUBLIC_AREA_VISIBLE_CANDIDATE";
            areas.Add(new(area, code, true, isVisible, true, empty, rows.Length, unknown,
                VisibleGroups: visibleGroups, VerifiedRegionRoots: verifiedRoots));
        }
        return areas;
    }

    internal static bool IsMeldRegionRoot(uint id) => id is >= 112 and <= 115 ||
        id / 10000 is >= 112 and <= 115 && id % 10000 is >= 1 and <= 256;

    private static bool TryMeldRoot(string path, out uint id, out uint family)
    {
        id = family = 0;
        if (!path.StartsWith("Emj/", StringComparison.Ordinal) || path.AsSpan(4).Contains('/') ||
            !uint.TryParse(path.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out id) ||
            path != "Emj/" + id.ToString(CultureInfo.InvariantCulture) || !IsMeldRegionRoot(id)) return false;
        family = id < 10000 ? id : id / 10000;
        return true;
    }

    /// <summary>
    /// Clone IDs identify only a bounded family of display templates, never time/order.
    /// The path match alone authorizes NO read: actual public owner addresses/types are mandatory below.
    /// </summary>
    internal static bool TryRoute(string path, out PublicTableRoute route)
    {
        route = null!;
        if (path.Length > 48) return false;
        var parts = path.Split('/');
        if (parts.Length is < 3 or > 5 || parts[0] != "Emj" || parts[^1] != "4" ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint id) ||
            id.ToString(CultureInfo.InvariantCulture) != parts[1]) return false;
        uint family = id;
        if (id >= 10000)
        {
            // A discovery ceiling, not a claim that 256 clones exist or form a complete river.
            if (id % 10000 is 0 or > 256) return false;
            family = id / 10000;
        }
        string root = "Emj/" + parts[1];
        if (parts.Length == 3 && family is >= 117 and <= 127 && family is not (119 or 122 or 125))
        {
            (string direction, uint owner, int template, float degrees) = family switch
            {
                117 => ("bottom", 116u, 1023, -90), 118 => ("bottom", 116u, 1021, 0),
                120 => ("right", 119u, 1024, 180), 121 => ("right", 119u, 1023, -90),
                123 => ("top", 122u, 1022, 90), 124 => ("top", 122u, 1024, 180),
                126 => ("left", 125u, 1021, 0), _ => ("left", 125u, 1022, 90),
            };
            route = new("river-" + direction, direction, root, template, owner, degrees,
                root, template, degrees, 1, 1, root, template);
            return true;
        }
        if (family is < 112 or > 115 || parts[2] is not ("2" or "3" or "4" or "5")) return false;
        bool upright = parts[2] is "2" or "3", lower = family == 112;
        if (lower ? parts.Length != 5 || parts[3] != "9" : parts.Length != 4) return false;
        (string side, int group, int ordinary, int rotated, float rotation) = family switch
        {
            112 => ("bottom", 1060, 1055, 1056, 0),
            113 => ("right", 1062, 1023, 1024, -90),
            114 => ("top", 1063, 1024, 1022, 180),
            _ => ("left", 1061, 1022, 1021, 90),
        };
        string tilePath = root + "/" + parts[2];
        int tileType = upright ? ordinary : rotated;
        route = new("meld-" + side, side, root, group, 111, rotation, tilePath, tileType,
            upright ? 0 : lower ? 270 : -90, lower && !upright ? -1 : lower ? 1 : 0.75f,
            lower ? 1 : 0.75f, lower ? tilePath + "/9" : tilePath,
            lower ? upright ? 1010 : 1013 : tileType);
        return true;
    }

    private string? CheckLayout(nint faceAddress, nint shellAddress, PublicTableRoute route,
        IReadOnlyDictionary<string, nint> addresses, out PublicTableProjection faceBounds, out PublicTableProjection shellBounds)
    {
        faceBounds = shellBounds = default;
        if (!Collect(faceAddress, out var face, out var fa) || !Collect(shellAddress, out var shell, out var sa))
            return "PUBLIC_PARENT_CHAIN_INVALID";
        if (!fa.Skip(1).SequenceEqual(sa.Skip(1))) return "PUBLIC_FACE_SHELL_OWNER_MISMATCH";
        if (!addresses.TryGetValue(route.RootPath, out var root) ||
            !addresses.TryGetValue(route.TilePath, out var tile) ||
            !addresses.TryGetValue(route.DisplayPath, out var display)) return "PUBLIC_COMPONENT_NOT_VISIBLE";
        int di = fa.IndexOf(display), ti = fa.IndexOf(tile), ri = fa.IndexOf(root);
        if (di < 3 || ti < di || ri < ti || ri + 4 != face.Count ||
            (int)face[di].Type != route.DisplayType || (int)face[ti].Type != route.TileType ||
            (int)face[ri].Type != route.RootType) return "PUBLIC_COMPONENT_ANCESTRY_MISMATCH";
        // Actual template wrappers: leaf -> 3 -> 2 -> [template root 1] -> component.
        if (di is not (3 or 4) || !Res(face[1], 3) || !Res(face[2], 2) || di == 4 && !Res(face[3], 1))
            return "PUBLIC_TEMPLATE_WRAPPER_MISMATCH";
        if (!OptionalRoot(face, di, ti) || !OptionalRoot(face, ti, ri) ||
            !Res(face[ri + 1], route.Owner) || !Res(face[ri + 2], 46) || !Res(face[ri + 3], 1))
            return "PUBLIC_REGION_OWNER_MISMATCH";
        if (!addresses.TryGetValue("Emj/" + route.Owner, out var ownerAddress) || ownerAddress != fa[ri + 1] ||
            !addresses.TryGetValue("Emj/46", out var fortySixAddress) || fortySixAddress != fa[ri + 2] ||
            !addresses.TryGetValue("Emj/1", out var outerAddress) || outerAddress != fa[ri + 3])
            return "PUBLIC_REGION_OWNER_ADDRESS_MISMATCH";
        bool large = route.DisplayType is 1010 or 1013;
        if (face[0].Type != NodeType.Image || shell[0].Type != NodeType.Image || face[0].NodeId != 4 || shell[0].NodeId != 5 ||
            face[0].Width != 40 || face[0].Height != 52 || shell[0].Width != (large ? 42 : 34) || shell[0].Height != (large ? 55 : 45))
            return "PUBLIC_IMAGE_SIZE_MISMATCH";
        if (!Rotation(face[ri], route.RootRotation) || face[ri].ScaleX <= 0 || face[ri].ScaleX != face[ri].ScaleY)
            return "PUBLIC_ROOT_TRANSFORM_MISMATCH";
        if (ti != ri && (!Rotation(face[ti], route.TileRotation) || !Scale(face[ti], route.TileScaleX, route.TileScaleY)))
            return "PUBLIC_TILE_TRANSFORM_MISMATCH";
        if (di != ti && (!Rotation(face[di], 0) || !Scale(face[di], 1, 1))) return "PUBLIC_BUTTON_TRANSFORM_MISMATCH";
        if (!Rotation(face[0], 0) || !Scale(face[0], route.DisplayType == 1013 ? -1 : large ? 1 : 0.78f, large ? 1 : 0.78f) ||
            !Rotation(shell[0], route.DisplayType == 1023 ? 180 : 0) ||
            !Scale(shell[0], route.DisplayType == 1024 ? -1 : 1, 1)) return "PUBLIC_IMAGE_TRANSFORM_MISMATCH";
        for (int i = 1; i < face.Count; i++)
            if (i != di && i != ti && i != ri && (!Rotation(face[i], 0) || face[i].ScaleX <= 0 || face[i].ScaleX != face[i].ScaleY))
                return "PUBLIC_PARENT_TRANSFORM_MISMATCH";
        if (!PublicTableGeometry.TryProject(face.Select(Transform).ToArray(), out faceBounds) ||
            !PublicTableGeometry.TryProject(shell.Select(Transform).ToArray(), out shellBounds)) return "PUBLIC_GEOMETRY_UNVERIFIED";
        // ScreenX/Y refer to the transformed origin, NOT the minimum corner for rotated nodes.
        if (Math.Abs(faceBounds.OriginX - face[0].ScreenX) > 0.5f || Math.Abs(faceBounds.OriginY - face[0].ScreenY) > 0.5f ||
            Math.Abs(shellBounds.OriginX - shell[0].ScreenX) > 0.5f || Math.Abs(shellBounds.OriginY - shell[0].ScreenY) > 0.5f)
            return "PUBLIC_GEOMETRY_CACHE_MISMATCH";
        float centerX = faceBounds.X + faceBounds.Width / 2, centerY = faceBounds.Y + faceBounds.Height / 2;
        if (centerX < shellBounds.X || centerX > shellBounds.X + shellBounds.Width ||
            centerY < shellBounds.Y || centerY > shellBounds.Y + shellBounds.Height)
            return "PUBLIC_FACE_OUTSIDE_FRONT_SHELL";
        return null;
    }

    private bool Collect(nint address, out List<AtkResNode> ns, out List<nint> addresses)
    {
        ns = []; addresses = [];
        while (address != 0)
        {
            if (ns.Count >= 16 || addresses.Contains(address)) return false;
            var n = node(address, "publicTable.parent");
            if ((n.NodeFlags & NodeFlags.Visible) == 0 || n.Color.A == 0 || n.IsDrawDisabled) return false;
            addresses.Add(address); ns.Add(n); address = (nint)n.ParentNode;
        }
        return true;
    }
    private bool CheckBackLayout(nint shellAddress, PublicTableRoute route, IReadOnlyDictionary<string, nint> addresses,
        out PublicTableProjection bounds)
    {
        bounds = default;
        if (!Collect(shellAddress, out var ns, out var actual) ||
            !addresses.TryGetValue(route.RootPath, out var root) || !addresses.TryGetValue(route.TilePath, out var tile) ||
            !addresses.TryGetValue(route.DisplayPath, out var display)) return false;
        int di = actual.IndexOf(display), ti = actual.IndexOf(tile), ri = actual.IndexOf(root);
        if (di is not (3 or 4) || ti < di || ri < ti || ri + 4 != ns.Count ||
            (int)ns[di].Type != route.DisplayType || (int)ns[ti].Type != route.TileType || (int)ns[ri].Type != route.RootType ||
            !Res(ns[1], 3) || !Res(ns[2], 2) || di == 4 && !Res(ns[3], 1) ||
            !OptionalRoot(ns, di, ti) || !OptionalRoot(ns, ti, ri) ||
            !Res(ns[ri + 1], route.Owner) || !Res(ns[ri + 2], 46) || !Res(ns[ri + 3], 1)) return false;
        if (!addresses.TryGetValue("Emj/" + route.Owner, out var owner) || owner != actual[ri + 1] ||
            !addresses.TryGetValue("Emj/46", out var fortySix) || fortySix != actual[ri + 2] ||
            !addresses.TryGetValue("Emj/1", out var outer) || outer != actual[ri + 3]) return false;
        bool large = route.DisplayType is 1010 or 1013;
        var shell = ns[0];
        if (shell.Type != NodeType.Image || shell.NodeId != 5 || shell.Width != (large ? 42 : 34) || shell.Height != (large ? 55 : 45) ||
            !Rotation(ns[ri], route.RootRotation) || ns[ri].ScaleX <= 0 || ns[ri].ScaleX != ns[ri].ScaleY ||
            ti != ri && (!Rotation(ns[ti], route.TileRotation) || !Scale(ns[ti], route.TileScaleX, route.TileScaleY)) ||
            di != ti && (!Rotation(ns[di], 0) || !Scale(ns[di], 1, 1))) return false;
        // Exact back poses in ULD timelines 27/32/58/63/68/73; none authorize face reads.
        if (!Rotation(shell, route.DisplayType is 1022 or 1024 ? 180 : 0) ||
            !Scale(shell, route.DisplayType is 1021 or 1024 ? -1 : 1, 1)) return false;
        for (int i = 1; i < ns.Count; i++)
            if (i != di && i != ti && i != ri && (!Rotation(ns[i], 0) || ns[i].ScaleX <= 0 || ns[i].ScaleX != ns[i].ScaleY)) return false;
        return PublicTableGeometry.TryProject(ns.Select(Transform).ToArray(), out bounds) &&
            Math.Abs(bounds.OriginX - shell.ScreenX) <= 0.5f && Math.Abs(bounds.OriginY - shell.ScreenY) <= 0.5f;
    }

    internal static bool RetainMeldLayoutNode(string managerPath, uint id, int type)
    {
        if (type == 1 && id is 1 or 2 or 3 && TryRoute(managerPath + "/4", out var display) &&
            display.Area.StartsWith("meld-", StringComparison.Ordinal) && display.DisplayPath == managerPath) return true;
        // Lower 1055/1056 button 9 and its common Res 1 may be hidden while the allocated tile component remains.
        return TryRoute(managerPath + "/9/4", out var lower) && lower.Area == "meld-bottom" &&
            (id == 1 && type == 1 || id == 9 && type == lower.DisplayType);
    }

    private bool InactiveMeldVisual(PublicTableRoute route, IReadOnlyDictionary<string, nint> addresses,
        IReadOnlyDictionary<string, nint> metadata)
    {
        nint start;
        bool wrappers = metadata.TryGetValue(route.DisplayPath + "/3", out start);
        if (!wrappers && (route.Area != "meld-bottom" || !metadata.TryGetValue(route.DisplayPath, out start))) return false;
        var ns = new List<AtkResNode>(); var actual = new List<nint>();
        for (nint address = start; address != 0;)
        {
            if (actual.Count >= 16 || actual.Contains(address)) return false;
            var n = node(address, "publicTable.inactiveMeldVisual"); ns.Add(n); actual.Add(address); address = (nint)n.ParentNode;
        }
        if (!addresses.TryGetValue(route.RootPath, out var root) || !addresses.TryGetValue(route.TilePath, out var tile)) return false;
        int di = wrappers ? actual.FindIndex(a => metadata.TryGetValue(route.DisplayPath, out var d) ? a == d :
            addresses.TryGetValue(route.DisplayPath, out d) && a == d) : 0;
        int ti = actual.IndexOf(tile), ri = actual.IndexOf(root);
        if (di < 0 || ti < di || ri < ti || ri + 4 != ns.Count || (int)ns[di].Type != route.DisplayType ||
            (int)ns[ti].Type != route.TileType || (int)ns[ri].Type != route.RootType ||
            wrappers && (di is not (2 or 3) || !Res(ns[0], 3) || !Res(ns[1], 2) || di == 3 && !Res(ns[2], 1)) ||
            !OptionalRoot(ns, di, ti) || !OptionalRoot(ns, ti, ri)) return false;
        foreach (var (id, index) in new (uint, int)[] { (111, ri + 1), (46, ri + 2), (1, ri + 3) })
            if (!Res(ns[index], id) || !addresses.TryGetValue("Emj/" + id, out var expected) || actual[index] != expected) return false;
        return ns.Take(ri).Any(n => (n.NodeFlags & NodeFlags.Visible) == 0 || n.Color.A == 0 || n.IsDrawDisabled);
    }

    internal static bool AuditedSmallTileFringe(int template, PublicTableProjection face,
        PublicTableProjection ownShell, PublicTableProjection otherShell)
    {
        if (template is not (1023 or 1024)) return false;
        float scale = Math.Max(ownShell.Width, ownShell.Height) / 45f;
        if (!float.IsFinite(scale) || scale <= 0) return false;
        float noise = scale * 0.001f;
        float overlapWidth = Math.Min(ownShell.X + ownShell.Width, otherShell.X + otherShell.Width) - Math.Max(ownShell.X, otherShell.X);
        float overlapHeight = Math.Min(ownShell.Y + ownShell.Height, otherShell.Y + otherShell.Height) - Math.Max(ownShell.Y, otherShell.Y);
        if (overlapWidth > noise && overlapHeight > noise) return false;
        // 1023/1024 front X=-1 + OriginX20*(1-.78)=3.4; width31.2 reaches34.6.
        // Only the .6-unit fringe outside its own exact 34x45 shell may touch another shell.
        // This also covers sideways rivers next to the previous row and orthogonal meld tiles.
        float fringe = Math.Max(Math.Max(ownShell.X - face.X, ownShell.Y - face.Y),
            Math.Max(face.X + face.Width - ownShell.X - ownShell.Width, face.Y + face.Height - ownShell.Y - ownShell.Height));
        return fringe > noise && fringe <= 0.601f * scale;
    }

    private bool AuditedRightMeldEdge(Pending own, Pending other, IReadOnlyDictionary<string, nint> addresses)
    {
        if (own.Route.Area != "meld-right" || other.Route.Area != "meld-right" ||
            own.Route.RootPath != other.Route.RootPath || own.Route.RootType != 1062 || other.Route.RootType != 1062 ||
            own.Route.DisplayType != 1023 || other.Route.DisplayType != 1024 ||
            !addresses.TryGetValue(own.Route.TilePath, out var a) || !addresses.TryGetValue(other.Route.TilePath, out var b)) return false;
        var x = node(a, "publicTable.rightMeldPose"); var y = node(b, "publicTable.rightMeldPose");
        if (!MatchesRightMeldPose(x.NodeId, x.X, x.Y, y.NodeId, y.X, y.Y)) return false;
        return RightMeldEdgeBounds(own.FaceBounds, own.ShellBounds, other.ShellBounds);
    }

    internal static bool MatchesRightMeldPose(uint upright, float x, float y, uint sideways, float sx, float sy)
    {
        // Exact CN emj.uld component 1062 timelines 232..235 (three source-player poses).
        // The second upright shell really overlaps the sideways shell by 0.5 local units;
        // this is a fixed public layout edge, not a general relaxation for overlapping tiles.
        if (upright is not (2 or 3) || sideways is not (4 or 5) || y != 2 || sy != (sideways == 4 ? 35 : 9)) return false;
        return new (float First, float Second, float Side)[] { (0, 26, 51), (0, 60, 25), (34, 60, -1) }
            .Any(p => x == (upright == 2 ? p.First : p.Second) && sx == p.Side);
    }

    internal static bool RightMeldEdgeBounds(PublicTableProjection face, PublicTableProjection own, PublicTableProjection other)
    {
        float scale = Math.Max(own.Width, own.Height) / 45f;
        if (!float.IsFinite(scale) || scale <= 0 ||
            Math.Abs(Math.Max(other.Width, other.Height) / 45f - scale) > scale * 0.0001f) return false;
        float width = Math.Min(face.X + face.Width, other.X + other.Width) - Math.Max(face.X, other.X);
        float height = Math.Min(face.Y + face.Height, other.Y + other.Height) - Math.Max(face.Y, other.Y);
        float shellWidth = Math.Min(own.X + own.Width, other.X + other.Width) - Math.Max(own.X, other.X);
        float shellHeight = Math.Min(own.Y + own.Height, other.Y + other.Height) - Math.Max(own.Y, other.Y);
        // 0.95 / 0.75 = 1.266667 face-edge overlap; 0.5 / 0.75 = 0.666667 shell overlap.
        return width > 0 && height > 0 && Math.Min(width, height) <= 1.268f * scale &&
            Math.Min(shellWidth, shellHeight) <= 0.668f * scale;
    }

    internal static bool NaturalRiverEdge(PublicTableFaceCandidate face, PublicTableProjection faceBounds,
        PublicTableFaceCandidate next, PublicTableProjection nextShell, IReadOnlyDictionary<string, PublicRiverAnchor> anchors)
    {
        if (face.Area != next.Area || face.Template != next.Template ||
            !(face.Area == "river-right" && face.Template == 1023 || face.Area == "river-top" && face.Template == 1024) ||
            !anchors.TryGetValue(face.SlotPath, out var a) || !anchors.TryGetValue(next.SlotPath, out var b) ||
            a.IsSideways || b.IsSideways || a.DisplayRow != b.DisplayRow || Math.Abs(b.Along - a.Along - 34) > 0.01f) return false;
        bool vertical = face.Template == 1023;
        float scale = (vertical ? faceBounds.Width : faceBounds.Height) / 40.56f;
        float shellScale = (vertical ? nextShell.Width : nextShell.Height) / 45f;
        if (!float.IsFinite(scale) || scale <= 0 || Math.Abs(scale - shellScale) > scale * 0.0001f) return false;
        float overlap = vertical
            ? Math.Min(faceBounds.Y + faceBounds.Height, nextShell.Y + nextShell.Height) - Math.Max(faceBounds.Y, nextShell.Y)
            : Math.Min(faceBounds.X + faceBounds.Width, nextShell.X + nextShell.Width) - Math.Max(faceBounds.X, nextShell.X);
        // Fixed face offset -1 + origin 20*(1-.78) = 3.4. After a quarter turn it extends
        // .6 local units into the next 34-wide shell. This is the normal public tile edge,
        // not a blanket allowance for stacked, crossed, hidden, or differently spaced tiles.
        return overlap > 0 && overlap <= 0.601f * scale;
    }

    private PublicTableFaceCandidate ReadBackShell(PublicTableFaceCandidate candidate, int template, nint shellAddress)
    {
        bool large = template is 1010 or 1013;
        var shell = Read<AtkImageNode>(shellAddress, "publicTable.back.image");
        ushort expected = large ? (ushort)6 : (ushort)21;
        if (shell.PartId != expected || (shell.Flags & (ImageNodeFlags.FlipH | ImageNodeFlags.FlipV)) != 0 || shell.PartsList == null)
            return candidate;
        var list = Read<AtkUldPartsList>((nint)shell.PartsList, "publicTable.back.parts");
        if (list.Id != 18 || list.PartCount != 23 || list.Parts == null) return candidate with { Code = "PUBLIC_BACK_PARTS_MISMATCH" };
        var part = Read<AtkUldPart>((nint)list.Parts + expected * sizeof(AtkUldPart), "publicTable.back.selectedPart");
        if (part.U != (large ? 0 : 132) || part.V != (large ? 56 : 46) ||
            part.Width != (large ? 42 : 34) || part.Height != (large ? 55 : 45) || part.UldAsset == null)
            return candidate with { Code = "PUBLIC_BACK_RECT_MISMATCH" };
        var asset = Read<AtkUldAsset>((nint)part.UldAsset, "publicTable.back.asset");
        if (asset.Id != 21 || asset.AtkTexture.TextureType != TextureType.Resource || asset.AtkTexture.Resource == null)
            return candidate with { Code = "PUBLIC_BACK_ASSET_MISMATCH" };
        uint hash = Scalar((nint)asset.AtkTexture.Resource, nameof(AtkTextureResource.TexPathHash), "publicTable.back.hash");
        return hash == LowerHandImageReader.ClientTexturePathHash("ui/uld/EmjTile.tex") ||
            hash == LowerHandImageReader.ClientTexturePathHash("ui/uld/EmjTile_hr1.tex")
            ? candidate with { Code = VerifiedBackCode } : candidate with { Code = "PUBLIC_BACK_HASH_MISMATCH" };
    }
    private static bool Res(AtkResNode n, uint id) => n.Type == NodeType.Res && n.NodeId == id;
    private static bool OptionalRoot(List<AtkResNode> ns, int from, int to) =>
        to == from || to == from + 1 || to == from + 2 && Res(ns[from + 1], 1);
    private static bool Rotation(AtkResNode n, float degrees) =>
        PublicTableGeometry.Near(MathF.IEEERemainder(n.Rotation - degrees * MathF.PI / 180, 2 * MathF.PI), 0);
    private static bool Scale(AtkResNode n, float x, float y) => PublicTableGeometry.Near(n.ScaleX, x) && PublicTableGeometry.Near(n.ScaleY, y);
    internal static PublicTileVisualMark ReadVisualMark(AtkResNode n)
    {
        bool normalMultiply = n.MultiplyRed == 100 && n.MultiplyGreen == 100 && n.MultiplyBlue == 100;
        string style = !normalMultiply ? "unclassified" : (n.AddRed, n.AddGreen, n.AddBlue) switch
        {
            (0, 0, 0) => "normal", (-75, -75, -75) => "darkened",
            (0, -75, -75) => "red-tinted", (-75, -150, -150) => "darkened-red-tinted",
            _ => "unclassified",
        };
        return new(n.AddRed, n.AddGreen, n.AddBlue, n.MultiplyRed, n.MultiplyGreen, n.MultiplyBlue, style);
    }
    private static LayoutTransform Transform(AtkResNode n) => new(n.NodeId, (int)n.Type, n.ScaleX, n.ScaleY, n.Rotation,
        n.OriginX, n.OriginY, n.Transform.M11, n.Transform.M12, n.Transform.M21, n.Transform.M22, n.Width, n.Height, n.X, n.Y);

    internal PublicTableFaceCandidate ReadSelectedResources(PublicTableFaceCandidate candidate, int template, nint faceAddress, nint shellAddress)
    {
        PublicTableFaceCandidate Reject(string code) => candidate with { Code = code };
        if (template is not (1010 or 1013 or 1021 or 1022 or 1023 or 1024)) return Reject("PUBLIC_TEMPLATE_UNSUPPORTED");
        bool large = template is 1010 or 1013;
        ushort expectedPart = large ? (ushort)0 : template == 1021 ? (ushort)18 : (ushort)20;
        var shell = Read<AtkImageNode>(shellAddress, "publicTable.shell.image");
        if (shell.PartId != expectedPart || (shell.Flags & (ImageNodeFlags.FlipH | ImageNodeFlags.FlipV)) != 0)
            return Reject("PUBLIC_SHELL_NOT_FRONT");
        if (shell.PartsList == null) return Reject("PUBLIC_SHELL_PARTS_UNAVAILABLE");
        var list = Read<AtkUldPartsList>((nint)shell.PartsList, "publicTable.shell.parts");
        if (list.Id != 18 || list.PartCount != 23 || list.Parts == null) return Reject("PUBLIC_SHELL_LAYOUT_MISMATCH");
        var part = Read<AtkUldPart>((nint)list.Parts + expectedPart * sizeof(AtkUldPart), "publicTable.shell.selectedPart");
        if (part.U != (large ? 0 : template == 1021 ? 97 : 132) || part.V != 0 ||
            part.Width != (large ? 42 : 34) || part.Height != (large ? 55 : 45) || part.UldAsset == null)
            return Reject("PUBLIC_SHELL_RECT_MISMATCH");
        var asset = Read<AtkUldAsset>((nint)part.UldAsset, "publicTable.shell.asset");
        if (asset.Id != 21 || asset.AtkTexture.TextureType != TextureType.Resource || asset.AtkTexture.Resource == null)
            return Reject("PUBLIC_SHELL_ASSET_MISMATCH");
        uint hash = Scalar((nint)asset.AtkTexture.Resource, nameof(AtkTextureResource.TexPathHash), "publicTable.shell.hash");
        if (hash != LowerHandImageReader.ClientTexturePathHash("ui/uld/EmjTile.tex") &&
            hash != LowerHandImageReader.ClientTexturePathHash("ui/uld/EmjTile_hr1.tex")) return Reject("PUBLIC_SHELL_HASH_MISMATCH");
        var face = Read<AtkImageNode>(faceAddress, "publicTable.face.image");
        if ((face.Flags & (ImageNodeFlags.FlipH | ImageNodeFlags.FlipV)) != 0 || face.PartsList == null)
            return Reject("PUBLIC_FACE_IMAGE_UNVERIFIED");
        var faceList = Read<AtkUldPartsList>((nint)face.PartsList, "publicTable.face.parts");
        if (faceList.Parts == null || faceList.PartCount is 0 or > 4096 || face.PartId >= faceList.PartCount)
            return Reject("PUBLIC_FACE_PART_INVALID");
        var selected = Read<AtkUldPart>((nint)faceList.Parts + face.PartId * sizeof(AtkUldPart), "publicTable.face.selectedPart");
        if (selected.UldAsset == null) return Reject("PUBLIC_FACE_ASSET_UNAVAILABLE");
        var faceAsset = Read<AtkUldAsset>((nint)selected.UldAsset, "publicTable.face.asset");
        if (faceAsset.AtkTexture.TextureType != TextureType.Resource || faceAsset.AtkTexture.Resource == null)
            return Reject("PUBLIC_FACE_RESOURCE_UNAVAILABLE");
        uint faceHash = Scalar((nint)faceAsset.AtkTexture.Resource, nameof(AtkTextureResource.TexPathHash), "publicTable.face.hash");
        uint icon = Scalar((nint)faceAsset.AtkTexture.Resource, nameof(AtkTextureResource.IconId), "publicTable.face.icon");
        // An icon scalar alone never identifies a tile; both fields must match the fixed catalog.
        return LowerTileCatalog.TryDecode(icon, faceHash, out _) ? candidate with
        { IconId = icon, FacePathHash = faceHash, Code = VerifiedResourceCode } : Reject("PUBLIC_TILE_CATALOG_MISMATCH");
    }
    private uint Scalar(nint address, string field, string label) =>
        Read<uint>(address + (int)Marshal.OffsetOf<AtkTextureResource>(field), label);
    private T Read<T>(nint address, string field) where T : unmanaged => MemoryMarshal.Read<T>(bytes(address, sizeof(T), field));
}

internal readonly record struct PublicRiverAnchor(int DisplayRow, float Along, bool IsSideways);

/// <summary>Fixed ULD root anchors plus observed 45-unit row pitch; clone numbers are never sorted.</summary>
internal static class PublicRiverSpatialMapper
{
    internal static bool TryAnchor(PublicTableRoute route, float localX, float localY, out PublicRiverAnchor anchor)
    {
        anchor = default;
        if (!route.Area.StartsWith("river-", StringComparison.Ordinal) || !float.IsFinite(localX) || !float.IsFinite(localY)) return false;
        uint id = uint.Parse(route.RootPath.AsSpan(4), CultureInfo.InvariantCulture), family = id < 10000 ? id : id / 10000;
        (float x, float y) origin = family switch
        {
            117 => (0, 34), 118 => (0, 0), 120 => (34, 204), 121 => (0, 204),
            123 => (204, 146), 124 => (204, 180), 126 => (145, 0), 127 => (180, 0), _ => (float.NaN, float.NaN),
        };
        float x = localX - origin.x, y = localY - origin.y;
        (float along, float across) = route.Direction switch
        {
            "bottom" => (x, y), "right" => (-y, x), "top" => (-x, -y), "left" => (y, -x), _ => (float.NaN, float.NaN),
        };
        float row = MathF.Round(across / 45f);
        // CN 2026.09.25 live root 126 is X=101,Y=102 on row two:
        // runtime sideways origin is 146, while the ULD initial pose uses 145.
        // Accept these two evidenced poses only; do not relax every river's grid.
        if (family == 126 && Math.Abs(across - row * 45 + 1) <= 0.01f) across += 1;
        // The upper bound is the existing read budget, not a claim about the game's maximum river size.
        if (!float.IsFinite(along) || !float.IsFinite(row) || along < -0.01f || row < 0 ||
            row >= PublicTableImageReader.MaximumCandidates || Math.Abs(across - row * 45) > 0.01f) return false;
        anchor = new((int)row + 1, along, family is 117 or 120 or 123 or 126);
        return true;
    }

    internal static List<PublicTableFaceCandidate> Assign(IReadOnlyList<PublicTableFaceCandidate> candidates,
        IReadOnlyDictionary<string, PublicRiverAnchor> anchors)
    {
        var positions = new Dictionary<string, PublicRiverPosition>(StringComparer.Ordinal);
        foreach (var area in candidates.Where(x => x.Area.StartsWith("river-", StringComparison.Ordinal)).GroupBy(x => x.Area))
        {
            if (area.Any(x => !anchors.ContainsKey(x.SlotPath))) continue;
            var sorted = area.OrderBy(x => anchors[x.SlotPath].DisplayRow).ThenBy(x => anchors[x.SlotPath].Along).ToArray();
            bool ambiguous = sorted.Zip(sorted.Skip(1)).Any(pair =>
                anchors[pair.First.SlotPath].DisplayRow == anchors[pair.Second.SlotPath].DisplayRow &&
                Math.Abs(anchors[pair.First.SlotPath].Along - anchors[pair.Second.SlotPath].Along) <= 0.01f);
            if (ambiguous) continue;
            int column = 0, previousRow = -1;
            for (int i = 0; i < sorted.Length; i++)
            {
                var a = anchors[sorted[i].SlotPath];
                column = a.DisplayRow == previousRow ? column + 1 : 1; previousRow = a.DisplayRow;
                positions[sorted[i].SlotPath] = new(a.DisplayRow, column, i + 1, a.IsSideways);
            }
        }
        return candidates.Select(x => x with { RiverPosition = positions.GetValueOrDefault(x.SlotPath) }).ToList();
    }
}
