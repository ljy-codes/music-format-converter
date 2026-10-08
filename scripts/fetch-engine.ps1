#Requires -Version 7.0
[CmdletBinding()]
param([string]$Proxy)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$lock = (Get-Content -LiteralPath "$PSScriptRoot/engine-lock.json" -Raw | ConvertFrom-Json).windows
$cache = Join-Path $root 'artifacts/engine-cache'
$target = Join-Path $root 'tools/win-x64'
New-Item -ItemType Directory -Force -Path $cache, $target | Out-Null

function Get-PinnedFile([string]$Url, [string]$Path, [string]$Sha256) {
    if (Test-Path -LiteralPath $Path) {
        if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256) { return }
        throw "Cached file hash mismatch: $Path. Inspect/remove this file before retrying."
    }
    $params = @{ Uri = $Url; OutFile = "$Path.partial"; MaximumRetryCount = 3; RetryIntervalSec = 3 }
    if ($Proxy) { $params.Proxy = $Proxy }
    Invoke-WebRequest @params
    if ((Get-FileHash -LiteralPath "$Path.partial" -Algorithm SHA256).Hash -ne $Sha256) {
        throw "Downloaded file hash mismatch: $Url (kept .partial for inspection)"
    }
    Move-Item -LiteralPath "$Path.partial" -Destination $Path
}

$base = "https://github.com/BtbN/FFmpeg-Builds/releases/download/$($lock.release)"
$checksums = Join-Path $cache 'checksums.sha256'
Get-PinnedFile "$base/checksums.sha256" $checksums $lock.checksumsSha256
$checksumLine = @(Get-Content -LiteralPath $checksums | Where-Object {
    $_ -match ('^[0-9a-fA-F]{64}\s+\*?' + [regex]::Escape($lock.asset) + '$')
})
if ($checksumLine.Count -ne 1 -or $checksumLine[0].Substring(0,64) -ne $lock.sha256) {
    throw 'Published checksum does not match the repository lock.'
}
$archive = Join-Path $cache $lock.asset
Get-PinnedFile "$base/$($lock.asset)" $archive $lock.sha256
$sourceName = "ffmpeg-$($lock.sourceCommit).tar.gz"
$recipeName = "ffmpeg-builds-$($lock.recipeCommit).tar.gz"
Get-PinnedFile "https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/$($lock.sourceCommit)" (Join-Path $cache $sourceName) $lock.sourceSha256
Get-PinnedFile "https://codeload.github.com/BtbN/FFmpeg-Builds/tar.gz/$($lock.recipeCommit)" (Join-Path $cache $recipeName) $lock.recipeSha256

# A fresh staging directory prevents obsolete DLLs from becoming part of the manifest.
$stage = Join-Path $cache ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $stage
$bundle = Join-Path $stage ([IO.Path]::GetFileNameWithoutExtension($lock.asset))
$engineStage = Join-Path $stage 'engine'
$sources = Join-Path $engineStage 'provenance/sources'
$licenses = Join-Path $engineStage 'licenses'
New-Item -ItemType Directory -Force -Path $sources, $licenses | Out-Null
foreach ($name in 'ffmpeg.exe','ffprobe.exe') {
    Copy-Item -LiteralPath (Join-Path $bundle "bin/$name") -Destination $engineStage
}
# Copy ALL shared libraries, not a hard-coded partial dependency list.
Get-ChildItem -LiteralPath (Join-Path $bundle 'bin') -Filter '*.dll' | Copy-Item -Destination $engineStage
Copy-Item -LiteralPath (Join-Path $bundle 'LICENSE.txt') -Destination $engineStage
Copy-Item -LiteralPath $checksums -Destination (Join-Path $engineStage 'provenance/upstream-checksums.sha256')
Copy-Item -LiteralPath (Join-Path $cache $sourceName), (Join-Path $cache $recipeName) -Destination $sources
Copy-Item -LiteralPath "$PSScriptRoot/engine-lock.json" -Destination (Join-Path $engineStage 'provenance')

# LGPLv3 incorporates GPLv3; preserve both texts from the exact source revision.
& tar -xzf (Join-Path $cache $sourceName) -C $licenses --strip-components=1 `
    "FFmpeg-$($lock.sourceCommit)/COPYING.LGPLv3" "FFmpeg-$($lock.sourceCommit)/COPYING.GPLv3" `
    "FFmpeg-$($lock.sourceCommit)/COPYING.LGPLv2.1" "FFmpeg-$($lock.sourceCommit)/LICENSE.md"
if ($LASTEXITCODE -ne 0) { throw 'Could not extract corresponding source license notices.' }
$version = & (Join-Path $engineStage 'ffmpeg.exe') -version 2>&1 | Out-String
if ($LASTEXITCODE -ne 0 -or $version -notmatch [regex]::Escape($lock.version) -or
    $version -match '--enable-(gpl|nonfree)(\s|$)') { throw 'Unexpected engine version or license configuration.' }
$version | Set-Content -LiteralPath (Join-Path $engineStage 'provenance/ffmpeg-version.txt') -Encoding utf8
$notice = @"
FFmpeg LGPL shared engine, independently executed by MusicFormatConverter.
Release: $($lock.release)
Asset: $($lock.asset)
Binary SHA256: $($lock.sha256)
Source revision: $($lock.sourceCommit)
Build recipe revision: $($lock.recipeCommit)
Exact FFmpeg source and recipe archives are in provenance/sources.
License: LGPL-3.0-or-later; see LICENSE.txt and licenses/ (including incorporated GPLv3).
No GPL/nonfree configure switches are enabled. LGPL is not public domain.
Dependencies have their own notices/source requirements. The upstream build recipe
identifies dependency versions, sources and patches; its archive is NOT a complete
mirror of all dependency sources. Before public redistribution maintain matching
dependency source/notices alongside the binaries; see docs/engine-provenance.md.
The application does not link to FFmpeg and permits replacement of this engine.
ffplay is intentionally not distributed by this application.
"@
$notice | Set-Content -LiteralPath (Join-Path $engineStage 'provenance/NOTICE.txt') -Encoding utf8
$files = @(Get-ChildItem -LiteralPath $engineStage -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($engineStage, $_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
})
[ordered]@{ schema = 1; runtime = 'win-x64'; version = $lock.version; archiveSha256 = $lock.sha256; files = $files } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $engineStage 'engine-manifest.json') -Encoding utf8
# Preserve unrelated user files; packaging copies only the manifest-listed files.
Copy-Item -Path "$engineStage/*" -Destination $target -Recurse -Force
& "$PSScriptRoot/test-engine.ps1" -EngineDirectory $target
Write-Host "Verified engine: $target"
