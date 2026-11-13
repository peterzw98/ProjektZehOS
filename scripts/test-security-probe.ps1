param([Parameter(Mandatory = $true)][string] $IsoPath, [ValidateRange(1,120)][int] $TimeoutSeconds = 45)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$result = Join-Path $root ('.tools/security-probe/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $result -Force | Out-Null
$logPath = Join-Path $result 'serial.log'
$qemu = (Get-Command qemu-system-x86_64 -ErrorAction Stop).Source
$share = Join-Path (Split-Path (Split-Path $qemu -Parent) -Parent) 'share/qemu'
$iso = (Resolve-Path -LiteralPath $IsoPath).Path
$arguments = @('-L', ('"{0}"' -f $share), '-machine', 'q35', '-cpu', 'max', '-m', '512M', '-vga', 'std', '-boot', 'd', '-cdrom', ('"{0}"' -f $iso), '-display', 'none', '-serial', ('"file:{0}"' -f $logPath), '-monitor', 'none', '-action', 'shutdown=poweroff,reboot=reset')
$guest = Start-Process -FilePath $qemu -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardError (Join-Path $result 'stderr.log')
try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $log = ''
        if (Test-Path -LiteralPath $logPath) {
            $stream = [IO.File]::Open($logPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $reader = [IO.StreamReader]::new($stream)
            try { $log = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        if ($log.Contains('SECURITY_PROBE FAIL')) { throw "Kryptografieprobe fehlgeschlagen: $logPath`n$log" }
        if ($log.Contains('SECURITY_PROBE PASS')) { Write-Output $log; return }
        if ($guest.HasExited) { throw "QEMU vor Ergebnis beendet: $logPath" }
        Start-Sleep -Milliseconds 100
    }
    throw "Kein SECURITY_PROBE PASS: $logPath`n$log"
}
finally {
    # Nur die eigene plattenlose Probe beenden.
    if (-not $guest.HasExited) { $guest.Kill(); $guest.WaitForExit() }
    $guest.Dispose()
}
