import sys, re
import importlib.util, pathlib
spec = importlib.util.spec_from_file_location('t', pathlib.Path(__file__).with_name('tls13-pcap-check.py')); t = importlib.util.module_from_spec(spec); spec.loader.exec_module(t)
pcap, serial = sys.argv[1], sys.argv[2]
txt = open(serial, errors='replace').read()
diag = [(int(a), b) for a, b in re.findall(r'TLSDIAG (\d+) ([0-9a-f]{8})', txt)]
tot = re.findall(r'TLSDIAG total=(\d+) (\S+)', txt)
print('diag', tot, len(diag), 'Bloecke')
st = t.streams(pcap)
best = max((v[0] for k, v in st.items() if k[1] != 0 and v[0][:1] == b'\x16' and len(v[0]) > 1000000), key=len)
def fnv(b):
    h = 2166136261
    for x in b: h = ((h ^ x) * 16777619) & 0xffffffff
    return '%08x' % h
bad = [(o, h, fnv(best[o:o+1024])) for o, h in diag if fnv(best[o:o+1024]) != h]
print('Bloecke abweichend:', len(bad), bad[:10])
