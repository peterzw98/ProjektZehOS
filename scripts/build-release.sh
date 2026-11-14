#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 || $# -gt 2 || ! $1 =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]]; then
  echo 'Aufruf: build-release.sh <major.minor.patch> [Ausgabeverzeichnis]' >&2
  exit 1
fi
version=$1
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
out=${2:-"$root/release"}
mkdir -p "$out"
out=$(cd "$out" && pwd)
payload=$(mktemp -d)
trap 'rm -rf "$payload"' EXIT
cd "$root"
packages=$(dotnet nuget locals global-packages -l)
packages=${packages#global-packages: }
boot="$packages/cosmos.build.common/3.0.94/tools/Limine/BOOTX64.EFI"
test -f "$boot" || { echo "Limine fehlt: $boot" >&2; exit 1; }

dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -t:Rebuild -p:Version="$version"
cp ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso "$out/ZehOS-$version.iso"
cp ProjektZeh/bin/Release/net10.0/linux-x64/ProjektZeh.elf "$out/ProjektZeh-$version.elf"
cp "$out/ProjektZeh-$version.elf" "$payload/ProjektZeh.elf"
cp "$boot" "$payload/BOOTX64.EFI"
if ! grep -q 'ZehOSInstaller' ProjektZeh/ProjektZeh.csproj; then
  echo 'Installer-Build ist im Projekt noch nicht eingerichtet.' >&2
  exit 1
fi
dotnet build ProjektZeh/ProjektZeh.csproj --configuration Release -t:Rebuild -p:Version="$version" -p:ZehOSInstaller=true -p:ZehOSPayloadDir="$payload"
cp ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso "$out/ZehOS-Setup-$version.iso"
size=$(wc -c < "$out/ProjektZeh-$version.elf" | tr -d '[:space:]')
hash=$(sha256sum "$out/ProjektZeh-$version.elf" | cut -d ' ' -f 1)
printf 'zehos-update 1\nversion=%s\nkernel=ProjektZeh-%s.elf\nsize=%s\nsha256=%s\n' "$version" "$version" "$size" "$hash" > "$out/update-manifest.txt"
