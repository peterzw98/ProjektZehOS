#!/usr/bin/env python3
"""Prueft Cosmos-Unlink ueber ext2-Blockgrenzen auf frischen Images mit e2fsck und debugfs."""
import argparse
import struct
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PROJECT = ROOT / "experiments/StorageProbe/StorageProbe.csproj"
DLL = ROOT / "experiments/StorageProbe/bin/Release/net10.0/StorageProbe.dll"
BLOCK = 4096


def records(image):
    result = subprocess.run(["debugfs", "-R", "blocks /span", str(image)], capture_output=True, text=True, check=True)
    blocks = [int(x) for x in result.stdout.split()]
    if len(blocks) < 2:
        raise AssertionError("Testverzeichnis belegt weniger als zwei Bloecke")
    data = image.read_bytes()
    result = []
    for index, block in enumerate(blocks):
        position = 0
        while position < BLOCK:
            offset = block * BLOCK + position
            inode, length, name_len = struct.unpack_from("<IHB", data, offset)
            if length < 8 or length % 4 or position + length > BLOCK or name_len > length - 8:
                raise ValueError(f"Block {index}, Offset {position}: ungueltiges rec_len={length}")
            name = data[offset + 8:offset + 8 + name_len].decode("utf-8")
            result.append((index, position, inode, length, name))
            position += length
    return result


def fsck(image):
    result = subprocess.run(["e2fsck", "-fn", str(image)], capture_output=True, text=True)
    return result.returncode, result.stdout + result.stderr


def run_case(work, target, boundary):
    path = work / target
    subprocess.run(["dotnet", str(DLL), "boundary-probe", str(path), target], check=True)
    before, after = path / "before.img", path / "after.img"
    old = records(before)
    entry = next(x for x in old if x[4] == target)
    if (entry[0] > 0 and entry[1] == 0) != boundary:
        raise AssertionError(f"{target}: unerwartete Position {entry}")
    code, report = fsck(before)
    if code != 0:
        raise AssertionError(f"Ausgangsabbild fehlerhaft: {code}\n{report}")
    code, report = fsck(after)
    try:
        new = records(after)
        corruption = None
    except ValueError as exc:
        new, corruption = None, str(exc)
    print(f"{target}: Start={entry[0]}:{entry[1]}, e2fsck vorher=0 nachher={code}, Struktur={corruption or 'gueltig'}")
    print(report)
    if corruption or new is None:
        return False
    expected = {x[4]: x[2] for x in old if x[2] and x[4] != target}
    actual = {x[4]: x[2] for x in new if x[2]}
    if actual != expected:
        print("Eintragsnamen oder Inodes stimmen nach Unlink nicht ueberein")
        return False
    # Auch der unabhaengige Inode-dtime-Befund bleibt sichtbar und gilt als Fehler.
    return code == 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("work", type=Path, help="neues Verzeichnis fuer isolierte Rohimages")
    args = parser.parse_args()
    args.work.mkdir(parents=True, exist_ok=False)
    subprocess.run(["dotnet", "build", str(PROJECT), "-c", "Release", "--no-restore"], check=True)
    control = run_case(args.work, "n0255", False)
    boundary = run_case(args.work, "n0254", True)
    print(f"Kontrollfall={'PASS' if control else 'FAIL'}, Blockgrenze={'PASS' if boundary else 'FAIL'}")
    return 0 if control and boundary else 1


if __name__ == "__main__":
    raise SystemExit(main())
