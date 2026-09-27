# Shared, side-effect-free source selection and report redaction helpers.
Set-StrictMode -Version Latest

function Get-ReviewedSourceEntries([string] $Root) {
    $Root = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $listPath = Join-Path $Root 'scripts/source-files.txt'
    if (-not (Test-Path -LiteralPath $listPath -PathType Leaf)) { throw 'Reviewed source list is missing.' }
    $paths = @(Get-Content -LiteralPath $listPath -Encoding UTF8 | Where-Object { $_ -and -not $_.StartsWith('#') })
    $names = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $entries = [ordered]@{}
    foreach ($relative in $paths) {
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('\') -or $relative.Contains(':') -or
            $relative -match '(^|/)(\.|\.\.|)(/|$)' -or -not $names.Add($relative)) { throw 'Invalid or duplicate source-list path.' }
        $template = $relative -match '\.(example|sample|template)$'
        if ($relative -match '(^|/)(\.git|\.work|\.local|\.private|private|personal|bin|obj|artifacts|logs|captures|node_modules|TestResults)(/|$)' -or
            $relative -match '(?i)\.(zip|7z|dmp|dump|log|trx|dll|exe|pyd|pth)$' -or
            (-not $template -and $relative -match '(?i)(^|/)(\.env|\.secrets|\.dev\.vars)(\.|$)|(^|/)(secrets|credentials)(\.|$)|\.(pfx|p12|pem|key)$') -or
            $relative -eq 'server/wrangler.toml') { throw "Private/generated file is not eligible for source packaging: $relative" }
        $full = [IO.Path]::GetFullPath((Join-Path $Root $relative))
        if (-not $full.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $full -PathType Leaf)) { throw "Missing reviewed source: $relative" }
        for ($parent = $full; $parent -ne $Root; $parent = [IO.Path]::GetDirectoryName($parent)) {
            if ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not source inputs.' }
        }
        $entries[$relative] = $full
    }
    if (Test-Path -LiteralPath (Join-Path $Root '.git')) {
        $dirty = @(& git -C $Root status --porcelain --untracked-files=no)
        if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'Formal packaging requires a clean committed tree. Use -VerifyOnly while editing.' }
        $tracked = @(& git -C $Root -c core.quotepath=false ls-files)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate committed source.' }
        $trackedNames = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($name in $tracked) { $null = $trackedNames.Add($name) }
        foreach ($name in $paths) { if (-not $trackedNames.Contains($name)) { throw "Reviewed source must be committed: $name" } }
        # Conversely, an added tracked file must receive explicit packaging review.
        foreach ($name in $tracked) { if (-not $names.Contains($name)) { throw "Tracked file is absent from reviewed source list: $name" } }
    }
    else {
        $manifestPath = Join-Path $Root 'source-file-manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Source archive manifest is missing.' }
        $manifest = Get-Content -Raw -Encoding UTF8 -LiteralPath $manifestPath | ConvertFrom-Json
        if ($manifest.baseCommit -notmatch '^[a-f0-9]{40}$' -or @($manifest.files).Count -ne $entries.Count) { throw 'Invalid source manifest identity/count.' }
        $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($file in $manifest.files) {
            if (-not $entries.Contains($file.path) -or -not $seen.Add($file.path) -or $file.sha256 -notmatch '^[a-f0-9]{64}$' -or
                (Get-FileHash -LiteralPath $entries[$file.path] -Algorithm SHA256).Hash -ine $file.sha256) { throw 'Source archive hash/list mismatch.' }
        }
    }
    foreach ($required in @('LICENSE.md','NOTICE','SOURCE.txt','README-CN.md','scripts/source-files.txt',
        'scripts/build-cn.ps1','scripts/source-package.ps1','Mahjong.Plugin.CN/Mahjong.Plugin.CN.csproj',
        'Mahjong.Cn.Core/Mahjong.Cn.Core.csproj','tools/MortalBridge/session.py','docs/cn/local-version-evidence.json')) {
        if (-not $entries.Contains($required)) { throw "Required corresponding source missing: $required" }
    }
    return $entries
}

function ConvertTo-PublicBuildText([string] $Text, [string] $Root) {
    $result = $Text
    $replacements = @(@($Root,'/src'), @($env:USERPROFILE,'/user'), @($env:TEMP,'/temp'))
    foreach ($pair in $replacements) {
        if ([string]::IsNullOrWhiteSpace($pair[0])) { continue }
        foreach ($path in @($pair[0], $pair[0].Replace('\','/'), $pair[0].Replace('\','\\'))) {
            $result = [regex]::Replace($result, [regex]::Escape($path), $pair[1], [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
    }
    # TestRun/TRX can carry names separately from filesystem paths.
    foreach ($identity in @($env:USERNAME, $env:COMPUTERNAME)) {
        if (-not [string]::IsNullOrWhiteSpace($identity)) {
            $result = [regex]::Replace($result, '(?<![A-Za-z0-9])' + [regex]::Escape($identity) + '(?![A-Za-z0-9])', '[local]', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        }
    }
    return $result
}
