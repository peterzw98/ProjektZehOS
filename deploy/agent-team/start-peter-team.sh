#!/bin/bash
# Startet Peters Agenten auf diesem Rechner (Liste agents-peter.tsv) als Einzellaeufe im Hintergrund.
#   start-peter-team.sh            alle Agenten der Liste
#   start-peter-team.sh <nick>     nur diesen Agenten
# Jede Einstellung laesst sich per Umgebung ueberschreiben (Bedeutung im Kopf von start-agent.sh).
set -eu
here=$(cd "$(dirname "$0")" && pwd)
# Haupt-Arbeitsbaum: dort liegen IRC-Passwort und die Objekte, die neue Klone mitbenutzen
main=$(cd "$(git -C "$here" rev-parse --path-format=absolute --git-common-dir)/.." && pwd)
export AGENTS_FILE=${AGENTS_FILE:-$here/agents-peter.tsv}
export PROJECT_COORDINATOR_BASE=${PROJECT_COORDINATOR_BASE:-$HOME/zehos-team}
export IRCKEY_FILE=${IRCKEY_FILE:-$main/.tools/IRCKEY}
export CLONE_REFERENCE=${CLONE_REFERENCE:-$main}
# sbin und zehos-tools: e2fsck, sgdisk, mtools fuer Gastlaeufe; ohne sie suchen die Agenten danach
export PATH_PREFIX=${PATH_PREFIX:-$HOME/.dotnet:$HOME/.local/bin:$HOME/.local/opt/zehos-tools/root/usr/bin:$HOME/.local/opt/zehos-tools/root/usr/sbin:/usr/sbin}
# Bauende Agenten (Engineers, Senior) hoechstens 2 gleichzeitig: drei Laeufe mit nativem Bau oder KVM-Gast ueberlasteten MSI (19.11.).
# Der Reviewer hat einen eigenen Topf (pool=review:1 in agents-peter.tsv) und laeuft zusaetzlich.
export DISPATCH_PARALLEL=${DISPATCH_PARALLEL:-2}
# Wer Auftraege geben darf: Koordinator und Peters Leads (feste Nicks, siehe TEAM.md); dieselben duerfen Laeufe starten
export TEAM_ASSIGNERS=${TEAM_ASSIGNERS:-agent-coordinator,agent-peter-lead,agent-nweh-lead1,agent-nweh-lead2}
export DISPATCH_ALLOW=${DISPATCH_ALLOW:-$TEAM_ASSIGNERS}
[ -r "$IRCKEY_FILE" ] || { echo "start-peter-team: IRC-Passwort fehlt ($IRCKEY_FILE)" >&2; exit 1; }
mkdir -p "$PROJECT_COORDINATOR_BASE"
log=$PROJECT_COORDINATOR_BASE/start.log
# In eine Datei, nicht in eine Pipe: Die abgekoppelten screen-Sitzungen hielten eine Pipe offen
run=$PROJECT_COORDINATOR_BASE/start.last
bash "$here/start-agent.sh" "${1:---all}" >"$run" 2>&1 || { cat "$run" >>"$log"; tail -n 20 "$run" >&2; exit 1; }
cat "$run" >>"$log"
grep -E '^(gestartet|laeuft schon|start-agent)' "$run" || true
echo "Zustand: cat $PROJECT_COORDINATOR_BASE/<nick>/dispatch/heartbeat   Beenden: screen -S <nick> -X quit"
