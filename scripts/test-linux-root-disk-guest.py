"""Ladybird V28: ein Programm aus einer Prozesswurzel auf der Datenplatte im Desktop-Terminal starten (KVM, Testbau -p:ZehOSLinuxRun=true).

Nutzung: scripts/test-linux-root-disk-guest.py <iso> <platte.qcow2> <arbeitsordner> --dest lb --run "bin/prog arg..."
             [--expect TEXT ...] [--status "exit 0"] [--timeout 300] [--smp 1] [--memory 2G]
Die Platte kommt von scripts/linux-root-disk.py (im Gast /data/<dest>). Das Skript meldet sich an, uebernimmt /data/<dest>,
dessen bin/ und das Programm (security adopt, wie test-linux-js.py), wechselt im Terminal nach /data/<dest> und startet --run.
Prueft: LINUXRUN BEGIN, das Ende mit --status und jeden --expect-Text in der Ausgabe. Die ganze Ausgabe steht danach in
<arbeitsordner>/program-out.txt, die seriellen Zeilen in serial.log. Die Platte wird als Kopie verwendet; das Original bleibt.
"""
import argparse
import importlib.util
import os
from pathlib import Path
import re
import shutil
import sys
import time

R = Path(__file__).resolve().parent.parent
ANSI = re.compile(r"\x1b\[[0-9;?]*[A-Za-z]")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("iso", type=Path)
    parser.add_argument("disk", type=Path)
    parser.add_argument("work", type=Path)
    parser.add_argument("--dest", default="lb")
    parser.add_argument("--run", required=True)
    parser.add_argument("--expect", action="append", default=[])
    parser.add_argument("--status", default="exit 0")
    parser.add_argument("--timeout", type=int, default=300)
    parser.add_argument("--smp", default="1")
    parser.add_argument("--memory")
    args = parser.parse_args()
    spec = importlib.util.spec_from_file_location("t", R / "scripts/test-wasm-doom-desktop.py")
    t = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(t)
    args.work.mkdir()
    os.environ["ZEHOS_QEMU_SMP"] = args.smp
    disk = args.work / "disk.qcow2"
    shutil.copyfile(args.disk, disk)
    program = args.run.split()[0]
    port = t.free_port()
    process = t.start_qemu(args.iso, disk, args.work, port, "kvm", None, args.memory)
    serial = args.work / "serial.log"
    results = []

    def check(name, condition):
        results.append(condition)
        print(("PASS " if condition else "FAIL ") + name, flush=True)

    def serial_text(mark):
        return serial.read_text(encoding="utf-8", errors="replace")[mark:]

    def wait_serial(pattern, timeout, mark):
        end = time.monotonic() + timeout
        while time.monotonic() < end:
            if found := re.search(pattern, serial_text(mark)):
                return found
            time.sleep(0.5)
        return None

    def output(mark):
        data = b""
        for line in serial_text(mark).splitlines():
            if "LINUXRUN OUT " in line:
                try:
                    data += bytes.fromhex(line.split("LINUXRUN OUT ", 1)[1].split()[0])
                except ValueError:
                    pass
        return ANSI.sub("", data.decode("utf-8", errors="replace"))

    try:
        guest = t.Guest(args.work, port, "http://127.0.0.1:1", time.monotonic())
        guest.wait("Desktop ist bereit", 240)
        guest.connect()
        time.sleep(1)
        guest.login(first=True)
        root = f"/data/{args.dest}"
        guest.shell(f"security adopt {root} tester", f"security adopt {root}/bin tester",
                    f"security adopt {root}/{program} tester")
        time.sleep(1)
        guest.desktop()
        guest.launch("Terminal")
        time.sleep(3)
        guest.qmp.line(f"cd {root}")
        time.sleep(1)
        mark = guest.mark()
        guest.qmp.line(f"run {args.run}")
        check("start", wait_serial(r"LINUXRUN BEGIN", 60, mark) is not None)
        end = wait_serial(r"LINUXRUN END ([^\r\n]*)", args.timeout, mark)
        status = end.group(1).strip() if end else "kein Ende"
        print(f"ENDE {status}", flush=True)
        check(f"status {args.status}", status.startswith(args.status))
        text = output(mark)
        (args.work / "program-out.txt").write_text(text, encoding="utf-8")
        for expected in args.expect:
            check(f"ausgabe {expected[:60]}", expected in text)
        guest.shot("ende")
        try:
            guest.qmp.line("shutdown")
        except Exception:
            pass
        process.wait(timeout=60)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
    print(f"ERGEBNIS {sum(results)}/{len(results)}")
    return 0 if results and all(results) else 1


if __name__ == "__main__":
    sys.exit(main())
