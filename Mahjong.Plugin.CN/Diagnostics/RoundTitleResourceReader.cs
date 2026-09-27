using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Diagnostics;

internal sealed record RoundTitleResourceCandidate(string Path, string Code, uint? PartsListId = null,
    uint? PartCount = null, ushort? SelectedPart = null, ushort? U = null, ushort? V = null,
    ushort? Width = null, ushort? Height = null, uint? AssetId = null, int? TextureType = null,
    uint? IconId = null, uint? TexturePathHash = null)
{
    public string MappingStatus => "Candidate";
    public bool SemanticVerified => false;
}

/// <summary>
/// Only the current public round-title image Emj/19. The static sibling Emj/20 is a
/// gradient background, not round information. No path strings or texture payloads.
/// </summary>
internal sealed unsafe class RoundTitleResourceReader(Func<nint, int, string, byte[]> bytes,
    Func<nint, string, AtkResNode> node)
{
    internal RoundTitleResourceCandidate Read(IReadOnlyList<UiNode> visible,
        IReadOnlyDictionary<string, nint> addresses, IReadOnlyDictionary<string, nint>? containers = null)
    {
        var result = new RoundTitleResourceCandidate("Emj/19", "ROUND_TITLE_NOT_VISIBLE");
        if (!visible.Any(n => n.Path == "Emj/19" && n.Type == 2) ||
            !addresses.TryGetValue("Emj/19", out nint imageAddress) ||
            !addresses.TryGetValue("Emj/16", out nint ownerAddress) ||
            !addresses.TryGetValue("Emj/1", out nint rootAddress)) return result;
        if (imageAddress == ownerAddress || ownerAddress == rootAddress || imageAddress == rootAddress)
            return result with { Code = "ROUND_TITLE_PARENT_CYCLE" };
        var imageNode = node(imageAddress, "roundTitle.imageNode");
        var owner = node(ownerAddress, "roundTitle.owner");
        var root = node(rootAddress, "roundTitle.addonRoot");
        if (imageNode.NodeId != 19 || imageNode.Type != NodeType.Image || (nint)imageNode.ParentNode != ownerAddress ||
            owner.NodeId != 16 || owner.Type != NodeType.Res || (nint)owner.ParentNode != rootAddress ||
            root.NodeId != 1 || root.Type != NodeType.Res || root.ParentNode != null)
            return result with { Code = "ROUND_TITLE_OWNER_MISMATCH" };
        foreach (var n in new[] { imageNode, owner, root })
            if ((n.NodeFlags & NodeFlags.Visible) == 0 || n.Color.A == 0 || n.IsDrawDisabled ||
                !float.IsFinite(n.Rotation) || Math.Abs(n.Rotation) > 0.0001f ||
                !float.IsFinite(n.ScaleX) || n.ScaleX <= 0 || n.ScaleX != n.ScaleY)
                return result with { Code = "ROUND_TITLE_HIDDEN_OR_TRANSFORMED" };
        if (imageNode.Width != 640 || imageNode.Height != 80 || owner.Width != 640 || owner.Height != 80 ||
            imageNode.ScaleX != 1 || owner.ScaleX != 0.5f)
            return result with { Code = "ROUND_TITLE_LAYOUT_MISMATCH" };
        var image = Read<AtkImageNode>(imageAddress, "roundTitle.selectedImage");
        if (image.PartsList == null || (image.Flags & (ImageNodeFlags.FlipH | ImageNodeFlags.FlipV)) != 0)
            return result with { Code = "ROUND_TITLE_IMAGE_UNAVAILABLE" };
        var list = Read<AtkUldPartsList>((nint)image.PartsList, "roundTitle.partsHeader");
        result = result with { PartsListId = list.Id, PartCount = list.PartCount, SelectedPart = image.PartId };
        if (list.PartCount is 0 or > 64 || image.PartId >= list.PartCount || list.Parts == null)
            return result with { Code = "ROUND_TITLE_PART_INVALID" };
        var selected = Read<AtkUldPart>((nint)list.Parts + image.PartId * sizeof(AtkUldPart), "roundTitle.selectedPart");
        result = result with { U = selected.U, V = selected.V, Width = selected.Width, Height = selected.Height };
        if (selected.Width == 0 || selected.Height == 0 || selected.UldAsset == null)
            return result with { Code = "ROUND_TITLE_ASSET_UNAVAILABLE" };
        var asset = Read<AtkUldAsset>((nint)selected.UldAsset, "roundTitle.selectedAsset");
        result = result with { AssetId = asset.Id, TextureType = (int)asset.AtkTexture.TextureType };
        if (asset.AtkTexture.TextureType != TextureType.Resource || asset.AtkTexture.Resource == null)
            return result with { Code = "ROUND_TITLE_NOT_RESOURCE_TEXTURE" };
        var resource = (nint)asset.AtkTexture.Resource;
        uint hash = Read<uint>(resource + (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash)), "roundTitle.textureHash");
        uint icon = Read<uint>(resource + (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.IconId)), "roundTitle.iconId");
        return result with { Code = "ROUND_TITLE_RESOURCE_CANDIDATE", IconId = icon, TexturePathHash = hash };
    }

    private T Read<T>(nint address, string field) where T : unmanaged => MemoryMarshal.Read<T>(bytes(address, sizeof(T), field));
}
