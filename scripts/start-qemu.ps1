param(
    [string] $IsoPath,
    [switch] $PrepareDiskOnly,
    [switch] $WaitForDebugger
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$diskDirectory = Join-Path $repositoryRoot '.tools\qemu'
$diskPath = Join-Path $diskDirectory 'data-fat32.img'
$diskSize = 64MB

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

& $cosmos.Source @runArguments
if ($LASTEXITCODE -ne 0) {
    throw "Cosmos konnte QEMU nicht erfolgreich ausfuehren (Exitcode $LASTEXITCODE)."
}
