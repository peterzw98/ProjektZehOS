cd /home/peter/zehos-team/agent-peter-engineer2/v47
for i in 1 2 3; do for s in 1 4; do
rm -rf h$s-$i; LEAK_N=1000 LEAK_WARM=100 timeout 1500 python3 leak.py fix07ff.iso h$s-$i $s 512M > h1000-smp$s-$i.txt 2>&1
rm -f h$s-$i/*.qcow2; grep verlust h1000-smp$s-$i.txt
done; done
