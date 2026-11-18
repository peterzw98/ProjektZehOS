#!/bin/bash
# Bereitet nur die festgelegte Prüfversion vor; vorhandene Änderungen werden niemals überschrieben.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
repo=$(git -C "${1:-$here/../..}" rev-parse --show-toplevel)
pin=$(tr -d '[:space:]' < "$here/BRIDGE_COMMIT")
fail() { echo "prepare-bridge: $*" >&2; exit 1; }
[[ "$pin" =~ ^[0-9a-f]{40}$ ]] || fail "ungueltiger Commit-Pin"
copy="$repo/.tools/disc-bridge"
if ! git -C "$repo" cat-file -e "$pin^{commit}" 2>/dev/null; then
  git -C "$repo" fetch --no-tags origin "$pin"
fi
if [ -e "$copy" ]; then
  [ -f "$copy/.git" ] || fail "Pruefkopie ist kein Worktree: $copy"
  [ "$(git -C "$copy" rev-parse HEAD)" = "$pin" ] || fail "Pruefkopie hat anderen HEAD; manuell pruefen: $copy"
else
  mkdir -p "$repo/.tools"
  git -c core.autocrlf=false -c core.eol=lf -C "$repo" worktree add --detach "$copy" "$pin"
fi
# Index-Flags dürfen Änderungen an den tatsächlich ausgeführten Prüfdateien nicht verstecken.
for file in verify.sh bridge-pubkey.asc; do
  expected=$(git -C "$copy" rev-parse "$pin:$file")
  actual=$(git -C "$copy" hash-object --no-filters "$copy/$file")
  [ "$actual" = "$expected" ] || fail "Pruefdatei weicht vom Pin ab; bleibt erhalten: $copy/$file"
done
printf '%s/verify.sh\n' "$copy"
