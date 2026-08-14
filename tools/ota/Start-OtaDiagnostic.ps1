param(
    [switch] $CaptureOnly,
    [ValidateRange(1, 120)] [int] $Seconds = 70
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$capture = Join-Path $PSScriptRoot 'Capture-Com6Console.ps1'
$output = Join-Path $root 'firmware\build\com6-ota-console.log'
$companion = Join-Path $root 'companion\IntercomCompanion\bin\Release\net10.0-windows\IntercomCompanion.exe'

$captureArguments = "-NoProfile -ExecutionPolicy Bypass -File `"$capture`" -Port COM6 -Seconds $Seconds -Output `"$output`""
Start-Process -FilePath 'powershell.exe' -WindowStyle Hidden -ArgumentList $captureArguments
if (-not $CaptureOnly) { Start-Process -FilePath $companion }
