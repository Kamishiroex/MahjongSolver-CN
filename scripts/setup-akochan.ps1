[CmdletBinding()]
param(
    [string] $SourceDirectory = '.work/akochan',
    [string] $BuildDirectory = '.work/akochan-build',
    [string] $Destination = (Join-Path $env:APPDATA 'XIVLauncherCN\pluginConfigs\Mahjong.Plugin.CN\engines\akochan'),
    [switch] $NativeCounterTestsOnly,
    [switch] $GlobalSnapshot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($GlobalSnapshot -and -not $PSBoundParameters.ContainsKey('Destination')) {
    $Destination = Join-Path $env:APPDATA 'XIVLauncherCN\pluginConfigs\Mahjong.Plugin.CN\engines\akochan-global-v5'
}
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRepository = 'https://github.com/critter-mj/akochan'
$sourceCommit = '53188a0b926fbab38177f88c3cd87d554cf412af'
$boostUrl = 'https://archives.boost.io/release/1.70.0/source/boost_1_70_0.tar.bz2'
$boostHash = '430ae8354789de4fd19ee52f3b1f739e1fba576f0aded0897c3c2bc00fb38778'
$utf8 = New-Object Text.UTF8Encoding($false)
$savedLocation = Get-Location
$savedEnvironment = @{}

function Resolve-TaskPath([string] $Path, [bool] $InsideRepository) {
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $repoRoot $Path }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($InsideRepository -and -not $full.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Source/build paths must stay below the checkout: $full"
    }
    $current = $full
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a reparse point in a task path: $current"
        }
        $parent = [IO.Path]::GetDirectoryName($current)
        if ($parent -eq $current) { break }
        $current = $parent
    }
    return $full
}

function Assert-EmptyDestination {
    if (Test-Path -LiteralPath $Destination) {
        if (-not (Test-Path -LiteralPath $Destination -PathType Container)) { throw "Destination is not a directory: $Destination" }
        if (@(Get-ChildItem -LiteralPath $Destination -Force).Count -ne 0) {
            throw "Destination already contains files. Nothing was overwritten. Choose a new -Destination; existing engine files are never deleted or replaced: $Destination"
        }
    }
}

function Invoke-Checked([string] $Program, [string[]] $Arguments) {
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $Program @Arguments | Out-Host
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { throw "$Program failed with exit code $code" }
}

function Find-VcVars {
    $candidates = New-Object 'System.Collections.Generic.List[string]'
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path -LiteralPath $vswhere) {
        # An existing compiler can be usable even if a later VS install was cancelled.
        $installations = & $vswhere -all -products '*' -property installationPath
        foreach ($installation in $installations) {
            if ($installation) { $candidates.Add((Join-Path $installation 'VC\Auxiliary\Build\vcvars64.bat')) }
        }
    }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        $vsRoot = Join-Path $base 'Microsoft Visual Studio'
        if (Test-Path -LiteralPath $vsRoot) {
            foreach ($year in @(Get-ChildItem -LiteralPath $vsRoot -Directory)) {
                foreach ($edition in @(Get-ChildItem -LiteralPath $year.FullName -Directory)) {
                    $candidates.Add((Join-Path $edition.FullName 'VC\Auxiliary\Build\vcvars64.bat'))
                }
            }
        }
    }
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate } }
    throw 'No existing Visual C++ x64 build toolchain was found. Install/select VS C++ Build Tools with a Windows SDK, then rerun. This script does not install global tools.'
}

function Invoke-Compile([string] $Name, [string[]] $Arguments) {
    $log = Join-Path $runDirectory "$Name.log"
    [IO.File]::WriteAllText((Join-Path $runDirectory "$Name.arguments.json"), (ConvertTo-Json -InputObject $Arguments), $utf8)
    Write-Host "Building $Name (log: $log)"
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & $compiler @Arguments *> $log
        $code = $LASTEXITCODE
    } finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) {
        Get-Content -LiteralPath $log -Tail 25 | Out-Host
        throw "$Name build failed with exit code $code. Full output: $log"
    }
}

function Invoke-NativeSample([string] $EngineDirectory, [string] $InputText) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $EngineDirectory 'system.exe'
    $start.WorkingDirectory = $EngineDirectory
    $start.Arguments = 'pipe setup_mjai.json 0'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['OMP_NUM_THREADS'] = '2'
    $start.EnvironmentVariables['OMP_THREAD_LIMIT'] = '2'
    $start.EnvironmentVariables['OMP_DYNAMIC'] = 'FALSE'
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $savedInputEncoding = [Console]::InputEncoding
    try {
        # .NET Framework lacks ProcessStartInfo.StandardInputEncoding and creates
        # its StreamWriter from Console.InputEncoding, emitting its preamble on
        # process start. Configure BOM-free UTF-8 before creating that writer.
        [Console]::InputEncoding = $utf8
        if (-not $process.Start()) { throw 'Native engine failed to start.' }
        [Console]::InputEncoding = $savedInputEncoding
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        # Windows PowerShell/.NET may choose UTF-8 with a BOM for StreamWriter.
        # json11 rejects that BOM; write exact UTF-8 bytes to the underlying pipe.
        $inputBytes = $utf8.GetBytes($InputText)
        $process.StandardInput.BaseStream.Write($inputBytes, 0, $inputBytes.Length)
        $process.StandardInput.BaseStream.Flush()
        $process.StandardInput.BaseStream.Close()
        if (-not $process.WaitForExit(120000)) {
            $process.Kill()
            throw 'Native upstream sample timed out after 120 seconds.'
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errors = $stderr.GetAwaiter().GetResult()
        $timer.Stop()
        if ($process.ExitCode -ne 0 -or $errors.Length -ne 0) { throw "Native sample failed ($($process.ExitCode)): $errors $output" }
        $actions = @(ConvertFrom-Json -InputObject $output)
        if ($actions.Count -ne 1 -or $actions[0].type -ne 'dahai' -or $actions[0].pai -ne 'N' -or $actions[0].actor -ne 0) {
            throw "Unexpected native result for the pinned upstream sample: $output"
        }
        return [ordered]@{ seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 3); exitCode = $process.ExitCode; stdout = $output.Trim(); stderr = $errors }
    } finally {
        [Console]::InputEncoding = $savedInputEncoding
        $process.Dispose()
    }
}

function Invoke-CounterProbe([string] $Executable, [string] $Mode) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = $Mode
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Native counter probe failed to start.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Native counter probe timed out.' }
        return [pscustomobject]@{ exitCode = $process.ExitCode; stdout = $stdout.GetAwaiter().GetResult(); stderr = $stderr.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}

try {
    $SourceDirectory = Resolve-TaskPath $SourceDirectory $true
    $BuildDirectory = Resolve-TaskPath $BuildDirectory $true
    $Destination = Resolve-TaskPath $Destination $false
    if ($SourceDirectory -eq $BuildDirectory -or $BuildDirectory.StartsWith($SourceDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'BuildDirectory must be outside the upstream source checkout.'
    }
    if (-not $NativeCounterTestsOnly) { Assert-EmptyDestination }
    $git = (Get-Command git -ErrorAction Stop).Source
    $tar = (Get-Command tar -ErrorAction Stop).Source
    $vcvars = Find-VcVars
    if ($vcvars -match '["%\r\n]') { throw 'Unsupported shell characters in the installed VS toolchain path.' }
    $vcEnvironment = & $env:ComSpec /d /c "call `"$vcvars`" >nul && set"
    if ($LASTEXITCODE -ne 0) { throw "Existing VS toolchain initialization failed: $vcvars" }
    foreach ($entry in $vcEnvironment) {
        if ($entry -match '^(PATH|INCLUDE|LIB|LIBPATH|VCToolsInstallDir|VCToolsRedistDir|WindowsSdkDir|WindowsSDKVersion)=(.*)$') {
            $savedEnvironment[$matches[1]] = [Environment]::GetEnvironmentVariable($matches[1], 'Process')
            [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
        }
    }
    $compiler = (Get-Command cl.exe -ErrorAction Stop).Source
    if (-not $env:WindowsSdkDir -or -not (Test-Path -LiteralPath (Join-Path $env:WindowsSdkDir "Include\$($env:WindowsSDKVersion.TrimEnd('\'))\um\Windows.h"))) {
        throw 'The existing toolchain did not provide a usable Windows SDK.'
    }
    Write-Host "Compiler: $compiler; Windows SDK: $env:WindowsSDKVersion"

    if (-not (Test-Path -LiteralPath $SourceDirectory)) {
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($SourceDirectory)) -Force | Out-Null
        Invoke-Checked $git @('clone', '--no-checkout', $sourceRepository, $SourceDirectory)
        Invoke-Checked $git @('-C', $SourceDirectory, 'checkout', '--detach', $sourceCommit)
    }
    $actualCommit = & $git -C $SourceDirectory rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $actualCommit -ne $sourceCommit) { throw "Source HEAD must be exactly $sourceCommit; existing sources were not changed: $SourceDirectory" }
    $dirty = & $git -C $SourceDirectory status --porcelain=v1
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'The upstream checkout contains changes. Preserve them and select a separate clean -SourceDirectory.' }

    $toolsDirectory = Resolve-TaskPath (Join-Path ([IO.Path]::GetDirectoryName($BuildDirectory)) 'akochan-tools') $true
    New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
    $archive = Join-Path $toolsDirectory 'boost_1_70_0.tar.bz2'
    if (-not (Test-Path -LiteralPath $archive)) {
        Write-Host "Downloading pinned Boost 1.70.0: $boostUrl"
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $boostUrl -OutFile $archive -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $boostHash) { throw "Boost SHA256 mismatch: $archive" }
    $boostDirectory = Join-Path $toolsDirectory 'boost_1_70_0'
    if (-not (Test-Path -LiteralPath (Join-Path $boostDirectory 'boost\version.hpp'))) {
        Invoke-Checked $tar @('-xf', $archive, '-C', $toolsDirectory, 'boost_1_70_0/boost', 'boost_1_70_0/LICENSE_1_0.txt')
    }
    if (-not (Select-String -LiteralPath (Join-Path $boostDirectory 'boost\version.hpp') -Pattern '^#define BOOST_VERSION 107000$' -Quiet)) { throw 'Unexpected extracted Boost headers.' }

    $runDirectory = Resolve-TaskPath (Join-Path $BuildDirectory ('build-' + [Guid]::NewGuid().ToString('N'))) $true
    $objectDirectory = Join-Path $runDirectory 'obj'
    $stageDirectory = Join-Path $runDirectory 'installation'
    New-Item -ItemType Directory -Path $objectDirectory, $stageDirectory -Force | Out-Null
    Set-Location -LiteralPath $SourceDirectory
    $common = @('/nologo', '/std:c++14', '/EHsc', '/MD', '/O2', '/openmp', '/utf-8', '/DWINSTD', '/DNPROCS=2', '/DNOMINMAX', '/D_WIN32_WINNT=0x0601', '/DBOOST_ALL_NO_LIB', '/DBOOST_ERROR_CODE_HEADER_ONLY', '/D_CRT_SECURE_NO_WARNINGS', "/I$boostDirectory", "/Fo$objectDirectory\")
    $shared = @(Get-ChildItem -LiteralPath (Join-Path $SourceDirectory 'share') -Filter '*.cpp' | Sort-Object Name | ForEach-Object { 'share/' + $_.Name })
    $originalShared = $shared
    $patch = & (Join-Path $PSScriptRoot 'patch-akochan-local.ps1') -SourceDirectory $SourceDirectory -OutputDirectory (Join-Path $runDirectory 'patched-share')
    # The pristine upstream tree stays unchanged. Both native components use exactly
    # this generated local TU; only its relative header lookup needs the original share path.
    $common += "/I$(Join-Path $SourceDirectory 'share')"
    $shared = @($shared | ForEach-Object { if ($_ -eq 'share/types.cpp') { $patch.outputFile } else { $_ } })
    $patchManifest = [ordered]@{ id = $patch.id; sourceFile = $patch.sourceFile; sourceCommit = $patch.sourceCommit;
        originalLfSha256 = $patch.originalLfSha256; patchedSha256 = $patch.patchedSha256; patchScriptSha256 = $patch.patchScriptSha256 }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'local-patch.json'), (ConvertTo-Json -InputObject $patchManifest -Depth 5), $utf8)

    $probeSource = Join-Path $repoRoot 'tests\native\akochan-kan-counter.cpp'
    $baselineProbe = Join-Path $runDirectory 'kan-counter-unpatched.exe'
    $patchedProbe = Join-Path $runDirectory 'kan-counter-patched.exe'
    Invoke-Compile 'kan-counter-unpatched' ($common + @('/Gy', "/Fe$baselineProbe", $probeSource) + $originalShared + @('/link', 'ws2_32.lib', 'mswsock.lib', '/OPT:REF'))
    Invoke-Compile 'kan-counter-patched' ($common + @('/Gy', "/Fe$patchedProbe", $probeSource) + $shared + @('/link', 'ws2_32.lib', 'mswsock.lib', '/OPT:REF'))
    $baseline = Invoke-CounterProbe $baselineProbe '--expect-unpatched'
    $patched = Invoke-CounterProbe $patchedProbe '--expect-fixed'
    $regression = Invoke-CounterProbe $baselineProbe '--expect-fixed'
    $ankanGuard = Invoke-CounterProbe $patchedProbe '--invalid-ankan'
    if ($baseline.exitCode -ne 0 -or $patched.exitCode -ne 0 -or $regression.exitCode -ne 1 -or
        $ankanGuard.exitCode -eq 0 -or -not $ankanGuard.stderr.Contains('ankan')) {
        throw 'Native kan counter comparison or preserved ankan assertion failed.'
    }
    $nativeCounters = [ordered]@{ sourceCommit = $sourceCommit; patch = $patchManifest;
        baseline = (ConvertFrom-Json -InputObject $baseline.stdout); patched = (ConvertFrom-Json -InputObject $patched.stdout);
        baselineAgainstFixedExpectations = (ConvertFrom-Json -InputObject $regression.stdout);
        directAnkanStillRejected = $true; directAnkanExitCode = $ankanGuard.exitCode }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'native-kan-counter-results.json'), (ConvertTo-Json -InputObject $nativeCounters -Depth 8), $utf8)
    if ($NativeCounterTestsOnly) {
        Write-Host "Real native counter checks passed; preserved direct-ankan assertion. Evidence: $runDirectory"
        return
    }
    $globalPatchManifest = $null
    if ($GlobalSnapshot) {
        $globalPatch = & (Join-Path $PSScriptRoot 'patch-akochan-global.ps1') -SourceDirectory $SourceDirectory -CounterPatchedTypes $patch.outputFile -OutputDirectory (Join-Path $runDirectory 'patched-global')
        $shared = @($shared | ForEach-Object { if ($_ -eq $patch.outputFile) { $globalPatch.types } else { $_ } })
        $common += @("/I$SourceDirectory", "/I$(Join-Path $SourceDirectory 'ai_src')")
        $globalPatchManifest = [ordered]@{ id=$globalPatch.id; typesSha256=$globalPatch.typesSha256; mjutilSha256=$globalPatch.mjutilSha256; bridgeSha256=$globalPatch.bridgeSha256; selectorSha256=$globalPatch.selectorSha256; patchScriptSha256=$globalPatch.patchScriptSha256 }
    }
    $ai = @(Get-ChildItem -LiteralPath (Join-Path $SourceDirectory 'ai_src') -Filter '*.cpp' | Sort-Object Name | ForEach-Object { 'ai_src/' + $_.Name })
    $main = @(Get-ChildItem -LiteralPath $SourceDirectory -Filter '*.cpp' | Sort-Object Name | ForEach-Object { $_.Name })
    if ($GlobalSnapshot) {
        $ai = @($ai | ForEach-Object { if ($_ -eq 'ai_src/mjutil.cpp') { $globalPatch.mjutil } elseif ($_ -eq 'ai_src/selector.cpp') { $globalPatch.selector } else { $_ } })
        $main = @($main | ForEach-Object { if ($_ -eq 'main.cpp') { $globalPatch.main } else { $_ } }) + @($globalPatch.bridge)
    }
    # Upstream declares these same globals both const and non-const in different TUs.
    # MSVC distinguishes their decorated names; bind references to the existing
    # definitions at link time. The separate recorded local types.cpp patch fixes
    # replacement-draw classification; learned AI parameters are unchanged.
    $aliases = @('/ALTERNATENAME:?out_console@@3_NB=?out_console@@3_NA', '/ALTERNATENAME:?tactics_json@@3V?$array@VJson@json11@@$03@std@@B=?tactics_json@@3V?$array@VJson@json11@@$03@std@@A')
    Invoke-Compile 'ai' ($common + @('/LD', "/Fe$(Join-Path $stageDirectory 'ai.dll')") + $ai + $shared + @('/link', 'ws2_32.lib', 'mswsock.lib', "/IMPLIB:$(Join-Path $runDirectory 'ai.lib')") + $aliases)
    Invoke-Compile 'main' ($common + @("/Fe$(Join-Path $stageDirectory 'system.exe')") + $main + $shared + @('/link', (Join-Path $runDirectory 'ai.lib'), 'ws2_32.lib', 'mswsock.lib', '/STACK:16777216'))

    Copy-Item -LiteralPath (Join-Path $SourceDirectory 'setup_mjai.json'), (Join-Path $SourceDirectory 'LICENSE') -Destination $stageDirectory
    Copy-Item -LiteralPath (Join-Path $SourceDirectory 'params') -Destination $stageDirectory -Recurse
    $licenses = Join-Path $stageDirectory 'licenses'
    New-Item -ItemType Directory -Path $licenses | Out-Null
    Copy-Item -LiteralPath (Join-Path $boostDirectory 'LICENSE_1_0.txt') -Destination (Join-Path $licenses 'BOOST_LICENSE_1_0.txt')
    $jsonHeader = [IO.File]::ReadAllText((Join-Path $SourceDirectory 'share\json11.hpp'))
    $licenseEnd = $jsonHeader.IndexOf('*/')
    if ($licenseEnd -lt 0) { throw 'Unable to locate the bundled json11 license notice.' }
    [IO.File]::WriteAllText((Join-Path $licenses 'json11-license.txt'), $jsonHeader.Substring(0, $licenseEnd + 2), $utf8)
    $redistRoot = Join-Path $env:VCToolsRedistDir 'x64'
    foreach ($runtime in @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcomp140.dll')) {
        $runtimeFile = @(Get-ChildItem -LiteralPath $redistRoot -Filter $runtime -File -Recurse | Sort-Object FullName | Select-Object -First 1)
        if ($runtimeFile.Count -ne 1) { throw "Required existing Visual C++ runtime DLL not found: $runtime" }
        Copy-Item -LiteralPath $runtimeFile[0].FullName -Destination $stageDirectory
    }

    $sample = @(Get-Content -LiteralPath (Join-Path $SourceDirectory 'haifu_log_sample.json') -Encoding UTF8 -TotalCount 3 | ForEach-Object { ConvertFrom-Json -InputObject $_ })
    if ($sample.Count -ne 3 -or $sample[0].type -ne 'start_game' -or $sample[1].type -ne 'start_kyoku' -or $sample[2].type -ne 'tsumo') { throw 'Pinned upstream sample has an unexpected shape.' }
    for ($seat = 1; $seat -le 3; $seat++) { $sample[1].tehais[$seat] = @('?') * 13 }
    $sample[0] | Add-Member -MemberType NoteProperty -Name can_act -Value $false
    $sample[1] | Add-Member -MemberType NoteProperty -Name can_act -Value $false
    $payload = (($sample | ForEach-Object { ConvertTo-Json -InputObject $_ -Depth 20 -Compress }) -join "`n") + "`n"
    [IO.File]::WriteAllText((Join-Path $runDirectory 'upstream-first-draw-public.jsonl'), $payload, $utf8)
    $result = Invoke-NativeSample $stageDirectory $payload
    [IO.File]::WriteAllText((Join-Path $runDirectory 'selftest-result.json'), (ConvertTo-Json -InputObject $result -Depth 5), $utf8)
    $dirtyAfterBuild = & $git -C $SourceDirectory status --porcelain=v1
    if ($LASTEXITCODE -ne 0 -or $dirtyAfterBuild) { throw 'Unexpected upstream source changes detected after building.' }

    $files = [ordered]@{}
    foreach ($file in @(Get-ChildItem -LiteralPath $stageDirectory -File -Recurse | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($stageDirectory.Length + 1).Replace('\', '/')
        $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    $manifest = [ordered]@{ schema = 1; sourceRepository = $sourceRepository; sourceCommit = $sourceCommit; executable = 'system.exe'; tactics = 'setup_mjai.json'; threads = 2; localPatches = @($patchManifest); files = $files }
    if ($GlobalSnapshot) { $manifest['globalSnapshot'] = $globalPatchManifest }
    [IO.File]::WriteAllText((Join-Path $stageDirectory 'akochan-installation.json'), (ConvertTo-Json -InputObject $manifest -Depth 5), $utf8)
    Assert-EmptyDestination
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($entry in @(Get-ChildItem -LiteralPath $stageDirectory)) { Copy-Item -LiteralPath $entry.FullName -Destination $Destination -Recurse }
    $installedResult = Invoke-NativeSample $Destination $payload
    $report = [ordered]@{ sourceCommit = $sourceCommit; boostSha256 = $boostHash; compiler = $compiler; compilerVersion = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion; windowsSdk = $env:WindowsSDKVersion; destination = $Destination; nativeSelfTest = $installedResult; files = $files.Count; logDirectory = $runDirectory }
    [IO.File]::WriteAllText((Join-Path $runDirectory 'installation-result.json'), (ConvertTo-Json -InputObject $report -Depth 6), $utf8)
    Write-Host "Installed locally: $Destination"
    Write-Host "Real upstream sample: $($installedResult.stdout); $($installedResult.seconds)s"
    Write-Host "Build/validation evidence: $runDirectory"
    Write-Host 'No game/CN validation is implied. akochan uses its own restrictive LICENSE; do not include this local installation in the plugin release ZIP.'
} finally {
    Set-Location $savedLocation
    foreach ($entry in $savedEnvironment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
}
