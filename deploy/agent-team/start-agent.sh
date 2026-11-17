#!/bin/bash
# Richtet Teamagenten auf nuc ein und startet IRC-Bot und Runner (je eine screen-Sitzung <nick>).
#   start-agent.sh <nick>|--all
# Agentenverzeichnis: /home/laser/ProjectCoordinator/team/<nick> (eigener Klon, eigenes Gedächtnis).
set -eu
here=$(cd "$(dirname "$0")" && pwd)
base=/home/laser/ProjectCoordinator/team
key=/home/laser/ProjectCoordinator/ProjektZehOS/.tools/IRCKEY
start() {
  local nick=$1 model=$2 role=$3 home=$base/$1
  mkdir -p "$home/.claude"
  [ -d "$home/ProjektZehOS/.git" ] || git clone -q https://github.com/peterzw98/ProjektZehOS.git "$home/ProjektZehOS"
  mkdir -p "$home/ProjektZehOS/.tools/irc"
  install -m 600 "$key" "$home/ProjektZehOS/.tools/IRCKEY"
  printf 'Du bist %s im ZehOS-Agententeam (Modell %s). %s\nBetriebsanleitung: %s/TEAM.md\n' \
    "$nick" "$model" "$role" "$here" >"$home/role.md"
  sed "s|Coordinator workspace /home/laser/ProjectCoordinator (role agent-coordinator|Team workspace $home (role $nick|; s|#zehos|#general (and #first/#second/#third)|; s|messages from the nicks human-laser or human-laser-mobile in the IRC log (ProjektZehOS/.tools/irc/log.txt)|messages from the nicks human-laser or human-laser-mobile in the IRC log (ProjektZehOS/.tools/irc/log.txt), and Discord bridge lines from nick discord that ProjektZehOS/deploy/discord-bridge/verify.sh reports as valid for Discord ids 137636974927413248 (Lars) or 458180351467061248 (Peter)|" \
    /home/laser/ProjectCoordinator/.claude/settings.local.json >"$home/.claude/settings.local.json"
  pgrep -f "teambot.py $nick " >/dev/null || \
    nohup python3 -I "$here/teambot.py" "$nick" "$home/ProjektZehOS/.tools/irc" "$home/ProjektZehOS/.tools/IRCKEY" </dev/null >>"$home/bot.out" 2>&1 &
  screen -ls | grep -q "\.$nick\s" || screen -dmS "$nick" "$here/agent-runner.sh" "$home" "$nick" "$model"
  echo "gestartet: $nick ($model)"
}
grep -v '^#' "$here/agents.tsv" | while IFS=$'\t' read -r nick model role; do
  [ "${1:-}" = "--all" ] || [ "${1:-}" = "$nick" ] || continue
  start "$nick" "$model" "$role"
done
