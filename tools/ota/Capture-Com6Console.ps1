param(
    [string] $Port = 'COM6',
    [ValidateRange(1, 120)] [int] $Seconds = 70,
    [Parameter(Mandatory)] [string] $Output
)

$ErrorActionPreference = 'Stop'
$serial = $null
$text = ''
try {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($null -eq $serial -or -not $serial.IsOpen) {
            try {
                if ($null -ne $serial) { $serial.Dispose() }
                $serial = [System.IO.Ports.SerialPort]::new($Port, 115200, 'None', 8, 'One')
                $serial.ReadTimeout = 250
                $serial.Open()
                # USB Serial/JTAG does not consistently emit application logs until DTR is asserted.
                $serial.DtrEnable = $true
            }
            catch {
                if ($null -ne $serial) { $serial.Dispose() }
                $serial = $null
                Start-Sleep -Milliseconds 250
                continue
            }
        }
        try { $text += $serial.ReadExisting() }
        catch {
            $serial.Dispose()
            $serial = $null
            Start-Sleep -Milliseconds 250
            continue
        }
        Start-Sleep -Milliseconds 20
    }
}
finally {
    if ($null -ne $serial) {
        if ($serial.IsOpen) { $serial.Close() }
        $serial.Dispose()
    }
    [IO.File]::WriteAllText($Output, $text)
}
