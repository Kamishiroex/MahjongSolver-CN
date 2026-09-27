using System.Text.Json;
using Mahjong.Cn.Engines;
using System.Diagnostics;

if (args.Length < 2) return 2;
try
{
    if (args[0] == "import")
    {
        var imported = await MortalInstallation.ImportAsync(args[1], args[2]);
        Console.WriteLine(imported.Directory);
        return 0;
    }
    var timing = Stopwatch.StartNew();
    var install = await MortalInstallation.LoadAsync(args[0]);
    double validationMs = timing.Elapsed.TotalMilliseconds;
    using var doc = JsonDocument.Parse(File.ReadAllText(args[1]));
    var input = doc.RootElement;
    if (input.TryGetProperty("Cases", out var cases))
    {
        await using var suiteEngine = new MortalGlobalEngine();
        var report = new List<object>();
        foreach (var item in cases.EnumerateArray())
        {
            var sample = item.GetProperty("Snapshot").Deserialize<AkochanGlobalSnapshot>()!;
            var decision = await suiteEngine.AnalyzeAsync(install, sample);
            var types = decision.Candidates.Select(c => c.Moves[0].Type).Distinct().Order().ToArray();
            var required = item.GetProperty("RequiredTypes").EnumerateArray().Select(x => x.GetString() == "ryukyoku" ? "kyushukyuhai" : x.GetString());
            if (required.Except(types).Any()) throw new Exception("Missing expected candidate: " + item.GetProperty("Name").GetString());
            report.Add(new { Name = item.GetProperty("Name").GetString(), Passed = true, decision.InputSha256,
                Types = types, decision.StartToResponseMilliseconds });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Passed = report.Count, Cases = report, CnLiveVerified = false }));
        return 0;
    }
    if (input.TryGetProperty("ActualPublicInput", out var actual)) input = actual;
    var snapshot = input.Deserialize<AkochanGlobalSnapshot>()!;
    await using var engine = new MortalGlobalEngine();
    timing.Restart();
    await engine.PrepareAsync(install);
    double warmupMs = timing.Elapsed.TotalMilliseconds;
    int? preparedProcess = engine.ProcessId;
    var cold = await engine.AnalyzeAsync(install, snapshot);
    var result = await engine.AnalyzeAsync(install, snapshot);
    if (cold.InputSha256 != result.InputSha256) throw new Exception("warm response mismatch");
    if (args.Length > 2 && args[2] == "cancel-check")
    {
        using var cancel = new CancellationTokenSource();
        cancel.CancelAfter(1);
        try { await engine.AnalyzeAsync(install, snapshot, cancel.Token); }
        catch (OperationCanceledException) { }
        catch (EngineProcessException ex) when (ex.Code == EngineProcessErrorCode.Cancelled) { }
        var recovered = await engine.AnalyzeAsync(install, snapshot);
        if (recovered.InputSha256 != result.InputSha256 || recovered.Candidates.Length == 0)
            throw new Exception("cancel/restart mismatch");
        if (preparedProcess != engine.ProcessId) throw new Exception("Cancelled turn restarted a warm model");
    }
    Console.WriteLine(JsonSerializer.Serialize(new { ValidationMilliseconds = validationMs,
        WarmupMilliseconds = warmupMs, FirstDecisionMilliseconds = cold.StartToResponseMilliseconds,
        RepeatDecisionMilliseconds = result.StartToResponseMilliseconds, WarmProcessRetained = preparedProcess == engine.ProcessId,
        Decision = result, CnLiveVerified = false }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
