param(
    [string] $IsoPath = (Join-Path $PSScriptRoot '../ProjektZeh/output-x64/ProjektZeh.iso')
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
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$arguments = @('-L', ('"{0}"' -f $share), '-machine', 'q35', '-cpu', 'max', '-m', '512M',
    '-vga', 'std', '-boot', 'd', '-cdrom', ('"{0}"' -f $iso), '-display', 'none',
    '-serial', ('"file:{0}"' -f $serial), '-qmp', "tcp:127.0.0.1:$port,server=on,wait=off", '-no-reboot', '-no-shutdown')
$arguments += $extraArgs
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
    Send-Command 'shutdown'
    if (-not $guest.WaitForExit(5000) -or $guest.ExitCode -ne 0) { throw 'QEMU wurde nicht sauber beendet.' }
    Write-Output 'PASS: shutdown hat QEMU sauber beendet (Exitcode 0).'
}
finally {
    $client.Dispose()
    if (-not $guest.HasExited) { $guest.Kill(); $guest.WaitForExit() }
    $guest.Dispose()
}
