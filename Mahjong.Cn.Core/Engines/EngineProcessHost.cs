using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Mahjong.Cn.Engines;

public enum EngineProcessState { Running, Faulted, Disposed, TerminationFailed }
public enum EngineProcessErrorCode
{
    StartFailed, InvalidRequest, MessageTooLarge, InvalidUtf8, InvalidJson, UnexpectedOutput,
    ResponseMismatch, EndOfStream, TransportFailure, Timeout, Cancelled, Disposed, TerminationFailed,
}

/// <summary>Contains only a structured code; process output is never part of the message.</summary>
public sealed class EngineProcessException(EngineProcessErrorCode code) : Exception($"Engine process failed: {code}.")
{
    public EngineProcessErrorCode Code { get; } = code;
}

public sealed record EngineProcessSpec(string ExecutablePath, string WorkingDirectory, ImmutableArray<string> Arguments)
{
    public int MaxMessageBytes { get; init; } = 1024 * 1024;
    public int MaxMessageChars { get; init; } = 1024 * 1024;
    public int MaxStderrChars { get; init; } = 8192;
    public JsonValueKind ResponseRootKind { get; init; } = JsonValueKind.Object;
    public ImmutableDictionary<string, string> EnvironmentVariables { get; init; } = ImmutableDictionary<string, string>.Empty;
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Owns one process and one serialized JSONL conversation. No restart or protocol repair occurs.
/// A bridge protocol should echo a request ID and supply a response matcher: JSONL alone cannot
/// distinguish a delayed old response from the next request's response without correlation data.
/// </summary>
public sealed class EngineProcessHost : IAsyncDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Process process;
    private readonly EngineProcessSpec spec;
    private readonly SemaphoreSlim exchangeGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object sync = new();
    private readonly Task stdoutPump;
    private readonly Task stderrPump;
    private readonly StringBuilder stderr = new();
    private PendingExchange? pending;
    private EngineProcessState state = EngineProcessState.Running;
    private EngineProcessException? failure;
    private int stderrTruncated;
    private Task? disposal;
    private Task<bool>? termination;

    private EngineProcessHost(Process process, EngineProcessSpec spec)
    {
        this.process = process;
        this.spec = spec;
        ProcessId = process.Id;
        stdoutPump = Task.Run(ReadStdoutAsync);
        stderrPump = Task.Run(DrainStderrAsync);
    }

    public int ProcessId { get; }
    public EngineProcessState State { get { lock (sync) return state; } }
    public EngineProcessErrorCode? LastErrorCode { get { lock (sync) return failure?.Code; } }
    public int RetainedStderrCharacters { get { lock (sync) return stderr.Length; } }
    public bool StderrTruncated => Volatile.Read(ref stderrTruncated) != 0;

    public static async Task<EngineProcessHost> StartAsync(EngineProcessSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (!Path.IsPathFullyQualified(spec.ExecutablePath) || !Path.IsPathFullyQualified(spec.WorkingDirectory)
            || spec.Arguments.IsDefault || spec.Arguments.Any(a => a is null)
            || spec.MaxMessageBytes is < 1 or > 16 * 1024 * 1024
            || spec.MaxMessageChars is < 1 or > 16 * 1024 * 1024
            || spec.MaxStderrChars is < 0 or > 1024 * 1024
            || spec.ShutdownTimeout <= TimeSpan.Zero || spec.ShutdownTimeout > TimeSpan.FromSeconds(30)
            || spec.ResponseRootKind is not (JsonValueKind.Object or JsonValueKind.Array)
            || spec.EnvironmentVariables is null || spec.EnvironmentVariables.Count > 32
            || spec.EnvironmentVariables.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != spec.EnvironmentVariables.Count
            || spec.EnvironmentVariables.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 256
                || pair.Key.IndexOfAny(['=', '\0']) >= 0 || pair.Value is null || pair.Value.Length > 4096 || pair.Value.Contains('\0')))
            throw new ArgumentException("Engine paths must be absolute and limits must be bounded.", nameof(spec));
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo
        {
            FileName = spec.ExecutablePath, WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = StrictUtf8, StandardOutputEncoding = StrictUtf8,
            StandardErrorEncoding = new UTF8Encoding(false, false),
        };
        foreach (string argument in spec.Arguments) info.ArgumentList.Add(argument);
        // Child-only overrides. Never mutate the plugin/game process's environment.
        foreach (var pair in spec.EnvironmentVariables) info.Environment[pair.Key] = pair.Value;
        var process = new Process { StartInfo = info };
        EngineProcessHost? host = null;
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!process.Start()) throw new EngineProcessException(EngineProcessErrorCode.StartFailed);
            }, cancellationToken).ConfigureAwait(false);
            host = new EngineProcessHost(process, spec);
            if (cancellationToken.IsCancellationRequested)
            {
                await host.DisposeAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            return host;
        }
        catch (OperationCanceledException)
        {
            // Once constructed, the host owns both bounded cleanup and any deferred kill.
            if (host is null) { TryKill(process); process.Dispose(); }
            throw;
        }
        catch (EngineProcessException ex) when (host is not null && ex.Code == EngineProcessErrorCode.TerminationFailed)
        {
            // Cancellation after startup can fail during DisposeAsync. Preserve that lifecycle
            // error and its deferred handle ownership instead of relabeling it StartFailed.
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (host is null) { TryKill(process); process.Dispose(); }
            else await host.DisposeAsync().ConfigureAwait(false);
            throw new EngineProcessException(EngineProcessErrorCode.StartFailed);
        }
    }

    public Task<string> ExchangeAsync(string request, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        ExchangeAsync(request, timeout, null, cancellationToken);

    /// <summary>
    /// The matcher must validate the bridge's echoed correlation ID. It runs on the background stdout
    /// reader and must be fast and side-effect free. Any matcher exception invalidates the process.
    /// The deadline includes time spent waiting behind another exchange.
    /// </summary>
    public Task<string> ExchangeAsync(string request, TimeSpan timeout, Func<string, bool>? responseMatcher,
        CancellationToken cancellationToken = default) =>
        ExchangeBatchAsync(new[] { request }, timeout, responseMatcher, cancellationToken);

    public Task<string> ExchangeBatchAsync(IReadOnlyList<string> requests, TimeSpan timeout,
        CancellationToken cancellationToken = default) => ExchangeBatchAsync(requests, timeout, null, cancellationToken);

    /// <summary>Sends at most 4096 JSON object lines as one bounded transaction, then expects exactly one response.</summary>
    public async Task<string> ExchangeBatchAsync(IReadOnlyList<string> requests, TimeSpan timeout,
        Func<string, bool>? responseMatcher, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        byte[] bytes;
        try
        {
            if (requests is null || requests.Count is < 1 or > 4096)
                throw new EngineProcessException(EngineProcessErrorCode.InvalidRequest);
            using var payload = new MemoryStream();
            long characters = 0;
            foreach (string request in requests)
            {
                if (request is null || request.Contains('\n') || request.Contains('\r'))
                    throw new EngineProcessException(EngineProcessErrorCode.InvalidRequest);
                characters += request.Length + 1L;
                if (characters > spec.MaxMessageChars || payload.Length + StrictUtf8.GetByteCount(request) + 1L > spec.MaxMessageBytes)
                    throw new EngineProcessException(EngineProcessErrorCode.MessageTooLarge);
                if (!IsJsonKind(request, JsonValueKind.Object))
                    throw new EngineProcessException(EngineProcessErrorCode.InvalidRequest);
                payload.Write(StrictUtf8.GetBytes(request));
                payload.WriteByte((byte)'\n');
            }
            bytes = payload.ToArray();
        }
        catch (EngineProcessException ex) { Fail(ex.Code); throw CurrentFailure(); }
        catch (EncoderFallbackException) { Fail(EngineProcessErrorCode.InvalidUtf8); throw CurrentFailure(); }

        using var deadline = new CancellationTokenSource(timeout);
        using var deadlineRegistration = deadline.Token.Register(() => Fail(EngineProcessErrorCode.Timeout));
        using var cancellationRegistration = cancellationToken.Register(() => Fail(EngineProcessErrorCode.Cancelled));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken, deadline.Token);
        bool acquired = false;
        PendingExchange? exchange = null;
        try
        {
            await exchangeGate.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            lock (sync)
            {
                ThrowUnlessRunning();
                exchange = new PendingExchange(responseMatcher);
                pending = exchange;
            }
            await process.StandardInput.BaseStream.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(linked.Token).ConfigureAwait(false);
            string response = await exchange.Completion.Task.ConfigureAwait(false);
            lock (sync) ThrowUnlessRunning();
            return response;
        }
        catch (EngineProcessException) { throw; }
        catch (OperationCanceledException)
        {
            Fail(cancellationToken.IsCancellationRequested ? EngineProcessErrorCode.Cancelled : EngineProcessErrorCode.Timeout);
            throw CurrentFailure();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Fail(EngineProcessErrorCode.TransportFailure);
            throw CurrentFailure();
        }
        finally
        {
            if (exchange is not null)
            {
                lock (sync) if (ReferenceEquals(pending, exchange)) pending = null;
                // A write failure can fault the completion before the await was reached.
                if (exchange.Completion.Task.IsFaulted) _ = exchange.Completion.Task.Exception;
            }
            if (acquired) exchangeGate.Release();
        }
    }

    private async Task ReadStdoutAsync()
    {
        var buffer = new byte[4096];
        var line = new byte[spec.MaxMessageBytes + 1];
        int length = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                int count = await process.StandardOutput.BaseStream.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (count == 0) { Fail(EngineProcessErrorCode.EndOfStream); return; }
                int offset = 0;
                PendingExchange? completed = null;
                while (offset < count)
                {
                    lock (sync)
                    {
                        if (state != EngineProcessState.Running) return;
                        if (pending is null || pending.Response is not null)
                        { Fail(EngineProcessErrorCode.UnexpectedOutput); return; }
                    }
                    int newline = Array.IndexOf(buffer, (byte)'\n', offset, count - offset);
                    int end = newline < 0 ? count : newline;
                    int segmentLength = end - offset;
                    if (segmentLength > line.Length - length)
                    { Fail(EngineProcessErrorCode.MessageTooLarge); return; }
                    Buffer.BlockCopy(buffer, offset, line, length, segmentLength);
                    length += segmentLength;
                    offset = newline < 0 ? count : newline + 1;
                    if (newline < 0)
                    {
                        // One optional CR may precede the not-yet-received LF. Any other
                        // byte above the limit is already oversized; do not wait for EOF.
                        if (length > spec.MaxMessageBytes && line[length - 1] != '\r')
                        { Fail(EngineProcessErrorCode.MessageTooLarge); return; }
                        continue;
                    }
                    int payloadLength = length > 0 && line[length - 1] == '\r' ? length - 1 : length;
                    if (payloadLength > spec.MaxMessageBytes) { Fail(EngineProcessErrorCode.MessageTooLarge); return; }
                    string text = StrictUtf8.GetString(line, 0, payloadLength);
                    length = 0;
                    if (text.Length > spec.MaxMessageChars) { Fail(EngineProcessErrorCode.MessageTooLarge); return; }
                    if (!IsJsonKind(text, spec.ResponseRootKind)) { Fail(EngineProcessErrorCode.InvalidJson); return; }
                    PendingExchange candidate;
                    lock (sync)
                    {
                        if (state != EngineProcessState.Running) return;
                        if (pending is null || pending.Response is not null)
                        { Fail(EngineProcessErrorCode.UnexpectedOutput); return; }
                        candidate = pending;
                    }
                    // Foreign matcher code must not hold the lifecycle lock: even a broken
                    // matcher cannot prevent cancellation or the bounded disposal deadline.
                    try
                    {
                        if (candidate.Matcher is not null && !candidate.Matcher(text))
                        { Fail(EngineProcessErrorCode.ResponseMismatch); return; }
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    { Fail(EngineProcessErrorCode.ResponseMismatch); return; }
                    lock (sync)
                    {
                        if (state != EngineProcessState.Running) return;
                        if (!ReferenceEquals(pending, candidate) || candidate.Response is not null)
                        { Fail(EngineProcessErrorCode.UnexpectedOutput); return; }
                        candidate.Response = text;
                        completed = candidate;
                    }
                }
                // Process the entire received byte batch before releasing its response. A second
                // line or partial extra line in that batch cannot become the next exchange's input.
                if (completed is not null)
                    lock (sync)
                        if (state == EngineProcessState.Running && ReferenceEquals(pending, completed))
                            completed.Completion.TrySetResult(completed.Response!);
            }
        }
        catch (DecoderFallbackException) { Fail(EngineProcessErrorCode.InvalidUtf8); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(EngineProcessErrorCode.TransportFailure); }
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new char[2048];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                int count = await process.StandardError.ReadAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false);
                if (count == 0) return;
                lock (sync)
                {
                    int retained = Math.Min(count, spec.MaxStderrChars - stderr.Length);
                    if (retained > 0) stderr.Append(buffer, 0, retained);
                    if (retained < count) Volatile.Write(ref stderrTruncated, 1);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Fail(EngineProcessErrorCode.TransportFailure); }
    }

    private static bool IsJsonKind(string text, JsonValueKind rootKind)
    {
        try { using var document = JsonDocument.Parse(text); return document.RootElement.ValueKind == rootKind; }
        catch (JsonException) { return false; }
    }

    private void ThrowUnlessRunning()
    {
        if (state != EngineProcessState.Running) throw failure ?? new EngineProcessException(EngineProcessErrorCode.Disposed);
    }

    private EngineProcessException CurrentFailure()
    { lock (sync) return failure ?? new EngineProcessException(EngineProcessErrorCode.TransportFailure); }

    private void Fail(EngineProcessErrorCode code)
    {
        lock (sync)
        {
            if (state != EngineProcessState.Running) return;
            failure = new(code);
            state = EngineProcessState.Faulted;
            pending?.Completion.TrySetException(failure);
        }
        lifetime.Cancel();
        _ = TerminateAsync();
    }

    private Task<bool> TerminateAsync()
    {
        // Cancellation can be requested by the framework thread. OS process-tree enumeration
        // and termination therefore run in the background, while state becomes invalid at once.
        lock (sync) return termination ??= Task.Run(() => TryKill(process));
    }

    private static bool TryKill(Process process)
    {
        // This is best-effort tree enumeration while the owned parent is alive, not a
        // Windows Job. Detached descendants of an already-exited parent are not tracked.
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); return true; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { return false; }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync) return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        lock (sync)
        {
            if (state == EngineProcessState.Disposed) return;
            failure ??= new(EngineProcessErrorCode.Disposed);
            state = EngineProcessState.Disposed;
            pending?.Completion.TrySetException(failure);
        }
        lifetime.Cancel();
        using var deadline = new CancellationTokenSource(spec.ShutdownTimeout);
        var terminationTask = TerminateAsync();
        try
        {
            var processExit = process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(terminationTask, stdoutPump, stderrPump, processExit)
                .WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!await terminationTask.ConfigureAwait(false)) throw new EngineProcessException(EngineProcessErrorCode.TerminationFailed);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            lock (sync)
            {
                state = EngineProcessState.TerminationFailed;
                failure = new(EngineProcessErrorCode.TerminationFailed);
            }
            throw new EngineProcessException(EngineProcessErrorCode.TerminationFailed);
        }
        finally
        {
            // A deadline can expire before the background kill has been scheduled by the OS.
            // Keep its process handle usable until that already-owned termination attempt ends.
            if (terminationTask.IsCompleted) process.Dispose();
            else _ = DisposeProcessAfterTerminationAsync(terminationTask);
        }
        // Do not dispose the gate/CTS while ExchangeAsync finally blocks may still be unwinding.
    }

    private async Task DisposeProcessAfterTerminationAsync(Task<bool> terminationTask)
    {
        try { await terminationTask.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* Already reported as TerminationFailed. */ }
        finally { process.Dispose(); }
    }

    private sealed class PendingExchange(Func<string, bool>? matcher)
    {
        internal readonly TaskCompletionSource<string> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Func<string, bool>? Matcher = matcher;
        internal string? Response;
    }
}
