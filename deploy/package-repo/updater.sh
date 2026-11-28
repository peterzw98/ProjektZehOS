#!/bin/sh
# Haelt das ausgelieferte Paketrepository auf dem Stand eines Git-Branches mit signierten Paketen (Layout wie von
# scripts/build-package.py). Jeder Stand wird vollstaendig in releases/<commit> ausgepackt und erst dann ueber den
# Symlink "current" veroeffentlicht, den nginx ausliefert. Zwei Abrufe koennen einen Wechsel ueberschneiden.
# ZehOS prueft Signaturen selbst. Der vorige Stand bleibt auf der Platte.
set -eu
: "${PACKAGES_GIT_URL:?PACKAGES_GIT_URL fehlt (siehe .env.example)}"
branch=${PACKAGES_BRANCH:-packages}
interval=${PACKAGES_INTERVAL_SECONDS:-300}
case "$#:${1:-}" in
    0:|1:--once) ;;
    *) echo 'Aufruf: updater.sh [--once]' >&2; exit 2 ;;
esac
root=${PACKAGES_ROOT:-/srv/zehos-packages}
mkdir -p "$root"
root=$(cd "$root" && pwd -P)
[ "$root" != / ] || { echo 'Ungueltiges Paketverzeichnis: /' >&2; exit 2; }
clone=$root/.git-clone
releases=$root/releases

publish_release() (
    commit=$1
    staging=$(mktemp -d "$root/.publish.XXXXXX") || exit 1
    trap 'rm -rf -- "$staging"' 0
    trap 'exit 1' HUP INT TERM
    mkdir "$staging/release" || exit 1
    # Beide Programme einzeln pruefen. Eine Pipeline ohne pipefail kann ein unvollstaendiges Archiv uebersehen.
    git -C "$clone" archive -o "$staging/archive.tar" "$commit" &&
        tar -xf "$staging/archive.tar" -C "$staging/release" || exit 1
    catalog=$staging/release/catalog2
    signature=$catalog.sig
    if [ -e "$catalog" ] || [ -L "$catalog" ] || [ -e "$signature" ] || [ -L "$signature" ]; then
        if [ ! -f "$catalog" ] || [ -L "$catalog" ] || [ ! -s "$catalog" ] ||
            [ ! -f "$signature" ] || [ -L "$signature" ] || [ ! -s "$signature" ]; then
            echo 'catalog2 und catalog2.sig muessen als nichtleeres Dateipaar vorliegen.' >&2
            exit 1
        fi
    elif [ -e "$root/current/catalog2" ] || [ -e "$root/current/catalog2.sig" ]; then
        echo 'Ein aktiver v2-Katalog darf nicht durch einen reinen v1-Stand ersetzt werden.' >&2
        exit 1
    fi
    target=$releases/$commit
    [ -d "$target" ] || mv -T "$staging/release" "$target" || exit 1
    previous=$(readlink "$root/current" 2>/dev/null || true)
    # Beide Pfade liegen auf demselben Volume. mv -T ersetzt den Link ueber rename.
    ln -s "releases/$commit" "$staging/current" && mv -Tf "$staging/current" "$root/current" || exit 1
    echo "Paketrepository veroeffentlicht: $commit"
    for old in "$releases"/*; do
        name=releases/$(basename "$old")
        [ "$name" = "releases/$commit" ] || [ "$name" = "$previous" ] || rm -rf "$old"
    done
    git -C "$clone" gc -q --prune=now || true
)

sync_release() {
    # Je Repository laeuft ein Updater. SIGKILL kann den Trap des vorigen Versuchs umgehen.
    for orphan in "$root"/.publish.*; do
        [ -d "$orphan" ] && [ ! -L "$orphan" ] || continue
        rm -rf -- "$orphan" || return 1
    done
    if [ ! -d "$clone/.git" ]; then
        rm -rf "$clone"
        git clone -q --branch "$branch" --depth 1 "$PACKAGES_GIT_URL" "$clone" || return 1
    elif ! git -C "$clone" fetch -q --depth 1 origin "$branch" || ! git -C "$clone" reset -q --hard FETCH_HEAD; then
        return 1
    fi
    commit=$(git -C "$clone" rev-parse HEAD) || return 1
    [ "$(readlink "$root/current" 2>/dev/null || true)" = "releases/$commit" ] || publish_release "$commit"
}

mkdir -p "$releases"
while true; do
    if sync_release; then
        [ "${1:-}" != --once ] || exit 0
    else
        echo 'Paketrepository konnte nicht aktualisiert werden; bisheriger Stand bleibt.' >&2
        [ "${1:-}" != --once ] || exit 1
    fi
    sleep "$interval"
done
