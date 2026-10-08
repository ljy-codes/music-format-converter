#Requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' | ForEach-Object {
    $tokens = $null; $errors = $null
    [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count) { throw ($errors | Out-String) }
    Write-Host "PowerShell syntax OK: $($_.Name)"
}
& "$PSScriptRoot/test-engine.ps1"
$fixture = Join-Path $root ("artifacts/packaging-tests/" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
Copy-Item -Path "$root/tools/win-x64/*" -Destination $fixture -Recurse
function Expect-Failure([string]$Expected) {
    $caught = $false
    try { & "$PSScriptRoot/test-engine.ps1" -EngineDirectory $fixture -SkipConversion }
    catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw }
        Write-Host "Correctly rejected: $Expected"
        $caught = $true
    }
    if (-not $caught) { throw "Expected failure was not observed: $Expected" }
}
# Only a generated fixture is changed; never alter the shared development engine.
Move-Item -LiteralPath "$fixture/LICENSE.txt" -Destination "$fixture/LICENSE.saved"
Expect-Failure 'Missing engine component: LICENSE.txt'
Move-Item -LiteralPath "$fixture/LICENSE.saved" -Destination "$fixture/LICENSE.txt"
Add-Content -LiteralPath "$fixture/LICENSE.txt" -Value 'tampered test fixture'
Expect-Failure 'Engine hash mismatch: LICENSE.txt'
Copy-Item -LiteralPath "$root/tools/win-x64/LICENSE.txt" -Destination "$fixture/LICENSE.txt" -Force
Move-Item -LiteralPath "$fixture/avcodec-62.dll" -Destination "$fixture/avcodec.saved"
Expect-Failure 'Engine hash mismatch: avcodec-62.dll'
Move-Item -LiteralPath "$fixture/avcodec.saved" -Destination "$fixture/avcodec-62.dll"
$manifestPath = "$fixture/engine-manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.files[0].path = '../escaped-file'
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Expect-Failure 'Unsafe/duplicate manifest path'
Write-Host "Packaging checks passed; isolated negative-test fixture retained at $fixture"
