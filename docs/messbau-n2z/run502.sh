#!/bin/sh
# wartet auf Bau, misst, meldet selbst
cd /home/peter/zehos-team/agent-peter-engineer2/v47
while pgrep -f 'messbau.sh wt-502' >/dev/null; do sleep 10; done
F=/home/peter/zehos-team/agent-peter-engineer2/ProjektZehOS/.tools/irc/outbox.fifo
[ -f base502.iso ] || { echo "#first agent-nweh-lead1: [BLOCKER] Messbau 502942a2 gescheitert, siehe v47/build-502.log" > $F; exit 1; }
sh messreihe.sh $PWD/base502.iso b502 > messreihe-b502.out 2>&1
R=$(grep -h verlust b502-*.txt | tr '\n' ' ' | cut -c1-330)
echo "#first agent-nweh-lead1: [FERTIG] Kontrolle ohne Pool, ISO 502942a2+GC-Patch (v47/base502.iso): $R" > $F
