#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$EngineDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools/win-x64'),
    [switch]$SkipConversion
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$lock = (Get-Content -LiteralPath "$PSScriptRoot/engine-lock.json" -Raw | ConvertFrom-Json).windows
$EngineDirectory = [IO.Path]::GetFullPath($EngineDirectory)
foreach ($required in 'ffmpeg.exe','ffprobe.exe','LICENSE.txt','licenses/COPYING.GPLv3','licenses/COPYING.LGPLv3','engine-manifest.json','provenance/NOTICE.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path $EngineDirectory $required) -PathType Leaf)) { throw "Missing engine component: $required" }
}
if ((Get-Content -LiteralPath "$EngineDirectory/LICENSE.txt" -Raw) -notmatch 'GNU LESSER GENERAL PUBLIC LICENSE') { throw 'Missing LGPL license text.' }
$manifest = Get-Content -LiteralPath "$EngineDirectory/engine-manifest.json" -Raw | ConvertFrom-Json
if ($manifest.runtime -ne 'win-x64' -or $manifest.version -ne $lock.version -or $manifest.archiveSha256 -ne $lock.sha256) { throw 'Engine manifest does not match lock.' }
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $EngineDirectory $entry.path))
    if (-not $path.StartsWith($EngineDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not $seen.Add($entry.path)) { throw 'Unsafe/duplicate manifest path.' }
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path).Hash -ne $entry.sha256) { throw "Engine hash mismatch: $($entry.path)" }
}
foreach ($required in 'ffmpeg.exe','ffprobe.exe','LICENSE.txt','licenses/COPYING.GPLv3','licenses/COPYING.LGPLv3','provenance/NOTICE.txt') {
    if (-not $seen.Contains($required)) { throw "Required component not hashed: $required" }
}
foreach ($dll in 'avcodec-62.dll','avdevice-62.dll','avfilter-11.dll','avformat-62.dll','avutil-60.dll','swresample-6.dll','swscale-9.dll') {
    if (-not $seen.Contains($dll)) { throw "Shared engine DLL missing from manifest: $dll" }
}
foreach ($source in @(
    @{ Path = "provenance/sources/ffmpeg-$($lock.sourceCommit).tar.gz"; Hash = $lock.sourceSha256 },
    @{ Path = "provenance/sources/ffmpeg-builds-$($lock.recipeCommit).tar.gz"; Hash = $lock.recipeSha256 },
    @{ Path = 'provenance/upstream-checksums.sha256'; Hash = $lock.checksumsSha256 }
)) {
    if (-not $seen.Contains($source.Path) -or (Get-FileHash -LiteralPath (Join-Path $EngineDirectory $source.Path)).Hash -ne $source.Hash) {
        throw "Pinned source/provenance mismatch: $($source.Path)"
    }
}
foreach ($tool in 'ffmpeg','ffprobe') {
    $version = & "$EngineDirectory/$tool.exe" -version 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0 -or $version -notmatch [regex]::Escape($lock.version) -or $version -match '--enable-(gpl|nonfree)(\s|$)') { throw "$tool version/license check failed." }
}
if (-not $SkipConversion) {
    $temp = Join-Path ([IO.Path]::GetTempPath()) ("mfc-engine-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    try {
        & "$EngineDirectory/ffmpeg.exe" -nostdin -v error -f lavfi -i 'sine=frequency=997:sample_rate=48000:duration=0.2' -c:a flac "$temp/source.flac"
        if ($LASTEXITCODE -ne 0) { throw 'FLAC generation failed.' }
        & "$EngineDirectory/ffmpeg.exe" -nostdin -v error -i "$temp/source.flac" -c:a alac "$temp/result.m4a"
        if ($LASTEXITCODE -ne 0) { throw 'ALAC encode failed.' }
        $probe = & "$EngineDirectory/ffprobe.exe" -v error -show_streams -of json "$temp/result.m4a"
        if ($LASTEXITCODE -ne 0) { throw 'ffprobe failed.' }
        $audio = ($probe | ConvertFrom-Json).streams | Where-Object codec_type -EQ audio
        if ($audio.codec_name -ne 'alac' -or $audio.sample_rate -ne '48000') { throw 'Unexpected conversion result.' }
        foreach ($pair in @(@('source.flac','source.pcm'),@('result.m4a','result.pcm'))) {
            & "$EngineDirectory/ffmpeg.exe" -nostdin -v error -i "$temp/$($pair[0])" -map 0:a:0 -f s16le "$temp/$($pair[1])"
            if ($LASTEXITCODE -ne 0) { throw 'PCM decoding failed.' }
        }
        if ((Get-FileHash "$temp/source.pcm").Hash -ne (Get-FileHash "$temp/result.pcm").Hash) { throw 'PCM roundtrip mismatch.' }
    } finally {
        # Only our GUID-named temp directory is recursively removed.
        $resolved = [IO.Path]::GetFullPath($temp)
        $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar
        if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($resolved) -notmatch '^mfc-engine-[0-9a-f]{32}$') { throw 'Unsafe temporary cleanup path.' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
Write-Host 'Engine hashes, licenses, versions and requested conversion checks passed.'
