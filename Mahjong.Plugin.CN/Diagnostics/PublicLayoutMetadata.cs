using System.Collections.Immutable;

namespace Mahjong.Plugin.CN.Diagnostics;

internal sealed record PublicPathType(string Path, int Type);
internal sealed record PublicLayoutRoute(string Area, string RootPath, int RootType, uint[] OwnerIds,
    bool ReadShellPart, PublicPathType[]? RequiredPathTypes = null);
internal sealed record LayoutTransform(uint NodeId, int Type, float ScaleX, float ScaleY, float Rotation,
    float OriginX, float OriginY, float M11, float M12, float M21, float M22, int Width, int Height,
    float? LocalX = null, float? LocalY = null);
internal sealed record SelectedShellPart(ushort PartId, uint PartsListId, uint PartCount,
    ushort U, ushort V, ushort Width, ushort Height);

/// <summary>
/// Only bounded transforms and selected SHELL part metadata in audited public regions.
/// No face image header, resource pointer, icon, resource hash, string or Mahjong semantics.
/// Parent records contain numeric UI IDs, never memory addresses.
/// </summary>
internal sealed record PublicLayoutMetadata(string Path, string Area, string Status,
    ImmutableArray<LayoutTransform> NodeAndParents, SelectedShellPart? ShellPart = null);
