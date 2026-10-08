#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$IsccPath = "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe",
    [switch]$Zip
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Windows packaging must run on Windows.' }
$root = Split-Path $PSScriptRoot -Parent
$version = ([xml](Get-Content -LiteralPath "$root/Directory.Build.props" -Raw)).Project.PropertyGroup.Version
$engine = Join-Path $root 'tools/win-x64'
# Validate before publishing anything. This script never fetches an engine.
& "$PSScriptRoot/test-engine.ps1" -EngineDirectory $engine
if (-not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) { throw "Inno Setup 6 compiler missing: $IsccPath" }
$project = Join-Path $root 'src/MusicFormatConverter.App/MusicFormatConverter.App.csproj'
if (-not (Test-Path -LiteralPath $project)) { throw "App project missing: $project" }
$build = Join-Path $root ("artifacts/build/windows-" + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $build 'app'
$out = Join-Path $root 'artifacts/installer'
New-Item -ItemType Directory -Force -Path $publish, $out | Out-Null
Push-Location $root
try {
    $sdk = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $sdk.Trim() -ne '10.0.400') { throw "Required SDK 10.0.400; got $sdk" }
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish `
        -p:Version=$version -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
} finally { Pop-Location }
foreach ($required in 'MusicFormatConverter.App.exe','MusicFormatConverter.App.dll','coreclr.dll','hostfxr.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $required))) { throw "Self-contained output missing: $required" }
}
$publishedEngine = Join-Path $publish 'tools'
New-Item -ItemType Directory -Path $publishedEngine | Out-Null
$manifest = Get-Content -LiteralPath "$engine/engine-manifest.json" -Raw | ConvertFrom-Json
foreach ($entry in $manifest.files) {
    if ($entry.path -match '(^|/)ffplay(\.exe)?$') { throw 'ffplay must not be in the package manifest.' }
    $destination = Join-Path $publishedEngine $entry.path
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath (Join-Path $engine $entry.path) -Destination $destination
}
Copy-Item -LiteralPath "$engine/engine-manifest.json" -Destination $publishedEngine
& "$PSScriptRoot/test-engine.ps1" -EngineDirectory $publishedEngine
$docOut = Join-Path $publish 'docs'
New-Item -ItemType Directory -Force -Path $docOut | Out-Null
Copy-Item -LiteralPath "$root/docs/engine-provenance.md", "$root/docs/packaging.md" -Destination $docOut
Copy-Item -LiteralPath "$root/README.md", "$root/LICENSE", "$root/THIRD-PARTY-NOTICES.md" -Destination $publish
Copy-Item -LiteralPath "$root/docs/implementation.md" -Destination $docOut
& $IsccPath "/DSourceDir=$publish" "/DOutputDir=$out" "/DAppVersion=$version" "$root/installer/windows.iss"
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
$installer = Join-Path $out "MusicFormatConverter-$version-win-x64-setup.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw 'Compiler did not create expected installer.' }
$outputs = @($installer)
if ($Zip) {
    $zipPath = Join-Path $root "artifacts/MusicFormatConverter-$version-win-x64.zip"
    Compress-Archive -Path "$publish/*" -DestinationPath $zipPath -Force
    $outputs += $zipPath
}
foreach ($file in $outputs) {
    "$((Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($file))" |
        Set-Content -LiteralPath "$file.sha256" -Encoding ascii
}
Write-Host 'Built locally only; not installed, signed, uploaded or released.'
Write-Host "Self-contained app: $publish"
$outputs | ForEach-Object { Write-Output $_ }
