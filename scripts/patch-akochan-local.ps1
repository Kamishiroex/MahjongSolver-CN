[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $SourceDirectory,
      [Parameter(Mandatory=$true)][string] $OutputDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$expectedCommit = '53188a0b926fbab38177f88c3cd87d554cf412af'
$expectedLfSha256 = 'a0bac9e40b13e99b9b9e30dd5fd43b71de6225505a118a538cf0915b3a243076'
$patchId = 'mjcn-kan-dora-rinshan-v1'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$utf8 = New-Object Text.UTF8Encoding($false, $true)

function Safe-LocalDirectory([string] $Path) {
    if (-not [IO.Path]::IsPathRooted($Path)) { $Path = Join-Path $repoRoot $Path }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if (-not $full.StartsWith($repoRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Patch paths must be inside this checkout.' }
    $ancestor = $full
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Patch paths must not contain reparse points.'
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    return $full
}
function Hash-Bytes([byte[]] $Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

$SourceDirectory = Safe-LocalDirectory $SourceDirectory
$OutputDirectory = Safe-LocalDirectory $OutputDirectory
if ($OutputDirectory -eq $SourceDirectory -or $OutputDirectory.StartsWith($SourceDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Patch output must be outside the pristine upstream source directory.'
}
$head = & git -C $SourceDirectory rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $head -ne $expectedCommit) { throw 'AKOCHAN_PATCH_COMMIT_MISMATCH' }
$sourceFile = Join-Path $SourceDirectory 'share\types.cpp'
$null = Safe-LocalDirectory ([IO.Path]::GetDirectoryName($sourceFile))
if ((Get-Item -LiteralPath $sourceFile -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'AKOCHAN_PATCH_SOURCE_REPARSE_POINT' }
$originalBytes = [IO.File]::ReadAllBytes($sourceFile)
$text = $utf8.GetString($originalBytes).Replace("`r`n", "`n")
if ((Hash-Bytes $utf8.GetBytes($text)) -ne $expectedLfSha256) { throw 'AKOCHAN_PATCH_SOURCE_MISMATCH' }

# Only a new classification condition is supplied here; upstream source stays local.
# Preserve the original direct-ankan assertion and both already-supported direct open-kan paths.
$needle = '(game_record[i-1]["type"].string_value() == "dora" && game_record[i-2]["type"].string_value() == "ankan")'
$replacement = @'
(i >= 2 && game_record[i-1]["type"].string_value() == "dora" &&
                    (game_record[i-2]["type"].string_value() == "ankan" ||
                     game_record[i-2]["type"].string_value() == "daiminkan" ||
                     game_record[i-2]["type"].string_value() == "kakan"))
'@
if (($text.Split([string[]]@($needle), [StringSplitOptions]::None)).Length -ne 2) { throw 'AKOCHAN_PATCH_ANCHOR_MISMATCH' }
$patchedBytes = $utf8.GetBytes($text.Replace($needle, $replacement.Replace("`r`n", "`n")))
$patchedHash = Hash-Bytes $patchedBytes
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$outputFile = Join-Path $OutputDirectory 'types.cpp'
if (Test-Path -LiteralPath $outputFile) {
    if ((Get-Item -LiteralPath $outputFile -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'AKOCHAN_PATCH_OUTPUT_REPARSE_POINT' }
    if ((Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $patchedHash) {
        throw 'AKOCHAN_PATCH_OUTPUT_EXISTS_DIFFERENTLY'
    }
} else { [IO.File]::WriteAllBytes($outputFile, $patchedBytes) }

[pscustomobject]@{
    id = $patchId; sourceCommit = $expectedCommit; sourceFile = 'share/types.cpp'
    originalLfSha256 = $expectedLfSha256; originalFileSha256 = Hash-Bytes $originalBytes
    patchedSha256 = $patchedHash; patchScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    outputFile = $outputFile
}
