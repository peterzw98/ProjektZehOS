#!/usr/bin/env bash
# Baut Cosmos (Quelle: Branch "cosmos") zu NuGet-Paketen und installiert cosmos/cosmos.patcher.
# Siehe deploy/cosmos/README.md und docs/superpowers/plans/2026-11-18-multicore-e1-cosmos-quelle.md (E1-B).
# Schreibt nur unter .tools/ und deploy/cosmos/feed/; der NuGet-Cache und die NuGet-Konfiguration des Benutzers bleiben unberuehrt.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# global.json (SDK-Wahl) wird ueber das Arbeitsverzeichnis gesucht; immer im Repository arbeiten.
call_dir="$PWD"
cd "$root"
commit="" version="" feed="" tools=1 global=0 plan=0 any_sdk=0
while [ $# -gt 0 ]; do
    case "$1" in
        --commit) commit="${2:?--commit braucht einen Wert}"; shift 2 ;;
        --version) version="${2:?--version braucht einen Wert}"; shift 2 ;;
        --feed) feed="${2:?--feed braucht einen Wert}"; [[ "$feed" = /* ]] || feed="$call_dir/$feed"; shift 2 ;;
        --no-tools) tools=0; shift ;;
        --global) global=1; shift ;;
        --any-sdk) any_sdk=1; shift ;;
        --print-plan) plan=1; shift ;;
        *) echo "Unbekanntes Argument: $1" >&2; exit 2 ;;
    esac
done

fail() { echo "build-cosmos: $*" >&2; exit 1; }

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
# Eine offizielle Versionsnummer (ohne "-zehos.") im Standardfeed wuerde Eigenbau und nuget.org verwechselbar machen.
if [ -n "$version" ] && [[ "$version" != *-zehos.* ]] && [ -z "$feed" ]; then
    fail "Version $version ohne -zehos.: nur mit ausdruecklichem --feed (ausserhalb von deploy/cosmos/feed) erlaubt"
fi
[ -n "$feed" ] || feed="$root/deploy/cosmos/feed"
feed="$(mkdir -p "$feed" && cd "$feed" && pwd)"
src="$root/.tools/cosmos-src"
out="$src/artifacts/package/release"
toolpath="$root/.tools/cosmos-tools/$version"
limine_tag="v10.8.5-binary"
limine_commit="7a9013f305de0bea1f6310e76e7baba30499fef0"
limine_sha256="91ef81c1f95c4ac8bf77c4393a0b2fcd2628550827d7abf89a43194400e17a85"
build_path_map="$src=/home/runner/work/Cosmos/Cosmos"
# Eigene NuGet-Konfiguration fuer den Quellbau: unabhaengig von NuGet.Config des Repositorys und vom Benutzer; die Zwischenpakete
# des Baus (Cosmos.*) kommen nur aus $out, alles andere aus nuget.org.
build_nuget_config="$root/.tools/cosmos-build.NuGet.Config"

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

# Commit, den ein Kernpaket in seiner nuspec traegt (aus dem Paket selbst oder dem NuGet-Cache des Rechners).
nuspec_commit() { grep -o '<repository[^>]*commit="[0-9a-f]*"' | grep -o 'commit="[0-9a-f]*"' | cut -d'"' -f2; }
if [ "$plan" = 0 ]; then
    cache_dir="$(dotnet nuget locals global-packages -l)"; cache_dir="${cache_dir#global-packages: }"
    cached="$cache_dir/cosmos.kernel/$version/cosmos.kernel.nuspec"
    if [ -f "$cached" ]; then
        cached_commit="$(nuspec_commit < "$cached")"
        [ "$cached_commit" = "$commit" ] || \
            fail "NuGet-Cache enthaelt Cosmos $version aus Commit '"${cached_commit:-?}"', nicht $commit; deploy/cosmos/VERSION erhoehen"
    fi
fi

run() {
    if [ "$plan" = 1 ]; then printf '%s\n' "$*"; else "$@"; fi
}
dn() { # dotnet im Quellbaum mit Version und PathMap
    local verb="$1"; shift
    run dotnet "$verb" "$@" -c Release "-p:VersionPrefix=$version" "-p:PathMap=$build_path_map" "-p:RestoreConfigFile=$build_nuget_config"
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
    # Das SDK bestimmt IL-Details (Quellgenerator, Stackgroessen); Referenzbau nur mit dem in deploy/cosmos/SDK festgelegten.
    # Gemessen im Quellbaum, weil dort Cosmos' eigene global.json gilt. --any-sdk nur fuer lokale Versuche; solche Feeds
    # nie fuer Release oder IL-Vergleich (E1-D) verwenden.
    if [ "$plan" = 0 ] && [ "$any_sdk" = 0 ]; then
        want_sdk="$(tr -d '[:space:]' < "$root/deploy/cosmos/SDK")"
        have_sdk="$(cd "$src" && dotnet --version)"
        [ "$have_sdk" = "$want_sdk" ] || fail "SDK $have_sdk, erwartet $want_sdk (deploy/cosmos/SDK); --any-sdk nur fuer lokale Versuche"
    fi
    run rm -rf "$src/artifacts/limine"
    run git clone --depth 1 --branch "$limine_tag" https://github.com/Limine-Bootloader/Limine.git "$src/artifacts/limine"
    if [ "$plan" = 0 ]; then
        # Gepackt wird der ganze Ordner (auch limine-bios*.bin); der feste Commit deckt alle Dateien ab, nicht nur BOOTX64.EFI.
        actual_commit="$(git -C "$src/artifacts/limine" rev-parse HEAD)"
        [ "$actual_commit" = "$limine_commit" ] || fail "Limine $limine_tag: Commit $actual_commit statt $limine_commit"
    fi
    run rm -rf "$src/artifacts/limine/.git"
    if [ "$plan" = 0 ]; then
        actual="$(sha256sum "$src/artifacts/limine/BOOTX64.EFI" | cut -d' ' -f1)"
        [ "$actual" = "$limine_sha256" ] || fail "Limine BOOTX64.EFI: SHA-256 $actual statt $limine_sha256"
    fi
    export VersionPrefix="$version"
    run mkdir -p "$out"
    if [ "$plan" = 0 ]; then
        mkdir -p "$(dirname "$build_nuget_config")"
        cat > "$build_nuget_config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="cosmos-build" value="$out" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="cosmos-build">
      <package pattern="Cosmos.*" />
      <package pattern="_Cosmos.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
XML
    else
        echo "schreibe $build_nuget_config"
    fi
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
        for id in Cosmos.Sdk Cosmos.Build.API Cosmos.Build.Analyzer.Patcher _Cosmos.Build.Analyzer.Patcher Cosmos.Build.Analyzer.Patcher.CodeFixes \
                  Cosmos.Build.Asm Cosmos.Build.CC Cosmos.Build.Common Cosmos.Build.Ilc Cosmos.Build.Patcher \
                  Cosmos.Kernel Cosmos.Kernel.Boot.Limine Cosmos.Kernel.Core Cosmos.Kernel.Core.X64 Cosmos.Kernel.Debug Cosmos.Kernel.Drivers \
                  Cosmos.Kernel.HAL Cosmos.Kernel.HAL.X64 Cosmos.Kernel.Native.MultiArch Cosmos.Kernel.Native.X64 Cosmos.Kernel.Plugs \
                  Cosmos.Kernel.SourceGenerators Cosmos.Kernel.System Cosmos.Patcher Cosmos.Tools; do
            [ -f "$out/$id.$version.nupkg" ] || fail "Paket fehlt im Bau: $id.$version.nupkg"
        done
        built_commit="$(unzip -p "$out/Cosmos.Kernel.$version.nupkg" Cosmos.Kernel.nuspec | nuspec_commit)"
        [ "$built_commit" = "$commit" ] || fail "Cosmos.Kernel.$version.nupkg traegt Commit '$built_commit' statt $commit"
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
