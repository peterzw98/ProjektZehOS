#!/bin/sh
# Messbau: baut aus einem ZehOS-Worktree (Hauptbaum eines Branches) eine ISO mit GC.Collect in sysinfo und -p:ZehOSLinuxRun=true.
# Nutzung: sh messbau.sh <worktree> <ziel.iso>   (Worktree wird nach dem Bau wieder zurueckgesetzt: nur CosmosLinuxRun.cs)
# Voraussetzung: Cosmos-Werkzeuge in <worktree>/.tools/cosmos-tools/<deploy/cosmos/VERSION> (scripts/build-cosmos.sh), dotnet unter ~/.dotnet.
set -e
W=$(cd "$1" && pwd); OUT=$2; V=$(dirname "$0"); V=$(cd "$V" && pwd)
export DOTNET_ROOT=${DOTNET_ROOT:-$HOME/.dotnet} DOTNET_CLI_USE_MSBUILD_SERVER=0 MSBUILDDISABLENODEREUSE=1
export PATH=$DOTNET_ROOT:$W/.tools/cosmos-tools/$(cat "$W/deploy/cosmos/VERSION"):$PATH
cd "$W"
git diff --quiet -- ProjektZeh/Runtime/CosmosLinuxRun.cs || { echo "CosmosLinuxRun.cs hat Aenderungen, Abbruch"; exit 1; }
patch -p1 < "$V/messbau-gc.patch"
trap 'git checkout -- ProjektZeh/Runtime/CosmosLinuxRun.cs' EXIT
dotnet build ProjektZeh/ProjektZeh.csproj -c Release -warnaserror -p:UseSharedCompilation=false -p:ZehOSLinuxRun=true
cp ProjektZeh/bin/Release/net10.0/linux-x64/cosmos/ProjektZeh.iso "$OUT"
echo "ISO: $OUT"
