#!/bin/sh
# Haelt das ausgelieferte Paketrepository auf dem Stand eines Git-Branches mit signierten Paketen (Layout wie von
# scripts/build-package.py). Jeder Stand wird vollstaendig in releases/<commit> ausgepackt und erst dann ueber den
# Symlink "current" veroeffentlicht, den nginx ausliefert. So sieht ZehOS nie Katalog und Pakete aus verschiedenen
# Staenden; der vorige Stand bleibt fuer laufende Downloads erhalten. Signaturen prueft ZehOS selbst.
set -eu
: "${PACKAGES_GIT_URL:?PACKAGES_GIT_URL fehlt (siehe .env.example)}"
branch=${PACKAGES_BRANCH:-packages}
interval=${PACKAGES_INTERVAL_SECONDS:-300}
root=/srv/zehos-packages
clone=$root/.git-clone
releases=$root/releases

mkdir -p "$releases"
while true; do
    if [ ! -d "$clone/.git" ]; then
        rm -rf "$clone"
        git clone -q --branch "$branch" --depth 1 "$PACKAGES_GIT_URL" "$clone" || echo "Klonen fehlgeschlagen; naechster Versuch in ${interval} s." >&2
    elif ! git -C "$clone" fetch -q --depth 1 origin "$branch" || ! git -C "$clone" reset -q --hard FETCH_HEAD; then
        echo "Abruf fehlgeschlagen; Server liefert weiter den bisherigen Stand." >&2
    fi
    if [ -d "$clone/.git" ]; then
        commit=$(git -C "$clone" rev-parse HEAD)
        if [ "$(readlink "$root/current" 2>/dev/null || true)" != "releases/$commit" ]; then
            # Vollstaendig in einen neuen Ordner auspacken; ein abgebrochener Versuch wird beim naechsten Lauf ersetzt.
            rm -rf "$releases/$commit.tmp"
            mkdir -p "$releases/$commit.tmp"
            if git -C "$clone" archive "$commit" | tar -x -C "$releases/$commit.tmp"; then
                rm -rf "${releases:?}/${commit:?}"
                mv "$releases/$commit.tmp" "$releases/$commit"
                previous=$(readlink "$root/current" 2>/dev/null || true)
                # ln -sfn ersetzt den Link in einem Schritt; zwischen Entfernen und Anlegen liegt nur ein sehr kurzes Fenster.
                ln -sfn "releases/$commit" "$root/current"
                echo "Paketrepository veroeffentlicht: $commit"
                for old in "$releases"/*; do
                    name=releases/$(basename "$old")
                    [ "$name" = "releases/$commit" ] || [ "$name" = "$previous" ] || rm -rf "$old"
                done
                git -C "$clone" gc -q --prune=now || true
            else
                echo "Auspacken fehlgeschlagen; bisheriger Stand bleibt." >&2
                rm -rf "$releases/$commit.tmp"
            fi
        fi
    fi
    sleep "$interval"
done
