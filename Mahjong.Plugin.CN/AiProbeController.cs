using Mahjong.Cn.Engines;

namespace Mahjong.Plugin.CN;

/// <summary>Explicit local AI self-check. The sample is never represented as the user's current game.</summary>
internal sealed class AiProbeController : IDisposable
{
    private readonly object gate = new();
    private readonly DecisionCoordinator<AkochanDecision> coordinator = new();
    private readonly Func<string, AkochanReplay, CancellationToken, Task<AkochanDecision>> worker;
    private readonly Action<string>? cleanupLog;
    private const int MaximumCleanupLogs = 8;
    private int cleanupLogs;
    private string? cleanupWarning;
    private Task completion = Task.CompletedTask;
    private long generation;
    private bool disposed;
    private bool busy;
    private string status = "尚未运行本地 akochan。自检使用已脱敏的上游公开样例，不读取当前牌局。";
    internal bool Busy { get { lock (gate) return busy; } }
    internal string Status { get { lock (gate) return status; } }
    internal string? CleanupWarning { get { lock (gate) return cleanupWarning; } }
    internal Task Completion { get { lock (gate) return completion; } }
    internal AkochanDecision? Result => coordinator.Current?.Value;

    internal AiProbeController(Action<string>? cleanupLog = null) : this(RunRealEngineAsync, cleanupLog) { }

    // The plugin always uses the real worker above. Managed tests can supply a deterministic
    // worker without adding a fake-engine configuration or changing any gameplay permissions.
    internal AiProbeController(Func<string, AkochanReplay, CancellationToken, Task<AkochanDecision>> worker,
        Action<string>? cleanupLog = null)
    {
        ArgumentNullException.ThrowIfNull(worker);
        this.worker = worker;
        this.cleanupLog = cleanupLog;
    }

    private static async Task<AkochanDecision> RunRealEngineAsync(string directory, AkochanReplay replay,
        CancellationToken cancellation)
    {
        var installation = await AkochanInstallation.LoadAsync(directory, cancellation).ConfigureAwait(false);
        return await new AkochanDecisionEngine().AnalyzeOfflineAsync(installation, replay,
            TimeSpan.FromSeconds(60), cancellation).ConfigureAwait(false);
    }

    internal void Start(string directory)
    {
        long mine;
        lock (gate)
        {
            if (disposed || busy) return;
            mine = ++generation;
            busy = true;
            status = "正在核对本机引擎文件并计算公开样例；可随时停止。";
            coordinator.Invalidate();
            // No file hashing, native process startup or inference runs on the game thread.
            completion = Task.Run(async () =>
            {
                try
                {
                    var replay = AkochanSamples.PublicOpening();
                    var context = new DecisionContext(Guid.NewGuid(), "offline-example-round", 1,
                        "offline-example-draw", replay.Events.Length - 1, DateTimeOffset.UtcNow.AddSeconds(90), replay.InputSha256);
                    Task<CoordinatedDecision<AkochanDecision>?> pending;
                    lock (gate)
                    {
                        if (disposed || mine != generation) return;
                        pending = coordinator.SubmitAsync(context, cancellation => Task.Run(
                            () => worker(directory, replay, cancellation), cancellation));
                    }
                    var decision = await pending.ConfigureAwait(false);
                    lock (gate)
                        if (!disposed && mine == generation)
                            status = decision is null ? "计算已取消或超过期限，结果已作废。" :
                                "本地 akochan 已实际返回建议；以下是公开离线样例的结果。";
                }
                catch (Exception ex)
                {
                    if (ex is EngineProcessException { Code: EngineProcessErrorCode.TerminationFailed })
                        RecordCleanupFailure();
                    string code = ex switch
                    {
                        AkochanException a => a.Code, AkochanProtocolException p => p.Code,
                        EngineProcessException p => p.Code.ToString(),
                        OperationCanceledException => "CANCELLED", _ => ex.GetType().Name,
                    };
                    lock (gate) if (!disposed && mine == generation) status = "本地 AI 未就绪：" + code;
                }
                finally { lock (gate) if (mine == generation) busy = false; }
            });
        }
    }

    private void RecordCleanupFailure()
    {
        bool log;
        lock (gate)
        {
            // This lifecycle failure remains relevant after Stop/Dispose or a new generation.
            // Keep it separate from suggestions so no obsolete decision or status is revived.
            cleanupWarning = "AKOCHAN_TERMINATION_FAILED：引擎进程清理未能确认完成，可能仍占用资源。请检查本机进程后再重试。";
            log = cleanupLog is not null && cleanupLogs < MaximumCleanupLogs;
            if (log) cleanupLogs++;
        }
        if (!log) return;
        try { cleanupLog!("AKOCHAN_TERMINATION_FAILED"); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { /* Logging can be unavailable after unload; the bounded managed warning is retained. */ }
    }

    internal void Stop(string reason)
    {
        lock (gate)
        {
            ++generation;
            busy = false;
            status = reason;
            coordinator.Invalidate();
        }
    }

    public void Dispose()
    {
        lock (gate) { disposed = true; Stop("本地 AI 已卸载。"); coordinator.Dispose(); }
    }
}
