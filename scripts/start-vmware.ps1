param(
    [Parameter(Mandatory = $true)]
    [string] $IsoPath
)

$ErrorActionPreference = 'Stop'

$resolvedIso = (Resolve-Path -LiteralPath $IsoPath -ErrorAction Stop).Path
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$vmDirectory = Join-Path $repositoryRoot '.tools\vmware'
$vmxPath = Join-Path $vmDirectory 'ProjektZeh.vmx'
$serialPath = Join-Path $vmDirectory 'serial.log'

$programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
$vmrunCandidates = @(
    @{ Path = (Join-Path $programFilesX86 'VMware\VMware Player\vmrun.exe'); HostType = 'player' }
    @{ Path = (Join-Path $programFilesX86 'VMware\VMware Workstation\vmrun.exe'); HostType = 'ws' }
    @{ Path = (Join-Path $env:ProgramFiles 'VMware\VMware Workstation\vmrun.exe'); HostType = 'ws' }
)
$vmrunInstallation = $vmrunCandidates | Where-Object { Test-Path -LiteralPath $_.Path -PathType Leaf } | Select-Object -First 1
if (-not $vmrunInstallation) {
    throw 'VMware Player oder Workstation mit vmrun.exe wurde nicht gefunden.'
}
$vmrun = $vmrunInstallation.Path
$hostType = $vmrunInstallation.HostType

New-Item -ItemType Directory -Path $vmDirectory -Force | Out-Null

# A separate diskless UEFI VM avoids changing the old Cosmos Gen2 machine.
$escapedIso = $resolvedIso.Replace('"', '\"')
$escapedSerial = $serialPath.Replace('"', '\"')
$vmx = @"
.encoding = "UTF-8"
config.version = "8"
virtualHW.version = "17"
displayName = "ProjektZeh (Cosmos Gen3)"
guestOS = "other-64"
memsize = "1024"
numvcpus = "1"
firmware = "efi"
efi.secureBoot.enabled = "FALSE"
ide1:0.present = "TRUE"
ide1:0.deviceType = "cdrom-image"
ide1:0.fileName = "$escapedIso"
ide1:0.startConnected = "TRUE"
floppy0.present = "FALSE"
ethernet0.present = "TRUE"
ethernet0.connectionType = "nat"
usb.present = "TRUE"
sound.present = "FALSE"
serial0.present = "TRUE"
serial0.fileType = "file"
serial0.fileName = "$escapedSerial"
serial0.startConnected = "TRUE"
"@

$runningVms = & $vmrun -T $hostType list 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "VMware konnte laufende VMs nicht abfragen: $runningVms"
}
if ($runningVms -contains $vmxPath) {
    throw 'ProjektZeh läuft bereits in VMware. VM ausschalten und erneut in Visual Studio starten.'
}

if (Test-Path -LiteralPath $serialPath -PathType Leaf) {
    Remove-Item -LiteralPath $serialPath -Force
}

[System.IO.File]::WriteAllText($vmxPath, $vmx, [System.Text.UTF8Encoding]::new($false))

& $vmrun -T $hostType start $vmxPath gui
if ($LASTEXITCODE -ne 0) {
    throw "VMware konnte die VM nicht starten (Exitcode $LASTEXITCODE)."
}

Write-Host "ProjektZeh in VMware gestartet: $resolvedIso"
