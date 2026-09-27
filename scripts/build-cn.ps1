[CmdletBinding()]
param(
    [string] $DalamudLibPath = (Join-Path $env:APPDATA 'XIVLauncherCN\addon\Hooks\26-09-25-01'),
    [string] $DotnetPath = '',
    [string] $PythonPath = 'python',
    [string] $AkochanDirectory = '',
    [string] $JournalReplayFile = '',
    [switch] $SkipTests,
    [switch] $VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactsDir = Join-Path $repoRoot 'artifacts'
$sdkVersion = '10.0.100'
$pluginName = 'Mahjong.Plugin.CN'
$pluginVersion = '4.1.4'
$assemblyVersion = '4.1.4.0'
$utf8 = New-Object Text.UTF8Encoding($false)
. (Join-Path $PSScriptRoot 'source-package.ps1')
$originalDirectory = Get-Location
$savedEnvironment = @{}
$environmentNames = @('DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_ROLL_FORWARD', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_NOLOGO')
foreach ($name in $environmentNames) { $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }

function Assert-RepositoryPath([string] $Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside the checkout: $fullPath"
    }
    $current = $fullPath
    while ($current -ne $repoRoot) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a reparse point in a package/output path: $current"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $fullPath
}

function Write-Log([string] $Message) {
    $Message = ConvertTo-PublicBuildText $Message $repoRoot
    Write-Host $Message
    [IO.File]::AppendAllText($buildLog, $Message + [Environment]::NewLine, $utf8)
}

function Invoke-Dotnet([string[]] $ToolArguments, [string] $ExtraLog = '') {
    $mapRoot = $repoRoot
    if ((Get-Variable sourceVerifyDir -ErrorAction SilentlyContinue) -and
        @($ToolArguments | Where-Object { $_.StartsWith($sourceVerifyDir, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) { $mapRoot = $sourceVerifyDir }
    # A source ZIP has no Git index. Do not let SDK source-control discovery embed
    # its files as "untracked" sources or inherit the enclosing checkout's metadata.
    $ToolArguments += @('-p:Deterministic=true', "-p:PathMap=$mapRoot=/_/",
        '-p:EnableSourceControlManagerQueries=false', '-p:EnableSourceLink=false', '-p:EmbedUntrackedSources=false')
    if (Get-Variable headCommit -ErrorAction SilentlyContinue) { $ToolArguments += "-p:SourceRevisionId=$headCommit" }
    Write-Log ('dotnet ' + ($ToolArguments -join ' '))
    $previousPreference = $ErrorActionPreference
    try {
        # PowerShell 5.1 otherwise treats harmless native stderr as a terminating error.
        $ErrorActionPreference = 'Continue'
        & $DotnetPath @ToolArguments 2>&1 | ForEach-Object {
            $line = ConvertTo-PublicBuildText $_.ToString() $repoRoot
            Write-Log $line
            if ($ExtraLog) { [IO.File]::AppendAllText($ExtraLog, $line + [Environment]::NewLine, $utf8) }
        }
        $toolExitCode = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    if ($toolExitCode -ne 0) { throw "dotnet exited with code $toolExitCode. See artifacts/build-log.txt." }
}

function Invoke-AkochanSelfCheck([string] $ReplayDll, [string] $EngineDirectory, [string] $ReportPath) {
    # Only the public built-in replay is used. Do not log a private absolute engine path.
    Write-Log 'dotnet Mahjong.Akochan.Replay.dll <explicit local engine directory> (built-in public opening)'
    if ($ReplayDll.Contains('"') -or $EngineDirectory.Contains('"')) { throw 'Invalid quoted replay/engine path.' }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $DotnetPath
    $start.Arguments = '"' + $ReplayDll + '" "' + $EngineDirectory.TrimEnd('\') + '"'
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Akochan replay CLI could not start.' }
        $akochanOfflineValidation.executed = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(150000)) {
            $process.Kill()
            throw 'Akochan replay CLI timed out after 150 seconds.'
        }
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($ReportPath, $stdout, $utf8)
        if ($process.ExitCode -ne 0 -or $stderr.Trim().Length -ne 0) {
            if ($stderr.Trim().Length -ne 0) { Write-Log ('Akochan replay stderr: ' + $stderr.Trim()) }
            throw "Akochan replay CLI failed with code $($process.ExitCode). No offline-pass claim was recorded."
        }
        $decision = ConvertFrom-Json -InputObject $stdout
        # The CLI currently serializes default System.Text.Json PascalCase properties.
        if ($decision.EngineCommit -cne '53188a0b926fbab38177f88c3cd87d554cf412af' -or
            $decision.IsLiveGameDecision -isnot [bool] -or $decision.IsLiveGameDecision -ne $false -or
            $decision.IsSynthetic -isnot [bool] -or $decision.IsSynthetic -ne $false -or
            $decision.SourceLabel -cne 'offline:akochan-public-opening' -or
            @($decision.Moves).Count -ne 1 -or $decision.Moves[0].Type -cne 'dahai' -or $decision.Moves[0].Actor -ne 0) {
            throw 'Akochan replay JSON did not prove a real pinned-engine offline decision on the built-in public sample.'
        }
        Write-Log 'Akochan public-opening replay passed; IsLiveGameDecision=false. Raw JSON: artifacts/akochan-self-check.json'
    }
    finally { $process.Dispose() }
}

function Get-ProjectAssemblyNames([string] $ProjectPath, [Collections.Generic.HashSet[string]] $Visited) {
    $fullPath = Assert-RepositoryPath $ProjectPath
    if (-not $Visited.Add($fullPath)) { return }
    [xml] $projectXml = [IO.File]::ReadAllText($fullPath)
    $assembly = $projectXml.SelectSingleNode('/Project/PropertyGroup/AssemblyName')
    if ($null -ne $assembly) { $assembly.InnerText } else { [IO.Path]::GetFileNameWithoutExtension($fullPath) }
    foreach ($reference in $projectXml.SelectNodes('/Project/ItemGroup/ProjectReference')) {
        Get-ProjectAssemblyNames (Join-Path ([IO.Path]::GetDirectoryName($fullPath)) $reference.GetAttribute('Include')) $Visited
    }
}

function Invoke-GlobalAkochanCheck([string[]] $Arguments, [string] $ReportPath) {
    if (@($Arguments | Where-Object { $_.Contains('"') }).Count -ne 0) { throw 'Quoted global probe path is invalid.' }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $DotnetPath
    $start.Arguments = (($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $start.WorkingDirectory = $repoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = $utf8
    $start.StandardErrorEncoding = $utf8
    $start.EnvironmentVariables['DOTNET_ROLL_FORWARD'] = 'Major'
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Global probe failed to start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(150000)) { $process.Kill(); throw 'Global native probe timed out.' }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($ReportPath, $output, $utf8)
        if ($process.ExitCode -ne 0 -or $errors.Trim()) { throw "Global probe failed: $errors" }
        $parsed = ConvertFrom-Json -InputObject $output
        if ($Arguments.Count -eq 2) {
            $expectedCount = if ($parsed.PSObject.Properties.Name -contains 'observedFuritenBridge' -and $parsed.observedFuritenBridge) { 31 } elseif ($parsed.PSObject.Properties.Name -contains 'doubleRiichiBridge' -and $parsed.doubleRiichiBridge) { 24 } elseif ($parsed.PSObject.Properties.Name -contains 'partialHistoryFuritenFix' -and $parsed.partialHistoryFuritenFix) { 18 } elseif ($parsed.winContextBridge) { 14 } else { 7 }
            if (-not $parsed.native -or @($parsed.reports).Count -ne $expectedCount) { throw 'Global synthetic report is incomplete.' }
        } elseif (@($parsed.result.Candidates).Count -lt 1) { throw 'Global recorded-data report has no candidates.' }
    } finally { $process.Dispose() }
}

function Get-SourceEntries {
    return Get-ReviewedSourceEntries $repoRoot
}

function New-Zip([string] $OutputPath, [Collections.IDictionary] $Entries) {
    $safeOutput = Assert-RepositoryPath $OutputPath
    $temporaryZip = Assert-RepositoryPath ($safeOutput + '.partial')
    $stream = [IO.File]::Open($temporaryZip, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($entry in $Entries.GetEnumerator()) {
                $sourceFile = Assert-RepositoryPath $entry.Value
                $zipEntry = $archive.CreateEntry($entry.Key, [IO.Compression.CompressionLevel]::Optimal)
                # Create-mode metadata must be set BEFORE opening the entry on Windows PowerShell 5.1.
                $zipEntry.LastWriteTime = [DateTimeOffset]::Parse('2026-09-23T00:00:00Z')
                $sourceStream = [IO.File]::OpenRead($sourceFile)
                try {
                    $entryStream = $zipEntry.Open()
                    try { $sourceStream.CopyTo($entryStream) }
                    finally { $entryStream.Dispose() }
                }
                finally { $sourceStream.Dispose() }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $stream.Dispose() }
    Move-Item -LiteralPath $temporaryZip -Destination $safeOutput -Force
}

try {
    Set-Location -LiteralPath $repoRoot
    if (-not $VerifyOnly) {
        $sourceEntries = Get-SourceEntries
        if (Test-Path -LiteralPath (Join-Path $repoRoot '.git')) {
            $headCommit = (& git rev-parse HEAD | Out-String).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Cannot read source commit.' }
        } else {
            $headCommit = (Get-Content -Raw -Encoding UTF8 (Join-Path $repoRoot 'source-file-manifest.json') | ConvertFrom-Json).baseCommit
        }
        $initialSourceHashes = @{}
        foreach ($entry in $sourceEntries.GetEnumerator()) {
            $initialSourceHashes[$entry.Key] = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash
        }
    }
    $null = Assert-RepositoryPath $artifactsDir
    New-Item -ItemType Directory -Force -Path $artifactsDir | Out-Null
    $buildLog = Assert-RepositoryPath (Join-Path $artifactsDir 'build-log.txt')
    [IO.File]::WriteAllText($buildLog, '', $utf8)
    $manifestOutput = Assert-RepositoryPath (Join-Path $artifactsDir 'build-manifest.json')
    [IO.File]::WriteAllText($manifestOutput, ('{"status":"Running","buildPassed":false,"cnLiveGameVerified":false}'), $utf8)
    [IO.File]::WriteAllText((Assert-RepositoryPath (Join-Path $artifactsDir 'SHA256SUMS')), "# Current build has not completed successfully.`n", $utf8)
    Write-Log ('Build started UTC: ' + [DateTime]::UtcNow.ToString('O'))
    if (-not $DotnetPath) { $DotnetPath = Join-Path $repoRoot '.work\dotnet\dotnet.exe' }
    $DotnetPath = [IO.Path]::GetFullPath($DotnetPath)
    if (-not (Test-Path -LiteralPath $DotnetPath -PathType Leaf)) {
        throw 'SDK not found. Run scripts/bootstrap-dotnet.ps1 first, or pass -DotnetPath to SDK 10.0.100.'
    }
    $env:DOTNET_ROOT = [IO.Path]::GetDirectoryName($DotnetPath)
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    $actualSdk = (& $DotnetPath --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $sdkVersion) {
        throw "Expected exact SDK $sdkVersion, got '$actualSdk'. Select a dedicated install; latest SDK fallback is disabled."
    }
    Write-Log "SDK: $actualSdk"
    Invoke-Dotnet @('--list-runtimes')

    $evidence = Get-Content -LiteralPath (Join-Path $repoRoot 'docs\cn\local-version-evidence.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $DalamudLibPath = [IO.Path]::GetFullPath($DalamudLibPath)
    foreach ($check in @(
        @{ Name = 'Dalamud.dll'; Hash = $evidence.dalamud.sha256; Version = $evidence.dalamud.informationalVersion },
        @{ Name = 'FFXIVClientStructs.dll'; Hash = $evidence.clientStructs.sha256; Version = $evidence.clientStructs.informationalVersion }
    )) {
        $library = Join-Path $DalamudLibPath $check.Name
        if (-not (Test-Path -LiteralPath $library -PathType Leaf)) { throw "Missing fixed framework reference: $($check.Name)" }
        $actualHash = (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash
        $actualVersion = (Get-Item -LiteralPath $library).VersionInfo.ProductVersion
        if ($actualHash -ne $check.Hash -or $actualVersion -ne $check.Version) {
            throw "Framework identity mismatch for $($check.Name). Expected $($check.Version) / $($check.Hash); got $actualVersion / $actualHash. No fallback or compatibility override is allowed."
        }
        Write-Log "Verified $($check.Name): $actualVersion SHA256=$actualHash"
    }
    $manifestPath = Join-Path $repoRoot "$pluginName\$pluginName.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.InternalName -ne $pluginName -or $manifest.AssemblyVersion -ne $assemblyVersion -or
        $manifest.DalamudApiLevel -ne $evidence.dalamud.apiLevel -or $manifest.ApplicableVersion -ne $evidence.gameVersion) {
        throw 'Plugin manifest does not match the pinned plugin/framework/game identity.'
    }

    $projectPath = Join-Path $repoRoot "$pluginName\$pluginName.csproj"
    Invoke-Dotnet @('restore', $projectPath, "-p:DalamudLibPath=$DalamudLibPath")
    Invoke-Dotnet @('build', $projectPath, '--configuration', 'Release', '--no-restore', "-p:DalamudLibPath=$DalamudLibPath")

    $replayProjectRelativePath = 'tools\Mahjong.Akochan.Replay\Mahjong.Akochan.Replay.csproj'
    $globalProbeRelativePath = 'tools\Mahjong.Cn.GlobalEngineProbe\Mahjong.Cn.GlobalEngineProbe.csproj'
    $engineTransferRelativePath = 'tools\Mahjong.Cn.EngineTransfer\Mahjong.Cn.EngineTransfer.csproj'
    $ponProbeRelativePath = 'tools\Mahjong.Cn.PonResponseProbe\Mahjong.Cn.PonResponseProbe.csproj'
    $chiProbeRelativePath = 'tools\Mahjong.Cn.ChiResponseProbe\Mahjong.Cn.ChiResponseProbe.csproj'
    $journalReplayRelativePath = 'tools\Mahjong.Cn.JournalReplay\Mahjong.Cn.JournalReplay.csproj'
    Invoke-Dotnet @('build', (Join-Path $repoRoot $globalProbeRelativePath), '--configuration', 'Release', '-p:RestorePackagesWithLockFile=true')
    Invoke-Dotnet @('build', (Join-Path $repoRoot $engineTransferRelativePath), '--configuration', 'Release', '-p:RestoreLockedMode=true')
    Invoke-Dotnet @('build', (Join-Path $repoRoot $ponProbeRelativePath), '--configuration', 'Release', '-p:RestorePackagesWithLockFile=true', "-p:DalamudLibPath=$DalamudLibPath")
    Invoke-Dotnet @('build', (Join-Path $repoRoot $chiProbeRelativePath), '--configuration', 'Release', '-p:RestorePackagesWithLockFile=true', "-p:DalamudLibPath=$DalamudLibPath")
    Invoke-Dotnet @('build', (Join-Path $repoRoot $journalReplayRelativePath), '--configuration', 'Release', '-p:RestorePackagesWithLockFile=true', "-p:DalamudLibPath=$DalamudLibPath")
    $replayProjectPath = Join-Path $repoRoot $replayProjectRelativePath
    Invoke-Dotnet @('build', $replayProjectPath, '--configuration', 'Release', '-p:RestoreLockedMode=true')
    $akochanReport = Assert-RepositoryPath (Join-Path $artifactsDir 'akochan-self-check.json')
    $akochanOfflineValidation = [ordered]@{ executed = $false; result = 'Skipped'; report = 'akochan-self-check.json'; sha256 = $null }
    [IO.File]::WriteAllText($akochanReport, '{"executed":false,"status":"NotExecuted"}', $utf8)
    if ($AkochanDirectory) {
        $AkochanDirectory = [IO.Path]::GetFullPath($AkochanDirectory)
        if (-not (Test-Path -LiteralPath $AkochanDirectory -PathType Container)) { throw 'The explicit -AkochanDirectory does not exist. No automatic installation is performed.' }
        $replayDll = Assert-RepositoryPath (Join-Path $repoRoot 'tools\Mahjong.Akochan.Replay\bin\Release\net10.0\Mahjong.Akochan.Replay.dll')
        if (-not (Test-Path -LiteralPath $replayDll -PathType Leaf)) { throw 'Built Akochan replay CLI DLL is missing.' }
        $akochanOfflineValidation.result = 'Failed'
        Invoke-AkochanSelfCheck $replayDll $AkochanDirectory $akochanReport
        $akochanOfflineValidation.result = 'Passed'
    }
    else {
        Write-Log 'Akochan offline validation skipped: no -AkochanDirectory was supplied. No automatic installation or native execution was attempted.'
        [IO.File]::WriteAllText($akochanReport, ([ordered]@{ executed = $false; reason = 'AkochanDirectoryNotSupplied'; IsLiveGameDecision = $false } | ConvertTo-Json), $utf8)
    }
    $akochanOfflineValidation.sha256 = (Get-FileHash -LiteralPath $akochanReport -Algorithm SHA256).Hash.ToLowerInvariant()
    $globalAiValidation = [ordered]@{ executed=$false; result='Skipped'; reports=@() }
    $globalReports = [ordered]@{}
    if ($AkochanDirectory) {
        $localEngine = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $AkochanDirectory 'akochan-installation.json') | ConvertFrom-Json
        if ($localEngine.PSObject.Properties.Name -contains 'globalSnapshot') {
            $globalAiValidation.executed=$true; $globalAiValidation.result='Failed'
            $globalProbeDll = Join-Path $repoRoot 'tools\Mahjong.Cn.GlobalEngineProbe\bin\Release\net8.0\Mahjong.Cn.GlobalEngineProbe.dll'
            foreach ($sample in @('synthetic','open-table','closed-table','own-open-hand')) {
                $reportName="global-ai-$sample.json"
                $reportPath=Join-Path $artifactsDir $reportName
                $probeArguments=@($globalProbeDll,$AkochanDirectory)
                if ($sample -ne 'synthetic') { $probeArguments += Join-Path $repoRoot "docs\cn\evidence\global-ai-captured-$sample-20260924.json" }
                Write-Log "Running global native probe: $sample (offline, no game input)"
                Invoke-GlobalAkochanCheck $probeArguments $reportPath
                $globalReports[$reportName]=$reportPath
                $globalAiValidation.reports += [ordered]@{ file=$reportName; sha256=(Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant() }
            }
            if ($localEngine.globalSnapshot.id -in @('mjcn-public-snapshot-v3','mjcn-public-snapshot-v4','mjcn-public-snapshot-v5')) {
                $ronReportName = 'global-ai-ron-furiten-regression.json'
                $ronReportPath = Join-Path $artifactsDir $ronReportName
                Write-Log 'Running recorded missed Ron through native partial-history fix; requires actual hora candidate'
                Invoke-GlobalAkochanCheck @($globalProbeDll,$AkochanDirectory,
                    (Join-Path $repoRoot 'tests/Mahjong.Plugin.CN.Gameplay.Tests/fixtures/ron-false-furiten-20260925.json')) $ronReportPath
                $globalReports[$ronReportName]=$ronReportPath
                $globalAiValidation.reports += [ordered]@{ file=$ronReportName; sha256=(Get-FileHash -LiteralPath $ronReportPath -Algorithm SHA256).Hash.ToLowerInvariant() }
            }
            $ponReportName = 'global-ai-pon-response.json'
            $kyushuReportName = 'global-ai-kyushu-regression.json'
            $kyushuReportPath = Join-Path $artifactsDir $kyushuReportName
            Write-Log 'Running recorded nine-terminals protocol regression through the native engine'
            Invoke-GlobalAkochanCheck @($globalProbeDll,$AkochanDirectory,
                (Join-Path $repoRoot 'tests/Mahjong.Plugin.CN.Gameplay.Tests/fixtures/kyushu-protocol-20260925.json')) $kyushuReportPath
            $globalReports[$kyushuReportName]=$kyushuReportPath
            $globalAiValidation.reports += [ordered]@{ file=$kyushuReportName; sha256=(Get-FileHash -LiteralPath $kyushuReportPath -Algorithm SHA256).Hash.ToLowerInvariant() }
            $ponReportPath = Join-Path $artifactsDir $ponReportName
            $ponProbeDll = Join-Path $repoRoot 'tools\Mahjong.Cn.PonResponseProbe\bin\Release\net10.0-windows\Mahjong.Plugin.CN.Gameplay.Tests.dll'
            Write-Log 'Running recorded Pon response through projector, native engine and mapper (offline, no game input)'
            Invoke-GlobalAkochanCheck @($ponProbeDll, (Join-Path $repoRoot 'docs\cn\evidence\pon-response-20260924-input.json'), $AkochanDirectory) $ponReportPath
            $ponResult = Get-Content -Raw -Encoding UTF8 -LiteralPath $ponReportPath | ConvertFrom-Json
            if (-not $ponResult.Passed -or -not $ponResult.SameNativeInputAcross483Samples -or $ponResult.GameCallbackSubmitted) {
                throw 'Recorded Pon response integration report did not pass.'
            }
            $globalReports[$ponReportName] = $ponReportPath
            $globalAiValidation.reports += [ordered]@{ file=$ponReportName; sha256=(Get-FileHash -LiteralPath $ponReportPath -Algorithm SHA256).Hash.ToLowerInvariant() }
            $chiReportName = 'global-ai-chi-response.json'
            $chiReportPath = Join-Path $artifactsDir $chiReportName
            $chiProbeDll = Join-Path $repoRoot 'tools\Mahjong.Cn.ChiResponseProbe\bin\Release\net10.0-windows\Mahjong.Plugin.CN.Gameplay.Tests.dll'
            Write-Log 'Running recorded missed-opening Chi observation chain and independent current-menu recovery (offline, no game input)'
            Invoke-GlobalAkochanCheck @($chiProbeDll, (Join-Path $repoRoot 'docs\cn\evidence\chi-response-20260924-input.json'), $AkochanDirectory) $chiReportPath
            $chiResult = Get-Content -Raw -Encoding UTF8 -LiteralPath $chiReportPath | ConvertFrom-Json
            if (-not $chiResult.Passed -or $chiResult.GameCallbackSubmitted -or $chiResult.InjectedTypedRiverEvents -ne 0 -or
                $chiResult.ReconstructedHoldSamples -ne 1 -or @($chiResult.LateReadOnlyRecovery.result.Candidates).Count -lt 1) {
                throw 'Recorded Chi observation/recovery integration did not pass.'
            }
            $globalReports[$chiReportName] = $chiReportPath
            $globalAiValidation.reports += [ordered]@{ file=$chiReportName; sha256=(Get-FileHash -LiteralPath $chiReportPath -Algorithm SHA256).Hash.ToLowerInvariant() }
            $globalAiValidation.result='Passed'
        }
    }

    $journalValidation = [ordered]@{ executed=$false; result='Skipped'; report=$null }
    $journalReportPath = ''
    if ($JournalReplayFile) {
        $journalReplayDir = Assert-RepositoryPath (Join-Path $artifactsDir ('journal-replay/' + [Guid]::NewGuid().ToString('N')))
        $journalReplayDll = Join-Path $repoRoot 'tools/Mahjong.Cn.JournalReplay/bin/Release/net10.0-windows/Mahjong.Plugin.CN.Gameplay.Tests.dll'
        Write-Log 'Running lossless recorded journal replay with forced rotation (private source path omitted)'
        $journalText = & $DotnetPath $journalReplayDll $JournalReplayFile $journalReplayDir
        if ($LASTEXITCODE -ne 0) { throw 'Recorded journal replay failed.' }
        $journalReportPath = Join-Path $artifactsDir 'journal-storage-validation.json'
        [IO.File]::WriteAllText($journalReportPath, ($journalText -join [Environment]::NewLine), $utf8)
        $journalProof = Get-Content -LiteralPath $journalReportPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $journalProof.Passed -or -not $journalProof.AllPayloadsAndKindsAndSequencesAndTimesEqual -or
            $journalProof.ReductionPercent -lt 80 -or $journalProof.Segments -lt 2 -or $journalProof.JournalFault) {
            throw 'Recorded journal replay did not satisfy lossless/size/rotation checks.'
        }
        $analyzerOutput = Join-Path $journalReplayDir 'python-summary.json'
        & $PythonPath (Join-Path $repoRoot 'scripts/analyze-game-logs.py') (Join-Path $journalReplayDir 'verified-export.zip') --output $analyzerOutput | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Python could not verify the exported journal replay.' }
        $journalValidation = [ordered]@{ executed=$true; result='Passed'; report='journal-storage-validation.json';
            reductionPercent=$journalProof.ReductionPercent; records=$journalProof.ReplayedRecords; pythonExportVerified=$true }
    }

    $testResults = @()
    $testFiles = [ordered]@{}
    $suites = @('Mahjong.Cn.Core.Tests', 'Mahjong.Plugin.CN.Tests', 'Mahjong.Plugin.CN.Gameplay.Tests', 'Mahjong.Core.Tests', 'Mahjong.Engine.Tests', 'Mahjong.Policy.Tests',
        'Mahjong.Rules.Tests', 'Mahjong.Plugin.Game.Tests', 'Mahjong.Replay.Tests')
    if ($SkipTests) {
        Write-Log 'TESTS SKIPPED by explicit -SkipTests. This package has no test-pass claim for this run.'
    }
    else {
        $testResultsDir = Assert-RepositoryPath (Join-Path $artifactsDir 'test-results')
        New-Item -ItemType Directory -Force -Path $testResultsDir | Out-Null
        $env:DOTNET_ROLL_FORWARD = 'Major'
        Write-Log 'Tests use DOTNET_ROLL_FORWARD=Major: net8 test hosts may run on the installed .NET 10 runtime. This is not a native .NET 8 runtime claim.'
        foreach ($suite in $suites) {
            $testProject = Join-Path $repoRoot "tests\$suite\$suite.csproj"
            if (-not (Test-Path -LiteralPath $testProject -PathType Leaf)) { throw "Required test project missing: tests/$suite/$suite.csproj" }
            $suiteLog = Assert-RepositoryPath (Join-Path $testResultsDir "$suite.log")
            [IO.File]::WriteAllText($suiteLog, '', $utf8)
            Invoke-Dotnet @('test', $testProject, '--configuration', 'Release', '--logger', "trx;LogFileName=$suite.trx",
                '--results-directory', $testResultsDir, '-p:RestorePackagesWithLockFile=true', "-p:DalamudLibPath=$DalamudLibPath") $suiteLog
            $trxPath = Assert-RepositoryPath (Join-Path $testResultsDir "$suite.trx")
            if (-not (Test-Path -LiteralPath $trxPath -PathType Leaf)) { throw "Test runner did not produce $suite.trx" }
            [xml] $trx = [IO.File]::ReadAllText($trxPath)
            $counters = $trx.SelectSingleNode('//*[local-name()="Counters"]')
            $summary = $trx.SelectSingleNode('//*[local-name()="ResultSummary"]')
            if ($null -eq $counters -or $null -eq $summary -or [int]$counters.GetAttribute('executed') -lt 1 -or
                [int]$counters.GetAttribute('failed') -ne 0 -or $summary.GetAttribute('outcome') -ne 'Completed') {
                throw "The $suite TRX did not prove a completed non-empty successful run."
            }
            [IO.File]::WriteAllText($trxPath, (ConvertTo-PublicBuildText ([IO.File]::ReadAllText($trxPath)) $repoRoot), $utf8)
            $testResults += [ordered]@{ suite = $suite; outcome = $summary.GetAttribute('outcome');
                total = [int]$counters.GetAttribute('total'); executed = [int]$counters.GetAttribute('executed');
                passed = [int]$counters.GetAttribute('passed'); failed = [int]$counters.GetAttribute('failed') }
            $testFiles["test-results/$suite.log"] = $suiteLog
            $testFiles["test-results/$suite.trx"] = $trxPath
        }
    }

    $analyzerTests = [ordered]@{ executed = 0; passed = 0; pythonVersion = $null; skipped = [bool]$SkipTests }
    if (-not $SkipTests) {
        $null = Get-Command $PythonPath -ErrorAction Stop
        $analyzerTests.pythonVersion = (& $PythonPath --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Python version check failed.' }
        $analyzerLog = Assert-RepositoryPath (Join-Path $testResultsDir 'cn-session-analyzer.log')
        [IO.File]::WriteAllText($analyzerLog, '', $utf8)
        Write-Log 'python -m unittest discover -s tests -p test_analyze*.py -v'
        $previousPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            & $PythonPath -m unittest discover -s tests -p 'test_analyze*.py' -v 2>&1 | ForEach-Object {
                $line = ConvertTo-PublicBuildText $_.ToString() $repoRoot
                Write-Log $line
                [IO.File]::AppendAllText($analyzerLog, $line + [Environment]::NewLine, $utf8)
            }
            $pythonExitCode = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previousPreference }
        $analyzerOutput = [IO.File]::ReadAllText($analyzerLog)
        if ($pythonExitCode -ne 0 -or $analyzerOutput -notmatch 'Ran ([1-9][0-9]*) tests?' -or $analyzerOutput -notmatch '(?m)^OK\s*$') {
            throw 'Session analyzer tests did not prove a non-empty successful run.'
        }
        $testCount = [int][regex]::Match($analyzerOutput, 'Ran ([1-9][0-9]*) tests?').Groups[1].Value
        $analyzerTests.executed = $testCount
        $analyzerTests.passed = $testCount
        $testFiles['test-results/cn-session-analyzer.log'] = $analyzerLog
    }

    $outputDir = Join-Path $repoRoot "$pluginName\bin\Release\net10.0-windows"
    $sourcePackageChecks = [ordered]@{ executed = $false; passed = $false }
    if (-not $SkipTests) {
        $checkLog = Assert-RepositoryPath (Join-Path $testResultsDir 'source-package-checks.log')
        [IO.File]::WriteAllText($checkLog, '', $utf8)
        foreach ($checkArgs in @(@('-m','unittest','discover','-s','tests','-p','test_source_package.py','-v'), @('scripts/check-doc-links.py'))) {
            $previousPreference = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                & $PythonPath @checkArgs 2>&1 | ForEach-Object {
                    $line = ConvertTo-PublicBuildText $_.ToString() $repoRoot
                    Write-Log $line
                    [IO.File]::AppendAllText($checkLog, $line + [Environment]::NewLine, $utf8)
                }
                $checkExit = $LASTEXITCODE
            } finally { $ErrorActionPreference = $previousPreference }
            if ($checkExit -ne 0) { throw 'Source packaging or documentation checks failed.' }
        }
        $sourcePackageChecks.executed = $true; $sourcePackageChecks.passed = $true
        $testFiles['test-results/source-package-checks.log'] = $checkLog
    }
    $assemblyNames = @(Get-ProjectAssemblyNames $projectPath (New-Object 'Collections.Generic.HashSet[string]'))
    $packageEntries = [ordered]@{}
    foreach ($assemblyName in $assemblyNames) {
        if ($assemblyName -notmatch '^Mahjong\.[A-Za-z0-9.]+$') { throw "Unexpected project assembly name: $assemblyName" }
        $assemblyPath = Assert-RepositoryPath (Join-Path $outputDir "$assemblyName.dll")
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) { throw "Required project dependency missing: $assemblyName.dll" }
        $packageEntries["$assemblyName.dll"] = $assemblyPath
    }
    $pluginAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $outputDir "$pluginName.dll")).Version.ToString()
    if ($pluginAssemblyVersion -ne $assemblyVersion) { throw "Compiled plugin version $pluginAssemblyVersion does not match $assemblyVersion" }
    $packageEntries["$pluginName.json"] = $manifestPath
    $depsPath = Assert-RepositoryPath (Join-Path $outputDir "$pluginName.deps.json")
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) { throw 'Compiled dependency manifest is missing.' }
    $packageEntries["$pluginName.deps.json"] = $depsPath
    foreach ($layout in @('emj.json', 'emj_l.json')) {
        $layoutPath = Assert-RepositoryPath (Join-Path $outputDir "layouts\$layout")
        if (-not (Test-Path -LiteralPath $layoutPath -PathType Leaf)) { throw "Required gameplay layout missing: $layout" }
        $packageEntries["layouts/$layout"] = $layoutPath
    }
    foreach ($file in @('LICENSE.md', 'NOTICE', 'README-CN.md')) {
        $filePath = Assert-RepositoryPath (Join-Path $repoRoot $file)
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw "Distribution document missing: $file" }
        $packageEntries[$file] = $filePath
    }

    if ($VerifyOnly) {
        # Development verification deliberately produces no installable/source ZIP and
        # cannot be mistaken for a completed distribution or source-ZIP rebuild.
        $verification = [ordered]@{
            status = 'VerifiedWithoutPackaging'; pluginVersion = $pluginVersion; buildPassed = $true;
            packagesProduced = $false; sourceZipRebuildExecuted = $false; cnLiveGameVerified = $false;
            sdkVersion = $actualSdk; targetGame = $evidence.gameVersion;
            testsSkipped = [bool]$SkipTests; testRuntimeRollForward = 'Major'; tests = $testResults;
            sessionAnalyzerTests = $analyzerTests; akochanOfflineValidation = $akochanOfflineValidation; globalAiValidation = $globalAiValidation;
            completedUtc = [DateTime]::UtcNow.ToString('O')
        }
        [IO.File]::WriteAllText($manifestOutput, ($verification | ConvertTo-Json -Depth 10), $utf8)
        Write-Log 'Verification completed. No package created, no install index updated, no release published.'
        $verificationHashFiles = [ordered]@{ 'build-manifest.json' = $manifestOutput; 'build-log.txt' = $buildLog }
        foreach ($entry in $testFiles.GetEnumerator()) { $verificationHashFiles[$entry.Key] = $entry.Value }
        $verificationHashes = foreach ($entry in $verificationHashFiles.GetEnumerator()) {
            (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $entry.Key
        }
        [IO.File]::WriteAllLines((Join-Path $artifactsDir 'SHA256SUMS'), $verificationHashes, $utf8)
        return
    }

    $finalSourceEntries = Get-SourceEntries
    if ($finalSourceEntries.Count -ne $sourceEntries.Count) { throw 'Source set changed during build.' }
    if ((Test-Path -LiteralPath (Join-Path $repoRoot '.git')) -and
        ((& git rev-parse HEAD | Out-String).Trim() -ne $headCommit)) { throw 'Source commit changed during build.' }
    foreach ($entry in $finalSourceEntries.GetEnumerator()) {
        if (-not $initialSourceHashes.ContainsKey($entry.Key) -or
            (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash -ne $initialSourceHashes[$entry.Key]) {
            throw 'Reviewed source changed during build.'
        }
    }
    $sourceListOutput = Assert-RepositoryPath (Join-Path $artifactsDir 'source-file-manifest.json')
    $sourceFileList = foreach ($entry in $sourceEntries.GetEnumerator()) {
        [ordered]@{ path = $entry.Key; sha256 = (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    [IO.File]::WriteAllText($sourceListOutput, ([ordered]@{ baseCommit = $headCommit; files = @($sourceFileList) } | ConvertTo-Json -Depth 5), $utf8)
    $sourceEntries['source-file-manifest.json'] = $sourceListOutput
    $sourceZipName = "$pluginName-$pluginVersion-source.zip"
    $sourceNotice = Assert-RepositoryPath (Join-Path $repoRoot 'SOURCE.txt')
    if (-not [IO.File]::ReadAllText($sourceNotice).Contains($sourceZipName)) {
        throw 'SOURCE.txt does not identify the exact corresponding-source archive.'
    }
    $packageEntries['SOURCE.txt'] = $sourceNotice

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sourceZip = Join-Path $artifactsDir $sourceZipName
    $pluginZip = Join-Path $artifactsDir "$pluginName-$pluginVersion.zip"
    New-Zip $sourceZip $sourceEntries
    # Build the ACTUAL extracted source archive. This catches source-filter omissions that a working-tree build cannot.
    $sourceVerifyDir = Assert-RepositoryPath (Join-Path $artifactsDir ('source-verification\' + [Guid]::NewGuid().ToString('N')))
    [IO.Compression.ZipFile]::ExtractToDirectory($sourceZip, $sourceVerifyDir)
    $null = Get-ReviewedSourceEntries $sourceVerifyDir
    $sourceRebuildLog = Assert-RepositoryPath (Join-Path $artifactsDir 'source-rebuild.log')
    [IO.File]::WriteAllText($sourceRebuildLog, '', $utf8)
    Invoke-Dotnet @('build', (Join-Path $sourceVerifyDir "$pluginName\$pluginName.csproj"),
        '--configuration', 'Release', '-p:RestoreLockedMode=true', "-p:DalamudLibPath=$DalamudLibPath") $sourceRebuildLog
    Invoke-Dotnet @('build', (Join-Path $sourceVerifyDir $replayProjectRelativePath),
        '--configuration', 'Release', '-p:RestoreLockedMode=true') $sourceRebuildLog
    Invoke-Dotnet @('build', (Join-Path $sourceVerifyDir $globalProbeRelativePath),
        '--configuration', 'Release', '-p:RestoreLockedMode=true') $sourceRebuildLog
    Invoke-Dotnet @('build', (Join-Path $sourceVerifyDir $ponProbeRelativePath),
        '--configuration', 'Release', '-p:RestoreLockedMode=true', "-p:DalamudLibPath=$DalamudLibPath") $sourceRebuildLog
    Invoke-Dotnet @('build', (Join-Path $sourceVerifyDir $chiProbeRelativePath),
        '--configuration', 'Release', '-p:RestoreLockedMode=true', "-p:DalamudLibPath=$DalamudLibPath") $sourceRebuildLog
    Invoke-Dotnet @('build', (Join-Path $sourceVerifyDir $journalReplayRelativePath),
        '--configuration', 'Release', '-p:RestoreLockedMode=true', "-p:DalamudLibPath=$DalamudLibPath") $sourceRebuildLog
    # The archive must reconstruct every distributed project DLL byte-for-byte.
    # This also detects local/untracked/ignored source accidentally compiled by a glob.
    foreach ($assemblyName in $assemblyNames) {
        $rebuilt = Join-Path $sourceVerifyDir "$pluginName/bin/Release/net10.0-windows/$assemblyName.dll"
        if (-not (Test-Path -LiteralPath $rebuilt) -or
            (Get-FileHash -LiteralPath $rebuilt).Hash -ne (Get-FileHash -LiteralPath $packageEntries["$assemblyName.dll"]).Hash) {
            throw "Source ZIP does not reproduce distributed assembly: $assemblyName"
        }
        $packageEntries["$assemblyName.dll"] = $rebuilt
    }
    # Ship static files from the verified archive, never from a subsequently edited tree.
    foreach ($name in @($packageEntries.Keys)) {
        if ($sourceEntries.Contains($name)) { $packageEntries[$name] = Join-Path $sourceVerifyDir $name }
    }
    $packageEntries["$pluginName.deps.json"] = Join-Path $sourceVerifyDir "$pluginName/bin/Release/net10.0-windows/$pluginName.deps.json"
    New-Zip $pluginZip $packageEntries
    $buildManifest = [ordered]@{
        status = 'Completed'; completedUtc = [DateTime]::UtcNow.ToString('O'); sdkVersion = $actualSdk; sourceBaseCommit = $headCommit;
        sourceArchiveIncludesWorkingChanges = $false; sourceAssembliesMatch = $true; sourcePackageChecks = $sourcePackageChecks;
        pluginVersion = $pluginVersion; gameVersion = $evidence.gameVersion;
        dalamud = $evidence.dalamud; clientStructs = $evidence.clientStructs; buildPassed = $true;
        testsSkipped = [bool]$SkipTests; testRuntimeRollForward = 'Major'; tests = $testResults; sessionAnalyzerTests = $analyzerTests;
        akochanOfflineValidation = $akochanOfflineValidation; globalAiValidation = $globalAiValidation; journalStorageValidation = $journalValidation;
        cnLiveGameVerified = $false; sourceArchiveRebuildPassed = $true; pluginZip = [IO.Path]::GetFileName($pluginZip); sourceZip = $sourceZipName;
        pluginEntries = @($packageEntries.Keys); sourceFileCount = $sourceEntries.Count
    }
    [IO.File]::WriteAllText($manifestOutput, ($buildManifest | ConvertTo-Json -Depth 10), $utf8)
    Write-Log "Packaged $($packageEntries.Count) entries; framework DLLs are excluded."
    Write-Log "Corresponding source: $sourceZipName ($($sourceEntries.Count) files)."
    Write-Log 'Build and packaging completed. No CN live-game validation was performed.'

    $hashFiles = [ordered]@{}
    $hashFiles[[IO.Path]::GetFileName($pluginZip)] = $pluginZip
    $hashFiles[$sourceZipName] = $sourceZip
    $hashFiles['build-manifest.json'] = $manifestOutput
    $hashFiles['build-log.txt'] = $buildLog
    $hashFiles['source-rebuild.log'] = $sourceRebuildLog
    $hashFiles['akochan-self-check.json'] = $akochanReport
    if ($journalReportPath) { $hashFiles['journal-storage-validation.json'] = $journalReportPath }
    foreach ($entry in $globalReports.GetEnumerator()) { $hashFiles[$entry.Key]=$entry.Value }
    foreach ($entry in $testFiles.GetEnumerator()) { $hashFiles[$entry.Key] = $entry.Value }
    $hashLines = foreach ($entry in $hashFiles.GetEnumerator()) { (Get-FileHash -LiteralPath $entry.Value -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $entry.Key }
    [IO.File]::WriteAllLines((Assert-RepositoryPath (Join-Path $artifactsDir 'SHA256SUMS')), [string[]]$hashLines, $utf8)
    Write-Host "Ready: $pluginZip"
    Write-Host "Source: $sourceZip"
    Write-Host 'Evidence: artifacts/build-manifest.json, artifacts/build-log.txt, artifacts/test-results, artifacts/SHA256SUMS'
}
catch {
    $failure = ConvertTo-PublicBuildText $_.Exception.Message $repoRoot
    if (Get-Variable -Name buildLog -ErrorAction SilentlyContinue) { Write-Log "BUILD FAILED: $failure" }
    if (Get-Variable -Name manifestOutput -ErrorAction SilentlyContinue) {
        $failedAkochanValidation = [ordered]@{ executed = $false; result = 'NotReached'; report = $null; sha256 = $null }
        if (Get-Variable -Name akochanOfflineValidation -ErrorAction SilentlyContinue) {
            $failedAkochanValidation = $akochanOfflineValidation
            if ((Get-Variable -Name akochanReport -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $akochanReport -PathType Leaf)) {
                $failedAkochanValidation.sha256 = (Get-FileHash -LiteralPath $akochanReport -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
        [IO.File]::WriteAllText($manifestOutput, ([ordered]@{
            status = 'Failed'; failedUtc = [DateTime]::UtcNow.ToString('O'); buildPassed = $false;
            cnLiveGameVerified = $false; error = $failure;
            akochanOfflineValidation = $failedAkochanValidation;
            note = 'Any existing ZIP files may belong to an earlier run; this run did not produce a verified delivery.'
        } | ConvertTo-Json -Depth 6), $utf8)
    }
    throw
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    Set-Location -LiteralPath $originalDirectory
}
