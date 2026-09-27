using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Mahjong.Plugin.CN.Diagnostics;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>
/// Synthetic struct buffers, never pointers to real game/process memory. No native constructors,
/// virtual methods, FFXIV signature resolver, Dalamud service, or ReadProcessMemory are called.
/// </summary>
internal sealed unsafe class SyntheticMemory
{
    internal const long AddonAddress = 0x10000;
    internal const long ListAddress = 0x20000;
    private readonly Dictionary<nint, byte[]> segments = [];
    internal readonly List<(nint Address, int Count)> Reads = [];

    internal void Store<T>(nint address, T value) where T : unmanaged
    {
        var bytes = new byte[sizeof(T)];
        MemoryMarshal.Write(bytes.AsSpan(), in value);
        segments[address] = bytes;
    }

    internal void StoreComponentNode(nint address, AtkResNode header, nint component)
    {
        AtkComponentNode value = default;
        value.Component = (AtkComponentBase*)component;
        var bytes = new byte[sizeof(AtkComponentNode)];
        MemoryMarshal.Write(bytes.AsSpan(), in value);
        MemoryMarshal.Write(bytes.AsSpan(), in header);
        segments[address] = bytes;
    }

    internal AtkUldManager NodeList(params nint[] addresses) => NodeListAt((nint)ListAddress, addresses);

    internal AtkUldManager NodeListAt(nint listAddress, params nint[] addresses)
    {
        var bytes = new byte[addresses.Length * IntPtr.Size];
        for (int i = 0; i < addresses.Length; i++)
            MemoryMarshal.Write(bytes.AsSpan(i * IntPtr.Size), in addresses[i]);
        segments[listAddress] = bytes;
        AtkUldManager manager = default;
        manager.NodeList = (AtkResNode**)listAddress;
        manager.NodeListCount = checked((ushort)addresses.Length);
        manager.NodeListSize = checked((ushort)addresses.Length);
        return manager;
    }

    internal void Addon(AtkUldManager manager, bool visible = true, bool ready = true, byte alpha = 255,
        nint address = default)
    {
        AtkUnitBase unit = default;
        unit.UldManager = manager;
        unit.IsVisible = visible; // Generated managed bitfield setter, not Show()/Hide().
        unit.Flags1A1 = ready ? (byte)1 : (byte)0;
        unit.Alpha = alpha;
        unit.AtkValuesCount = 109;
        unit.AtkValues = (AtkValue*)0x7FFF0000; // Intentionally unmapped; must never be followed.
        Store(address == 0 ? (nint)AddonAddress : address, unit);
    }

    internal static AtkResNode Node(uint id, nint parent = default, NodeType type = NodeType.Image)
    {
        AtkResNode node = default;
        node.NodeId = id;
        node.NodeFlags = NodeFlags.Visible;
        node.Color.A = 255;
        node.Type = type;
        node.ParentNode = (AtkResNode*)parent;
        node.ScreenX = 100;
        node.ScreenY = 200;
        node.Width = 32;
        node.Height = 48;
        return node;
    }

    internal byte[]? Read(nint address, int count)
    {
        Reads.Add((address, count));
        // Deliberately expose only registered prefixes/ranges; unmapped text/image payloads fail.
        foreach (var pair in segments)
        {
            long start = address - pair.Key;
            if (start >= 0 && start <= pair.Value.Length && count <= pair.Value.Length - start)
                return pair.Value.AsSpan((int)start, count).ToArray();
        }
        return null;
    }

    internal VisibleUiReader Reader(UiReadLimits? limits = null, Func<double>? clock = null,
        Func<string, nint>? lookup = null) =>
        new(lookup ?? (_ => (nint)AddonAddress), Read, limits, clock ?? (() => 0));
}
