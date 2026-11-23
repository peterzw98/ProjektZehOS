"""Prueft den Abbruch von aussen (Roadmap 12.4-I2-A) mit der echten C-Bruecke und wasm3 (benoetigt gcc).

Die Pruefpunkte liegen vor und nach jedem Hostaufruf und bei jedem Wasm-Aufruf (m3_Yield); ein gesetzter Merker beendet
den Lauf mit Status 9 (ZEHOS_WASM_ABORTED). Jeder Lauf laeuft in einem Hintergrund-Thread: Wirkt der Abbruch nicht,
scheitert der Test an der Frist, statt zu haengen.
"""
import concurrent.futures
import ctypes
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import time
import unittest

ROOT = Path(__file__).resolve().parents[2]
ABORTED = 9
TRAP = 5


def leb(value):
    result = bytearray()
    while True:
        byte = value & 127
        value >>= 7
        result.append(byte | (128 if value else 0))
        if not value:
            return bytes(result)


def section(kind, payload):
    return bytes([kind]) + leb(len(payload)) + payload


def string(value):
    value = value.encode()
    return leb(len(value)) + value


def vector(items):
    return leb(len(items)) + b"".join(items)


def body(code):
    code = b"\x00" + code + b"\x0b"
    return leb(len(code)) + code


def module(types, imports, functions, export):
    """types: Bytefolgen der Funktionstypen; imports: (Modul, Name, Typ); functions: (Typ, Code); export: Funktionsindex."""
    return (b"\x00asm\x01\x00\x00\x00"
            + section(1, vector([b"\x60" + t for t in types]))
            + (section(2, vector([string(m) + string(n) + b"\x00" + leb(t) for m, n, t in imports])) if imports else b"")
            + section(3, vector([leb(t) for t, _ in functions]))
            + section(7, vector([string("run") + b"\x00" + leb(export)]))
            + section(10, vector([body(code) for _, code in functions])))


I32_RESULT = b"\x00\x01\x7f"   # () -> i32
I32_PARAM = b"\x01\x7f\x00"    # (i32) -> ()
NOTHING = b"\x00\x00"          # () -> ()

# loop { call should_exit; drop; br 0 } i32.const 0: fragt je Durchlauf, verwirft die Antwort.
HOST_LOOP = module([I32_RESULT], [("zehos_v1", "should_exit", 0)],
                   [(0, b"\x03\x40\x10\x00\x1a\x0c\x00\x0b\x41\x00")], 1)
# loop { i32.const 10; call sleep_ms; br 0 } i32.const 0: "taube" App, fragt nie.
SLEEP_LOOP = module([I32_RESULT, I32_PARAM], [("zehos_v1", "sleep_ms", 1)],
                    [(0, b"\x03\x40\x41\x0a\x10\x00\x0c\x00\x0b\x41\x00")], 1)
# Funktion 0 ist leer; run ruft sie endlos: kein Import, nur der Pruefpunkt am Wasm-Aufruf.
CALL_LOOP = module([I32_RESULT, NOTHING], [], [(1, b""), (0, b"\x03\x40\x10\x00\x0c\x00\x0b\x41\x00")], 1)
SEVEN = module([I32_RESULT], [], [(0, b"\x41\x07")], 0)
# Speicherzugriff weit hinter der einen Seite: gewoehnlicher Trap, kein Abbruch.
OUT_OF_BOUNDS = (b"\x00asm\x01\x00\x00\x00" + section(1, vector([b"\x60" + I32_RESULT]))
                 + section(3, vector([b"\x00"])) + section(5, b"\x01\x00\x01")
                 + section(7, vector([string("run") + b"\x00\x00"]))
                 + section(10, vector([body(b"\x41\x80\x80\x80\x08\x28\x02\x00")])))


class NativeAbortTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        compiler = shutil.which("gcc")
        if compiler is None:
            raise RuntimeError("gcc fuer die native Abbruchpruefung erforderlich")
        work = ROOT / ".tools" / "wasm-abort-native"
        work.mkdir(parents=True, exist_ok=True)
        cls.temp = tempfile.TemporaryDirectory(dir=work, ignore_cleanup_errors=True)
        source = ROOT / "ProjektZeh/ThirdParty/wasm3/source"
        library = Path(cls.temp.name) / ("abort.dll" if os.name == "nt" else "abort.so")
        sources = [*(ROOT / "ProjektZeh/Native/Wasm/host" / name for name in ("zehos_host_core.c", "zehos_host_wasm3.c")),
                   ROOT / "scripts/tests/wasm-abort-host.c", *sorted(source.glob("*.c"))]
        objects = [Path(cls.temp.name) / f"{index}.o" for index in range(len(sources))]

        def compile_one(item):
            subprocess.run([compiler, "-c", "-fPIC", "-O2", "-I", str(source), str(item[0]), "-o", str(item[1])], check=True)

        with concurrent.futures.ThreadPoolExecutor(os.cpu_count() or 2) as pool:
            list(pool.map(compile_one, zip(sources, objects)))
        subprocess.run([compiler, "-shared", *map(str, objects), "-lm", "-o", str(library)], check=True)
        if os.name == "nt":
            cls.dll_directory = os.add_dll_directory(str(Path(compiler).parent))
        cls.lib = ctypes.CDLL(str(library))
        cls.lib.zehos_wasm_run.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_char_p,
            ctypes.c_uint64, ctypes.c_uint32, ctypes.c_uint64, ctypes.POINTER(ctypes.c_int32),
            ctypes.c_void_p, ctypes.c_uint32]
        cls.lib.zehos_host_set_abort.argtypes = [ctypes.c_int32]

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()
        if os.name == "nt":
            cls.dll_directory.close()

    def setUp(self):
        self.lib.abort_test_reset(0)
        self.lib.poll_test_reset(-1)

    def execute(self, data, timeout=5.0, abort_after=None):
        """Fuehrt run ohne Gasbudget aus; abort_after setzt den Merker aus einem zweiten Thread. Liefert Status, Wert,
        Meldung und Dauer; scheitert, wenn der Lauf die Frist ueberschreitet."""
        outcome = {}

        def worker():
            value = ctypes.c_int32()
            message = ctypes.create_string_buffer(512)
            started = time.monotonic()
            status = self.lib.zehos_wasm_run(data, len(data), b"run", 0, 65536, 65536 * 4,
                                             ctypes.byref(value), message, len(message))
            outcome["result"] = (status, value.value, message.value.decode(), time.monotonic() - started)

        # Daemon: Ein haengender Lauf darf das Testende nicht blockieren; der Fehlschlag steht dann schon fest.
        thread = threading.Thread(target=worker, daemon=True)
        thread.start()
        if abort_after is not None:
            time.sleep(abort_after)
            self.lib.zehos_host_set_abort(1)
        thread.join(timeout)
        self.assertFalse(thread.is_alive(), "Lauf endete nicht innerhalb der Frist")
        return outcome["result"]

    def test_abort_at_host_call(self):
        self.lib.abort_test_reset(3)
        status, _, message, _ = self.execute(HOST_LOOP)
        self.assertEqual(status, ABORTED, message)
        self.assertIn("aborted by host", message)
        self.assertEqual(self.lib.abort_test_calls(), 3)

    def test_abort_at_host_call_without_should_exit(self):
        status, _, message, _ = self.execute(SLEEP_LOOP, abort_after=0.1)
        self.assertEqual(status, ABORTED, message)
        self.assertGreater(self.lib.poll_test_sleeps(), 0)

    def test_abort_at_wasm_call(self):
        status, _, message, elapsed = self.execute(CALL_LOOP, abort_after=0.1)
        self.assertEqual(status, ABORTED, message)
        self.assertLess(elapsed, 5.0)

    def test_flag_cleared_at_start(self):
        self.lib.zehos_host_set_abort(1)
        status, value, message, _ = self.execute(SEVEN)
        self.assertEqual(status, 0, message)
        self.assertEqual(value, 7)

    def test_flag_left_from_aborted_run_does_not_hit_next(self):
        self.lib.abort_test_reset(1)
        self.assertEqual(self.execute(HOST_LOOP)[0], ABORTED)
        # Der Merker steht nach dem Abbruch noch; der naechste Lauf loescht ihn selbst.
        self.lib.abort_test_reset(0)
        status, value, message, _ = self.execute(SEVEN)
        self.assertEqual((status, value), (0, 7), message)

    def test_ordinary_trap_stays_trap(self):
        status, _, message, _ = self.execute(OUT_OF_BOUNDS)
        self.assertEqual(status, TRAP, message)
        self.assertNotIn("aborted", message)


if __name__ == "__main__":
    unittest.main()
