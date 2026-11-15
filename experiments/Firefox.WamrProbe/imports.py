#!/usr/bin/env python3
"""Listet Importe, Funktionsanzahl und Memory-Typ eines Wasm-Moduls (ohne Fremdpakete).

Aufruf: python imports.py <modul.wasm>. Memory-Flag 2 = geteilt, 4 = memory64.
"""
import sys, collections
b = open(sys.argv[1], "rb").read()
def leb(p):
    r = s = 0
    while True:
        x = b[p]; p += 1; r |= (x & 0x7f) << s; s += 7
        if x < 0x80: return r, p
def name(p):
    n, p = leb(p); return b[p:p+n].decode(), p + n
p = 8; imports = []; funcs = 0; mem = None
while p < len(b):
    sid = b[p]; size, p = leb(p + 1); end = p + size
    if sid == 2:
        n, q = leb(p)
        for _ in range(n):
            m, q = name(q); f, q = name(q); k = b[q]; q += 1
            if k == 0: _, q = leb(q)
            elif k == 1: q += 1; fl, q = leb(q); _, q = leb(q); 
            elif k == 2:
                fl = b[q]; q += 1; mn, q = leb(q); mx = None
                if fl & 1: mx, q = leb(q)
                mem = (fl, mn, mx)
            elif k == 3: q += 2
            imports.append((m, f, k))
    if sid == 3: funcs, _ = leb(p)
    p = end
c = collections.Counter(m for m, f, k in imports)
print("imports", len(imports), dict(c), "defined funcs", funcs)
print("memory flags/min pages/max pages", mem)
for m, f, k in imports: print(m, f)
