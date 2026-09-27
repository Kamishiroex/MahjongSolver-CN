using System.Runtime.InteropServices;
using System.Text;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>
/// Second-round diagnostics only. The caller must first verify the exact ULD, lower-row
/// ownership, template types, visibility of both siblings and explicit user opt-in.
/// A back/unknown shell is rejected BEFORE even reading the face image header.
/// Uses only the selected image part; never enumerates hidden parts or reads resource paths.
/// </summary>
internal sealed unsafe class LowerHandImageReader(Func<nint, int, string, byte[]> bytes)
{
    // The pinned client computes finalized CRC32 over the original ULD path, optionally
    // inserting _hr1 before .tex. It does not lowercase it. Lumina.Get returns the
    // unfinalized state instead; see docs/cn/TEXTURE-HASH-EVIDENCE.md.
    private static readonly HashSet<uint> ShellHashes =
        [ClientTexturePathHash("ui/uld/EmjTile.tex"), ClientTexturePathHash("ui/uld/EmjTile_hr1.tex")];

    internal static uint ClientTexturePathHash(string path)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in Encoding.UTF8.GetBytes(path))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        }
        return ~crc;
    }

    internal HandFaceCandidate Probe(UiNode face, nint faceAddress, nint shellAddress)
    {
        HandFaceCandidate Result(string status, uint? icon = null, uint? faceHash = null) =>
            new(face.Path, face.X, face.Y, face.Width, face.Height, icon, status, faceHash);
        var shell = Read<AtkImageNode>(shellAddress, face.Path + ".shell.image");
        if (shell.PartId != 0) return Result($"SHELL_NOT_FRONT: part={shell.PartId}");
        if (shell.PartsList == null) return Result("SHELL_NOT_READY: parts");
        var list = Read<AtkUldPartsList>((nint)shell.PartsList, face.Path + ".shell.parts");
        if (list.Id != 18 || list.PartCount != 23 || list.Parts == null)
            return Result($"SHELL_LAYOUT_MISMATCH: list={list.Id},count={list.PartCount}");
        var part = Read<AtkUldPart>((nint)list.Parts, face.Path + ".shell.part0");
        if (part.U != 0 || part.V != 0 || part.Width != 42 || part.Height != 55)
            return Result($"SHELL_RECT_UNVERIFIED: {part.U},{part.V},{part.Width},{part.Height}");
        if (part.UldAsset == null) return Result("SHELL_NOT_READY: asset");
        var asset = Read<AtkUldAsset>((nint)part.UldAsset, face.Path + ".shell.asset");
        if (asset.Id != 21 || asset.AtkTexture.TextureType != TextureType.Resource || asset.AtkTexture.Resource == null)
            return Result($"SHELL_ASSET_MISMATCH: asset={asset.Id},type={(int)asset.AtkTexture.TextureType}");
        // Only the named hash scalar, no texture handles/path strings or native calls.
        uint hash = Read<uint>((nint)asset.AtkTexture.Resource +
            (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash)), face.Path + ".shell.hash");
        if (!ShellHashes.Contains(hash)) return Result($"SHELL_HASH_UNVERIFIED: {hash:X8}");

        var image = Read<AtkImageNode>(faceAddress, face.Path + ".face.image");
        if (image.PartsList == null) return Result("FACE_NOT_READY: parts");
        var faceList = Read<AtkUldPartsList>((nint)image.PartsList, face.Path + ".face.parts");
        if (faceList.PartCount is 0 or > 4096 || image.PartId >= faceList.PartCount || faceList.Parts == null)
            return Result("FACE_PART_INVALID");
        var facePart = Read<AtkUldPart>(checked((nint)faceList.Parts + image.PartId * sizeof(AtkUldPart)), face.Path + ".face.selectedPart");
        if (facePart.UldAsset == null) return Result("FACE_NOT_READY: asset");
        var faceAsset = Read<AtkUldAsset>((nint)facePart.UldAsset, face.Path + ".face.asset");
        if (faceAsset.AtkTexture.TextureType != TextureType.Resource || faceAsset.AtkTexture.Resource == null)
            return Result("FACE_ICON_RESOURCE_UNAVAILABLE");
        // Diagnostic scalar from the same selected, visible face resource only. It is
        // not a fallback icon lookup or Mahjong identity and never opens other parts.
        uint faceHash = Read<uint>((nint)faceAsset.AtkTexture.Resource +
            (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash)), face.Path + ".face.hash");
        uint icon = Read<uint>((nint)faceAsset.AtkTexture.Resource +
            (int)Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.IconId)), face.Path + ".face.iconId");
        return icon is > 0 and < 1000000
            ? Result(LowerHandProfile.VerifiedIconStatus, icon, faceHash)
            : Result("FACE_ICON_ID_UNAVAILABLE", faceHash: faceHash);
    }

    private T Read<T>(nint address, string field) where T : unmanaged =>
        MemoryMarshal.Read<T>(bytes(address, sizeof(T), field));
}
