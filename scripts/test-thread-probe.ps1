param(
    [Parameter(Mandatory = $true)]
    [string] $IsoPath,
    [ValidateRange(1, 120)]
    [int] $TimeoutSeconds = 45
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resultDirectory = Join-Path $repositoryRoot ('.tools/thread-probe/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
$serialLog = Join-Path $resultDirectory 'serial.log'
$qemuPath = (Get-Command qemu-system-x86_64 -ErrorAction Stop).Source
$sharePath = Join-Path (Split-Path (Split-Path $qemuPath -Parent) -Parent) 'share/qemu'
$resolvedIso = (Resolve-Path -LiteralPath $IsoPath -ErrorAction Stop).Path
$arguments = @('-L', ('"{0}"' -f $sharePath), '-machine', 'q35', '-cpu', 'max', '-m', '512M',
    '-vga', 'std', '-boot', 'd', '-cdrom', ('"{0}"' -f $resolvedIso), '-display', 'none',
    '-serial', ('"file:{0}"' -f $serialLog), '-monitor', 'none',
    '-action', 'shutdown=poweroff,reboot=reset')
$guest = Start-Process -FilePath $qemuPath -ArgumentList $arguments -PassThru -WindowStyle Hidden `
    -RedirectStandardError (Join-Path $resultDirectory 'stderr.log')
try {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $log = ''
        if (Test-Path -LiteralPath $serialLog) {
            $stream = [IO.File]::Open($serialLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $reader = [IO.StreamReader]::new($stream)
            try { $log = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        if ($log.Contains('THREAD_PROBE FAIL')) { throw "Threadprobe fehlgeschlagen. Log: $serialLog" }
        if ($log.Contains('THREAD_PROBE PASS')) {
            Write-Output ($log -split '\r?\n' | Where-Object { $_ -match 'THREAD_PROBE' })
            Write-Output "Gastprobe bestanden. Log: $serialLog"
            return
        }
        if ($guest.HasExited) { throw "QEMU vor dem Prüfergebnis beendet. Log: $serialLog" }
        Start-Sleep -Milliseconds 100
    }
    throw "Kein THREAD_PROBE PASS innerhalb von $TimeoutSeconds Sekunden. Log: $serialLog"
}
finally {
    # Nur die eigene plattenlose Test-VM beenden; bestehende Sitzungen bleiben erhalten.
    if (-not $guest.HasExited) { $guest.Kill(); $guest.WaitForExit() }
    $guest.Dispose()
}
