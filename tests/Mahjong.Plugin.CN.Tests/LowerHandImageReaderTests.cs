using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>
/// Tests a caller that has already established profile/version/visibility/consent. Addresses reference only
/// SyntheticMemory segments, never the game. Shell-before-face and selected-part-only reads are asserted.
/// </summary>
public sealed unsafe class LowerHandImageReaderTests
{
    [Theory]
    [InlineData("", 0u)]
    [InlineData("123456789", 0xCBF43926u)]
    [InlineData("ui/uld/EmjTile.tex", 0x08BFF738u)]
    [InlineData("ui/uld/EmjTile_hr1.tex", 0x17234180u)]
    public void Client_path_hash_matches_finalized_crc_and_the_second_live_capture(string path, uint expected)
        => Assert.Equal(expected, LowerHandImageReader.ClientTexturePathHash(path));

    [Fact]
    public void Back_shell_part_six_stops_before_face_or_parts_payload_reads()
    {
        var f = new Fixture(); f.Shell(partId: 6);
        var candidate = f.Probe();
        Assert.StartsWith("SHELL_NOT_FRONT", candidate.DiagnosticStatus);
        Assert.Null(candidate.IconId);
        Assert.Single(f.Memory.Reads);
        Assert.Equal(Fixture.ShellAddress, f.Memory.Reads[0].Address);
        f.AssertNoFaceReads();
    }

    [Theory]
    [InlineData(17, 23)]
    [InlineData(18, 22)]
    [InlineData(18, 24)]
    public void Unrecognized_shell_layout_stops_before_face_and_asset_reads(uint listId, uint count)
    {
        var f = new Fixture(); f.Shell(listId: listId, partCount: count);
        Assert.StartsWith("SHELL_LAYOUT_MISMATCH", f.Probe().DiagnosticStatus);
        Assert.Equal(2, f.Memory.Reads.Count);
        f.AssertNoFaceReads();
    }

    [Theory]
    [InlineData(20, TextureType.Resource)]
    [InlineData(21, TextureType.KernelTexture)]
    [InlineData(21, TextureType.Crest)]
    public void Unrecognized_shell_asset_or_texture_type_stops_before_face_and_hash(uint assetId, TextureType textureType)
    {
        var f = new Fixture(); f.Shell(assetId: assetId, textureType: textureType);
        Assert.StartsWith("SHELL_ASSET_MISMATCH", f.Probe().DiagnosticStatus);
        Assert.Equal(4, f.Memory.Reads.Count);
        Assert.DoesNotContain(f.Memory.Reads, x => x.Address == Fixture.ShellHashAddress);
        f.AssertNoFaceReads();
    }

    [Theory]
    [InlineData(0xDEADBEEFu)]
    [InlineData(0xF74008C7u)] // Lumina unfinalized standard path
    [InlineData(0x88AAE546u)] // Lumina unfinalized lowercase path
    [InlineData(0xE8DCBE7Fu)] // Lumina unfinalized hr1 path
    [InlineData(0x78285B19u)] // Lumina unfinalized lowercase hr1 path
    [InlineData(0x77551AB9u)] // Finalized but lowercased: not the ULD path
    [InlineData(0x87D7A4E6u)]
    public void Unknown_or_wrong_algorithm_shell_hash_blocks_face_access_even_when_a_complete_face_is_available(uint hash)
    {
        var f = new Fixture(); f.Shell(hash: hash);
        var result = f.Probe();
        Assert.StartsWith("SHELL_HASH_UNVERIFIED", result.DiagnosticStatus);
        Assert.Null(result.IconId); Assert.Null(result.FacePathHash);
        Assert.Equal(5, f.Memory.Reads.Count);
        Assert.Equal((Fixture.ShellHashAddress, sizeof(uint)), f.Memory.Reads[^1]);
        f.AssertNoFaceReads();
    }

    [Theory]
    [InlineData(1, 0, 42, 55)]
    [InlineData(0, 1, 42, 55)]
    [InlineData(0, 0, 41, 55)]
    [InlineData(0, 0, 42, 54)]
    public void Shell_rectangle_must_match_the_audited_front_before_assets_or_face_reads(ushort u, ushort v, ushort width, ushort height)
    {
        var f = new Fixture();
        f.Memory.Store(Fixture.ShellPartsAddress, new AtkUldPart
        {
            UldAsset = (AtkUldAsset*)Fixture.ShellAssetAddress, U = u, V = v, Width = width, Height = height,
        });
        Assert.StartsWith("SHELL_RECT_UNVERIFIED", f.Probe().DiagnosticStatus);
        Assert.Equal(3, f.Memory.Reads.Count);
        Assert.DoesNotContain(f.Memory.Reads, x => x.Address == Fixture.ShellAssetAddress || x.Address == Fixture.ShellHashAddress);
        f.AssertNoFaceReads();
    }

    [Theory]
    [InlineData(0x08BFF738u)]
    [InlineData(0x17234180u)] // Observed in 1036 candidates from the second live capture
    public void Verified_front_shell_reads_only_selected_face_part_and_resource_scalars(uint hash)
    {
        var f = new Fixture(); f.Shell(hash: hash);
        var candidate = f.Probe();
        Assert.Equal(LowerHandProfile.VerifiedIconStatus, candidate.DiagnosticStatus);
        Assert.Equal(1000u, candidate.IconId);
        Assert.Equal(Fixture.TestFaceHash, candidate.FacePathHash);
        Assert.Equal(Fixture.Path, candidate.Path);
        Assert.Equal(100, candidate.X); Assert.Equal(640, candidate.Y);
        Assert.Equal(40, candidate.Width); Assert.Equal(60, candidate.Height);
        Assert.Equal(new (nint Address, int Count)[]
        {
            (Fixture.ShellAddress, sizeof(AtkImageNode)),
            (Fixture.ShellListAddress, sizeof(AtkUldPartsList)),
            (Fixture.ShellPartsAddress, sizeof(AtkUldPart)),
            (Fixture.ShellAssetAddress, sizeof(AtkUldAsset)),
            (Fixture.ShellHashAddress, sizeof(uint)),
            (Fixture.FaceAddress, sizeof(AtkImageNode)),
            (Fixture.FaceListAddress, sizeof(AtkUldPartsList)),
            (Fixture.FacePartsAddress + 3 * sizeof(AtkUldPart), sizeof(AtkUldPart)),
            (Fixture.FaceAssetAddress, sizeof(AtkUldAsset)),
            (Fixture.FaceHashAddress, sizeof(uint)),
            (Fixture.FaceIconAddress, sizeof(uint)),
        }, f.Memory.Reads);
        // No full AtkTextureResource, texture paths, other parts or texture pixel payload are mapped at all.
        Assert.Equal("Emj/1340001/9/4.face.iconId", f.Fields[^1]);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(0, 0)]
    [InlineData(3, 4097)]
    public void Invalid_face_part_bounds_prevent_selected_part_or_asset_reads(ushort selectedPart, uint partCount)
    {
        var f = new Fixture(); f.Face(selectedPart: selectedPart, partCount: partCount);
        var result = f.Probe();
        Assert.Equal("FACE_PART_INVALID", result.DiagnosticStatus); Assert.Null(result.IconId);
        Assert.Equal(7, f.Memory.Reads.Count);
        Assert.DoesNotContain(f.Fields, field => field.Contains("selectedPart", StringComparison.Ordinal));
        Assert.DoesNotContain(f.Memory.Reads, x => x.Address == Fixture.FaceIconAddress);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1000000u)]
    [InlineData(uint.MaxValue)]
    public void Unrecognized_icon_id_is_exported_as_unknown(uint icon)
    {
        var f = new Fixture(); f.Face(icon: icon);
        var result = f.Probe();
        Assert.Equal("FACE_ICON_ID_UNAVAILABLE", result.DiagnosticStatus);
        Assert.Null(result.IconId);
        Assert.Equal(Fixture.TestFaceHash, result.FacePathHash);
        Assert.False(LowerHandProfile.CheckPreview([result], _ => true).Eligible);
    }

    [Theory]
    [InlineData(TextureType.Crest)]
    [InlineData(TextureType.KernelTexture)]
    public void Nonresource_face_texture_never_reads_icon_or_resource_payload(TextureType textureType)
    {
        var f = new Fixture(); f.Face(textureType: textureType);
        var result = f.Probe();
        Assert.Equal("FACE_ICON_RESOURCE_UNAVAILABLE", result.DiagnosticStatus); Assert.Null(result.IconId);
        Assert.Null(result.FacePathHash);
        Assert.Equal(9, f.Memory.Reads.Count);
        Assert.DoesNotContain(f.Memory.Reads, x => x.Address == Fixture.FaceIconAddress);
    }

    [Fact]
    public void Unreadable_face_hash_propagates_failure_before_icon_read_instead_of_using_stale_data()
    {
        var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => f.Probe(Fixture.FaceHashAddress));
        Assert.Equal("Emj/1340001/9/4.face.hash", f.Fields[^1]);
        Assert.DoesNotContain(f.Memory.Reads, x => x.Address == Fixture.FaceIconAddress);
    }

    [Fact]
    public void Zero_face_hash_is_preserved_as_a_value_not_replaced_with_null()
    {
        var f = new Fixture(); f.Memory.Store(Fixture.FaceHashAddress, 0u);
        Assert.Equal(0u, f.Probe().FacePathHash);
    }

    private sealed class Fixture
    {
        internal const string Path = "Emj/1340001/9/4";
        internal const nint ShellAddress = 0x30000;
        internal const nint ShellListAddress = 0x31000;
        internal const nint ShellPartsAddress = 0x32000;
        internal const nint ShellAssetAddress = 0x33000;
        internal const nint ShellResourceAddress = 0x34000;
        internal const nint FaceAddress = 0x40000;
        internal const nint FaceListAddress = 0x41000;
        internal const nint FacePartsAddress = 0x42000;
        internal const nint FaceAssetAddress = 0x43000;
        internal const nint FaceResourceAddress = 0x44000;
        internal const uint TestFaceHash = 0x12345678;
        internal static readonly nint ShellHashAddress = ShellResourceAddress + Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash));
        internal static readonly nint FaceHashAddress = FaceResourceAddress + Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.TexPathHash));
        internal static readonly nint FaceIconAddress = FaceResourceAddress + Marshal.OffsetOf<AtkTextureResource>(nameof(AtkTextureResource.IconId));
        internal readonly SyntheticMemory Memory = new();
        internal readonly List<string> Fields = [];

        internal Fixture() { Shell(); Face(); }

        internal void Shell(ushort partId = 0, uint listId = 18, uint partCount = 23,
            uint assetId = 21, TextureType textureType = TextureType.Resource, uint hash = 0x17234180)
        {
            Memory.Store(ShellAddress, new AtkImageNode { PartId = partId, PartsList = (AtkUldPartsList*)ShellListAddress });
            Memory.Store(ShellListAddress, new AtkUldPartsList { Id = listId, PartCount = partCount, Parts = (AtkUldPart*)ShellPartsAddress });
            Memory.Store(ShellPartsAddress, new AtkUldPart { UldAsset = (AtkUldAsset*)ShellAssetAddress, U = 0, V = 0, Width = 42, Height = 55 });
            Memory.Store(ShellAssetAddress, new AtkUldAsset
            {
                Id = assetId, AtkTexture = new AtkTexture { TextureType = textureType, Resource = (AtkTextureResource*)ShellResourceAddress },
            });
            Memory.Store(ShellHashAddress, hash);
        }

        internal void Face(ushort selectedPart = 3, uint partCount = 5, uint icon = 1000,
            TextureType textureType = TextureType.Resource)
        {
            Memory.Store(FaceAddress, new AtkImageNode { PartId = selectedPart, PartsList = (AtkUldPartsList*)FaceListAddress });
            Memory.Store(FaceListAddress, new AtkUldPartsList { Id = 42, PartCount = partCount, Parts = (AtkUldPart*)FacePartsAddress });
            // Deliberately map only the selected part; reading adjacent parts will fail the test.
            Memory.Store(FacePartsAddress + selectedPart * sizeof(AtkUldPart), new AtkUldPart { UldAsset = (AtkUldAsset*)FaceAssetAddress });
            Memory.Store(FaceAssetAddress, new AtkUldAsset
            {
                Id = 999, AtkTexture = new AtkTexture { TextureType = textureType, Resource = (AtkTextureResource*)FaceResourceAddress },
            });
            Memory.Store(FaceHashAddress, TestFaceHash);
            Memory.Store(FaceIconAddress, icon);
        }

        internal HandFaceCandidate Probe(nint unreadableAddress = default)
        {
            var reader = new LowerHandImageReader((address, count, field) =>
            {
                Fields.Add(field);
                if (address == unreadableAddress) throw new InvalidOperationException("Synthetic unreadable field: " + field);
                return Memory.Read(address, count) ?? throw new InvalidOperationException("Unmapped synthetic read: " + field);
            });
            return reader.Probe(new(Path, 4, 2, 100, 640, 0, 40, 60, null), FaceAddress, ShellAddress);
        }

        internal void AssertNoFaceReads()
        {
            Assert.DoesNotContain(Memory.Reads, x => x.Address >= FaceAddress && x.Address < FaceResourceAddress + 0x1000);
            Assert.DoesNotContain(Fields, field => field.Contains(".face.", StringComparison.Ordinal));
        }
    }
}
