using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Mahjong.Cn.Engines;

public sealed record AkochanDecision(ImmutableArray<AkochanMove> Moves, string InputSha256,
    string SourceLabel, bool IsSynthetic, string EngineCommit, string InstallationManifestSha256,
    double StartToResponseMilliseconds, long? PeakWorkingSetBytes, double? CpuMilliseconds)
{
    public string EngineName => "akochan (local CPU)";
    public bool IsLiveGameDecision => false;
    public bool KanDoraCounterFixApplied { get; init; }
    public string Explanation => "原生 pipe 未提供候选评分、概率或自然语言理由。";
}

/// <summary>
/// Calls the unmodified official pipe entry point. Each request starts a fresh process,
/// replays the complete supplied round with can_act:false, then reads one Moves array.
/// Never attempts to fabricate an event stream from incomplete public observations.
/// </summary>
public sealed class AkochanDecisionEngine
{
    public async Task<AkochanDecision> AnalyzeOfflineAsync(AkochanInstallation installation,
        AkochanReplay replay, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(replay);
        if (installation.SourceCommit != AkochanInstallation.ExpectedCommit)
            throw new AkochanException("AKOCHAN_IDENTITY_MISMATCH");
        if (replay.RequiresKanDoraCounterFix && !installation.HasKanDoraCounterFix)
            throw new AkochanException("AKOCHAN_KAN_DORA_PATCH_REQUIRED");
        var spec = new EngineProcessSpec(installation.Executable, installation.Directory,
            ["pipe", installation.Tactics, replay.PlayerId.ToString(CultureInfo.InvariantCulture)])
        {
            ResponseRootKind = JsonValueKind.Array,
            EnvironmentVariables = ImmutableDictionary<string, string>.Empty
                .Add("OMP_NUM_THREADS", "2").Add("OMP_THREAD_LIMIT", "2").Add("OMP_DYNAMIC", "FALSE"),
        };
        var watch = Stopwatch.StartNew();
        await using var host = await EngineProcessHost.StartAsync(spec, cancellationToken).ConfigureAwait(false);
        TimeSpan remaining = timeout - watch.Elapsed;
        if (remaining <= TimeSpan.Zero) throw new EngineProcessException(EngineProcessErrorCode.Timeout);
        string response = await host.ExchangeBatchAsync(replay.Events, remaining, cancellationToken).ConfigureAwait(false);
        var moves = AkochanReplay.ParseMoves(response, replay);
        watch.Stop();
        long? memory = null;
        double? cpu = null;
        try
        {
            using var process = Process.GetProcessById(host.ProcessId);
            memory = process.PeakWorkingSet64;
            cpu = process.TotalProcessorTime.TotalMilliseconds;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { /* Metrics are optional; their absence is not zero resource usage. */ }
        return new(moves, replay.InputSha256, replay.SourceLabel, replay.IsSynthetic, installation.SourceCommit,
            installation.ManifestSha256, watch.Elapsed.TotalMilliseconds, memory, cpu)
            { KanDoraCounterFixApplied = installation.HasKanDoraCounterFix };
    }
}
