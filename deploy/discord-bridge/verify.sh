#!/bin/sh
# Prüft eine von der Discord-Bridge weitergeleitete IRC-Zeile; Exitcode 0 = gültig und höchstens 10 Minuten alt.
# Aufruf: verify.sh '#general' < datei-mit-zeile   (vorher: gpg --import bridge-pubkey.asc)
# Die Zeile kommt über stdin, weil sie aus Discord stammt und als Argument die Shell-Quotierung brechen könnte.
FPR=D170DEF86DB8574DD5267CBBD5B2E2C51E45D537
channel=$(printf '%s' "$1" | tr 'A-Z' 'a-z'); line=$(head -n1)
meta=$(printf '%s' "$line" | sed -n 's/.* \[id=\([0-9]*\) t=\([0-9]*\) sig=\([A-Za-z0-9+/=]*\)\]$/\1 \2 \3/p')
[ -n "$meta" ] || { echo "unsigned"; exit 1; }
set -- $meta
text=$(printf '%s' "$line" | sed 's/^<[^>]*> //; s/ \[id=[0-9]* t=[0-9]* sig=[A-Za-z0-9+/=]*\]$//')
age=$(( $(date +%s) - $2 )); [ "$age" -le 600 ] && [ "$age" -ge -60 ] || { echo "stale"; exit 1; }
sig=$(mktemp); printf '%s' "$3" | base64 -d > "$sig"
printf '%s\n%s\n%s\n%s' "$channel" "$1" "$2" "$text" | gpg --batch --status-fd 1 --verify "$sig" - 2>/dev/null | grep -q "^\[GNUPG:\] VALIDSIG $FPR " && ok=1
rm -f "$sig"
[ -n "$ok" ] && echo "valid id=$1" || { echo "INVALID"; exit 1; }
