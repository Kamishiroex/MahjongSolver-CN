using Mahjong.Plugin.Game;
using Xunit;

namespace Mahjong.Plugin.Game.Tests;

public sealed class QueuedActionGateTests
{
    [Fact]
    public void StopThenRearmCannotRevivePreviouslyQueuedInput()
    {
        var gate = new QueuedActionGate();
        long old = gate.Capture();
        gate.Invalidate();
        bool touchedDisposedState = false;
        int dispatched = 0;
        Assert.False(gate.TryExecute(old, () => { touchedDisposedState = true; return true; }, () => dispatched++));
        Assert.False(touchedDisposedState);
        Assert.True(gate.TryExecute(gate.Capture(), () => true, () => dispatched++));
        Assert.Equal(1, dispatched);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void ChangedArmingRuntimeOrContextPreventsTheQueuedInput(bool armed, bool runtime, bool context)
    {
        var gate = new QueuedActionGate();
        int dispatched = 0;
        Assert.False(gate.TryExecute(gate.Capture(), () => armed && runtime && context, () => dispatched++));
        Assert.Equal(0, dispatched);
    }

    [Fact]
    public void ReadFailureThatStopsDuringValidationCannotDispatch()
    {
        var gate = new QueuedActionGate();
        long pending = gate.Capture();
        Assert.False(gate.TryExecute(pending, () => { gate.Invalidate(); return true; },
            () => throw new Exception("A canceled input ran.")));
        Assert.False(gate.IsCurrent(pending));
    }

    [Fact]
    public void StopWithinAnExecutingActionInvalidatesOtherWork()
    {
        var gate = new QueuedActionGate();
        long pending = gate.Capture();
        Assert.True(gate.TryExecute(pending, () => true, gate.Invalidate));
        Assert.False(gate.TryExecute(pending, () => true, () => throw new Exception("Old work resumed.")));
    }

    [Fact]
    public void ThrowingActionReleasesTheGateForStop()
    {
        var gate = new QueuedActionGate();
        long pending = gate.Capture();
        Assert.Throws<InvalidOperationException>(() => gate.TryExecute(pending, () => true,
            () => throw new InvalidOperationException("synthetic dispatch error")));
        gate.Invalidate();
        Assert.False(gate.IsCurrent(pending));
    }

    [Fact]
    public async Task CancellationDoesNotWaitForAnExecutingCallbackThatNeedsTheOwnersLock()
    {
        var gate = new QueuedActionGate();
        var ownerLock = new object();
        var ownerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseOwner = new ManualResetEventSlim();
        long pending = gate.Capture();
        var owner = Task.Run(() =>
        {
            lock (ownerLock)
            {
                ownerEntered.SetResult();
                releaseOwner.Wait(TimeSpan.FromSeconds(20));
            }
        });
        Task<bool>? execution = null;
        try
        {
            await ownerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            execution = Task.Run(() => gate.TryExecute(pending, () => true, () =>
            {
                callbackEntered.SetResult();
                // Models a native callback reentering the plugin's lifecycle owner lock.
                lock (ownerLock) { }
            }));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A bounded wait fails rather than hanging the suite if lock inversion returns.
            await Task.Run(gate.Invalidate).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(gate.IsCurrent(pending));
        }
        finally { releaseOwner.Set(); }
        await owner.WaitAsync(TimeSpan.FromSeconds(5));
        // Already-started work may finish. Canceling the generation is not an input rollback.
        Assert.NotNull(execution);
        Assert.True(await execution.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(gate.TryExecute(pending, () => true, () => throw new Exception("Old work resumed.")));
    }
}
