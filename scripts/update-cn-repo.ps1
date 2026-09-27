[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')] [string] $Repository,
    [string] $Tag = 'v4.1.4',
    [string] $ArtifactDirectory = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $ArtifactDirectory) { $ArtifactDirectory = Join-Path $root 'artifacts' }
$ArtifactDirectory = (Resolve-Path -LiteralPath $ArtifactDirectory -ErrorAction Stop).Path
$utf8 = New-Object Text.UTF8Encoding($false)
$plugin = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root 'Mahjong.Plugin.CN\Mahjong.Plugin.CN.json') | ConvertFrom-Json
$build = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $ArtifactDirectory 'build-manifest.json') | ConvertFrom-Json
if ($build.status -ne 'Completed' -or -not $build.buildPassed -or $build.testsSkipped -or -not $build.sourceArchiveRebuildPassed) {
    throw 'A completed tested build and verified source archive are required.'
}
if ($build.sessionAnalyzerTests.skipped -or $build.sessionAnalyzerTests.executed -lt 1 -or
    $build.sessionAnalyzerTests.passed -ne $build.sessionAnalyzerTests.executed) {
    throw 'Completed local-session analyzer tests are required.'
}
$release = Invoke-RestMethod -Uri ("https://api.github.com/repos/$Repository/releases/tags/" + [Uri]::EscapeDataString($Tag))
if ($release.draft -or $release.tag_name -ne $Tag) { throw 'The release must already be publicly published.' }
# GitHub's tag response can retain an empty embedded asset list after upload. Read
# the release's asset endpoint and still verify every public download before indexing.
$publishedAssets = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/$($release.id)/assets?per_page=100"
$expectedAssets = @($build.pluginZip, $build.sourceZip, 'SHA256SUMS')
$pluginUrl = $null
foreach ($name in $expectedAssets) {
    $matches = @($publishedAssets | Where-Object { $_.name -eq $name -and $_.state -eq 'uploaded' })
    if ($matches.Count -ne 1) { throw "Missing or ambiguous published asset: $name" }
    $asset = $matches[0]
    if ($asset.size -le 0 -or $asset.browser_download_url -notlike "https://github.com/$Repository/releases/download/*") {
        throw "Unexpected release asset URL or size: $name"
    }
    $path = Join-Path $ArtifactDirectory $name
    if ((Get-Item -LiteralPath $path).Length -ne $asset.size) { throw "Published size mismatch: $name" }
    if ($asset.PSObject.Properties.Name -contains 'digest' -and $asset.digest) {
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($asset.digest -ne ('sha256:' + $hash)) { throw "Published digest mismatch: $name" }
    }
    $response = Invoke-WebRequest -UseBasicParsing -Method Head -Uri $asset.browser_download_url
    if ($response.StatusCode -ne 200) { throw "Asset is not publicly downloadable: $name" }
    if ($name -eq $build.pluginZip) { $pluginUrl = $asset.browser_download_url }
}

$entry = [ordered]@{}
foreach ($property in $plugin.PSObject.Properties) { $entry[$property.Name] = $property.Value }
$entry['RepoUrl'] = "https://github.com/$Repository"
$entry['IsTestingExclusive'] = $false
$entry['DownloadLinkInstall'] = $pluginUrl
$entry['DownloadLinkUpdate'] = $pluginUrl
$entry['DownloadLinkTesting'] = $pluginUrl
$entry['AcceptsFeedback'] = $false
$entry['LastUpdate'] = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$json = ConvertTo-Json -InputObject @($entry) -Depth 10
[IO.File]::WriteAllText((Join-Path $root 'repo.json'), $json + [Environment]::NewLine, $utf8)
Write-Host "Created CN repo.json from the existing release $($release.html_url)"
Write-Host 'Commit and push repo.json, then verify its public raw URL before giving an installation address.'
