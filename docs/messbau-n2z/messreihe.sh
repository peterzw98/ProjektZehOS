#!/bin/sh
# Messreihe: 3x1000 Starts smp1+smp4 (100 Warm), danach 1x10000 smp4. Nutzung: sh messreihe.sh <iso> <praefix>
# Ergebnisse: <praefix>-1000-smpS-I.txt, <praefix>-10000-smp4.txt; Gast 512M, KVM, ein Gast gleichzeitig. Vorher #first auf Gast-Sperre lesen.
cd "$(dirname "$0")"; ISO=$1; P=$2
for i in 1 2 3; do for s in 1 4; do
  rm -rf $P-w$s-$i; LEAK_N=1000 LEAK_WARM=100 timeout 1500 python3 leak.py $ISO $P-w$s-$i $s 512M > $P-1000-smp$s-$i.txt 2>&1
  rm -f $P-w$s-$i/*.qcow2; grep verlust $P-1000-smp$s-$i.txt
done; done
rm -rf $P-w10k; LEAK_N=10000 LEAK_WARM=100 timeout 3600 python3 leak.py $ISO $P-w10k 4 512M > $P-10000-smp4.txt 2>&1
rm -f $P-w10k/*.qcow2; grep verlust $P-10000-smp4.txt
