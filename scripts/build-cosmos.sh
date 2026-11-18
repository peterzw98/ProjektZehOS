#!/usr/bin/env bash
# Baut Cosmos (Quelle: Branch "cosmos") zu NuGet-Paketen und installiert cosmos/cosmos.patcher.
# Siehe deploy/cosmos/README.md und docs/superpowers/plans/2026-11-18-multicore-e1-cosmos-quelle.md (E1-B).
# Schreibt nur unter .tools/ und deploy/cosmos/feed/; der NuGet-Cache und die NuGet-Konfiguration des Benutzers bleiben unberuehrt.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
commit="" version="" feed="" tools=1 global=0 plan=0 any_sdk=0
while [ $# -gt 0 ]; do
    case "$1" in
        --commit) commit="${2:?--commit braucht einen Wert}"; shift 2 ;;
        --version) version="${2:?--version braucht einen Wert}"; shift 2 ;;
        --feed) feed="${2:?--feed braucht einen Wert}"; shift 2 ;;
        --no-tools) tools=0; shift ;;
        --global) global=1; shift ;;
        --any-sdk) any_sdk=1; shift ;;
        --print-plan) plan=1; shift ;;
        *) echo "Unbekanntes Argument: $1" >&2; exit 2 ;;
    esac
done

fail() { echo "build-cosmos: $*" >&2; exit 1; }

# Das SDK bestimmt IL-Details (Quellgenerator, Stackgroessen); Referenzbau nur mit dem in deploy/cosmos/SDK festgelegten.
# --any-sdk gilt nur fuer lokale Versuche; solche Feeds nie fuer Release oder IL-Vergleich (E1-D) verwenden.
if [ "$plan" = 0 ] && [ "$any_sdk" = 0 ]; then
    want_sdk="$(tr -d '[:space:]' < "$root/deploy/cosmos/SDK")"
    have_sdk="$(cd "$root" && dotnet --version)"
    [ "$have_sdk" = "$want_sdk" ] || fail "SDK $have_sdk, erwartet $want_sdk (deploy/cosmos/SDK); --any-sdk nur fuer lokale Versuche"
fi

default_commit="$(tr -d '[:space:]' < "$root/deploy/cosmos/COMMIT")"
default_version="$(tr -d '[:space:]' < "$root/deploy/cosmos/VERSION")"
[ -n "$commit" ] || commit="$default_commit"
if [ -z "$version" ]; then
    if [ "$commit" = "$default_commit" ]; then
        version="$default_version"
    else
        # Versuchsbau aus einem anderen Commit: eindeutige Version, kollidiert nie mit einer veroeffentlichten.
        version="${default_version%%-*}-zehos.0.g${commit:0:7}"
    fi
fi
[ -n "$feed" ] || feed="$root/deploy/cosmos/feed"
feed="$(mkdir -p "$feed" && cd "$feed" && pwd)"
src="$root/.tools/cosmos-src"
out="$src/artifacts/package/release"
toolpath="$root/.tools/cosmos-tools/$version"
limine_tag="v10.8.5-binary"
limine_sha256="91ef81c1f95c4ac8bf77c4393a0b2fcd2628550827d7abf89a43194400e17a85"
build_path_map="$src=/home/runner/work/Cosmos/Cosmos"

# Stempelpruefung: dieselbe Version darf nur zu demselben Commit gehoeren (sonst liefert der NuGet-Cache den alten Stand).
stamp="$feed/$version.commit"
rebuild=1
if [ -f "$stamp" ]; then
    stamped="$(tr -d '[:space:]' < "$stamp")"
    if [ "$stamped" != "$commit" ]; then
        fail "Version $version gehoert zu Commit $stamped; deploy/cosmos/VERSION erhoehen"
    fi
    rebuild=0
fi

run() {
    if [ "$plan" = 1 ]; then printf '%s\n' "$*"; else "$@"; fi
}
dn() { # dotnet im Quellbaum mit Version und PathMap
    local verb="$1"; shift
    run dotnet "$verb" "$@" -c Release "-p:VersionPrefix=$version" "-p:PathMap=$build_path_map" "-p:RestoreAdditionalProjectSources=$out"
}
projects_build="Cosmos.Build.API Cosmos.Build.Common Cosmos.Build.Asm Cosmos.Build.CC Cosmos.Build.Ilc Cosmos.Build.Patcher Cosmos.Build.Analyzer.Patcher.Package Cosmos.Patcher Cosmos.Tools"
projects_arch_free="Cosmos.Kernel.Drivers Cosmos.Kernel.Debug Cosmos.Kernel.Boot.Limine Cosmos.Kernel.SourceGenerators"
projects_multi="Cosmos.Kernel.Core Cosmos.Kernel.HAL Cosmos.Kernel.System Cosmos.Kernel.Plugs Cosmos.Kernel"

if [ "$rebuild" = 1 ]; then
    run git -C "$root" fetch origin "$commit"
    if [ "$plan" = 1 ] || [ ! -e "$src/.git" ]; then
        run git -C "$root" worktree add --detach "$src" "$commit"
    else
        run git -C "$src" checkout --detach "$commit"
    fi
    run git -C "$src" submodule update --init --recursive --depth 1
    run rm -rf "$src/artifacts/limine"
    run git clone --depth 1 --branch "$limine_tag" https://github.com/Limine-Bootloader/Limine.git "$src/artifacts/limine"
    run rm -rf "$src/artifacts/limine/.git"
    if [ "$plan" = 0 ]; then
        actual="$(sha256sum "$src/artifacts/limine/BOOTX64.EFI" | cut -d' ' -f1)"
        [ "$actual" = "$limine_sha256" ] || fail "Limine BOOTX64.EFI: SHA-256 $actual statt $limine_sha256"
    fi
    export VersionPrefix="$version"
    run mkdir -p "$out"
    for p in $projects_build; do dn build "$src/src/$p/$p.csproj" --no-incremental; done
    for p in Cosmos.Kernel.Native.X64 Cosmos.Kernel.Native.MultiArch; do dn pack "$src/src/$p/$p.csproj" -o "$out"; done
    for p in $projects_arch_free Cosmos.Kernel.Core.X64 Cosmos.Kernel.HAL.X64; do
        dn build "$src/src/$p/$p.csproj" -p:GeneratePackageOnBuild=false
        dn pack "$src/src/$p/$p.csproj" --no-build -o "$out"
    done
    dn build "$src/src/Cosmos.Kernel/Cosmos.Kernel.csproj" -r linux-x64 -p:CosmosArch=x64 --no-incremental
    for p in $projects_multi; do
        run mkdir -p "$src/artifacts/multiarch/$p/x64"
        # Wie upstream: erst das RID-Verzeichnis, ersatzweise das RID-lose (Core, HAL, System und Plugs liegen unter release).
        built="$src/artifacts/bin/$p/release_linux-x64/$p.dll"
        if [ "$plan" = 0 ] && [ ! -f "$built" ]; then built="$src/artifacts/bin/$p/release/$p.dll"; fi
        if [ "$plan" = 0 ] && [ ! -f "$built" ]; then fail "gebaute DLL fehlt: $p.dll"; fi
        run cp "$built" "$src/artifacts/multiarch/$p/x64/"
        run find "$src/artifacts/obj/$p" -name '*.nuspec' -delete
        run rm -f "$out/$p".[0-9]*.nupkg
        dn pack "$src/src/$p/$p.csproj" -o "$out" -p:NoBuild=true
    done
    dn build "$src/src/Cosmos.Sdk/Cosmos.Sdk.csproj" --no-incremental
    if [ "$plan" = 0 ]; then
        for id in Cosmos.Sdk Cosmos.Build.API Cosmos.Build.Asm Cosmos.Build.CC Cosmos.Build.Common Cosmos.Build.Ilc Cosmos.Build.Patcher \
                  Cosmos.Kernel Cosmos.Kernel.Boot.Limine Cosmos.Kernel.Core Cosmos.Kernel.Core.X64 Cosmos.Kernel.Debug Cosmos.Kernel.Drivers \
                  Cosmos.Kernel.HAL Cosmos.Kernel.HAL.X64 Cosmos.Kernel.Native.MultiArch Cosmos.Kernel.Native.X64 Cosmos.Kernel.Plugs \
                  Cosmos.Kernel.SourceGenerators Cosmos.Kernel.System Cosmos.Patcher Cosmos.Tools; do
            [ -f "$out/$id.$version.nupkg" ] || fail "Paket fehlt im Bau: $id.$version.nupkg"
        done
        cp "$out"/*."$version".nupkg "$feed/"
        printf '%s\n' "$commit" > "$stamp"
    else
        printf '%s\n' "cp $out/*.$version.nupkg $feed/" "schreibe $stamp"
    fi
fi

if [ "$tools" = 1 ]; then
    for tool in Cosmos.Patcher Cosmos.Tools; do
        if [ "$global" = 1 ]; then
            run dotnet tool update --global "$tool" --version "$version" --add-source "$feed"
        else
            run dotnet tool install --tool-path "$toolpath" "$tool" --version "$version" --add-source "$feed"
        fi
    done
fi
