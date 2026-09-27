using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Diagnostics;

internal sealed record PublicDoraCandidate(string Path, int Slot, uint? IconId, uint? PathHash,
    int? Kind34, bool? RedFive, string Code)
{
    public SelectedShellPart? SelectedPart { get; init; }
    public byte? ImageFlags { get; init; }
}

/// <summary>Only the five explicitly displayed top dora boxes; no ura or concealed face read.</summary>
internal sealed unsafe class PublicDoraReader(Func<nint, int, string, byte[]> bytes, Func<nint, string, AtkResNode> node)
{
    internal IReadOnlyList<PublicDoraCandidate> Read(IReadOnlyDictionary<string, nint> addresses)
    {
        var result = new List<PublicDoraCandidate>();
        for (uint rootId = 28; rootId <= 32; rootId++)
        {
            string path = "Emj/" + rootId + "/2";
            if (!addresses.TryGetValue(path, out nint address)) continue;
            var candidate = new PublicDoraCandidate(path, (int)rootId - 28, null, null, null, null, "DORA_OWNER_UNVERIFIED");
            if (!Owner(address, rootId, addresses)) { result.Add(candidate); continue; }
            var image = Read<AtkImageNode>(address);
            candidate = candidate with { ImageFlags = (byte)image.Flags };
            if ((image.Flags & (ImageNodeFlags.FlipH | ImageNodeFlags.FlipV)) != 0)
            { result.Add(candidate with { Code = "DORA_IMAGE_FLIPPED" }); continue; }
            if (image.PartsList == null) { result.Add(candidate with { Code = "DORA_PART_UNAVAILABLE" }); continue; }
            var list = Read<AtkUldPartsList>((nint)image.PartsList);
            if (list.PartCount is 0 or > 4096 || image.PartId >= list.PartCount || list.Parts == null)
            { result.Add(candidate with { Code = "DORA_PART_INVALID" }); continue; }
            var part = Read<AtkUldPart>((nint)list.Parts + image.PartId * sizeof(AtkUldPart));
            candidate = candidate with { SelectedPart = new(image.PartId, list.Id, list.PartCount,
                part.U, part.V, part.Width, part.Height) };
            // Only the complete nominal face is supported. The fixed 1006 Image is
            // 40x52; other selected rectangles need explicit runtime evidence rather
            // than interpreting an icon scalar through an arbitrary crop/animation.
            if (part.U != 0 || part.V != 0 || part.Width != 40 || part.Height != 52)
            { result.Add(candidate with { Code = "DORA_SELECTED_RECT_UNVERIFIED" }); continue; }
            if (part.UldAsset == null) { result.Add(candidate with { Code = "DORA_ASSET_UNAVAILABLE" }); continue; }
            var asset = Read<AtkUldAsset>((nint)part.UldAsset);
            if (asset.AtkTexture.TextureType != TextureType.Resource || asset.AtkTexture.Resource == null)
            { result.Add(candidate with { Code = "DORA_RESOURCE_UNAVAILABLE" }); continue; }
            nint resource = (nint)asset.AtkTexture.Resource;
            uint icon = Read<uint>(resource + (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.IconId)));
            uint hash = Read<uint>(resource + (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash)));
            result.Add(LowerTileCatalog.TryDecode(icon, hash, out var tile)
                ? candidate with { IconId = icon, PathHash = hash, Kind34 = tile.Kind34, RedFive = tile.RedFive, Code = "PUBLIC_DORA_FACE_CANDIDATE" }
                : candidate with { IconId = icon, PathHash = hash, Code = "DORA_CATALOG_UNVERIFIED" });
        }
        return result;
    }

    private bool Owner(nint start, uint rootId, IReadOnlyDictionary<string,nint> addresses)
    {
        var current = node(start, "publicDora.face");
        if (current.NodeId != 2 || current.Type != NodeType.Image || current.Width != 40 || current.Height != 52 ||
            current.ScaleX != 1 || current.ScaleY != 1 || !Visible(current)) return false;
        nint parentAddress = (nint)current.ParentNode;
        if (parentAddress == 0) return false;
        current = node(parentAddress, "publicDora.parent");
        if (current.NodeId == 1 && current.Type == NodeType.Res)
        {
            if (!Visible(current) || current.Width != 50 || current.Height != 60) return false;
            parentAddress = (nint)current.ParentNode;
            if (parentAddress == 0) return false;
            current = node(parentAddress, "publicDora.component");
        }
        foreach (var (id, type) in new (uint, int)[] { (rootId,1006), (26,1), (21,1), (1,1) })
        {
            if (!addresses.TryGetValue("Emj/"+id, out nint expected) || parentAddress != expected ||
                current.NodeId != id || (int)current.Type != type || !Visible(current)) return false;
            // Fixed Emj.uld and 2026-09-24 live public metadata agree: the 1006
            // dora component is scaled to 0.75; the selected 40x52 face is 1.0.
            if (id == rootId && (current.Width != 50 || current.Height != 60 || current.ScaleX != 0.75f)) return false;
            if (id == 1) return current.ParentNode == null;
            parentAddress = (nint)current.ParentNode;
            if (parentAddress == 0) return false;
            current = node(parentAddress, "publicDora.owner");
        }
        return false;
    }

    private static bool Visible(AtkResNode n) => (n.NodeFlags & NodeFlags.Visible) != 0 && n.Color.A > 0 && !n.IsDrawDisabled &&
        float.IsFinite(n.ScaleX) && n.ScaleX > 0 && n.ScaleX == n.ScaleY && float.IsFinite(n.Rotation) && Math.Abs(n.Rotation) < 0.0001f &&
        float.IsFinite(n.ScreenX) && float.IsFinite(n.ScreenY);
    private T Read<T>(nint address) where T : unmanaged => MemoryMarshal.Read<T>(bytes(address, sizeof(T), "publicDora.resource"));
}
