[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$sdkVersion = '10.0.100'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$installDir = Join-Path $repoRoot '.work\dotnet'
$dotnetPath = Join-Path $installDir 'dotnet.exe'
$installerDir = Join-Path $repoRoot '.work\bootstrap'
$installerPath = Join-Path $installerDir 'dotnet-install.ps1'

# This script installs only into this checkout and never changes PATH or the system SDK.
if (Test-Path -LiteralPath (Join-Path $installDir "sdk\$sdkVersion\dotnet.dll")) {
    Write-Host "SDK $sdkVersion already exists at $installDir"
    Write-Host "Build with: powershell -NoProfile -File scripts/build-cn.ps1"
    return
}

foreach ($target in @($installDir, $installerDir)) {
    $fullTarget = [IO.Path]::GetFullPath($target)
    if (-not $fullTarget.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Bootstrap target is outside the checkout: $fullTarget"
    }
    $current = $fullTarget
    while ($current -ne $repoRoot) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Refusing a reparse point in the bootstrap path: $current"
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    New-Item -ItemType Directory -Force -Path $fullTarget | Out-Null
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
Write-Host 'Downloading the Microsoft dotnet-install script from https://dot.net/v1/dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath -UseBasicParsing
Write-Host "Installer SHA256: $((Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash)"
& $installerPath -Version $sdkVersion -Architecture x64 -InstallDir $installDir -NoPath
if (-not (Test-Path -LiteralPath (Join-Path $installDir "sdk\$sdkVersion\dotnet.dll"))) {
    throw "The official installer did not produce SDK $sdkVersion. No build was performed."
}
Write-Host "Installed SDK $sdkVersion into $installDir"
Write-Host "Build with: powershell -NoProfile -File scripts/build-cn.ps1"
