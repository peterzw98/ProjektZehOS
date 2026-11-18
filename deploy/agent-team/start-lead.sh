#!/bin/bash
# Startet eine interaktive Lead- oder Koordinator-Sitzung: IRC-Bot im Hintergrund (alle vier Kanaele) und Claude Code
# mit der passenden Rolle. Aufruf aus einer Arbeitskopie des Repositorys:
#   start-lead.sh <nick> [vorlage] [modell]        vorlage: lead (Standard) oder coordinator; modell: Standard Sonnet
# Danach im Claude-Fenster nur noch die Aufgabe nennen. Nur Linux (der Bot braucht FIFOs); unter Windows in WSL starten.
set -eu
nick=${1:?Aufruf: start-lead.sh <nick> [lead|coordinator] [modell]}
vorlage=${2:-lead}
model=${3:-claude-sonnet-5-5}
here=$(cd "$(dirname "$0")" && pwd)
main=$(cd "$(git -C "$here" rev-parse --path-format=absolute --git-common-dir)/.." && pwd)
key=${IRCKEY_FILE:-$main/.tools/IRCKEY}
case ${nick,,} in human-*) echo "start-lead: human-*-Nicks sind Menschen vorbehalten" >&2; exit 2 ;; esac
[ -f "$here/prompts/$vorlage.md" ] || { echo "start-lead: Vorlage prompts/$vorlage.md fehlt" >&2; exit 2; }
[ -r "$key" ] || { echo "start-lead: IRC-Passwort fehlt ($key)" >&2; exit 1; }
irc=$main/.tools/irc-$nick
mkdir -p "$irc" "$main/.tools/state"
# Ein Bot je Nick; laeuft er schon (etwa nach einem Neustart von Claude), bleibt er
pgrep -f "teambot.py $nick " >/dev/null || \
  nohup python3 -I "$here/teambot.py" "$nick" "$irc" "$key" '#general,#first,#second,#third' </dev/null >>"$irc/bot.out" 2>&1 &
role=$main/.tools/state/$nick-role.md
{
  printf 'Du bist %s (interaktive Sitzung, Rolle %s, Modell %s) im ZehOS-Agententeam. Betriebsanleitung: %s/TEAM.md\n' \
    "$nick" "$vorlage" "$model" "$here"
  printf 'Dein IRC-Ordner: %s (log.txt empfangen, outbox.fifo senden). Stand: %s/.tools/state/%s.md\n' "$irc" "$main" "$nick"
  printf 'Beobachter (als Monitor starten und nach Ablauf neu starten):\n  %s/team-watch.sh %s %s --all | grep --line-buffered -iE '\''%s|discord ->|\\[(FERTIG|BLOCKER|FRAGE|FREIGABE\\?|UEBERGABE)\\]|\\[dispatcher\\]|\\[!!\\]|gedrosselt'\''\n' \
    "$here" "$irc" "$nick" "$nick"
  for part in _kern "$vorlage"; do printf '\n'; cat "$here/prompts/$part.md"; done
} >"$role"
exec claude --model "$model" --append-system-prompt-file "$role"
