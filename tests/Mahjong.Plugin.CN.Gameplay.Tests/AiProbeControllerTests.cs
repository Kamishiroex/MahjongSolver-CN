using System.Collections.Concurrent;
using Mahjong.Cn;
using Mahjong.Cn.Engines;

namespace Mahjong.Plugin.CN.Gameplay.Tests;

/// <summary>Synthetic managed workers only. These tests neither start an AI nor read the game.</summary>
public sealed class AiProbeControllerTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource<T> Source<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static AkochanDecision Decision(AkochanReplay replay, int tile = 0) =>
        new([new("dahai", 0, null, new VisibleTile(tile), [], false)], replay.InputSha256,
            "synthetic-controller-test", true, new string('0', 40), new string('0', 64), 1, null, null);

    [Fact]
    public async Task Synchronous_expensive_worker_does_not_hold_busy_or_stop_lock()
    {
        var entered = Source<bool>();
        using var release = new ManualResetEventSlim();
        using var controller = new AiProbeController((_, replay, _) =>
        {
            entered.TrySetResult(true);
            release.Wait(CancellationToken.None); // Intentionally non-cooperative synchronous work.
            return Task.FromResult(Decision(replay));
        });
        controller.Start("synthetic-no-files");
        Task completion = controller.Completion;
        try
        {
            await entered.Task.WaitAsync(Deadline);
            Assert.True(await Task.Run(() => controller.Busy).WaitAsync(Deadline));
            await Task.Run(() => controller.Stop("synthetic-stop")).WaitAsync(Deadline);
            Assert.False(controller.Busy);
            Assert.Equal("synthetic-stop", controller.Status);
            Assert.Null(controller.Result);
        }
        finally { release.Set(); await completion.WaitAsync(Deadline); }
        Assert.Null(controller.Result);
    }

    [Fact]
    public async Task Stop_preserves_late_cleanup_failure_without_reviving_a_result()
    {
        var entered = Source<CancellationToken>();
        var work = Source<AkochanDecision>();
        var logs = new ConcurrentQueue<string>();
        using var controller = new AiProbeController((_, _, token) =>
        {
            entered.TrySetResult(token);
            return work.Task;
        }, logs.Enqueue);
        controller.Start("synthetic-no-files");
        Task completion = controller.Completion;
        var token = await entered.Task.WaitAsync(Deadline);
        controller.Stop("synthetic-stop");
        Assert.True(token.IsCancellationRequested);
        work.SetException(new EngineProcessException(EngineProcessErrorCode.TerminationFailed));
        await completion.WaitAsync(Deadline);
        Assert.False(controller.Busy);
        Assert.Null(controller.Result);
        Assert.Equal("synthetic-stop", controller.Status);
        Assert.Contains("AKOCHAN_TERMINATION_FAILED", controller.CleanupWarning);
        Assert.Equal("AKOCHAN_TERMINATION_FAILED", Assert.Single(logs));
    }

    [Fact]
    public async Task Old_cleanup_warning_does_not_replace_new_generation_result_or_status()
    {
        var oldEntered = Source<bool>();
        var oldWork = Source<AkochanDecision>();
        int calls = 0;
        using var controller = new AiProbeController((_, replay, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { oldEntered.TrySetResult(true); return oldWork.Task; }
            return Task.FromResult(Decision(replay, 4));
        });
        controller.Start("synthetic-old");
        Task oldCompletion = controller.Completion;
        await oldEntered.Task.WaitAsync(Deadline);
        controller.Stop("synthetic-switch");
        controller.Start("synthetic-new");
        await controller.Completion.WaitAsync(Deadline);
        var result = controller.Result;
        string status = controller.Status;
        Assert.NotNull(result);
        oldWork.SetException(new EngineProcessException(EngineProcessErrorCode.TerminationFailed));
        await oldCompletion.WaitAsync(Deadline);
        Assert.Same(result, controller.Result);
        Assert.Equal(status, controller.Status);
        Assert.Contains("AKOCHAN_TERMINATION_FAILED", controller.CleanupWarning);
    }

    [Fact]
    public async Task Dispose_rejects_late_result_and_later_start()
    {
        var entered = Source<AkochanReplay>();
        var work = Source<AkochanDecision>();
        int calls = 0;
        var controller = new AiProbeController((_, replay, _) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult(replay);
            return work.Task;
        });
        controller.Start("synthetic-no-files");
        Task completion = controller.Completion;
        var replay = await entered.Task.WaitAsync(Deadline);
        controller.Dispose();
        controller.Start("synthetic-after-unload");
        work.SetResult(Decision(replay));
        await completion.WaitAsync(Deadline);
        Assert.Equal(1, calls);
        Assert.False(controller.Busy);
        Assert.Null(controller.Result);
        Assert.Equal("本地 AI 已卸载。", controller.Status);
        controller.Dispose();
    }

    [Fact]
    public async Task Cleanup_failure_after_dispose_is_logged_with_no_private_exception_text()
    {
        var entered = Source<bool>();
        var work = Source<AkochanDecision>();
        var logs = new ConcurrentQueue<string>();
        var controller = new AiProbeController((_, _, _) => { entered.TrySetResult(true); return work.Task; }, logs.Enqueue);
        controller.Start("synthetic-private-directory");
        Task completion = controller.Completion;
        await entered.Task.WaitAsync(Deadline);
        controller.Dispose();
        work.SetException(new EngineProcessException(EngineProcessErrorCode.TerminationFailed));
        await completion.WaitAsync(Deadline);
        Assert.Null(controller.Result);
        Assert.Equal("本地 AI 已卸载。", controller.Status);
        Assert.Equal("AKOCHAN_TERMINATION_FAILED", Assert.Single(logs));
        Assert.DoesNotContain("synthetic-private", controller.CleanupWarning);
    }

    [Fact]
    public async Task Cleanup_logs_are_bounded_and_logging_failures_do_not_escape()
    {
        int logs = 0;
        using var controller = new AiProbeController((_, _, _) =>
            Task.FromException<AkochanDecision>(new EngineProcessException(EngineProcessErrorCode.TerminationFailed)),
            code => { Assert.Equal("AKOCHAN_TERMINATION_FAILED", code); Interlocked.Increment(ref logs); throw new IOException("synthetic-private-log-error"); });
        for (int attempt = 0; attempt < 12; attempt++)
        {
            controller.Start("synthetic-no-files");
            await controller.Completion.WaitAsync(Deadline);
        }
        Assert.Equal(8, logs);
        Assert.False(controller.Busy);
        Assert.Null(controller.Result);
        Assert.Contains("AKOCHAN_TERMINATION_FAILED", controller.CleanupWarning);
        Assert.DoesNotContain("synthetic-private", controller.CleanupWarning);
    }
}
