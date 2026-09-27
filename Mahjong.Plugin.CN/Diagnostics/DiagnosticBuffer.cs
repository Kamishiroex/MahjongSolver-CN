using System.Collections.Immutable;

namespace Mahjong.Plugin.CN.Diagnostics;

/// <summary>Layout recognition metadata only. Contains no tile interpretation or node payload.</summary>
internal sealed record AddonTimelineSummary(string Name, bool Present, bool Visible, bool Ready,
    int NodeCount, string? Error, int AtkValueCount = 0, int? LowerFaceCount = null, string? LowerReadingCode = null);

internal sealed record DiagnosticTimelineEntry(long Sequence, DateTimeOffset Utc, string Marker,
    string Recognition, ImmutableArray<AddonTimelineSummary> Addons);

/// <summary>
/// Bounded, managed capture retention. Call from the plugin's existing capture/export lock.
/// DetailedFrames includes FirstTableFrame exactly once; it is NOT additional node storage.
/// Returned collections and all nested frame collections are immutable snapshots.
/// </summary>
internal sealed class DiagnosticBuffer
{
    internal const int DefaultMaxDetailedFrames = 360;
    internal const int DefaultMaxDetailedNodes = 40000;
    internal const int DefaultMaxTimelineEntries = 360;
    internal const int MaxNodesPerFrame = 2048;

    private readonly int maxDetailedFrames;
    private readonly int maxDetailedNodes;
    private readonly int maxTimelineEntries;
    private readonly List<RetainedFrame> detailed = [];
    private readonly Queue<DiagnosticTimelineEntry> timeline = new();
    private int pinnedNodeCount;
    private int pinnedPublicNodeCount;

    internal DiagnosticBuffer(int maxDetailedFrames = DefaultMaxDetailedFrames,
        int maxDetailedNodes = DefaultMaxDetailedNodes, int maxTimelineEntries = DefaultMaxTimelineEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDetailedFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDetailedNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTimelineEntries);
        if (maxDetailedFrames > DefaultMaxDetailedFrames || maxDetailedNodes > DefaultMaxDetailedNodes ||
            maxTimelineEntries > DefaultMaxTimelineEntries)
            throw new ArgumentOutOfRangeException(nameof(maxDetailedFrames), "Capture limits may be reduced, never raised above the reviewed bounds.");
        this.maxDetailedFrames = maxDetailedFrames;
        this.maxDetailedNodes = maxDetailedNodes;
        this.maxTimelineEntries = maxTimelineEntries;
    }

    internal int Count => detailed.Count;
    internal int TimelineCount => timeline.Count;
    internal int TotalDetailedNodes { get; private set; }
    internal int TotalPublicLayoutNodes { get; private set; }
    internal long DroppedDetailedFrames { get; private set; }
    internal DiagnosticFrame? FirstTableFrame { get; private set; }
    internal ImmutableArray<DiagnosticFrame> DetailedFrames => detailed.Select(x => x.Frame).ToImmutableArray();
    internal ImmutableArray<DiagnosticTimelineEntry> Timeline => timeline.ToImmutableArray();

    internal void Append(DiagnosticFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(frame.Addons);
        var summaries = ImmutableArray.CreateBuilder<AddonTimelineSummary>(frame.Addons.Count);
        long nodeCount = 0;
        long publicNodeCount = 0;
        bool noProbeErrors = true;
        bool hasReadyTableNodes = false;
        foreach (var addon in frame.Addons)
        {
            ArgumentNullException.ThrowIfNull(addon);
            ArgumentNullException.ThrowIfNull(addon.VisibleNodes);
            int count = addon.VisibleNodes.Count;
            nodeCount += count;
            if (addon.PublicLayouts is { } layouts)
                foreach (var item in layouts) publicNodeCount += item.NodeAndParents.Length;
            noProbeErrors &= string.IsNullOrEmpty(addon.Error);
            hasReadyTableNodes |= addon.Name is "Emj" or "EmjL" && addon.Present && addon.Visible && addon.Ready && count > 0;
            summaries.Add(new(addon.Name, addon.Present, addon.Visible, addon.Ready, count, addon.Error,
                addon.AtkValueCount, addon.LowerHandFaces?.Count, addon.LowerHandReading?.Code));
        }

        // The timeline has an independent frame bound: node pressure must not erase entry/transition evidence.
        timeline.Enqueue(new(frame.Sequence, frame.Utc, frame.Marker, frame.Recognition, summaries.ToImmutable()));
        while (timeline.Count > maxTimelineEntries) timeline.Dequeue();

        // Keep only a summary for an oversize sample; do not copy or partially trim the detailed frame.
        if (nodeCount > MaxNodesPerFrame || nodeCount > maxDetailedNodes ||
            publicNodeCount > MaxNodesPerFrame || publicNodeCount > maxDetailedNodes ||
            (FirstTableFrame is not null && (nodeCount + pinnedNodeCount > maxDetailedNodes ||
                publicNodeCount + pinnedPublicNodeCount > maxDetailedNodes)))
        {
            DroppedDetailedFrames++;
            return;
        }

        var frozen = frame with
        {
            Addons = frame.Addons.Select(addon => addon with
            {
                VisibleNodes = addon.VisibleNodes.ToImmutableArray(),
                LowerHandFaces = addon.LowerHandFaces?.ToImmutableArray(),
                PublicLayouts = addon.PublicLayouts?.ToImmutableArray(),
            }).ToImmutableArray(),
        };
        detailed.Add(new(frozen, (int)nodeCount, (int)publicNodeCount));
        TotalDetailedNodes += (int)nodeCount;
        TotalPublicLayoutNodes += (int)publicNodeCount;
        if (FirstTableFrame is null && noProbeErrors && hasReadyTableNodes)
        {
            FirstTableFrame = frozen;
            pinnedNodeCount = (int)nodeCount;
            pinnedPublicNodeCount = (int)publicNodeCount;
        }

        while (detailed.Count > maxDetailedFrames || TotalDetailedNodes > maxDetailedNodes || TotalPublicLayoutNodes > maxDetailedNodes)
        {
            // There is at most one pinned frame and it individually fits both resource bounds.
            int index = detailed.FindIndex(x => !ReferenceEquals(x.Frame, FirstTableFrame));
            if (index < 0) throw new InvalidOperationException("Pinned diagnostic frame exceeded a capture invariant.");
            TotalDetailedNodes -= detailed[index].NodeCount;
            TotalPublicLayoutNodes -= detailed[index].PublicNodeCount;
            detailed.RemoveAt(index);
            DroppedDetailedFrames++;
        }
    }

    internal void Clear()
    {
        detailed.Clear();
        timeline.Clear();
        FirstTableFrame = null;
        pinnedNodeCount = 0;
        pinnedPublicNodeCount = 0;
        TotalDetailedNodes = 0;
        TotalPublicLayoutNodes = 0;
        DroppedDetailedFrames = 0;
    }

    private sealed record RetainedFrame(DiagnosticFrame Frame, int NodeCount, int PublicNodeCount);
}
