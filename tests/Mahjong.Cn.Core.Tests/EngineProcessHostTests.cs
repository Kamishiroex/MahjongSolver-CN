using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Mahjong.Cn.Engines;
using Xunit;

namespace Mahjong.Cn.Tests;

/// <summary>Real OS subprocesses running a synthetic test peer, never a Mahjong AI or game client.</summary>
public sealed class EngineProcessHostTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static string Root
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "Mahjong.Cn.Core", "Mahjong.Cn.Core.csproj"))) return directory.FullName;
            throw new InvalidOperationException("The synthetic process tests require the source checkout.");
        }
    }
    private static EngineProcessSpec Spec(string mode = "echo", params string[] arguments)
    {
        string? dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrEmpty(dotnet)) dotnet = Path.Combine(Root, ".work", "dotnet", "dotnet.exe");
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string helper = Path.Combine(Root, "tests", "Mahjong.Cn.EngineProcess.TestHelper", "bin", configuration,
            "net8.0", "Mahjong.Cn.EngineProcess.TestHelper.dll");
        Assert.True(File.Exists(helper), "The test-only helper project must be built with the test project.");
        return new(dotnet, Root, new[] { "--roll-forward", "Major", helper, mode }.Concat(arguments).ToImmutableArray());
    }
    private static string Request(int id) => JsonSerializer.Serialize(new { requestId = id });
    private static Func<string, bool> Match(int id) => response =>
    {
        using var document = JsonDocument.Parse(response);
        return document.RootElement.TryGetProperty("requestId", out var actual) && actual.GetInt32() == id;
    };
    private static async Task UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.Elapsed < Deadline) await Task.Delay(10);
        Assert.True(condition());
    }
    private static bool Exited(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }

    [Fact]
    public async Task Echoes_json_objects_with_correlated_serial_exchanges()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec());
        for (int id = 1; id <= 3; id++)
            Assert.Equal(Request(id), await host.ExchangeAsync(Request(id), Deadline, Match(id)));
        Assert.Equal(EngineProcessState.Running, host.State);
        Assert.Null(host.LastErrorCode);
    }

    [Fact]
    public async Task Valid_multibyte_utf8_is_preserved_without_bom_or_replacement()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec());
        const string request = "{\"requestId\":1,\"text\":\"赤五🀄\"}";
        Assert.Equal(request, await host.ExchangeAsync(request, Deadline, Match(1)));
    }

    [Fact]
    public async Task Batch_sends_all_history_lines_before_waiting_for_one_array_response()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec("batch") with { ResponseRootKind = JsonValueKind.Array });
        string response = await host.ExchangeBatchAsync(new[] { Request(1), Request(2), Request(3) }, Deadline);
        using var document = JsonDocument.Parse(response);
        Assert.Equal(3, document.RootElement[0].GetProperty("requestId").GetInt32());
        Assert.Equal(1, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Batch_has_one_total_byte_budget_not_one_budget_per_line()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec("batch") with { MaxMessageBytes = 30 });
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() =>
            host.ExchangeBatchAsync(new[] { Request(1), Request(2), Request(3) }, Deadline));
        Assert.Equal(EngineProcessErrorCode.MessageTooLarge, exception.Code);
        await UntilAsync(() => Exited(host.ProcessId));
    }

    [Fact]
    public async Task Batch_line_count_is_bounded()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec("batch"));
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() =>
            host.ExchangeBatchAsync(Enumerable.Repeat("{}", 4097).ToArray(), Deadline));
        Assert.Equal(EngineProcessErrorCode.InvalidRequest, exception.Code);
    }

    [Fact]
    public async Task Concurrent_callers_are_serialized_without_crossing_request_ids()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec("delay", "30"));
        var responses = await Task.WhenAll(Enumerable.Range(1, 8)
            .Select(id => host.ExchangeAsync(Request(id), Deadline, Match(id))));
        Assert.Equal(Enumerable.Range(1, 8).Select(Request), responses);
    }

    [Fact]
    public async Task Arguments_are_passed_as_literals_without_shell_interpretation()
    {
        string[] arguments = ["path with spaces", "quotes\"inside", "$(not-a-command)", "& | ; 中文"];
        await using var host = await EngineProcessHost.StartAsync(Spec("arguments", arguments));
        using var response = JsonDocument.Parse(await host.ExchangeAsync("{}", Deadline));
        Assert.Equal(arguments, response.RootElement.GetProperty("values").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public async Task Thread_limits_override_only_the_child_environment()
    {
        string? originalThreads = Environment.GetEnvironmentVariable("OMP_NUM_THREADS");
        string? originalDynamic = Environment.GetEnvironmentVariable("OMP_DYNAMIC");
        var spec = Spec("environment") with
        {
            EnvironmentVariables = ImmutableDictionary<string, string>.Empty
                .Add("OMP_NUM_THREADS", "2").Add("OMP_DYNAMIC", "FALSE"),
        };
        await using var host = await EngineProcessHost.StartAsync(spec);
        using var response = JsonDocument.Parse(await host.ExchangeAsync("{}", Deadline));
        Assert.Equal("2", response.RootElement.GetProperty("threads").GetString());
        Assert.Equal("FALSE", response.RootElement.GetProperty("dynamic").GetString());
        Assert.Equal(originalThreads, Environment.GetEnvironmentVariable("OMP_NUM_THREADS"));
        Assert.Equal(originalDynamic, Environment.GetEnvironmentVariable("OMP_DYNAMIC"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("BAD=KEY")]
    [InlineData("BAD\0KEY")]
    public async Task Invalid_environment_names_are_rejected_before_start(string name)
    {
        var spec = Spec() with { EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add(name, "value") };
        await Assert.ThrowsAsync<ArgumentException>(() => EngineProcessHost.StartAsync(spec));
    }

    [Fact]
    public async Task Environment_value_size_and_pair_count_are_bounded()
    {
        var tooLong = Spec() with { EnvironmentVariables = ImmutableDictionary<string, string>.Empty.Add("KEY", new string('x', 4097)) };
        await Assert.ThrowsAsync<ArgumentException>(() => EngineProcessHost.StartAsync(tooLong));
        var tooMany = Spec() with
        { EnvironmentVariables = Enumerable.Range(0, 33).ToImmutableDictionary(i => "KEY" + i, _ => "value") };
        await Assert.ThrowsAsync<ArgumentException>(() => EngineProcessHost.StartAsync(tooMany));
    }

    [Fact]
    public async Task Stderr_is_drained_and_retained_with_a_fixed_bound()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec("stderr") with { MaxStderrChars = 127 });
        Assert.Equal(Request(1), await host.ExchangeAsync(Request(1), Deadline, Match(1)));
        await UntilAsync(() => host.StderrTruncated);
        Assert.Equal(127, host.RetainedStderrCharacters);
        Assert.Equal(EngineProcessState.Running, host.State);
        Assert.DoesNotContain(typeof(EngineProcessHost).GetProperties(), p => p.PropertyType == typeof(string));
    }

    [Theory]
    [InlineData("invalid-json", EngineProcessErrorCode.InvalidJson)]
    [InlineData("array", EngineProcessErrorCode.InvalidJson)]
    [InlineData("invalid-utf8", EngineProcessErrorCode.InvalidUtf8)]
    [InlineData("eof", EngineProcessErrorCode.EndOfStream)]
    [InlineData("partial-eof", EngineProcessErrorCode.EndOfStream)]
    [InlineData("crash", EngineProcessErrorCode.EndOfStream)]
    public async Task Bad_output_or_exit_faults_and_terminates_the_owned_process(string mode, EngineProcessErrorCode code)
    {
        await using var host = await EngineProcessHost.StartAsync(Spec(mode));
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() => host.ExchangeAsync(Request(1), Deadline, Match(1)));
        Assert.Equal(code, exception.Code);
        Assert.DoesNotContain("synthetic", exception.Message);
        Assert.Equal(EngineProcessState.Faulted, host.State);
        await UntilAsync(() => Exited(host.ProcessId));
        var retry = await Assert.ThrowsAsync<EngineProcessException>(() => host.ExchangeAsync(Request(2), Deadline, Match(2)));
        Assert.Equal(code, retry.Code);
    }

    [Theory]
    [InlineData("long-bytes", 96, 1024)]
    [InlineData("long-chars", 1024, 48)]
    [InlineData("oversized-no-newline", 96, 1024)]
    public async Task Incoming_byte_and_character_limits_are_enforced(string mode, int bytes, int chars)
    {
        await using var host = await EngineProcessHost.StartAsync(Spec(mode) with { MaxMessageBytes = bytes, MaxMessageChars = chars });
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() => host.ExchangeAsync("{}", Deadline));
        Assert.Equal(EngineProcessErrorCode.MessageTooLarge, exception.Code);
        await UntilAsync(() => Exited(host.ProcessId));
    }

    [Theory]
    [InlineData("not-json", EngineProcessErrorCode.InvalidRequest)]
    [InlineData("[]", EngineProcessErrorCode.InvalidRequest)]
    [InlineData("{}\n{}", EngineProcessErrorCode.InvalidRequest)]
    [InlineData("{\"long\":\"12345678901234567890\"}", EngineProcessErrorCode.MessageTooLarge)]
    public async Task Invalid_outgoing_messages_fault_before_writing(string request, EngineProcessErrorCode code)
    {
        await using var host = await EngineProcessHost.StartAsync(Spec() with { MaxMessageBytes = 24 });
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() => host.ExchangeAsync(request, Deadline));
        Assert.Equal(code, exception.Code);
        await UntilAsync(() => Exited(host.ProcessId));
    }

    [Fact]
    public async Task Timeout_kills_process_and_forbids_continuing_its_old_state()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec("hang"));
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() => host.ExchangeAsync(Request(1), TimeSpan.FromMilliseconds(300), Match(1)));
        Assert.Equal(EngineProcessErrorCode.Timeout, exception.Code);
        await UntilAsync(() => Exited(host.ProcessId));
        Assert.Equal(EngineProcessState.Faulted, host.State);
        var retry = await Assert.ThrowsAsync<EngineProcessException>(() => host.ExchangeAsync(Request(2), Deadline));
        Assert.Equal(EngineProcessErrorCode.Timeout, retry.Code);
    }

    [Fact]
    public async Task Cancellation_kills_the_owned_process()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec("hang"));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() => host.ExchangeAsync(Request(1), Deadline, Match(1), cancellation.Token));
        Assert.Equal(EngineProcessErrorCode.Cancelled, exception.Code);
        await UntilAsync(() => Exited(host.ProcessId));
    }

    [Fact]
    public async Task Unsolicited_output_is_never_saved_for_the_next_request()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec("unsolicited"));
        await UntilAsync(() => host.State == EngineProcessState.Faulted);
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() => host.ExchangeAsync("{}", Deadline));
        Assert.Equal(EngineProcessErrorCode.UnexpectedOutput, exception.Code);
    }

    [Fact]
    public async Task Extra_response_line_faults_instead_of_becoming_a_second_response()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec("extra"));
        try { await host.ExchangeAsync(Request(1), Deadline, Match(1)); }
        catch (EngineProcessException exception) { Assert.Equal(EngineProcessErrorCode.UnexpectedOutput, exception.Code); }
        await UntilAsync(() => host.State == EngineProcessState.Faulted);
        Assert.Equal(EngineProcessErrorCode.UnexpectedOutput, host.LastErrorCode);
        await Assert.ThrowsAsync<EngineProcessException>(() => host.ExchangeAsync(Request(2), Deadline, Match(2)));
    }

    [Fact]
    public async Task Echoed_request_id_rejects_a_late_old_response_during_the_next_request()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec("late-extra"));
        Assert.Equal(Request(1), await host.ExchangeAsync(Request(1), Deadline, Match(1)));
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() => host.ExchangeAsync(Request(2), Deadline, Match(2)));
        Assert.Equal(EngineProcessErrorCode.ResponseMismatch, exception.Code);
        await UntilAsync(() => Exited(host.ProcessId));
    }

    [Fact]
    public async Task Matcher_failure_is_isolated_and_terminates_the_process()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec());
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() =>
            host.ExchangeAsync("{}", Deadline, _ => throw new InvalidOperationException("TEST_PRIVATE_MATCHER_DETAIL")));
        Assert.Equal(EngineProcessErrorCode.ResponseMismatch, exception.Code);
        Assert.DoesNotContain("TEST_PRIVATE", exception.Message);
    }

    [Fact]
    public async Task Dispose_cancels_an_inflight_exchange_and_is_idempotent()
    {
        var host = await EngineProcessHost.StartAsync(Spec("hang"));
        var exchange = host.ExchangeAsync(Request(1), Deadline, Match(1));
        await host.DisposeAsync();
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() => exchange);
        Assert.Equal(EngineProcessErrorCode.Disposed, exception.Code);
        Assert.Equal(EngineProcessState.Disposed, host.State);
        Assert.True(Exited(host.ProcessId));
        await host.DisposeAsync();
    }

    [Fact]
    public async Task Noncooperative_matcher_cannot_make_disposal_wait_forever()
    {
        var host = await EngineProcessHost.StartAsync(Spec() with { ShutdownTimeout = TimeSpan.FromMilliseconds(100) });
        using var release = new ManualResetEventSlim();
        var matcherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exchange = host.ExchangeAsync("{}", Deadline, _ =>
        {
            matcherStarted.TrySetResult();
            release.Wait();
            return true;
        });
        try
        {
            await matcherStarted.Task.WaitAsync(Deadline);
            var disposal = host.DisposeAsync().AsTask();
            var exception = await Assert.ThrowsAsync<EngineProcessException>(() => disposal.WaitAsync(Deadline));
            Assert.Equal(EngineProcessErrorCode.TerminationFailed, exception.Code);
            Assert.Equal(EngineProcessState.TerminationFailed, host.State);
            Assert.Equal(EngineProcessErrorCode.TerminationFailed, host.LastErrorCode);
            await UntilAsync(() => Exited(host.ProcessId));
            var cancelled = await Assert.ThrowsAsync<EngineProcessException>(() => exchange);
            Assert.Equal(EngineProcessErrorCode.Disposed, cancelled.Code);
        }
        finally
        {
            release.Set();
            try { await host.DisposeAsync(); } catch (EngineProcessException) { }
            if (!Exited(host.ProcessId))
            {
                using var process = Process.GetProcessById(host.ProcessId);
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public async Task Timeout_terminates_a_descendant_owned_by_the_helper()
    {
        await using var host = await EngineProcessHost.StartAsync(Spec("descendant"));
        using var response = JsonDocument.Parse(await host.ExchangeAsync("{}", Deadline));
        int childPid = response.RootElement.GetProperty("childPid").GetInt32();
        try
        {
            Assert.False(Exited(childPid));
            await Assert.ThrowsAsync<EngineProcessException>(() => host.ExchangeAsync("{}", TimeSpan.FromMilliseconds(300)));
            await UntilAsync(() => Exited(host.ProcessId) && Exited(childPid));
        }
        finally
        {
            // A failing test must not strand its synthetic child on the developer machine.
            if (!Exited(childPid)) { using var child = Process.GetProcessById(childPid); child.Kill(entireProcessTree: true); }
        }
    }

    [Fact]
    public async Task Start_failure_has_a_structured_code_without_private_path_text()
    {
        var spec = Spec() with { ExecutablePath = Path.Combine(Root, "does-not-exist-engine.exe") };
        var exception = await Assert.ThrowsAsync<EngineProcessException>(() => EngineProcessHost.StartAsync(spec));
        Assert.Equal(EngineProcessErrorCode.StartFailed, exception.Code);
        Assert.DoesNotContain(Root, exception.Message);
    }
}
