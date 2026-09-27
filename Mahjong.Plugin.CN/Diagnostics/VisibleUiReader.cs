using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Collections.Immutable;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>
/// Reads only version-pinned base UI structures through an injected bounded byte reader. No Emj offsets,
/// signatures, native calls or AtkValue contents are used; addresses are not exported. Opt-in
/// public readers export only fixed-catalog texture scalars and finite allowlisted status values.
/// Flag-visible is not proof of screen visibility (occlusion); until node ownership is mapped,
/// layout metadata alone is permitted. Even whitelist words could be character names.
/// The upstream names are discovery candidates, NOT claims of CN layout compatibility.
/// </summary>
internal sealed unsafe class VisibleUiReader
{
    internal static readonly string[] CandidateNames = ["Emj", "EmjL"];
    private const int MaxListNodes = 2048;
    // Fourth capture observed 61 ordinary river slots in total. The exact allowlist
    // can match at most 219 metadata nodes including five dora slots and two meld groups.
    // Keep a finite ceiling without truncating the later river rows (no new face reads).
    private const int MaxPublicLayoutNodes = 256;
    // Named, verified base-UI fields, not unverified private Mahjong offsets. Reading the
    // entire AtkTextNode would also copy Utf8String's inline text buffer. The pinned
    // Utf8String.Length/AsSpan use BufUsed - 1, not the independently stored StringLength.
    // CN capture 2026-09-23 returned StringLength=0 for every text node; those old
    // readings are NOT evidence that the displayed text was empty. No payload is read.
    internal static readonly int TextLengthOffset =
        checked((int)Marshal.OffsetOf<AtkTextNode>(nameof(AtkTextNode.NodeText)) +
                (int)Marshal.OffsetOf<Utf8String>(nameof(Utf8String.BufUsed)));
    private readonly Func<string, nint> lookup;
    private readonly Func<nint, int, byte[]?> readMemory;
    private readonly Func<double> clockMilliseconds;
    private readonly UiReadLimits limits;
    private readonly HashSet<nint> visited = [];
    private readonly List<UiNode> nodes = [];
    private readonly Dictionary<nint, AtkResNode> nodeCache = [];
    private readonly Dictionary<string, nint> visibleAddresses = [];
    private readonly Dictionary<string, nint> publicContainers = [];
    private int readBudget;
    private int nodeBudget;
    private double sampleStart;

    internal VisibleUiReader(Func<string, nint> lookup, Func<nint, int, byte[]?> readMemory,
        UiReadLimits? limits = null, Func<double>? clockMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        ArgumentNullException.ThrowIfNull(readMemory);
        this.lookup = lookup;
        this.readMemory = readMemory;
        this.limits = limits ?? new UiReadLimits();
        if (this.limits.MaxNodes <= 0 || this.limits.MaxReads <= 0 ||
            !double.IsFinite(this.limits.MaxSampleMilliseconds) || this.limits.MaxSampleMilliseconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "UI read budgets must be positive and finite.");
        this.clockMilliseconds = clockMilliseconds ??
            (() => Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
        BeginSample();
    }

    internal void BeginSample()
    {
        readBudget = limits.MaxReads;
        nodeBudget = limits.MaxNodes;
        sampleStart = clockMilliseconds();
    }

    internal AddonProbe Probe(string name, bool detail, bool captureLowerHand = false, bool capturePublicLayout = false,
        bool capturePublicFaces = false, bool capturePublicStatus = false)
    {
        nodes.Clear();
        visited.Clear();
        nodeCache.Clear();
        visibleAddresses.Clear();
        publicContainers.Clear();
        try
        {
            nint address = lookup(name);
            if (address == 0) return new(name, false, false, false, 0, [], null);
            var unit = Read<AtkUnitBase>(address, "addon.header");
            // These are managed bitfield getters on a COPY, never native calls.
            bool visible = unit.IsVisible && unit.Alpha > 0;
            if (visible && unit.IsReady && detail)
                Walk(unit.UldManager, name, 0);
            var lowerGeometry = capturePublicLayout ? new List<PublicLayoutMetadata>() : null;
            var lowerHand = captureLowerHand && name == "Emj" && visible && unit.IsReady && detail
                ? ReadLowerHandImages(lowerGeometry) : null;
            var publicLayout = capturePublicLayout && name == "Emj" && visible && unit.IsReady && detail
                ? ReadPublicLayouts().Concat(lowerGeometry!).ToImmutableArray() : (IReadOnlyList<PublicLayoutMetadata>?)null;
            // The caller must first pass the pinned identity/ULD gate. Separate opt-in preserves the old
            // metadata-only capture contract; these resources are confined to fixed public river/meld owners.
            var tableReader = capturePublicFaces && name == "Emj" && visible && unit.IsReady && detail
                ? new PublicTableImageReader(Bytes, Node) : null;
            var publicFaces = tableReader?.Read(nodes, visibleAddresses, publicContainers);
            var opponentHands = tableReader is not null
                ? new PublicOpponentHandReader(Bytes, Node).Read(nodes, visibleAddresses, publicContainers) : null;
            var publicStatus = capturePublicStatus && name == "Emj" && visible && unit.IsReady && detail
                ? new PublicStatusReader(Bytes, Node).Read(nodes, visibleAddresses, publicContainers) : null;
            var publicDora = capturePublicStatus && name == "Emj" && visible && unit.IsReady && detail
                ? new PublicDoraReader(Bytes, Node).Read(visibleAddresses) : null;
            var roundTitle = capturePublicStatus && name == "Emj" && visible && unit.IsReady && detail
                ? new RoundTitleResourceReader(Bytes, Node).Read(nodes, visibleAddresses, publicContainers) : null;
            var actionMenu = capturePublicStatus && name == "Emj" && visible && unit.IsReady && detail
                ? new PublicActionMenuReader(Bytes, Node).Read(nodes, visibleAddresses, publicContainers) : null;
            var handInteraction = capturePublicStatus && lowerHand is not null
                ? new PublicHandInteractionReader(Node).Read(lowerHand, visibleAddresses) : null;
            var riichi = capturePublicStatus && name == "Emj" && visible && unit.IsReady && detail
                ? new PublicRiichiReader(Bytes, Node).Read(visibleAddresses, publicContainers) : null;
            return new(name, true, visible, unit.IsReady, unit.AtkValuesCount, nodes.ToArray(), null, lowerHand,
                PublicLayouts: publicLayout, PublicTableFaces: publicFaces, PublicTableAreas: tableReader?.Areas,
                PublicStatusCandidates: publicStatus, PublicDoraCandidates: publicDora, RoundTitleResource: roundTitle,
                PublicActionMenu: actionMenu, PublicOpponentHands: opponentHands, PublicHandInteraction: handInteraction,
                PublicRiichiCandidates: riichi);
        }
        catch (ProbeException ex) { return new(name, true, false, false, 0, nodes.ToArray(), ex.Message); }
        catch (Exception ex) { return new(name, true, false, false, 0, [], $"ui.probe: {ex.GetType().Name}"); }
    }

    private void Walk(AtkUldManager manager, string path, int depth)
    {
        if (depth > 12 || manager.NodeListCount > MaxListNodes || manager.NodeListCount > manager.NodeListSize)
            throw new ProbeException(path + ": invalid node list/depth");
        if (manager.NodeListCount == 0) return;
        var pointers = Bytes((nint)manager.NodeList, manager.NodeListCount * IntPtr.Size, path + ".nodes");
        for (int i = 0; i < manager.NodeListCount; i++)
        {
            var address = MemoryMarshal.Read<nint>(pointers.AsSpan(i * IntPtr.Size));
            if (address == 0 || !visited.Add(address)) continue;
            if (--nodeBudget < 0) throw new ProbeException(path + ": BUDGET_EXCEEDED nodes");
            var node = Node(address, path + ".node");
            // Only retain addresses of already copied fixed top-level public containers. Hidden
            // container presence can explain an empty candidate area, never hidden face contents.
            if (path == "Emj" && (node.NodeId is 1 or 46 or 99 or 100 or 101 or 102 or 103 or 111 or 116 or 119 or 122 or 125 or 137 or 140 or 143 ||
                    PublicTableImageReader.IsMeldRegionRoot(node.NodeId)) ||
                PublicOpponentHandReader.RetainWrapper(path, node.NodeId, (int)node.Type) ||
                PublicTableImageReader.RetainMeldLayoutNode(path, node.NodeId, (int)node.Type) ||
                (path is "Emj/100" or "Emj/101" or "Emj/102" or "Emj/103" && node.NodeId is 1 or 2))
                if (!publicContainers.TryAdd(path + "/" + node.NodeId, address))
                    throw new ProbeException(path + ": duplicate public container");
            if (!Visible(node)) continue;
            if (!float.IsFinite(node.ScreenX) || !float.IsFinite(node.ScreenY) || !float.IsFinite(node.Rotation))
                throw new ProbeException(path + ".node: invalid coordinates");
            string nodePath = path + "/" + node.NodeId;
            if (!visibleAddresses.TryAdd(nodePath, address))
                throw new ProbeException(nodePath + ": duplicate node path");
            int? textLength = null;
            if (node.Type == NodeType.Text)
            {
                long used = Read<long>(checked(address + TextLengthOffset), nodePath + ".textBufferUsed");
                // Zero/uninitialized or implausible values remain unknown, not empty text.
                if (used is >= 1 and <= 4097) textLength = (int)used - 1;
            }
            nodes.Add(new(nodePath, node.NodeId, (int)node.Type, node.ScreenX, node.ScreenY, node.Rotation,
                node.Width, node.Height, textLength));
            if ((int)node.Type >= 1000)
            {
                var componentNode = Read<AtkComponentNode>(address, nodePath + ".componentNode");
                if (componentNode.Component != null)
                    Walk(Read<AtkComponentBase>((nint)componentNode.Component, nodePath + ".component").UldManager, nodePath, depth + 1);
            }
        }
    }

    private bool Visible(AtkResNode node)
    {
        var parents = new HashSet<nint>();
        for (int depth = 0; depth < 64; depth++)
        {
            if ((node.NodeFlags & NodeFlags.Visible) == 0 || node.Color.A == 0 || node.IsDrawDisabled)
                return false;
            if (node.ParentNode == null) return true;
            if (!parents.Add((nint)node.ParentNode)) throw new ProbeException("node.parent: cycle");
            node = Node((nint)node.ParentNode, "node.parent");
        }
        throw new ProbeException("node.parent: depth exceeded");
    }

    private IReadOnlyList<HandFaceCandidate> ReadLowerHandImages(List<PublicLayoutMetadata>? metadata)
    {
        var result = new List<HandFaceCandidate>();
        var pending = new List<(UiNode Face, nint FaceAddress, nint ShellAddress, LowerScreenBounds Bounds)>();
        var images = new LowerHandImageReader(Bytes);
        foreach (var face in nodes)
        {
            if (!face.Path.EndsWith("/9/4", StringComparison.Ordinal) ||
                !LowerHandProfile.TryMatchPath(face.Path, out _)) continue;
            string rootPath = face.Path[..face.Path.LastIndexOf("/9/", StringComparison.Ordinal)];
            if (!visibleAddresses.TryGetValue(rootPath, out var rootAddress) ||
                !visibleAddresses.TryGetValue(rootPath + "/9", out var buttonAddress) ||
                !visibleAddresses.TryGetValue(rootPath + "/9/5", out var shellAddress))
            {
                result.Add(new(face.Path, face.X, face.Y, face.Width, face.Height, null, "LOWER_ROW_SHELL_NOT_VISIBLE"));
                continue;
            }
            var root = Node(rootAddress, rootPath);
            var button = Node(buttonAddress, rootPath + "/9");
            var shell = Node(shellAddress, rootPath + "/9/5");
            if (!LowerHandProfile.CanReadResource(face.Path, (int)root.Type, (int)button.Type, face.Type, (int)shell.Type))
            {
                result.Add(new(face.Path, face.X, face.Y, face.Width, face.Height, null, "LOWER_ROW_TEMPLATE_MISMATCH"));
                continue;
            }
            bool ownership = true;
            foreach (uint expected in new uint[] { 133, 46, 1 })
            {
                if (root.ParentNode == null) { ownership = false; break; }
                root = Node((nint)root.ParentNode, rootPath + ".owner");
                if (root.NodeId != expected || root.Type != NodeType.Res) { ownership = false; break; }
            }
            if (!ownership || root.ParentNode != null || face.Width != 40 || face.Height != 52 || shell.Width != 42 || shell.Height != 55 ||
                Math.Abs(face.X - shell.ScreenX) > 0.5 || Math.Abs(face.Y - shell.ScreenY) > 0.5 ||
                face.Rotation != 0 || shell.Rotation != 0)
            {
                result.Add(new(face.Path, face.X, face.Y, face.Width, face.Height, null, "LOWER_ROW_LAYOUT_UNVERIFIED"));
                continue;
            }
            var faceAddress = visibleAddresses[face.Path];
            string? transformError = ProjectLowerNode(faceAddress, buttonAddress, rootAddress, face.Path,
                metadata, out var faceBounds);
            LowerScreenBounds shellBounds = default;
            if (transformError is null)
                transformError = ProjectLowerNode(shellAddress, buttonAddress, rootAddress, rootPath + "/9/5",
                    metadata, out shellBounds);
            if (transformError is null && (Node(faceAddress, face.Path).ParentNode != shell.ParentNode ||
                Math.Abs(faceBounds.X - shellBounds.X) > 0.5f ||
                Math.Abs(faceBounds.Y - shellBounds.Y) > 0.5f ||
                Math.Abs(faceBounds.Width / 40 - shellBounds.Width / 42) > 0.0001f * (faceBounds.Width / 40)))
                transformError = "LOWER_ROW_TRANSFORM_UNVERIFIED: face/shell bounds disagree";
            if (transformError is not null)
            {
                result.Add(new(face.Path, face.X, face.Y, face.Width, face.Height, null, transformError));
                continue;
            }
            pending.Add((face, faceAddress, shellAddress, faceBounds));
        }
        var geometry = pending.Select(x => new HandFaceCandidate(x.Face.Path, x.Bounds.X, x.Bounds.Y,
            x.Bounds.Width, x.Bounds.Height, null, "LAYOUT_ONLY")).ToArray();
        // An extra/missing/overlapping/unsupported candidate prevents all face-resource reads.
        if (result.Count > 0 || !LowerHandProfile.CheckLayout(geometry).Eligible)
        {
            result.AddRange(geometry.Select(x => x with { DiagnosticStatus = "LOWER_ROW_TRANSITION_OR_LAYOUT_UNVERIFIED" }));
            return result.OrderBy(x => x.X).ToArray();
        }
        foreach (var (face, address, shell, bounds) in pending)
            result.Add(images.Probe(face, address, shell) with
            { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height });
        return result.OrderBy(x => x.X).ThenBy(x => x.Y).ToArray();
    }

    private string? ProjectLowerNode(nint address, nint buttonAddress, nint rootAddress, string path,
        List<PublicLayoutMetadata>? metadata, out LowerScreenBounds bounds)
    {
        bounds = default;
        var addresses = new List<nint>();
        var chain = ImmutableArray.CreateBuilder<LayoutTransform>();
        var first = Node(address, "lowerHand.transform");
        while (address != 0)
        {
            if (chain.Count >= 16 || addresses.Contains(address)) return "LOWER_ROW_TRANSFORM_DEPTH";
            var node = Node(address, "lowerHand.transform");
            addresses.Add(address);
            chain.Add(new(node.NodeId, (int)node.Type, node.ScaleX, node.ScaleY, node.Rotation,
                node.OriginX, node.OriginY, node.Transform.M11, node.Transform.M12,
                node.Transform.M21, node.Transform.M22, node.Width, node.Height, node.X, node.Y));
            address = (nint)node.ParentNode;
        }
        // The ULD component path omits Res wrappers. Check actual parent addresses:
        // face/shell -> [3,2,1] -> button9 -> [1] -> root1055 -> 133 -> 46 -> 1.
        int buttonIndex = addresses.IndexOf(buttonAddress), rootIndex = addresses.IndexOf(rootAddress);
        bool owned = buttonIndex > 0 && rootIndex > buttonIndex && rootIndex + 4 == chain.Count;
        uint previousWrapper = 4;
        for (int i = 1; owned && i < buttonIndex; i++)
        {
            owned = chain[i].Type == (int)NodeType.Res && chain[i].NodeId is >= 1 and <= 3 &&
                chain[i].NodeId < previousWrapper;
            previousWrapper = chain[i].NodeId;
        }
        for (int i = buttonIndex + 1; owned && i < rootIndex; i++)
            owned = i == buttonIndex + 1 && chain[i].NodeId == 1 && chain[i].Type == (int)NodeType.Res;
        uint[] ownerIds = [133, 46, 1];
        for (int i = 0; owned && i < ownerIds.Length; i++)
            owned = chain[rootIndex + i + 1].NodeId == ownerIds[i] &&
                chain[rootIndex + i + 1].Type == (int)NodeType.Res;
        if (!owned) return "LOWER_ROW_TRANSFORM_UNVERIFIED: template parent mismatch";
        var transforms = chain.ToImmutable();
        bool projected = LowerHandGeometry.TryProject(transforms, out bounds, out var rejection);
        // A stale cached position/matrix means an update is in progress, not a scale to guess.
        if (projected && (Math.Abs(bounds.X - first.ScreenX) > 0.5f || Math.Abs(bounds.Y - first.ScreenY) > 0.5f))
        { projected = false; rejection = "LOWER_GEOMETRY_SCREEN_POSITION_MISMATCH"; }
        // Scalars only, never addresses. Separate from candidate identity so independently
        // allocated parent arrays do not prevent two-frame stability in LowerHandTracker.
        // Invalid floating-point values must never enter the JSON recorder.
        if (transforms.All(x => float.IsFinite(x.ScaleX) && float.IsFinite(x.ScaleY) && float.IsFinite(x.Rotation) &&
            float.IsFinite(x.OriginX) && float.IsFinite(x.OriginY) && float.IsFinite(x.M11) && float.IsFinite(x.M12) &&
            float.IsFinite(x.M21) && float.IsFinite(x.M22) && float.IsFinite(x.LocalX!.Value) && float.IsFinite(x.LocalY!.Value)))
            metadata?.Add(new(path, "lower-row-geometry", projected ? "PUBLIC_LAYOUT_METADATA_ONLY" : rejection!, transforms));
        return projected ? null : "LOWER_ROW_TRANSFORM_UNVERIFIED: " + rejection;
    }

    private IReadOnlyList<PublicLayoutMetadata> ReadPublicLayouts()
    {
        var result = new List<PublicLayoutMetadata>();
        foreach (var visibleNode in nodes)
        {
            if (!LowerMeldMetadataProfile.TryMatch(visibleNode.Path, visibleNode.Type, out var route) &&
                !PublicTableMetadataProfile.TryMatch(visibleNode.Path, visibleNode.Type, out route)) continue;
            if (result.Count >= MaxPublicLayoutNodes) throw new ProbeException("publicLayout: BUDGET_EXCEEDED candidates");
            var chain = ImmutableArray.CreateBuilder<LayoutTransform>();
            var addresses = new List<nint>();
            var seen = new HashSet<nint>();
            nint address = visibleAddresses[visibleNode.Path];
            while (address != 0)
            {
                if (chain.Count >= 16 || !seen.Add(address))
                    throw new ProbeException(visibleNode.Path + ".publicParent: cycle/depth exceeded");
                var node = Node(address, visibleNode.Path + ".publicParent");
                if (!float.IsFinite(node.ScaleX) || !float.IsFinite(node.ScaleY) || !float.IsFinite(node.Rotation) ||
                    !float.IsFinite(node.X) || !float.IsFinite(node.Y) ||
                    !float.IsFinite(node.OriginX) || !float.IsFinite(node.OriginY) ||
                    !float.IsFinite(node.Transform.M11) || !float.IsFinite(node.Transform.M12) ||
                    !float.IsFinite(node.Transform.M21) || !float.IsFinite(node.Transform.M22))
                    throw new ProbeException(visibleNode.Path + ".publicTransform: invalid numeric value");
                addresses.Add(address);
                chain.Add(new(node.NodeId, (int)node.Type, node.ScaleX, node.ScaleY, node.Rotation,
                    node.OriginX, node.OriginY, node.Transform.M11, node.Transform.M12,
                    node.Transform.M21, node.Transform.M22, node.Width, node.Height, node.X, node.Y));
                address = (nint)node.ParentNode;
            }
            var transforms = chain.ToImmutable();
            string? rejection = null;
            if (!visibleAddresses.TryGetValue(route.RootPath, out var rootAddress))
                rejection = "PUBLIC_ROOT_NOT_VISIBLE";
            int rootIndex = rejection is null ? addresses.IndexOf(rootAddress) : -1;
            if (rejection is null && (rootIndex < 0 || transforms[rootIndex].Type != route.RootType))
                rejection = "PUBLIC_ANCESTRY_UNVERIFIED";
            if (rejection is null)
            {
                if (rootIndex + 1 + route.OwnerIds.Length != transforms.Length)
                    rejection = "PUBLIC_OWNER_UNVERIFIED";
                for (int i = 0; i < route.OwnerIds.Length; i++)
                {
                    int at = rootIndex + i + 1;
                    if (at >= transforms.Length || transforms[at].NodeId != route.OwnerIds[i] || transforms[at].Type != (int)NodeType.Res)
                    { rejection = "PUBLIC_OWNER_UNVERIFIED"; break; }
                }
            }
            if (rejection is null && route.RequiredPathTypes is { } required)
            {
                int previousIndex = rootIndex + 1;
                foreach (var expected in required)
                {
                    if (!visibleAddresses.TryGetValue(expected.Path, out var expectedAddress) ||
                        !addresses.Contains(expectedAddress) || (int)Node(expectedAddress, expected.Path).Type != expected.Type)
                    { rejection = "PUBLIC_TEMPLATE_UNVERIFIED"; break; }
                    int at = addresses.IndexOf(expectedAddress);
                    if (at >= previousIndex) { rejection = "PUBLIC_TEMPLATE_UNVERIFIED"; break; }
                    previousIndex = at;
                }
            }
            if (rejection is null)
            {
                // Template roots may insert their audited Res wrappers (IDs 1..3).
                // An unrelated parent/component is not made safe merely by appearing
                // somewhere between the expected component addresses.
                var expectedAddresses = (route.RequiredPathTypes ?? [])
                    .Select(x => visibleAddresses[x.Path]).ToHashSet();
                for (int i = 1; i < rootIndex; i++)
                    if (!expectedAddresses.Contains(addresses[i]) &&
                        (transforms[i].Type != (int)NodeType.Res || transforms[i].NodeId is not (1 or 2 or 3)))
                    { rejection = "PUBLIC_TEMPLATE_UNVERIFIED"; break; }
            }
            if (rejection is not null)
            {
                result.Add(new(visibleNode.Path, route.Area, rejection, transforms));
                continue;
            }
            SelectedShellPart? shellPart = null;
            if (route.ReadShellPart)
            {
                // Only whitelisted shell Image nodes, never a face image or resource payload.
                if (visibleNode.Type != (int)NodeType.Image)
                    throw new ProbeException(visibleNode.Path + ".publicShell: invalid node type");
                var image = Read<AtkImageNode>(visibleAddresses[visibleNode.Path], visibleNode.Path + ".publicShell.image");
                if (image.PartsList == null)
                    rejection = "PUBLIC_SHELL_PARTS_UNAVAILABLE";
                else
                {
                    var list = Read<AtkUldPartsList>((nint)image.PartsList, visibleNode.Path + ".publicShell.parts");
                    if (list.Parts == null || list.PartCount is 0 or > 4096 || image.PartId >= list.PartCount)
                        rejection = "PUBLIC_SHELL_PART_INVALID";
                    else
                    {
                        var part = Read<AtkUldPart>(checked((nint)list.Parts + image.PartId * sizeof(AtkUldPart)),
                            visibleNode.Path + ".publicShell.selectedPart");
                        shellPart = new(image.PartId, list.Id, list.PartCount, part.U, part.V, part.Width, part.Height);
                        // Deliberately do NOT dereference part.UldAsset or any texture resource.
                    }
                }
            }
            result.Add(new(visibleNode.Path, route.Area, rejection ?? "PUBLIC_LAYOUT_METADATA_ONLY", transforms, shellPart));
        }
        return result.ToImmutableArray();
    }

    private AtkResNode Node(nint address, string field)
    {
        if (!nodeCache.TryGetValue(address, out var node))
            nodeCache.Add(address, node = Read<AtkResNode>(address, field));
        return node;
    }

    private T Read<T>(nint address, string field) where T : unmanaged =>
        MemoryMarshal.Read<T>(Bytes(address, sizeof(T), field));

    private byte[] Bytes(nint address, int count, string field)
    {
        if (--readBudget < 0 || clockMilliseconds() - sampleStart > limits.MaxSampleMilliseconds)
            throw new ProbeException(field + ": BUDGET_EXCEEDED reads/time");
        if (address == 0 || count < 0 || count > 65536)
            throw new ProbeException(field + ": unreadable");
        var bytes = readMemory(address, count);
        if (bytes is null || bytes.Length != count)
            throw new ProbeException(field + ": unreadable");
        return bytes;
    }

    private sealed class ProbeException(string message) : Exception(message);
}

internal sealed record UiReadLimits(int MaxNodes = 2048, int MaxReads = 8192,
    double MaxSampleMilliseconds = 20);
