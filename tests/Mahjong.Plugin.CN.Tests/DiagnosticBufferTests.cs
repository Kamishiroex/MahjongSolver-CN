using System.Collections.Immutable;
using System.Text.Json;
using Mahjong.Plugin.CN.Diagnostics;
using Xunit;

namespace Mahjong.Plugin.CN.Tests;

/// <summary>Artificial layout metadata, never interpreted as Mahjong tiles or live CN observations.</summary>
public sealed class DiagnosticBufferTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 23, 0, 0, 0, TimeSpan.Zero);
    private static UiNode Node(int id) => new($"Emj/{id}", (uint)id, 1, id, id, 0, 10, 20, null);
    private static AddonProbe Probe(int nodes, string name = "Emj", bool present = true,
        bool visible = true, bool ready = true, string? error = null) =>
        new(name, present, visible, ready, 0, Enumerable.Range(0, nodes).Select(Node).ToArray(), error);
    private static DiagnosticFrame Frame(long sequence, params AddonProbe[] probes) =>
        new(sequence, Epoch.AddMilliseconds(sequence * 500), "人工标记", "未映射的布局元数据", probes);

    [Fact]
    public void Node_pressure_keeps_first_ready_table_frame_and_independent_entry_timeline()
    {
        var buffer = new DiagnosticBuffer();
        buffer.Append(Frame(1));
        buffer.Append(Frame(2, Probe(0, visible: false, ready: false)));
        for (int i = 3; i <= 123; i++) buffer.Append(Frame(i, Probe(440)));
        Assert.Equal(123, buffer.Timeline.Length);
        Assert.Equal(1, buffer.Timeline[0].Sequence);
        Assert.Equal(3, buffer.FirstTableFrame!.Sequence);
        Assert.Equal(90, buffer.Count);
        Assert.Equal(39600, buffer.TotalDetailedNodes);
        Assert.Equal(33, buffer.DroppedDetailedFrames);
        Assert.Equal(3, buffer.DetailedFrames[0].Sequence);
        Assert.Equal(123, buffer.DetailedFrames[^1].Sequence);
        Assert.Single(buffer.DetailedFrames, x => x.Sequence == buffer.FirstTableFrame.Sequence);
    }

    [Theory]
    [InlineData("Emj")]
    [InlineData("EmjL")]
    public void Empty_transition_and_unrelated_addons_do_not_claim_the_first_table_slot(string name)
    {
        var buffer = new DiagnosticBuffer();
        buffer.Append(Frame(1, Probe(0, name)));
        buffer.Append(Frame(2, Probe(1, "EmjResult")));
        buffer.Append(Frame(3, Probe(2, name, visible: false)));
        buffer.Append(Frame(4, Probe(2, name, ready: false)));
        buffer.Append(Frame(5, Probe(2, name, present: false)));
        Assert.Null(buffer.FirstTableFrame);
        buffer.Append(Frame(6, Probe(2, name)));
        buffer.Append(Frame(7, Probe(3, name)));
        Assert.Equal(6, buffer.FirstTableFrame!.Sequence);
        Assert.Equal(7, buffer.Timeline.Length);
        Assert.Equal(0, buffer.Timeline[0].Addons[0].NodeCount);
    }

    [Fact]
    public void Any_probe_error_prevents_pinning_but_remains_in_the_timeline()
    {
        var buffer = new DiagnosticBuffer();
        buffer.Append(Frame(1, Probe(20), Probe(0, "EmjL", error: "READ_FAILURE")));
        Assert.Null(buffer.FirstTableFrame);
        Assert.Equal("READ_FAILURE", buffer.Timeline[0].Addons[1].Error);
        buffer.Append(Frame(2, Probe(20)));
        Assert.Equal(2, buffer.FirstTableFrame!.Sequence);
    }

    [Fact]
    public void Oversize_frame_preserves_summary_without_copying_partial_details_or_pinning()
    {
        var buffer = new DiagnosticBuffer();
        buffer.Append(Frame(1, Probe(2048), Probe(1, "EmjL")));
        Assert.Empty(buffer.DetailedFrames);
        Assert.Equal(1, buffer.DroppedDetailedFrames);
        Assert.Equal(0, buffer.TotalDetailedNodes);
        Assert.Null(buffer.FirstTableFrame);
        Assert.Equal(2048, buffer.Timeline[0].Addons[0].NodeCount);
        Assert.Equal(1, buffer.Timeline[0].Addons[1].NodeCount);
        buffer.Append(Frame(2, Probe(2048)));
        Assert.Equal(2, buffer.FirstTableFrame!.Sequence);
        Assert.Equal(2048, buffer.TotalDetailedNodes);
    }

    [Fact]
    public void Frame_that_cannot_fit_beside_the_pin_does_not_destroy_existing_rolling_history()
    {
        var buffer = new DiagnosticBuffer(maxDetailedNodes: 6);
        buffer.Append(Frame(1, Probe(3)));
        buffer.Append(Frame(2, Probe(3)));
        buffer.Append(Frame(3, Probe(4)));
        Assert.Equal(new long[] { 1, 2 }, buffer.DetailedFrames.Select(x => x.Sequence));
        Assert.Equal(6, buffer.TotalDetailedNodes);
        Assert.Equal(1, buffer.DroppedDetailedFrames);
        Assert.Equal(3, buffer.Timeline.Length);
    }

    [Fact]
    public void Single_frame_budget_cannot_evict_or_duplicate_the_pin()
    {
        var buffer = new DiagnosticBuffer(maxDetailedFrames: 1);
        buffer.Append(Frame(1));
        buffer.Append(Frame(2, Probe(10)));
        buffer.Append(Frame(3, Probe(20)));
        Assert.Single(buffer.DetailedFrames);
        Assert.Equal(2, buffer.DetailedFrames[0].Sequence);
        Assert.Equal(10, buffer.TotalDetailedNodes);
        Assert.Equal(2, buffer.DroppedDetailedFrames);
        Assert.Equal(3, buffer.Timeline.Length);
    }

    [Fact]
    public void Frame_and_timeline_time_limits_remain_bounded_even_with_zero_nodes()
    {
        var buffer = new DiagnosticBuffer();
        for (int i = 1; i <= 400; i++) buffer.Append(Frame(i));
        Assert.Equal(360, buffer.Count);
        Assert.Equal(360, buffer.Timeline.Length);
        Assert.Equal(41, buffer.Timeline[0].Sequence);
        Assert.Equal(400, buffer.Timeline[^1].Sequence);
        Assert.Equal(40, buffer.DroppedDetailedFrames);
        Assert.Equal(0, buffer.TotalDetailedNodes);
        Assert.Null(buffer.FirstTableFrame);
    }

    [Fact]
    public void First_table_frame_survives_even_after_its_lightweight_timeline_entry_ages_out()
    {
        var buffer = new DiagnosticBuffer();
        buffer.Append(Frame(1, Probe(1)));
        for (int i = 2; i <= 400; i++) buffer.Append(Frame(i));
        Assert.Equal(360, buffer.Count);
        Assert.Equal(360, buffer.Timeline.Length);
        Assert.Equal(41, buffer.Timeline[0].Sequence);
        Assert.Equal(1, buffer.FirstTableFrame!.Sequence);
        Assert.Equal(1, buffer.DetailedFrames[0].Sequence);
        Assert.Equal(1, buffer.TotalDetailedNodes);
        Assert.Equal(40, buffer.DroppedDetailedFrames);
    }

    [Fact]
    public void Append_and_export_are_deep_immutable_copies_after_source_mutation_and_session_clear()
    {
        var nodes = new List<UiNode> { Node(7) };
        var probes = new List<AddonProbe> { new("Emj", true, true, true, 0, nodes, null) };
        var original = new DiagnosticFrame(1, Epoch, "入桌/开局", "visible-ready", probes);
        var buffer = new DiagnosticBuffer();
        buffer.Append(original);
        var exportedDetails = buffer.DetailedFrames;
        var exportedTimeline = buffer.Timeline;
        var exportedFirst = buffer.FirstTableFrame;
        nodes.Clear(); probes.Clear();
        buffer.Append(Frame(2, Probe(5)));
        buffer.Clear();
        Assert.Single(exportedDetails);
        Assert.Single(exportedDetails[0].Addons);
        Assert.Equal(7u, exportedDetails[0].Addons[0].VisibleNodes[0].Id);
        Assert.IsType<ImmutableArray<AddonProbe>>(exportedDetails[0].Addons);
        Assert.IsType<ImmutableArray<UiNode>>(exportedDetails[0].Addons[0].VisibleNodes);
        Assert.Equal(1, exportedTimeline[0].Addons[0].NodeCount);
        Assert.Equal("入桌/开局", exportedTimeline[0].Marker);
        Assert.Equal("visible-ready", exportedTimeline[0].Recognition);
        Assert.Equal(Epoch, exportedTimeline[0].Utc);
        Assert.Same(exportedDetails[0], exportedFirst);
        Assert.Equal(0, buffer.Count); Assert.Equal(0, buffer.TotalDetailedNodes);
        Assert.Equal(0, buffer.DroppedDetailedFrames); Assert.Empty(buffer.Timeline); Assert.Null(buffer.FirstTableFrame);
        buffer.Append(Frame(3, Probe(2, "EmjL")));
        Assert.Equal(3, buffer.FirstTableFrame!.Sequence);
        Assert.Single(exportedDetails);
    }

    [Fact]
    public void Timeline_serializes_only_summary_metadata_without_node_payload()
    {
        var buffer = new DiagnosticBuffer();
        buffer.Append(Frame(1, Probe(1)));
        string json = JsonSerializer.Serialize(buffer.Timeline);
        Assert.Contains("NodeCount", json);
        Assert.DoesNotContain("VisibleNodes", json);
        Assert.DoesNotContain("TextByteLength", json);
        Assert.DoesNotContain("Emj/0", json);
        Assert.DoesNotContain("\"AtkValues\"", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(new[] { "Name", "Present", "Visible", "Ready", "NodeCount", "Error", "AtkValueCount", "LowerFaceCount", "LowerReadingCode" },
            document.RootElement[0].GetProperty("Addons")[0].EnumerateObject().Select(x => x.Name));
    }

    [Fact]
    public void Lower_face_candidates_are_copied_and_survive_stop_export_and_next_session_clear()
    {
        var candidates = new List<HandFaceCandidate>
        {
            new("Emj/1340001/9/4", 100, 640, 40, 60, 1000, LowerHandProfile.VerifiedIconStatus, 0x12345678),
        };
        var buffer = new DiagnosticBuffer();
        buffer.Append(Frame(1, Probe(1) with { LowerHandFaces = candidates }));
        var exported = buffer.DetailedFrames;
        candidates.Clear();
        buffer.Clear();
        var retained = exported[0].Addons[0].LowerHandFaces;
        Assert.NotNull(retained);
        Assert.Single(retained);
        Assert.IsType<ImmutableArray<HandFaceCandidate>>(retained);
        Assert.Equal(1000u, retained[0].IconId);
        Assert.Equal(0x12345678u, retained[0].FacePathHash);
        Assert.Empty(buffer.DetailedFrames);
    }

    [Fact]
    public void Public_parent_metadata_has_an_independent_retention_budget_and_immutable_export()
    {
        var parents = Enumerable.Range(1, 3).Select(i => new LayoutTransform((uint)i, 1, 1, 1, 0, 0, 0, 1, 0, 0, 1, 1, 1)).ToImmutableArray();
        var layouts = new List<PublicLayoutMetadata> { new("Emj/112/2/9/5", "lower-meld-candidate", "PUBLIC_LAYOUT_METADATA_ONLY", parents) };
        var buffer = new DiagnosticBuffer(maxDetailedNodes: 7);
        for (int i = 1; i <= 3; i++) buffer.Append(Frame(i, Probe(1) with { PublicLayouts = layouts }));
        Assert.Equal(2, buffer.Count);
        Assert.Equal(2, buffer.TotalDetailedNodes);
        Assert.Equal(6, buffer.TotalPublicLayoutNodes);
        Assert.Equal(new long[] { 1, 3 }, buffer.DetailedFrames.Select(x => x.Sequence));
        Assert.Equal(3, buffer.TimelineCount);
        var saved = buffer.DetailedFrames;
        layouts.Clear(); buffer.Clear();
        Assert.Equal(0, buffer.TotalPublicLayoutNodes);
        Assert.Equal(3, Assert.Single(saved[0].Addons[0].PublicLayouts!).NodeAndParents.Length);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(361, 1, 1)]
    [InlineData(1, 40001, 1)]
    [InlineData(1, 1, 361)]
    public void Reviewed_resource_bounds_cannot_be_disabled_or_increased(int frames, int nodes, int timeline)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticBuffer(frames, nodes, timeline));
}
