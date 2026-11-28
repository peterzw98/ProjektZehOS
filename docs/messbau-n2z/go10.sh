cd /home/peter/zehos-team/agent-peter-engineer2/v47
for i in 1 2 3 4; do for s in 1; do
rm -rf j$s-$i; ZEHOS_MEMPROBE=$PWD/spawnleak3 LEAK_N=1000 LEAK_WARM=100 timeout 1500 python3 leak.py fix07ff.iso j$s-$i $s 512M > j1000-smp$s-$i.txt 2>&1
rm -f j$s-$i/*.qcow2; grep "verlust\|ergebnis" j1000-smp$s-$i.txt
done; done
