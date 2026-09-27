using System.IO.Compression;
using System.Text.Json;
using Mahjong.Cn.Engines;

namespace Mahjong.Plugin.CN;

public sealed partial class Plugin
{
    private string? selectedEngineDirectory;
    private string? mortalEngineDirectory;
    private string? akochanEngineDirectory;
    internal bool MortalSelected { get; private set; }
    private string? selectedEngineName;
    internal string GlobalBackendLabel => selectedEngineName ?? (MortalSelected ? "凡夫 Mortal V4" : "akochan v5");
    internal EngineProfile[] InstalledEngines { get; private set; } = [];
    internal string? SelectedEngineId { get; private set; }
    private sealed record CheckedEngine(string Directory, bool Mortal, EngineProfile[]? Profiles = null);
    private Task<(CheckedEngine? Installation, string Message)>? engineMaintenance;
    private bool applyEngineAfterCheck;
    private readonly CancellationTokenSource engineMaintenanceCancellation = new();
    internal bool EngineMaintenanceBusy => engineMaintenance is not null;
    internal string EngineInstallationStatus { get; private set; } = "尚未检查。";
    internal string EngineTransferPath = "";
    internal string EngineImportName = "";
    internal string DefaultGlobalEngineDirectory => selectedEngineDirectory ?? AkochanTransfer.Locate(Interface.GetPluginConfigDirectory());
    private string EngineSettingsPath => Path.Combine(Interface.GetPluginConfigDirectory(), "engine-settings.json");
    internal bool SelectedEngineInstalled => new EngineProfile("selected", GlobalBackendLabel,
        MortalSelected ? "Mortal" : "Akochan", DefaultGlobalEngineDirectory).IsInstalled;
    internal string? EngineActivationError => EngineMaintenanceBusy ? "AI 初始化或校验尚未完成，请稍候。" :
        SelectedEngineInstalled ? null : GlobalBackendLabel + " 尚未就绪，请查看 AI 设置中的安装状态。";

    private void LoadEngineSettings()
    {
        try
        {
            if (File.Exists(EngineSettingsPath))
            {
                if (new FileInfo(EngineSettingsPath).Length > 16384) throw new IOException("Settings too large");
                var settings = JsonSerializer.Deserialize<EngineSettings>(File.ReadAllText(EngineSettingsPath));
                if (settings?.Directory is { Length: > 0 } path && Path.IsPathFullyQualified(path))
                {
                    selectedEngineDirectory = path;
                    MortalSelected = settings.Backend == "Mortal";
                    mortalEngineDirectory = MortalSelected ? path : settings.MortalDirectory;
                    akochanEngineDirectory = MortalSelected ? settings.AkochanDirectory : path;
                }
            }
        }
        catch (Exception ex) { EngineInstallationStatus = "引擎设置读取失败：" + ex.GetType().Name; }
        try
        {
            string root = Interface.GetPluginConfigDirectory();
            var library = EngineLibrary.Load(root);
            InstalledEngines = library.Profiles;
            string engines = Path.Combine(root, "engines");
            var paths = new[] { selectedEngineDirectory, mortalEngineDirectory, akochanEngineDirectory, AkochanTransfer.Locate(root) }
                .Concat(Directory.Exists(engines) ? Directory.EnumerateDirectories(engines) : []).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (InstalledEngines.Any(p => string.Equals(p.Directory, path, StringComparison.OrdinalIgnoreCase))) continue;
                var profile = EngineProfile.FromDirectory(path);
                if (profile.IsInstalled) InstalledEngines = EngineLibrary.Merge(InstalledEngines, profile);
            }
            var chosen = InstalledEngines.FirstOrDefault(p => p.Id == library.SelectedId)
                ?? InstalledEngines.FirstOrDefault(p => string.Equals(p.Directory, selectedEngineDirectory, StringComparison.OrdinalIgnoreCase))
                ?? InstalledEngines.FirstOrDefault(p => p.IsInstalled);
            if (chosen is not null) ApplyEngineProfile(chosen, false);
        }
        catch (Exception ex) { EngineInstallationStatus = "模型库读取失败：" + ex.GetType().Name; }
        if (!SelectedEngineInstalled) EngineInstallationStatus = "尚未安装模型：导入个人双模型整包，或单个兼容运行包。只需导入一次。";
    }

    internal void SelectInstalledEngine(string id)
    {
        if (id == SelectedEngineId || EngineMaintenanceBusy) return;
        var profile = InstalledEngines.FirstOrDefault(p => p.Id == id);
        if (profile is not null) CheckEngineDirectory(profile.Directory, true);
    }

    private void ApplyEngineProfile(EngineProfile profile, bool save)
    {
        if (save) Volatile.Write(ref qualifiedTaskAccess, null);
        if (save) EngineLibrary.Save(Interface.GetPluginConfigDirectory(), new(profile.Id, InstalledEngines));
        selectedEngineDirectory = profile.Directory; MortalSelected = profile.IsMortal;
        selectedEngineName = profile.Name; SelectedEngineId = profile.Id;
        if (profile.IsMortal) mortalEngineDirectory = profile.Directory; else akochanEngineDirectory = profile.Directory;
        // Selection stores preferences only; warmup belongs to explicit test play.
    }

    internal void SelectGlobalBackend(bool mortal)
    {
        var installed = InstalledEngines.FirstOrDefault(p => p.IsMortal == mortal && p.IsInstalled);
        if (installed is not null) { SelectInstalledEngine(installed.Id); return; }
        string path = mortal ? mortalEngineDirectory ?? Path.Combine(Interface.GetPluginConfigDirectory(), "engines", "mortal-v4")
            : akochanEngineDirectory ?? AkochanTransfer.Locate(Interface.GetPluginConfigDirectory());
        CheckEngineDirectory(path, true);
    }

    internal void CheckEngineDirectory(string directory, bool apply) => BeginEngineMaintenance(async token =>
    {
        string path = Path.GetFullPath(directory.Trim().Trim('"'));
        bool mortal = File.Exists(Path.Combine(path, "mortal-installation.json"));
        if (mortal) await MortalInstallation.LoadAsync(path, token).ConfigureAwait(false);
        else await AkochanTransfer.CheckAsync(path, token).ConfigureAwait(false);
        return (new CheckedEngine(path, mortal), mortal ? "凡夫模型、Python、运行库完整；校验通过。" : "akochan v5 引擎及运行库完整；校验通过。");
    }, apply);

    internal void ImportEngine(string archive) => BeginEngineMaintenance(async token =>
    {
        string path = archive.Trim().Trim('"');
        bool mortal, bundle;
        using (var zip = ZipFile.OpenRead(path))
        { mortal = zip.GetEntry("mortal-installation.json") is not null; bundle = zip.GetEntry("bundled-engines.json") is not null; }
        if (bundle)
        {
            var profiles = await EngineLibrary.ImportBundleAsync(path, Interface.GetPluginConfigDirectory(), InstalledEngines, token).ConfigureAwait(false);
            // The last entry belongs to this validated bundle. Never select an older,
            // merely discovered installation that was not checked by this import.
            var selected = profiles.Last(p => p.IsInstalled);
            return (new CheckedEngine(selected.Directory, selected.IsMortal, profiles), "整包已导入；两种模型均已保存，可从主界面直接切换。");
        }
        string root = Path.Combine(Interface.GetPluginConfigDirectory(), "engines");
        string directory = mortal ? (await MortalInstallation.ImportAsync(path, root, token).ConfigureAwait(false)).Directory
            : (await AkochanTransfer.ImportAsync(path, root, token).ConfigureAwait(false)).Directory;
        return (new CheckedEngine(directory, mortal), "已导入并选择" + (mortal ? "凡夫 Mortal V4" : "akochan v5") + "；请选择手动提醒或自动打牌。");
    }, true);

    internal void ExportEngine() => BeginEngineMaintenance(async token =>
    {
        if (MortalSelected) return (null, "凡夫可直接使用发布页的完整运行包，无需导出个人迁移包。");
        string folder = Path.Combine(Interface.GetPluginConfigDirectory(), "engine-backups");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"MJCN-AI-personal-v5-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        await AkochanTransfer.ExportAsync(DefaultGlobalEngineDirectory, file, token);
        return (null, "个人 AI 迁移包：" + file);
    }, false);

    private void BeginEngineMaintenance(Func<CancellationToken, Task<(CheckedEngine? Installation, string Message)>> work, bool apply)
    {
        lock (gate)
        {
            if (disposed || EngineMaintenanceBusy || !RequireTestAccessCore()) return;
            if (ExperimentalHandAiEnabled) PausePlay();
            applyEngineAfterCheck = apply;
            EngineInstallationStatus = "正在处理引擎文件，请稍候…";
            engineMaintenance = Task.Run(async () =>
            {
                try
                {
                    if (!TestAccessUnlocked) return ((CheckedEngine?)null, "测试版验证失效，文件处理未开始。");
                    return await work(engineMaintenanceCancellation.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    string code = ex is AkochanException ako ? ako.Code : ex.GetType().Name;
                    return ((CheckedEngine?)null, "引擎处理失败：" + code + "；当前引擎选择未修改。");
                }
            });
        }
    }

    private void PollEngineMaintenance()
    {
        if (engineMaintenance is not { IsCompleted: true } task) return;
        engineMaintenance = null;
        var result = task.GetAwaiter().GetResult();
        EngineInstallationStatus = result.Message;
        // Technical maintenance messages stay in the secondary test settings.
        if (!TestAccessUnlocked) { EngineInstallationStatus = "测试版验证失效；文件处理结果未应用，请验证后重新检查。"; return; }
        if (result.Installation is { } install && applyEngineAfterCheck)
        {
            try
            {
                if (install.Profiles is { } profiles) InstalledEngines = EngineLibrary.Merge(InstalledEngines, profiles);
                var profile = InstalledEngines.FirstOrDefault(p => p.IsMortal == install.Mortal &&
                        string.Equals(p.Directory, install.Directory, StringComparison.OrdinalIgnoreCase))
                    ?? EngineProfile.FromDirectory(install.Directory, EngineImportName);
                InstalledEngines = EngineLibrary.Merge(InstalledEngines, profile);
                ApplyEngineProfile(profile, true);
                EngineInstallationStatus = "测试版资源已就绪；有效求解来源未改变，启用和启动仍需主动选择。";
                ReportExperimentalHandAiStatus("已选择 " + GlobalBackendLabel + "；请重新选择手动或自动模式。");
            }
            catch (Exception ex) { EngineInstallationStatus = "引擎设置保存失败：" + ex.GetType().Name; }
        }
    }

    private sealed record EngineSettings(string Directory, string Backend = "Akochan", string? MortalDirectory = null, string? AkochanDirectory = null);
}
