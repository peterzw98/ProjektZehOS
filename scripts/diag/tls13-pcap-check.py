#!/usr/bin/env python3
"""#530-Diagnose: pcap (QEMU filter-dump) + SSLKEYLOG des Servers -> TLS-1.3-Records Server->Gast pruefen.
Meldet je TCP-Verbindung: Bytes, widerspruechliche Wiederholungen, erster Record mit falschem Tag."""
import struct, sys, collections
from cryptography.hazmat.primitives.ciphers.aead import AESGCM, ChaCha20Poly1305
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.kdf.hkdf import HKDFExpand

def frames(path):
    data = open(path, 'rb').read()
    magic = struct.unpack('<I', data[:4])[0]
    e = '<' if magic in (0xa1b2c3d4, 0xa1b23c4d) else '>'
    off = 24
    while off + 16 <= len(data):
        _, _, incl, _ = struct.unpack(e + 'IIII', data[off:off + 16]); off += 16
        yield data[off:off + incl]; off += incl

def tcp_segments(path):
    for f in frames(path):
        if len(f) < 14 or f[12:14] != b'\x08\x00': continue
        ip = f[14:]; ihl = (ip[0] & 15) * 4
        if ip[9] != 6: continue
        total = struct.unpack('>H', ip[2:4])[0]
        src, dst = ip[12:16], ip[16:20]
        t = ip[ihl:total]
        sp, dp, seq = struct.unpack('>HHI', t[:8]); doff = (t[12] >> 4) * 4; flags = t[13]
        yield (src, sp, dst, dp), seq, flags, t[doff:]

def streams(path):
    """(conn, Richtung) -> bytes; prueft, dass ueberlappende Wiederholungen gleich sind."""
    isn, chunks, conflicts = {}, collections.defaultdict(dict), collections.Counter()
    for key, seq, flags, payload in tcp_segments(path):
        if flags & 2: isn[key] = seq + 1; continue  # SYN
        if key not in isn or not payload: continue
        rel = (seq - isn[key]) & 0xffffffff
        c = chunks[key]
        if rel in c and c[rel][:len(payload)] != payload[:len(c[rel])]: conflicts[key] += 1
        if rel not in c or len(payload) > len(c[rel]): c[rel] = payload
    out = {}
    for key, c in chunks.items():
        buf = bytearray(); gaps = 0
        for rel in sorted(c):
            p = c[rel]
            if rel > len(buf): gaps += 1; break
            if rel + len(p) > len(buf):
                if bytes(buf[rel:]) != p[:len(buf) - rel]: conflicts[key] += 1
                buf += p[len(buf) - rel:]
        out[key] = (bytes(buf), gaps, conflicts[key])
    return out

def records(b):
    off = 0
    while off + 5 <= len(b):
        ln = struct.unpack('>H', b[off + 3:off + 5])[0]
        if off + 5 + ln > len(b): yield None, off, b[off:]; return
        yield b[off:off + 5], off, b[off + 5:off + 5 + ln]; off += 5 + ln

def label(secret, lab, length, h):
    full = b'tls13 ' + lab
    info = struct.pack('>H', length) + bytes([len(full)]) + full + b'\x00'
    return HKDFExpand(h, length, info).derive(secret)

SUITES = {0x1301: (AESGCM, 16, hashes.SHA256()), 0x1302: (AESGCM, 32, hashes.SHA384()), 0x1303: (ChaCha20Poly1305, 32, hashes.SHA256())}

def keys(secret, suite):
    cls, kl, h = SUITES[suite]
    return cls(label(secret, b'key', kl, h)), label(secret, b'iv', 12, h)

def main(pcap, keylog):
    log = collections.defaultdict(dict)
    for line in open(keylog):
        p = line.split()
        if len(p) == 3: log[bytes.fromhex(p[1])][p[0]] = bytes.fromhex(p[2])
    st = streams(pcap)
    for key, (data, gaps, conf) in st.items():
        rkey = (key[2], key[3], key[0], key[1])
        if rkey not in st or not data or data[0] != 0x16: continue
        cdata = st[rkey][0]
        if not cdata or cdata[0] != 0x16 or cdata[5] != 1: continue  # wir wollen Server->Gast, Gegenrichtung beginnt mit ClientHello
        crandom = cdata[11:43]
        sh = data[5:]
        if sh[0] != 2: continue
        sess_len = sh[38]; suite = struct.unpack('>H', sh[39 + sess_len:41 + sess_len])[0]
        secrets = log.get(crandom)
        name = f"{key[0][3]}:{key[1]}->{key[2][3]}:{key[3]}"
        if not secrets or suite not in SUITES:
            print(name, f"len={len(data)} suite={suite:#x} keine Schluessel/nicht TLS1.3"); continue
        hs = keys(secrets['SERVER_HANDSHAKE_TRAFFIC_SECRET'], suite)
        app = keys(secrets['SERVER_TRAFFIC_SECRET_0'], suite)
        phase, seq, n, bad = hs, 0, 0, None
        for hdr, off, body in records(data):
            if hdr is None: print(name, f"Rest {len(body)} B unvollstaendig bei {off}"); break
            if hdr[0] != 23: continue
            for attempt in range(2):
                aead, iv = phase
                nonce = bytes(a ^ b for a, b in zip(iv, seq.to_bytes(12, 'big')))
                try:
                    aead.decrypt(nonce, body, hdr); seq += 1; n += 1; break
                except Exception:
                    if phase is hs: phase, seq = app, 0; continue
                    bad = (n, off, len(body)); break
            if bad: break
        print(name, f"suite={suite:#x} bytes={len(data)} luecken={gaps} widersprueche={conf} records_ok={n}",
              f"ERSTER FEHLER record={bad[0]} offset={bad[1]} len={bad[2]}" if bad else "alle Tags gueltig")

if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
