using System.Text.Json;
using Mahjong.Cn.Engines;

// Offline only. This tool does not inspect the running game or infer a missing event history.
try
{
    if (args.Length is not (1 or 3))
    {
        Console.Error.WriteLine("Usage: Mahjong.Akochan.Replay <engine-directory> [<public-events.jsonl> <player-id>]");
        return 2;
    }
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var installation = await AkochanInstallation.LoadAsync(Path.GetFullPath(args[0]), cancellation.Token);
    var replay = args.Length == 1 ? AkochanSamples.PublicOpening() :
        new FileInfo(args[1]).Length <= 1024 * 1024
            ? AkochanReplay.Create(File.ReadLines(args[1]), int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), "user-import", false)
            : throw new AkochanException("AKOCHAN_INPUT_TOO_LARGE");
    var result = await new AkochanDecisionEngine().AnalyzeOfflineAsync(installation, replay, TimeSpan.FromSeconds(60), cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex)
{
    string code = ex switch
    {
        AkochanException a => a.Code,
        AkochanProtocolException p => p.Code,
        EngineProcessException p => p.Code.ToString(),
        OperationCanceledException => "CANCELLED",
        _ => ex.GetType().Name,
    };
    Console.Error.WriteLine(JsonSerializer.Serialize(new { error = code, liveGameInput = false }));
    return 1;
}
