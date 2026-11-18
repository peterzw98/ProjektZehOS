param(
    [string] $IsoPath,
    [switch] $PrepareDiskOnly,
    [switch] $WaitForDebugger,
    [switch] $StandardVga,
    [switch] $RelativeMouse,
    [string] $DataImagePath,
    [string] $Smp = $env:ZEHOS_QEMU_SMP
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$diskSize = 64MB

if ($PSBoundParameters.ContainsKey('DataImagePath')) {
    # Nur pruefen: nichts anlegen, formatieren oder vergroessern. Ein Komma wuerde die cosmos --disk-Liste teilen.
    if ([string]::IsNullOrWhiteSpace($DataImagePath)) {
        throw 'DataImagePath darf nicht leer sein.'
    }
    if ($DataImagePath.Contains(',')) {
        throw "DataImagePath darf kein Komma enthalten: $DataImagePath"
    }
    if (-not (Test-Path -LiteralPath $DataImagePath -PathType Leaf)) {
        throw "DataImagePath ist keine vorhandene Datei: $DataImagePath"
    }
    $diskPath = (Resolve-Path -LiteralPath $DataImagePath).Path
    if ($diskPath.Contains(',')) {
        throw "Der aufgeloeste Pfad darf kein Komma enthalten: $diskPath"
    }
    $length = (Get-Item -LiteralPath $diskPath).Length
    if ($length -lt $diskSize -or $length % 512 -ne 0) {
        throw "Das Datenimage muss mindestens 64 MiB gross und ein Vielfaches von 512 Byte sein: $diskPath ($length Byte)"
    }
    Write-Host "Vorhandenes Datenimage wird unveraendert verwendet: $diskPath"
}
else {
    $diskDirectory = Join-Path $repositoryRoot '.tools\qemu'
    $diskPath = Join-Path $diskDirectory 'data-fat32.img'

    New-Item -ItemType Directory -Path $diskDirectory -Force | Out-Null
    if (-not (Test-Path -LiteralPath $diskPath)) {
        # CreateNew verhindert auch bei einem parallelen Start das Ueberschreiben.
        $diskStream = [System.IO.File]::Open($diskPath, [System.IO.FileMode]::CreateNew)
        try {
            $diskStream.SetLength($diskSize)
        }
        finally {
            $diskStream.Dispose()
        }
        Write-Host "Leere Testfestplatte erstellt: $diskPath"
    }
    else {
        $disk = Get-Item -LiteralPath $diskPath
        if ($disk.Length -ne $diskSize) {
            throw "Die vorhandene Testfestplatte hat nicht die erwarteten 64 MiB: $diskPath"
        }
        Write-Host "Vorhandene Testfestplatte wird weiterverwendet: $diskPath"
    }
}

if ($PrepareDiskOnly) {
    return
}

if ([string]::IsNullOrWhiteSpace($IsoPath)) {
    $IsoPath = Join-Path $repositoryRoot 'ProjektZeh\output-x64\ProjektZeh.iso'
}
$resolvedIso = (Resolve-Path -LiteralPath $IsoPath -ErrorAction Stop).Path
$cosmos = Get-Command cosmos -ErrorAction Stop
$runArguments = @('run', '--iso', $resolvedIso, '--arch', 'x64', '--disk', "$diskPath,ahci")
if ($WaitForDebugger) {
    $runArguments += '--debug'
}
# Cosmos (geprueft mit 3.0.94) haelt den Gast sonst bei Shutdown und Reboot nur an.
$runArguments += @('--', '-action', 'shutdown=poweroff,reboot=reset')
if ($Smp) { $runArguments += @('-smp', $Smp) }
# Ein USB-Tablet meldet absolute Positionen; ein eingefangener Zeiger (Mouselook) endet dann am Fensterrand. Mit
# -RelativeMouse entfaellt es: QEMU liefert dann nur die relative PS/2-Maus (Hostfenster mit Strg+Alt+G einfangen).
if (-not $RelativeMouse) {
    $runArguments += @('-device', 'qemu-xhci,id=tabletbus', '-device', 'usb-tablet,bus=tabletbus.0')
}
# Nur der Cosmos-Treiber fuer VMware SVGA meldet mehrere Modi; damit laesst sich die Aufloesung im Betrieb aendern.
# Der Gast startet dann mit 1024 x 768 und stellt eine bestaetigte Aufloesung nach der Anmeldung wieder her.
if (-not $StandardVga) {
    $runArguments += @('-vga', 'vmware')
}

& $cosmos.Source @runArguments
if ($LASTEXITCODE -ne 0) {
    throw "Cosmos konnte QEMU nicht erfolgreich ausfuehren (Exitcode $LASTEXITCODE)."
}
