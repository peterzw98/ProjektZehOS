#!/bin/sh
# Haelt /srv/zehos-packages auf dem Stand eines Git-Repositorys mit signierten Paketen (Layout wie von
# scripts/build-package.py). nginx liest dasselbe Volume nur lesend; neue Dateien sind ohne Neustart sichtbar.
# Signaturen prueft ZehOS selbst, deshalb muss der Git-Server nicht vertrauenswuerdig sein.
set -eu
: "${PACKAGES_GIT_URL:?PACKAGES_GIT_URL fehlt (siehe .env.example)}"
branch=${PACKAGES_BRANCH:-main}
interval=${PACKAGES_INTERVAL_SECONDS:-300}
target=/srv/zehos-packages

while true; do
    if [ ! -d "$target/.git" ]; then
        # Erster Lauf: in einen leeren Ordner klonen; vorhandene Fremddateien werden nicht ueberschrieben.
        if git clone --branch "$branch" --depth 1 "$PACKAGES_GIT_URL" "$target/.clone" &&
           mv "$target/.clone/.git" "$target/.git" && rm -rf "$target/.clone" &&
           git -C "$target" reset --hard -q "origin/$branch"; then
            echo "Paketrepository geklont: $(git -C "$target" rev-parse --short HEAD)"
        else
            echo "Klonen fehlgeschlagen; naechster Versuch in ${interval} s." >&2
            rm -rf "$target/.clone"
        fi
    elif git -C "$target" fetch -q --depth 1 origin "$branch"; then
        old=$(git -C "$target" rev-parse HEAD)
        new=$(git -C "$target" rev-parse FETCH_HEAD)
        if [ "$old" != "$new" ]; then
            git -C "$target" reset --hard -q FETCH_HEAD
            # Alte Versionen bleiben im Git-Verlauf; entfernte Dateien verschwinden auch vom Server.
            git -C "$target" clean -fdq
            echo "Paketrepository aktualisiert: ${old%"${old#???????}"} -> ${new%"${new#???????}"}"
        fi
    else
        echo "Abruf fehlgeschlagen; Server liefert weiter den bisherigen Stand." >&2
    fi
    sleep "$interval"
done
