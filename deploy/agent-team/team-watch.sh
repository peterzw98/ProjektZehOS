#!/bin/bash
# Monitor-Quelle eines Teamagenten: neue IRC-Zeilen kompakt als "HH:MM nick -> ziel: text".
#   team-watch.sh <irc-dir> <nick>         nur Nachrichten, die <nick> nennen (Spezialisten)
#   team-watch.sh <irc-dir> <nick> --all   alles aus allen Kanälen (Koordinator)
# Jede Stufe ist zeilengepuffert, sonst kommen Benachrichtigungen verspätet.
dir=$1; nick=$2; mode=${3:-}
[ -n "$dir" ] && [ -n "$nick" ] || { echo "Aufruf: team-watch.sh <irc-dir> <nick> [--all]" >&2; exit 2; }
if [ "$mode" = "--all" ]; then
  events='<< :[^ ]+ (PRIVMSG|NOTICE|INVITE|JOIN|PART|QUIT|NICK|TOPIC|KICK) |<< :[^ ]+ 4[0-9][0-9] |!! '
  mention='.'
else
  events='<< :[^ ]+ (PRIVMSG|NOTICE|INVITE) |!! '
  # Ganzer Nick, nicht als Teil eines längeren (agent-orchestrator vs. agent-orchestrator-junior)
  mention="(?<![A-Za-z0-9_-])${nick}(?![A-Za-z0-9_-])|^.{20}!! "
fi
# mawk liest seine Eingabe blockweise und hält einzelne Zeilen sonst zurück
awk=(awk); awk -W version 2>&1 | grep -q mawk && awk=(awk -W interactive)
tail -n0 -F "$dir/log.txt" \
  | grep --line-buffered -E "$events" \
  | grep --line-buffered -v -E ":(${nick}|HistServ)!|:zeh\.ernst\.casa NOTICE" \
  | grep --line-buffered -iP "$mention" \
  | sed -u -E 's/^[0-9-]+ ([0-9]{2}:[0-9]{2}):[0-9]{2} << :([^!]+)![^ ]+ (PRIVMSG|NOTICE) ([^ ]+) :/\1 \2 -> \4: /; s/^[0-9-]+ ([0-9]{2}:[0-9]{2}):[0-9]{2} << :([^!]+)![^ ]+ ([A-Z]+) ?:?/\1 [\3] \2 /' \
  | "${awk[@]}" '
      # Bremse gegen Nennungsschleifen: höchstens 8 Zeilen je Absender und Minute, danach ein Hinweis.
      # Menschen kommen alle über den Nick discord; sie zählen einzeln nach dem <Namen>
      { split($0, f, " "); who = f[2]; min = f[1]; if (who == "discord") who = who f[5]
        if (min != last[who]) { last[who] = min; n[who] = 0 }
        if (++n[who] <= 8) print substr($0, 1, 1200)
        else if (n[who] == 9) print min " [gedrosselt] " who " sendet zu viel, Rest im log.txt"
        fflush() }'
