using System.Text.Json;
using Mahjong.Cn.Engines;
using System.Text.Json.Serialization;

if (args.Length == 4 && args[0] == "verify-bundle")
{
    string config = Path.GetFullPath(args[2]);
    if (Directory.Exists(config)) throw new IOException("Fresh verification config must not already exist.");
    var profiles = await EngineLibrary.ImportBundleAsync(Path.GetFullPath(args[1]), config, []);
    if (profiles.Length != 2 || profiles.Select(p => p.Backend).Distinct().Count() != 2)
        throw new Exception("Both backends required.");
    using var json = JsonDocument.Parse(File.ReadAllText(args[3]));
    var input = json.RootElement.TryGetProperty("ActualPublicInput", out var actual) ? actual : json.RootElement;
    var snapshot = input.Deserialize<AkochanGlobalSnapshot>(new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } })!;
    var results = new List<object>();
    foreach (var profile in profiles)
    {
        EngineLibrary.Save(config, new(profile.Id, profiles));
        var reloaded = EngineLibrary.Load(config);
        var selected = reloaded.Profiles.Single(p => p.Id == reloaded.SelectedId);
        await selected.ValidateAsync();
        AkochanGlobalDecision decision;
        if (selected.IsMortal)
        {
            await using var engine = new MortalGlobalEngine();
            var install = await MortalInstallation.LoadAsync(selected.Directory);
            await engine.AnalyzeAsync(install, snapshot);
            decision = await engine.AnalyzeAsync(install, snapshot);
        }
        else decision = await new AkochanGlobalEngine().AnalyzeAsync(await AkochanInstallation.LoadAsync(selected.Directory), snapshot, TimeSpan.FromSeconds(60));
        if (decision.Candidates.Length == 0) throw new Exception("Empty engine decision");
        results.Add(new { selected.Backend, selected.Id, selected.Name, decision.EngineName,
            Candidates = decision.Candidates.Length, Moves = decision.Candidates[0].Moves.Select(m => m.Type).ToArray(), decision.InputSha256 });
    }
    // Re-importing the bundle must reuse both validated installations rather than
    // losing a model or forcing the user to swap physical directories.
    var again = await EngineLibrary.ImportBundleAsync(Path.GetFullPath(args[1]), config, EngineLibrary.Load(config).Profiles);
    if (!again.Select(p => p.Directory).SequenceEqual(profiles.Select(p => p.Directory))) throw new Exception("Repeated import duplicated models");
    Console.WriteLine(JsonSerializer.Serialize(new { Passed = true, FreshConfig = true, Profiles = results,
        SelectionSurvivedReload = true, ReimportReusedInstallations = true, GameInputSubmitted = false }));
    return 0;
}

if (args.Length != 3 || args[0] is not ("export" or "import"))
{
    Console.Error.WriteLine("Personal local transfer only: export <engine directory> <new zip> | import <zip> <engines directory>");
    return 2;
}
try
{
    if (args[0] == "export")
        Console.WriteLine(await AkochanTransfer.ExportAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])));
    else
    {
        var installed = await AkochanTransfer.ImportAsync(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        Console.WriteLine(JsonSerializer.Serialize(new { installed.Directory, installed.ManifestSha256, installed.HasObservedFuritenBridge }));
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex is AkochanException ako ? ako.Code : ex.GetType().Name);
    return 1;
}
