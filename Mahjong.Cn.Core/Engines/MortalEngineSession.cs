using System.Diagnostics;

namespace Mahjong.Cn.Engines;

/// <summary>One validated warm process per selected model, independent of gameplay policy lifetimes.</summary>
public sealed class MortalEngineSession : IDisposable
{
    private readonly MortalGlobalEngine engine = new();
    private readonly CancellationTokenSource lifetime = new();
    public string Directory { get; }
    public Task<MortalInstallation> Preparation { get; }
    public double ValidationMilliseconds { get; private set; }
    public double WarmupMilliseconds { get; private set; }
    private string status = "凡夫预热中：正在校验模型与运行库，可在进桌前完成。";
    public string Status => Volatile.Read(ref status);
    private int disposed;

    public MortalEngineSession(string directory)
    {
        Directory = directory;
        Preparation = Task.Run(PrepareAsync);
        _ = Preparation.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task<MortalInstallation> PrepareAsync()
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var install = await MortalInstallation.LoadAsync(Directory, lifetime.Token).ConfigureAwait(false);
            ValidationMilliseconds = timer.Elapsed.TotalMilliseconds;
            Volatile.Write(ref status, "凡夫预热中：正在加载 Python、模型并执行首次推理。");
            timer.Restart();
            await engine.PrepareAsync(install, lifetime.Token).ConfigureAwait(false);
            WarmupMilliseconds = timer.Elapsed.TotalMilliseconds;
            Volatile.Write(ref status, $"凡夫已预热（校验 {ValidationMilliseconds:F0} ms，加载与预热 {WarmupMilliseconds:F0} ms）；切换手动/自动无需重新加载。");
            return install;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref status, "凡夫预热失败：" + (ex is AkochanException ako ? ako.Code : ex.Message));
            throw;
        }
    }

    public async Task<AkochanGlobalDecision> AnalyzeAsync(AkochanGlobalSnapshot snapshot, CancellationToken token)
        => await engine.AnalyzeAsync(await Preparation.WaitAsync(token).ConfigureAwait(false), snapshot, token).ConfigureAwait(false);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        engine.Dispose();
    }
}
