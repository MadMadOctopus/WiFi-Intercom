<#!
.SYNOPSIS
Creates a signed Wi-Fi Intercom OTA manifest beside an ESP-IDF application
binary. The private key stays outside git; firmware and the companion contain
only its public verification key.

.EXAMPLE
.\Create-OtaPackage.ps1 -Image ..\..\firmware\build\wifi_intercom.bin -Version 0.7.1 `
  -PrivateKey ..\..\.ota-keys\dev-release-key.pem
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Image,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $PrivateKey,
    [ValidateRange(1, 255)] [int] $Protocol = 1,
    [string] $Output
)

$ErrorActionPreference = 'Stop'
$imagePath = (Resolve-Path $Image).Path
$keyPath = (Resolve-Path $PrivateKey).Path
if (-not $Output) { $Output = "$imagePath.ota.json" }
$outputPath = [IO.Path]::GetFullPath($Output)
$openssl = (Get-Command openssl -ErrorAction SilentlyContinue).Source
if (-not $openssl) {
    $gitOpenSsl = Join-Path $env:ProgramFiles 'Git\usr\bin\openssl.exe'
    if (Test-Path $gitOpenSsl) { $openssl = $gitOpenSsl }
}
if (-not $openssl) { throw 'OpenSSL was not found. Install OpenSSL or Git for Windows.' }
Write-Verbose "Using OpenSSL: $openssl"
$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $imagePath).Hash.ToLowerInvariant()
$size = (Get-Item -LiteralPath $imagePath).Length
$canonical = "wifi-intercom-ota-1`n$Version`n$Protocol`n$size`n$sha`n"
$temporary = [IO.Path]::GetTempFileName()
try {
    [IO.File]::WriteAllText($temporary, $canonical, [Text.UTF8Encoding]::new($false))
    $signaturePath = "$temporary.sig"
    $arguments = "dgst -sha256 -sign `"$keyPath`" -out `"$signaturePath`" `"$temporary`""
    $sign = Start-Process -FilePath $openssl -ArgumentList $arguments -Wait -NoNewWindow -PassThru
    if ($sign.ExitCode -ne 0) { throw "OpenSSL signing failed with exit code $($sign.ExitCode)." }
    $manifest = [ordered]@{
        format = 'wifi-intercom-ota-1'
        version = $Version
        protocol = $Protocol
        size = $size
        sha256 = $sha
        signature = [Convert]::ToBase64String([IO.File]::ReadAllBytes($signaturePath))
        image = [IO.Path]::GetFileName($imagePath)
    }
    [IO.File]::WriteAllText($outputPath, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Host "Wrote signed OTA package: $outputPath"
}
finally {
    Remove-Item -LiteralPath $temporary -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "$temporary.sig" -ErrorAction SilentlyContinue
}
