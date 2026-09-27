[CmdletBinding()]
param([string] $SourceDirectory = '.work/akochan', [string] $BuildDirectory = '.work/akochan-patch-tests')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$utf8 = New-Object Text.UTF8Encoding($false)
foreach ($name in @('SourceDirectory', 'BuildDirectory')) {
    $value = Get-Variable -Name $name -ValueOnly
    if (-not [IO.Path]::IsPathRooted($value)) { $value = Join-Path $repoRoot $value }
    $value = [IO.Path]::GetFullPath($value)
    if (-not $value.StartsWith($repoRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Test paths must stay inside this checkout.' }
    $ancestor = $value
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Test paths must not contain reparse points.' }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    Set-Variable -Name $name -Value $value
}
$run = Join-Path $BuildDirectory ('test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
$patchScript = Join-Path $PSScriptRoot 'patch-akochan-local.ps1'
$sourceFile = Join-Path $SourceDirectory 'share\types.cpp'
$before = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash
$results = New-Object 'System.Collections.Generic.List[object]'
function Expect-Code([string] $Code, [scriptblock] $Body) {
    try { & $Body | Out-Null } catch {
        if ($_.Exception.Message -ne $Code) { throw }
        $results.Add([ordered]@{ test = $Code; passed = $true }); return
    }
    throw "Expected rejection was not raised: $Code"
}
$output = Join-Path $run 'generated'
$first = & $patchScript -SourceDirectory $SourceDirectory -OutputDirectory $output
$patchedBytes = [IO.File]::ReadAllBytes($first.outputFile)
$timestamp = (Get-Item -LiteralPath $first.outputFile).LastWriteTimeUtc
$again = & $patchScript -SourceDirectory $SourceDirectory -OutputDirectory $output
if ($again.patchedSha256 -ne $first.patchedSha256 -or (Get-Item -LiteralPath $first.outputFile).LastWriteTimeUtc -ne $timestamp) { throw 'Duplicate patch changed output.' }
$results.Add([ordered]@{ test = 'IDEMPOTENT_GENERATION'; passed = $true })

[IO.File]::WriteAllText($first.outputFile, 'test-owned-different-output', $utf8)
Expect-Code 'AKOCHAN_PATCH_OUTPUT_EXISTS_DIFFERENTLY' { & $patchScript -SourceDirectory $SourceDirectory -OutputDirectory $output }
if ([IO.File]::ReadAllText($first.outputFile) -ne 'test-owned-different-output') { throw 'Patch overwrote an unexpected file.' }

$fixture = Join-Path $run 'wrong-source'
& git clone --quiet --shared --no-checkout -- $SourceDirectory $fixture
if ($LASTEXITCODE -ne 0) { throw 'Failed to create isolated local test source.' }
New-Item -ItemType Directory -Path (Join-Path $fixture 'share') -Force | Out-Null
$fixtureFile = Join-Path $fixture 'share\types.cpp'
[IO.File]::WriteAllText($fixtureFile, 'test-owned-different-source', $utf8)
Expect-Code 'AKOCHAN_PATCH_SOURCE_MISMATCH' { & $patchScript -SourceDirectory $fixture -OutputDirectory (Join-Path $run 'wrong-source-output') }
[IO.File]::WriteAllBytes($fixtureFile, $patchedBytes)
Expect-Code 'AKOCHAN_PATCH_SOURCE_MISMATCH' { & $patchScript -SourceDirectory $fixture -OutputDirectory (Join-Path $run 'already-patched-source-output') }
& git -C $fixture update-ref HEAD HEAD~1
if ($LASTEXITCODE -ne 0) { throw 'Failed to prepare isolated wrong-version fixture.' }
Expect-Code 'AKOCHAN_PATCH_COMMIT_MISMATCH' { & $patchScript -SourceDirectory $fixture -OutputDirectory (Join-Path $run 'wrong-version-output') }
if ((Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash -ne $before) { throw 'Pristine upstream file changed.' }
$results.Add([ordered]@{ test = 'PRISTINE_SOURCE_UNCHANGED'; passed = $true })
$report = [ordered]@{ cases = $results.ToArray(); count = $results.Count; patchedSha256 = $first.patchedSha256 }
[IO.File]::WriteAllText((Join-Path $run 'result.json'), (ConvertTo-Json -InputObject $report -Depth 5), $utf8)
Write-Host "Patch guards passed: $($results.Count); evidence: $run"
