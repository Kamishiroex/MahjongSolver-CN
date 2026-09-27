namespace Mahjong.Cn.Engines;

/// <summary>Caller-owned context. A session ID is not an inferred Mahjong round ID.</summary>
public sealed record DecisionContext(Guid SessionId, string RoundId, long StateRevision,
    string DecisionWindowId, long LastEventSequence, DateTimeOffset Deadline, string InputSha256);

public sealed record CoordinatedDecision<T>(DecisionContext Context, long Generation, T Value);

/// <summary>
/// Cancels obsolete work and publishes only a still-current result. Does not read game state,
/// invent events, select an engine, validate a game's action menu or dispatch any inputs.
/// </summary>
public sealed class DecisionCoordinator<T> : IDisposable
{
    private readonly object gate = new();
    private readonly Func<DateTimeOffset> clock;
    private CancellationTokenSource? active;
    private long generation;
    private bool disposed;
    private CoordinatedDecision<T>? current;
    public CoordinatedDecision<T>? Current { get { lock (gate) return current; } }

    public DecisionCoordinator(Func<DateTimeOffset>? clock = null) => this.clock = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<CoordinatedDecision<T>?> SubmitAsync(DecisionContext context,
        Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);
        if (context.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(context.RoundId) ||
            string.IsNullOrWhiteSpace(context.DecisionWindowId) || context.StateRevision < 0 ||
            context.LastEventSequence < 0 || context.InputSha256.Length != 64 || !context.InputSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A complete immutable decision context is required.", nameof(context));
        CancellationTokenSource token;
        CancellationTokenSource? old;
        long mine;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            old = active;
            current = null;
            mine = ++generation;
            token = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            active = token;
        }
        Cancel(old);
        try
        {
            TimeSpan remaining = context.Deadline - clock();
            if (remaining <= TimeSpan.Zero) return null;
            token.CancelAfter(remaining);
            var result = await work(token.Token).ConfigureAwait(false);
            lock (gate)
            {
                if (disposed || mine != generation || token.IsCancellationRequested || clock() >= context.Deadline)
                    return null;
                return current = new(context, mine, result);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return null; }
        catch (EngineProcessException ex) when (token.IsCancellationRequested &&
            ex.Code is EngineProcessErrorCode.Cancelled or EngineProcessErrorCode.Timeout) { return null; }
        finally
        {
            lock (gate) if (ReferenceEquals(active, token)) active = null;
            token.Dispose();
        }
    }

    public void Invalidate()
    {
        CancellationTokenSource? old;
        lock (gate) { ++generation; current = null; old = active; active = null; }
        Cancel(old);
    }

    public void Dispose()
    {
        lock (gate) disposed = true;
        Invalidate();
    }

    private static void Cancel(CancellationTokenSource? token)
    {
        try { token?.Cancel(); }
        catch (ObjectDisposedException) { /* Completed between ownership transfer and cancellation. */ }
    }
}
