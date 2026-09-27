using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Diagnostics;

internal sealed record PublicRiichiCandidate(string ScreenDirection, string Path, bool? StickVisible, string Code)
{
    public string MappingStatus => "Candidate";
    public bool DeclarationTimingVerified => false;
}

/// <summary>Only the four fixed public riichi sticks. Absence from a visible-node list is not false riichi.</summary>
internal sealed unsafe class PublicRiichiReader(Func<nint, int, string, byte[]> bytes, Func<nint, string, AtkResNode> node)
{
    private static readonly HashSet<uint> Hashes =
        [LowerHandImageReader.ClientTexturePathHash("ui/uld/EmjParts.tex"), LowerHandImageReader.ClientTexturePathHash("ui/uld/EmjParts_hr1.tex")];

    internal IReadOnlyList<PublicRiichiCandidate> Read(IReadOnlyDictionary<string, nint> addresses,
        IReadOnlyDictionary<string, nint>? publicContainers = null)
    {
        var result = new List<PublicRiichiCandidate>(4);
        foreach (var (root, direction) in new (uint, string)[] { (100, "bottom"), (101, "right"), (102, "top"), (103, "left") })
        {
            string path = "Emj/" + root + "/2";
            var candidate = new PublicRiichiCandidate(direction, path, null, "RIICHI_STICK_VISIBILITY_UNOBSERVED");
            if (!addresses.TryGetValue(path, out var address))
            {
                // Only inspect the header of the fixed public stick owner, already copied by Walk.
                // Missing node, alpha animation or a hidden table is NOT a negative observation.
                result.Add(HiddenOwner(root, publicContainers) ? candidate with
                    { StickVisible = false, Code = "PUBLIC_RIICHI_STICK_HIDDEN_OWNER" } : candidate);
                continue;
            }
            if (!Owner(address, root, addresses)) { result.Add(candidate with { Code = "RIICHI_STICK_OWNER_UNVERIFIED" }); continue; }
            var image = Read<AtkImageNode>(address);
            if (image.PartId != 4 || image.PartsList == null || (image.Flags & (ImageNodeFlags.FlipH | ImageNodeFlags.FlipV)) != 0)
            { result.Add(candidate with { Code = "RIICHI_STICK_PART_UNVERIFIED" }); continue; }
            var list = Read<AtkUldPartsList>((nint)image.PartsList);
            if (list.Id != 14 || list.PartCount != 32 || list.Parts == null)
            { result.Add(candidate with { Code = "RIICHI_STICK_PART_UNVERIFIED" }); continue; }
            var part = Read<AtkUldPart>((nint)list.Parts + 4 * sizeof(AtkUldPart));
            if (part.U != 0 || part.V != 36 || part.Width != 78 || part.Height != 12 || part.UldAsset == null)
            { result.Add(candidate with { Code = "RIICHI_STICK_RECT_UNVERIFIED" }); continue; }
            var asset = Read<AtkUldAsset>((nint)part.UldAsset);
            if (asset.Id != 17 || asset.AtkTexture.TextureType != TextureType.Resource || asset.AtkTexture.Resource == null)
            { result.Add(candidate with { Code = "RIICHI_STICK_ASSET_UNVERIFIED" }); continue; }
            uint hash = Read<uint>((nint)asset.AtkTexture.Resource +
                (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash)));
            result.Add(Hashes.Contains(hash) ? candidate with { StickVisible = true, Code = "PUBLIC_RIICHI_STICK_CANDIDATE" }
                : candidate with { Code = "RIICHI_STICK_RESOURCE_UNVERIFIED" });
        }
        return result;
    }

    private bool HiddenOwner(uint rootId, IReadOnlyDictionary<string, nint>? containers)
    {
        if (containers is null || !containers.TryGetValue("Emj/" + rootId, out var address)) return false;
        var owner = node(address, "riichiStick.hiddenPublicOwner");
        if ((owner.NodeFlags & NodeFlags.Visible) != 0)
        {
            // The game can hide the fixed image/common Res child instead of its component.
            // Walk copied these two headers only while visiting the visible public stick component.
            if (!containers.TryGetValue($"Emj/{rootId}/2", out var image) ||
                !Owner(image, rootId, containers, allowHiddenInner: true)) return false;
            var leaf = node(image, "riichiStick.hiddenPublicImage");
            if ((leaf.NodeFlags & NodeFlags.Visible) == 0) return true;
            var parent = node((nint)leaf.ParentNode, "riichiStick.hiddenCommonOwner");
            return parent.Type == NodeType.Res && parent.NodeId == 1 && (parent.NodeFlags & NodeFlags.Visible) == 0;
        }
        bool sideways = rootId is 101 or 103;
        if (owner.NodeId != rootId || (int)owner.Type != 1037 || owner.Width != 78 || owner.Height != 12 ||
            owner.ScaleX != 1 || owner.ScaleY != (sideways ? -1 : 1) || !float.IsFinite(owner.Rotation) ||
            Math.Abs(owner.Rotation - (sideways ? MathF.PI / 2 : 0)) > 0.0001f ||
            (owner.NodeFlags & NodeFlags.Visible) != 0) return false;
        var seen = new HashSet<nint> { address };
        foreach (uint id in new uint[] { 99, 46, 1 })
        {
            address = (nint)owner.ParentNode;
            if (address == 0 || !seen.Add(address) || !containers.TryGetValue("Emj/" + id, out var expected) ||
                address != expected) return false;
            owner = node(address, "riichiStick.publicTableOwner");
            if (owner.NodeId != id || owner.Type != NodeType.Res || !Normal(owner) ||
                (owner.NodeFlags & NodeFlags.Visible) == 0 || owner.Color.A == 0 || owner.IsDrawDisabled) return false;
        }
        return owner.ParentNode == null;
    }

    private bool Owner(nint address, uint rootId, IReadOnlyDictionary<string, nint> addresses, bool allowHiddenInner = false)
    {
        var seen = new HashSet<nint>();
        var chain = new List<(nint Address, AtkResNode Node)>();
        while (address != 0 && chain.Count < 7)
        {
            if (!seen.Add(address)) return false;
            var current = node(address, "riichiStick.publicParent");
            bool inner = chain.Count == 0 && current.NodeId == 2 && current.Type == NodeType.Image ||
                chain.Count == 1 && current.NodeId == 1 && current.Type == NodeType.Res;
            if (((current.NodeFlags & NodeFlags.Visible) == 0 && !(allowHiddenInner && inner)) || current.Color.A == 0 || current.IsDrawDisabled ||
                !float.IsFinite(current.ScreenX) || !float.IsFinite(current.ScreenY) ||
                !float.IsFinite(current.Rotation) || !float.IsFinite(current.ScaleX) || !float.IsFinite(current.ScaleY)) return false;
            chain.Add((address, current)); address = (nint)current.ParentNode;
        }
        if (address != 0 || chain.Count is < 5 or > 6) return false;
        var image = chain[0].Node;
        if (image.NodeId != 2 || image.Type != NodeType.Image || image.Width != 78 || image.Height != 12 || !Normal(image)) return false;
        int i = 1;
        if (chain[i].Node.NodeId == 1 && chain[i].Node.Type == NodeType.Res)
        { if (chain[i].Node.Width != 78 || chain[i].Node.Height != 12 || !Normal(chain[i].Node)) return false; i++; }
        if (!addresses.TryGetValue("Emj/" + rootId, out nint root) || chain[i].Address != root) return false;
        var owner = chain[i++].Node;
        bool sideways = rootId is 101 or 103;
        if (owner.NodeId != rootId || (int)owner.Type != 1037 || owner.Width != 78 || owner.Height != 12 ||
            owner.ScaleX != 1 || owner.ScaleY != (sideways ? -1 : 1) ||
            Math.Abs(owner.Rotation - (sideways ? MathF.PI / 2 : 0)) > 0.0001f) return false;
        foreach (uint id in new uint[] { 99, 46, 1 })
        {
            if (i >= chain.Count || !addresses.TryGetValue("Emj/" + id, out nint expected) || chain[i].Address != expected ||
                chain[i].Node.NodeId != id || chain[i].Node.Type != NodeType.Res || !Normal(chain[i].Node)) return false;
            i++;
        }
        return i == chain.Count && chain[^1].Node.ParentNode == null;
    }

    private static bool Normal(AtkResNode n) => n.ScaleX > 0 && n.ScaleX == n.ScaleY && Math.Abs(n.Rotation) < 0.0001f;
    private T Read<T>(nint address) where T : unmanaged => MemoryMarshal.Read<T>(bytes(address, sizeof(T), "riichiStick.selectedResource"));
}
