using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mahjong.Cn.Engines;
using Xunit;

namespace Mahjong.Cn.Core.Tests;

public sealed class AkochanInstallationTests
{
    [Fact]
    public async Task Personal_transfer_preserves_all_files_and_never_overwrites_an_existing_engine()
    {
        using var files = new Fixture(); PrepareTransfer(files.Root);
        string work = Path.Combine(Path.GetTempPath(), "mjcn-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string zip = await AkochanTransfer.ExportAsync(files.Root, Path.Combine(work, "personal.zip"));
            var first = await AkochanTransfer.ImportAsync(zip, Path.Combine(work, "engines"));
            var second = await AkochanTransfer.ImportAsync(zip, Path.Combine(work, "engines"));
            Assert.NotEqual(first.Directory, second.Directory);
            Assert.True(first.HasObservedFuritenBridge);
            Assert.Equal(first.ManifestSha256, second.ManifestSha256);
            foreach (string original in Directory.EnumerateFiles(files.Root, "*", SearchOption.AllDirectories))
                Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(Path.Combine(first.Directory, Path.GetRelativePath(files.Root, original))));
            await Assert.ThrowsAsync<IOException>(() => AkochanTransfer.ExportAsync(files.Root, zip));
            Assert.Equal(first.ManifestSha256, (await AkochanTransfer.CheckAsync(first.Directory)).ManifestSha256);
        }
        finally { Directory.Delete(work, true); }
    }

    [Theory]
    [InlineData("../escape")][InlineData("C:/escape")][InlineData("/escape")]
    [InlineData("params/../../escape")][InlineData("params\\escape")][InlineData("NUL.txt")]
    [InlineData("params/file.")][InlineData("params/file ")][InlineData("duplicate")][InlineData("symlink")]
    public async Task Transfer_rejects_unsafe_archive_entries_before_extracting(string name)
    {
        string work = Path.Combine(Path.GetTempPath(), "mjcn-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string path = Path.Combine(work, "bad.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(name);
                if (name == "symlink") entry.ExternalAttributes = unchecked((int)0xA1FF0000);
                if (name == "duplicate") archive.CreateEntry("DUPLICATE");
            }
            var error = await Assert.ThrowsAsync<AkochanException>(() => AkochanTransfer.ImportAsync(path, Path.Combine(work, "engines")));
            Assert.Equal("AKOCHAN_ARCHIVE_PATH_INVALID", error.Code);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(work, "engines"), "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(work, true); }
    }

    [Theory]
    [InlineData("missing-runtime")][InlineData("tampered")][InlineData("old")]
    public async Task Transfer_does_not_accept_incomplete_tampered_or_old_engines(string kind)
    {
        using var files = new Fixture();
        if (kind != "old") PrepareTransfer(files.Root);
        if (kind == "missing-runtime") File.Delete(Path.Combine(files.Root, "vcomp140.dll"));
        if (kind == "tampered") File.AppendAllText(Path.Combine(files.Root, "system.exe"), "changed");
        await Assert.ThrowsAsync<AkochanException>(() => AkochanTransfer.CheckAsync(files.Root));
    }

    private static void PrepareTransfer(string root)
    {
        WritePatch(root); WriteGlobal(root, true);
        string path = Path.Combine(root, "akochan-installation.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var patch = manifest["globalSnapshot"]!.AsObject();
        patch["id"] = AkochanInstallation.ObservedFuritenPatchId;
        patch["typesSha256"] = AkochanInstallation.ObservedFuritenTypesSha256;
        patch["bridgeSha256"] = AkochanInstallation.ObservedFuritenBridgeSha256;
        patch["selectorSha256"] = AkochanInstallation.DoubleRiichiSelectorSha256;
        foreach (string name in new[] { "ai.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll" })
        {
            byte[] bytes = "synthetic fixture, not executable"u8.ToArray();
            File.WriteAllBytes(Path.Combine(root, name), bytes);
            manifest["files"]![name] = Convert.ToHexString(SHA256.HashData(bytes));
        }
        File.WriteAllText(path, manifest.ToJsonString());
    }

    [Theory]
    [InlineData(null)][InlineData("typesSha256")][InlineData("bridgeSha256")][InlineData("selectorSha256")]
    public async Task Observed_furiten_bridge_requires_exact_v5_components(string? corrupt)
    {
        using var files=new Fixture();WritePatch(files.Root);WriteGlobal(files.Root,true);
        string path=Path.Combine(files.Root,"akochan-installation.json");
        var manifest=JsonNode.Parse(File.ReadAllText(path))!.AsObject();var patch=manifest["globalSnapshot"]!.AsObject();
        patch["id"]=AkochanInstallation.ObservedFuritenPatchId;
        patch["typesSha256"]=AkochanInstallation.ObservedFuritenTypesSha256;
        patch["bridgeSha256"]=AkochanInstallation.ObservedFuritenBridgeSha256;
        patch["selectorSha256"]=AkochanInstallation.DoubleRiichiSelectorSha256;
        if(corrupt is not null)patch[corrupt]=new string('a',64);
        File.WriteAllText(path,manifest.ToJsonString());
        if(corrupt is not null)Assert.Equal("AKOCHAN_GLOBAL_PATCH_IDENTITY_MISMATCH",
            (await Assert.ThrowsAsync<AkochanException>(()=>AkochanInstallation.LoadAsync(files.Root))).Code);
        else {var install=await AkochanInstallation.LoadAsync(files.Root);Assert.True(install.HasObservedFuritenBridge);
            Assert.True(install.HasDoubleRiichiBridge);Assert.True(install.HasPartialHistoryFuritenFix);}
    }
    [Fact]
    public async Task Older_bridge_cannot_silently_ignore_a_confirmed_pass()
    {
        using var files=new Fixture();WritePatch(files.Root);WriteGlobal(files.Root,true);
        var install=await AkochanInstallation.LoadAsync(files.Root);
        var s=new AkochanGlobalSnapshot(0,0,1,0,0,0,40,[],[],[],new("dahai",1,new(0)),[]) {OwnTemporaryFuriten=true};
        var ex=await Assert.ThrowsAsync<AkochanException>(()=>new AkochanGlobalEngine().AnalyzeAsync(install,s,TimeSpan.FromSeconds(1)));
        Assert.Equal("AKOCHAN_OBSERVED_FURITEN_BRIDGE_REQUIRED",ex.Code);
    }
    [Theory]
    [InlineData(null)] [InlineData("typesSha256")] [InlineData("bridgeSha256")] [InlineData("selectorSha256")]
    public async Task Double_riichi_bridge_requires_exact_hashes_and_retains_previous_fixes(string? corrupt)
    {
        using var files=new Fixture();WritePatch(files.Root);WriteGlobal(files.Root,true);
        string path=Path.Combine(files.Root,"akochan-installation.json");
        var manifest=JsonNode.Parse(File.ReadAllText(path))!.AsObject();var patch=manifest["globalSnapshot"]!.AsObject();
        patch["id"]=AkochanInstallation.DoubleRiichiPatchId;
        patch["typesSha256"]=AkochanInstallation.PartialHistoryTypesSha256;
        patch["bridgeSha256"]=AkochanInstallation.DoubleRiichiBridgeSha256;
        patch["selectorSha256"]=AkochanInstallation.DoubleRiichiSelectorSha256;
        if(corrupt is not null)patch[corrupt]=new string('a',64);
        File.WriteAllText(path,manifest.ToJsonString());
        if(corrupt is not null)Assert.Equal("AKOCHAN_GLOBAL_PATCH_IDENTITY_MISMATCH",
            (await Assert.ThrowsAsync<AkochanException>(()=>AkochanInstallation.LoadAsync(files.Root))).Code);
        else {
            var install=await AkochanInstallation.LoadAsync(files.Root);
            Assert.True(install.HasDoubleRiichiBridge);Assert.True(install.HasWinContextBridge);
            Assert.True(install.HasPartialHistoryFuritenFix);Assert.True(install.HasKanDoraCounterFix);
        }
    }
    [Theory]
    [InlineData(null)] [InlineData("typesSha256")] [InlineData("bridgeSha256")] [InlineData("selectorSha256")]
    public async Task Partial_history_bridge_requires_exact_component_hashes(string? corrupt)
    {
        using var files = new Fixture(); WritePatch(files.Root); WriteGlobal(files.Root,true);
        string path = Path.Combine(files.Root,"akochan-installation.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var patch = manifest["globalSnapshot"]!.AsObject();
        patch["id"] = AkochanInstallation.PartialHistoryPatchId;
        patch["typesSha256"] = AkochanInstallation.PartialHistoryTypesSha256;
        patch["bridgeSha256"] = AkochanInstallation.PartialHistoryBridgeSha256;
        if (corrupt is not null) patch[corrupt] = new string('a',64);
        File.WriteAllText(path,manifest.ToJsonString());
        if (corrupt is not null)
            Assert.Equal("AKOCHAN_GLOBAL_PATCH_IDENTITY_MISMATCH",
                (await Assert.ThrowsAsync<AkochanException>(() => AkochanInstallation.LoadAsync(files.Root))).Code);
        else { var install = await AkochanInstallation.LoadAsync(files.Root);
            Assert.True(install.HasPartialHistoryFuritenFix); Assert.True(install.HasWinContextBridge); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Native_bridge_capability_requires_its_exact_source_hashes(bool modern)
    {
        using var files=new Fixture();WritePatch(files.Root);WriteGlobal(files.Root,modern);
        var install=await AkochanInstallation.LoadAsync(files.Root);
        Assert.True(install.HasGlobalSnapshotBridge);
        Assert.Equal(modern,install.HasWinContextBridge);
        Assert.False(install.HasDoubleRiichiBridge);
    }

    [Theory]
    [InlineData("selectorSha256")][InlineData("bridgeSha256")][InlineData("id")]
    public async Task Win_context_bridge_rejects_a_changed_native_component(string key)
    {
        using var files=new Fixture();WritePatch(files.Root);WriteGlobal(files.Root,true,key);
        var error=await Assert.ThrowsAsync<AkochanException>(()=>AkochanInstallation.LoadAsync(files.Root));
        Assert.Equal("AKOCHAN_GLOBAL_PATCH_IDENTITY_MISMATCH",error.Code);
    }

    private static void WriteGlobal(string root,bool modern,string? corrupt=null)
    {
        var patch=new JsonObject { ["id"]=modern?AkochanInstallation.WinContextPatchId:AkochanInstallation.GlobalPatchId,
            ["typesSha256"]=AkochanInstallation.GlobalTypesSha256,["mjutilSha256"]=AkochanInstallation.GlobalMjutilSha256,
            ["bridgeSha256"]=modern?AkochanInstallation.WinContextBridgeSha256:AkochanInstallation.GlobalBridgeSha256,
            ["patchScriptSha256"]=new string('a',64) };
        if(modern)patch["selectorSha256"]=AkochanInstallation.WinContextSelectorSha256;
        if(corrupt is not null)patch[corrupt]="wrong";
        string path=Path.Combine(root,"akochan-installation.json");var manifest=JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        manifest["globalSnapshot"]=patch;File.WriteAllText(path,manifest.ToJsonString());
    }
    [Fact]
    public async Task Pinned_files_are_hashed_without_executing_any_binary()
    {
        using var files = new Fixture();
        var install = await AkochanInstallation.LoadAsync(files.Root);
        Assert.Equal(AkochanInstallation.ExpectedCommit, install.SourceCommit);
        Assert.Equal(64, install.ManifestSha256.Length);
        Assert.Equal(Path.Combine(files.Root, "system.exe"), install.Executable);
        Assert.False(install.HasKanDoraCounterFix);
    }

    [Fact]
    public async Task Modified_parameter_is_rejected_before_engine_start()
    {
        using var files = new Fixture();
        File.AppendAllText(Path.Combine(files.Root, AkochanInstallation.ExpectedParameterFiles[0]), "changed");
        var ex = await Assert.ThrowsAsync<AkochanException>(() => AkochanInstallation.LoadAsync(files.Root));
        Assert.Equal("AKOCHAN_FILE_HASH_MISMATCH", ex.Code);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("params/../../outside.txt")]
    [InlineData("params/test.txt:stream")]
    public async Task Manifest_paths_cannot_escape_installation(string path)
    {
        using var files = new Fixture(path);
        var ex = await Assert.ThrowsAsync<AkochanException>(() => AkochanInstallation.LoadAsync(files.Root));
        Assert.Equal("AKOCHAN_PATH_INVALID", ex.Code);
    }

    [Fact]
    public async Task Different_source_commit_is_not_silently_accepted()
    {
        using var files = new Fixture(commit: new string('0', 40));
        var ex = await Assert.ThrowsAsync<AkochanException>(() => AkochanInstallation.LoadAsync(files.Root));
        Assert.Equal("AKOCHAN_IDENTITY_MISMATCH", ex.Code);
    }

    [Fact]
    public async Task Unlisted_file_is_not_allowed_to_evade_hash_checks()
    {
        using var files = new Fixture();
        File.WriteAllText(Path.Combine(files.Root, "params", "unlisted.txt"), "unlisted");
        var ex = await Assert.ThrowsAsync<AkochanException>(() => AkochanInstallation.LoadAsync(files.Root));
        Assert.Equal("AKOCHAN_UNLISTED_FILE", ex.Code);
    }

    [Fact]
    public async Task Removing_a_required_parameter_from_both_disk_and_manifest_still_fails()
    {
        using var files = new Fixture(omitParameter: true);
        var ex = await Assert.ThrowsAsync<AkochanException>(() => AkochanInstallation.LoadAsync(files.Root));
        Assert.Equal("AKOCHAN_MANIFEST_INCOMPLETE", ex.Code);
    }

    [Fact]
    public async Task Exact_local_patch_identity_is_retained_separately_from_upstream_commit()
    {
        using var files = new Fixture();
        WritePatch(files.Root);
        var install = await AkochanInstallation.LoadAsync(files.Root);
        Assert.True(install.HasKanDoraCounterFix);
        Assert.Equal(AkochanInstallation.ExpectedCommit, install.SourceCommit);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("sourceFile")]
    [InlineData("sourceCommit")]
    [InlineData("originalLfSha256")]
    [InlineData("patchedSha256")]
    [InlineData("patchScriptSha256")]
    public async Task Unrecognized_or_incomplete_patch_does_not_gain_the_fixed_engine_capability(string field)
    {
        using var files = new Fixture();
        WritePatch(files.Root, field);
        var error = await Assert.ThrowsAsync<AkochanException>(() => AkochanInstallation.LoadAsync(files.Root));
        Assert.Equal("AKOCHAN_PATCH_IDENTITY_MISMATCH", error.Code);
    }

    [Fact]
    public async Task Old_engine_is_rejected_before_executing_an_affected_kan_replay()
    {
        using var files = new Fixture(); // Its synthetic system.exe is deliberately not executable.
        var install = await AkochanInstallation.LoadAsync(files.Root);
        var error = await Assert.ThrowsAsync<AkochanException>(() => new AkochanDecisionEngine().AnalyzeOfflineAsync(
            install, AkochanReplayTests.KanDoraReplay("daiminkan"), TimeSpan.FromSeconds(1)));
        Assert.Equal("AKOCHAN_KAN_DORA_PATCH_REQUIRED", error.Code);
    }

    private static void WritePatch(string root, string? corrupt = null)
    {
        var patch = new JsonObject
        {
            ["id"] = AkochanInstallation.KanDoraPatchId, ["sourceFile"] = "share/types.cpp",
            ["sourceCommit"] = AkochanInstallation.ExpectedCommit,
            ["originalLfSha256"] = AkochanInstallation.KanDoraOriginalSha256,
            ["patchedSha256"] = AkochanInstallation.KanDoraPatchedSha256,
            ["patchScriptSha256"] = new string('a', 64),
        };
        if (corrupt is not null) patch[corrupt] = "unverified";
        string path = Path.Combine(root, "akochan-installation.json");
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        manifest["localPatches"] = new JsonArray(patch);
        File.WriteAllText(path, manifest.ToJsonString());
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string Root = Path.Combine(Path.GetTempPath(), "mjcn-install-test-" + Guid.NewGuid().ToString("N"));
        internal Fixture(string? additionalPath = null, string? commit = null, bool omitParameter = false)
        {
            Directory.CreateDirectory(Path.Combine(Root, "params"));
            var hashes = new Dictionary<string, string>();
            foreach (string name in new[] { "system.exe", "setup_mjai.json", "LICENSE" }.Concat(
                omitParameter ? AkochanInstallation.ExpectedParameterFiles.Skip(1) : AkochanInstallation.ExpectedParameterFiles))
            {
                byte[] bytes = "synthetic fixture, never executable"u8.ToArray();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Root, name))!);
                File.WriteAllBytes(Path.Combine(Root, name), bytes);
                hashes.Add(name, Convert.ToHexString(SHA256.HashData(bytes)));
            }
            if (additionalPath is not null) hashes.Add(additionalPath, new string('0', 64));
            File.WriteAllText(Path.Combine(Root, "akochan-installation.json"), JsonSerializer.Serialize(new
            {
                schema = 1, threads = 2, sourceRepository = AkochanInstallation.Repository,
                sourceCommit = commit ?? AkochanInstallation.ExpectedCommit,
                executable = "system.exe", tactics = "setup_mjai.json", files = hashes,
            }));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
