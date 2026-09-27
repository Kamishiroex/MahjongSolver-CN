using Mahjong.Cn.Engines;
using Xunit;

namespace Mahjong.Cn.Core.Tests;

public sealed class DecisionCoordinatorTests
{
    private static DecisionContext Context(int revision = 1) => new(Guid.NewGuid(), "offline-round", revision,
        "offline-window", 2, DateTimeOffset.UtcNow.AddSeconds(20), new string('A', 64));

    [Fact]
    public async Task New_request_discards_late_result_even_when_backend_ignores_cancellation()
    {
        using var coordinator = new DecisionCoordinator<string>();
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = coordinator.SubmitAsync(Context(), _ => delayed.Task);
        var currentContext = Context(2);
        var fresh = await coordinator.SubmitAsync(currentContext, _ => Task.FromResult("new"));
        delayed.SetResult("old");
        Assert.Null(await old);
        Assert.Equal("new", coordinator.Current!.Value);
        Assert.Same(fresh, coordinator.Current);
        Assert.Equal(currentContext, fresh!.Context);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_and_dispose_clear_results_and_prevent_late_publication(bool dispose)
    {
        using var coordinator = new DecisionCoordinator<string>();
        var delayed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken received = default;
        var task = coordinator.SubmitAsync(Context(), token => { received = token; return delayed.Task; });
        if (dispose) coordinator.Dispose(); else coordinator.Invalidate();
        Assert.True(received.IsCancellationRequested);
        delayed.SetResult("late");
        Assert.Null(await task);
        Assert.Null(coordinator.Current);
    }

    [Fact]
    public async Task Already_expired_context_does_not_start_engine()
    {
        using var coordinator = new DecisionCoordinator<int>();
        bool started = false;
        var result = await coordinator.SubmitAsync(Context() with { Deadline = DateTimeOffset.UtcNow.AddSeconds(-1) },
            _ => { started = true; return Task.FromResult(1); });
        Assert.Null(result);
        Assert.False(started);
    }

    [Fact]
    public async Task Deadline_is_rechecked_after_noncooperative_result()
    {
        var now = DateTimeOffset.UtcNow;
        using var coordinator = new DecisionCoordinator<int>(() => now);
        var context = Context() with { Deadline = now.AddSeconds(5) };
        Assert.Null(await coordinator.SubmitAsync(context, _ => { now = now.AddSeconds(6); return Task.FromResult(1); }));
    }

    [Fact]
    public async Task Failure_does_not_retain_previous_suggestion_or_fall_back()
    {
        using var coordinator = new DecisionCoordinator<int>();
        await coordinator.SubmitAsync(Context(), _ => Task.FromResult(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SubmitAsync(Context(2),
            _ => Task.FromException<int>(new InvalidOperationException())));
        Assert.Null(coordinator.Current);
    }

    [Theory]
    [InlineData(EngineProcessErrorCode.Cancelled)]
    [InlineData(EngineProcessErrorCode.Timeout)]
    public async Task Host_cancellation_does_not_surface_as_a_new_stale_error(EngineProcessErrorCode code)
    {
        using var coordinator = new DecisionCoordinator<int>();
        var result = await coordinator.SubmitAsync(Context(), _ =>
        {
            coordinator.Invalidate();
            return Task.FromException<int>(new EngineProcessException(code));
        });
        Assert.Null(result);
        Assert.Null(coordinator.Current);
    }
}
