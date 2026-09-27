using System;
using System.Threading;

namespace Mahjong.Plugin.Game;

/// <summary>
/// Invalidates delayed work without waiting for an executing action or its native callbacks.
/// Cancellation is cooperative: callers must also check their operation gate immediately
/// before each input. An input that has already begun cannot be retracted by this generation.
/// </summary>
public sealed class QueuedActionGate
{
    private long generation;

    public long Capture() => Volatile.Read(ref generation);

    public void Invalidate() => Interlocked.Increment(ref generation);

    public bool IsCurrent(long capturedGeneration) => Volatile.Read(ref generation) == capturedGeneration;

    /// <summary>Checks cancellation before touching the current runtime or addon state.</summary>
    public bool TryExecute(long capturedGeneration, Func<bool> canExecute, Action action)
    {
        ArgumentNullException.ThrowIfNull(canExecute);
        ArgumentNullException.ThrowIfNull(action);
        if (!IsCurrent(capturedGeneration) || !canExecute()) return false;
        // The predicate may itself stop automation after detecting a read error.
        if (!IsCurrent(capturedGeneration)) return false;
        action();
        return true;
    }
}
