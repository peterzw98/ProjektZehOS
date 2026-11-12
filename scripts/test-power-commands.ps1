param(
    [string] $IsoPath = (Join-Path $PSScriptRoot '../ProjektZeh/output-x64/ProjektZeh.iso'),
    [switch] $WithDebugger
)

$ErrorActionPreference = 'Stop'
$iso = (Resolve-Path -LiteralPath $IsoPath).Path
$qemu = (Get-Command qemu-system-x86_64 -ErrorAction Stop).Source
$share = Join-Path (Split-Path (Split-Path $qemu -Parent) -Parent) 'share/qemu'
$outputDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) '.tools/shutdown-test'
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$serial = Join-Path $outputDirectory 'serial.log'
$configPath = Join-Path $PSScriptRoot '../ProjektZeh/.cosmos/config.json'
$extraArgs = @()
if (Test-Path -LiteralPath $configPath) {
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $extraArgs = $config.qemu.extraArgs -split '\s+'
}
function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
$port = Get-FreePort
$arguments = @('-L', ('"{0}"' -f $share), '-machine', 'q35', '-cpu', 'max', '-m', '512M',
    '-vga', 'std', '-boot', 'd', '-cdrom', ('"{0}"' -f $iso), '-display', 'none',
    '-serial', ('"file:{0}"' -f $serial), '-qmp', "tcp:127.0.0.1:$port,server=on,wait=off", '-no-reboot', '-no-shutdown')
$arguments += $extraArgs
$debugger = $null
if ($WithDebugger) {
    $gdb = (Get-Command gdb-multiarch -ErrorAction Stop).Source
    $gdbPort = Get-FreePort
    $arguments += @('-gdb', "tcp:127.0.0.1:$gdbPort", '-S')
}
$guest = Start-Process -FilePath $qemu -ArgumentList $arguments -PassThru -WindowStyle Hidden `
    -RedirectStandardError (Join-Path $outputDirectory 'stderr.log')
# Windows PowerShell braucht den Handle vor Prozessende, um ExitCode zu behalten.
$null = $guest.Handle
$client = [System.Net.Sockets.TcpClient]::new()
function Read-BootLog {
    if (-not (Test-Path -LiteralPath $serial)) { return '' }
    $file = [System.IO.File]::Open($serial, 'Open', 'Read', 'ReadWrite')
    $logReader = [System.IO.StreamReader]::new($file)
    try { return $logReader.ReadToEnd() } finally { $logReader.Dispose() }
}
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not $client.Connected) {
        if ($guest.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw 'QEMU ist nicht erreichbar.' }
        try { $client.Connect('127.0.0.1', $port) } catch { Start-Sleep -Milliseconds 100 }
    }
    $stream = $client.GetStream()
    $stream.ReadTimeout = 10000
    $reader = [System.IO.StreamReader]::new($stream)
    $writer = [System.IO.StreamWriter]::new($stream)
    $writer.AutoFlush = $true
    function Read-QmpReply {
        do {
            $line = $reader.ReadLine()
            if ($null -eq $line) { throw 'QEMU hat die Monitorverbindung unerwartet geschlossen.' }
            $reply = $line | ConvertFrom-Json
            if ($null -ne $reply.error) { throw ($reply.error | ConvertTo-Json -Compress) }
        } while ($null -eq $reply.return)
        return $reply.return
    }
    $null = $reader.ReadLine()
    $writer.WriteLine('{"execute":"qmp_capabilities"}')
    $null = Read-QmpReply
    if ($WithDebugger) {
        $elf = (Resolve-Path -LiteralPath ([System.IO.Path]::ChangeExtension($iso, '.elf'))).Path
        $gdbArguments = @('--quiet', '--nx', '--batch', ('"{0}"' -f $elf),
            '-ex', ('"target remote 127.0.0.1:{0}"' -f $gdbPort), '-ex', 'continue')
        $debugger = Start-Process -FilePath $gdb -ArgumentList $gdbArguments -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $outputDirectory 'gdb.log') `
            -RedirectStandardError (Join-Path $outputDirectory 'gdb-stderr.log')
        $null = $debugger.Handle
    }

    while (-not (Read-BootLog).Contains('Textshell ist bereit.')) {
        if ($guest.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw 'Shell wurde nicht bereit.' }
        Start-Sleep -Milliseconds 100
    }

    function Send-Command([string] $command) {
        foreach ($key in @($command.ToCharArray() | ForEach-Object { [string] $_ }) + @('ret')) {
            $request = @{ execute = 'send-key'; arguments = @{ keys = @(@{ type = 'qcode'; data = $key }); 'hold-time' = 40 } }
            $writer.WriteLine(($request | ConvertTo-Json -Depth 5 -Compress))
            # Beim Ausschalten darf QEMU die Verbindung vor der Bestaetigung schliessen.
            if ($command -ne 'shutdown' -or $key -ne 'ret') { $null = Read-QmpReply }
            Start-Sleep -Milliseconds 120
        }
    }
    Send-Command 'reboot'
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([regex]::Matches((Read-BootLog), 'Textshell ist bereit\.').Count -lt 2) {
        if ($guest.HasExited -or [DateTime]::UtcNow -ge $deadline) {
            throw "reboot hat die Shell nicht erneut gestartet. Bootlog: $serial"
        }
        Start-Sleep -Milliseconds 100
    }
    Write-Output 'PASS: reboot hat den Gast in derselben VM erneut bis zur Shell gestartet.'
    $writer.WriteLine('{"execute":"query-status"}')
    $status = Read-QmpReply
    if (-not $status.running) { throw 'Der neu gestartete Gast laeuft nicht.' }
    if ($WithDebugger -and $debugger.HasExited) { throw 'GDB hat die Sitzung beim Neustart beendet.' }
    Send-Command 'shutdown'
    if (-not $guest.WaitForExit(5000) -or $guest.ExitCode -ne 0) { throw 'QEMU wurde nicht sauber beendet.' }
    Write-Output 'PASS: shutdown hat QEMU sauber beendet (Exitcode 0).'
    if ($WithDebugger) {
        if (-not $debugger.WaitForExit(5000) -or $debugger.ExitCode -ne 0) { throw 'GDB wurde nicht sauber beendet.' }
        $debugLog = Get-Content -LiteralPath (Join-Path $outputDirectory 'gdb.log') -Raw
        if ($debugLog -match 'received signal' -or $debugLog -notmatch 'exited normally') {
            throw "GDB meldet keinen normalen Gastabschluss: $debugLog"
        }
        Write-Output 'PASS: GDB blieb beim reboot verbunden und meldet nach shutdown einen normalen Gastabschluss.'
    }
}
finally {
    $client.Dispose()
    if (-not $guest.HasExited) { $guest.Kill(); $guest.WaitForExit() }
    $guest.Dispose()
    if ($null -ne $debugger) {
        if (-not $debugger.HasExited) { $debugger.Kill(); $debugger.WaitForExit() }
        $debugger.Dispose()
    }
}
