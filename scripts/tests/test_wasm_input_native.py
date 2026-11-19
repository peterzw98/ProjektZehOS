"""Prueft poll_event mit der echten C-Bruecke und wasm3 (benoetigt gcc)."""
import concurrent.futures
import ctypes
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


def leb(value, signed=False):
    result = bytearray()
    while True:
        byte = value & 127
        value >>= 7
        done = (value == 0 and (not signed or byte < 64)) or (signed and value == -1 and byte >= 64)
        result.append(byte if done else byte | 128)
        if done:
            return bytes(result)


def section(kind, payload):
    return bytes([kind]) + leb(len(payload)) + payload


def string(value):
    value = value.encode()
    return leb(len(value)) + value


def module(pointer, read_frame=False):
    # Eine Speicherseite; run ruft den Import auf und liefert Status oder positionsabhaengigen Pruefwert der vier Felder.
    signed_pointer = pointer if pointer < 0x80000000 else pointer - 0x100000000
    address = b"\x41" + leb(signed_pointer, True)
    code = b"\x00" + address + b"\x10\x00"
    if read_frame:
        code += b"\x1a"
        for offset in (0, 4, 8, 12):
            if offset:
                code += b"\x41\x1f\x6c"
            code += address + b"\x28\x00" + leb(offset)
            if offset:
                code += b"\x6a"
    code += b"\x0b"
    return (b"\x00asm\x01\x00\x00\x00"
            + section(1, b"\x02\x60\x01\x7f\x01\x7f\x60\x00\x01\x7f")
            + section(2, b"\x01" + string("zehos_v1") + string("poll_event") + b"\x00\x00")
            + section(3, b"\x01\x01") + section(5, b"\x01\x00\x01")
            + section(7, b"\x01" + string("run") + b"\x00\x01")
            + section(10, b"\x01" + leb(len(code)) + code))


def path_module(path, output=65472, read_stat=False, name="path_filestat_get", dir_fd=3, flags=0,
                path_pointer=128, buffer=256, buffer_length=32, used=300):
    def const(value):
        return b"\x41" + leb(value if value < 0x80000000 else value - 0x100000000, True)
    args = (dir_fd, flags, path_pointer, len(path), output) if name == "path_filestat_get" else (
        dir_fd, path_pointer, len(path), buffer, buffer_length, used)
    code = b"\x00" + b"".join(map(const, args)) + b"\x10\x00"
    if read_stat:
        # Dateityp (Byte 16) und Groesse (u64 ab Byte 32) getrennt gewichten.
        code += b"\x1a" + const(output + 16) + b"\x2d\x00\x00\x41\x10\x6c"
        code += const(output + 32) + b"\x28\x00\x00\x6a"
    code += b"\x0b"
    return (b"\x00asm\x01\x00\x00\x00"
            + section(1, b"\x02\x60" + leb(len(args)) + b"\x7f" * len(args) + b"\x01\x7f\x60\x00\x01\x7f")
            + section(2, b"\x01" + string("wasi_snapshot_preview1") + string(name) + b"\x00\x00")
            + section(3, b"\x01\x01") + section(5, b"\x01\x00\x01")
            + section(7, b"\x01" + string("run") + b"\x00\x01")
            + section(10, b"\x01" + leb(len(code)) + code)
            + section(11, b"\x01\x00" + const(128) + b"\x0b" + leb(len(path)) + path))


def random_module(pointer, length, read_first=False):
    # run ruft random_get auf und liefert dessen Status oder das erste Byte des Puffers.
    def const(value):
        return b"\x41" + leb(value if value < 0x80000000 else value - 0x100000000, True)
    code = b"\x00" + const(pointer) + const(length) + b"\x10\x00"
    if read_first:
        code += b"\x1a" + const(pointer) + b"\x2d\x00\x00"
    code += b"\x0b"
    return (b"\x00asm\x01\x00\x00\x00"
            + section(1, b"\x02\x60\x02\x7f\x7f\x01\x7f\x60\x00\x01\x7f")
            + section(2, b"\x01" + string("wasi_snapshot_preview1") + string("random_get") + b"\x00\x00")
            + section(3, b"\x01\x01") + section(5, b"\x01\x00\x01")
            + section(7, b"\x01" + string("run") + b"\x00\x01")
            + section(10, b"\x01" + leb(len(code)) + code))


def net_module(name, args):
    def const(value):
        return b"\x41" + leb(value if value < 0x80000000 else value - 0x100000000, True)
    code = b"\x00" + b"".join(map(const, args)) + b"\x10\x00\x0b"
    return (b"\x00asm\x01\x00\x00\x00"
            + section(1, b"\x02\x60" + leb(len(args)) + b"\x7f" * len(args) + b"\x01\x7f\x60\x00\x01\x7f")
            + section(2, b"\x01" + string("zehos_v1") + string(name) + b"\x00\x00")
            + section(3, b"\x01\x01") + section(5, b"\x01\x00\x01")
            + section(7, b"\x01" + string("run") + b"\x00\x01")
            + section(10, b"\x01" + leb(len(code)) + code))


def open_module(after):
    # run oeffnet asset.txt ueber path_open und fuehrt bei Erfolg after aus; scheitert das Oeffnen, liefert es 99.
    code = (b"\x00\x41\x03\x41\x00\x41\x80\x01\x41\x09\x41\x00\x42\x00\x42\x00\x41\x00\x41\xc0\x00"
            + b"\x10\x00\x45\x04\x40" + after + b"\x0b\x41\xe3\x00\x0b")
    return (b"\x00asm\x01\x00\x00\x00"
            + section(1, b"\x03\x60\x09" + b"\x7f" * 5 + b"\x7e\x7e\x7f\x7f\x01\x7f"
                      + b"\x60\x01\x7f\x00\x60\x00\x01\x7f")
            + section(2, b"\x02" + string("wasi_snapshot_preview1") + string("path_open") + b"\x00\x00"
                      + string("wasi_snapshot_preview1") + string("proc_exit") + b"\x00\x01")
            + section(3, b"\x01\x02") + section(5, b"\x01\x00\x01")
            + section(7, b"\x01" + string("run") + b"\x00\x02")
            + section(10, b"\x01" + leb(len(code)) + code)
            + section(11, b"\x01\x00\x41\x80\x01\x0b" + leb(9) + b"asset.txt"))


def wasi_module(name, params, args, data=b"", read_back=None):
    # run ruft einen WASI-Import mit festen Argumenten auf (i = i32, I = i64); read_back liest danach ein u32 aus dem Speicher.
    def const(kind, value):
        if kind == "I":
            return b"\x42" + leb(value, True)
        return b"\x41" + leb(value if value < 0x80000000 else value - 0x100000000, True)
    code = b"\x00" + b"".join(const(kind, value) for kind, value in zip(params, args)) + b"\x10\x00"
    if read_back is not None:
        code += b"\x1a" + const("i", read_back) + b"\x28\x02\x00"
    code += b"\x0b"
    types = bytes(0x7e if kind == "I" else 0x7f for kind in params)
    return (b"\x00asm\x01\x00\x00\x00"
            + section(1, b"\x02\x60" + leb(len(params)) + types + b"\x01\x7f\x60\x00\x01\x7f")
            + section(2, b"\x01" + string("wasi_snapshot_preview1") + string(name) + b"\x00\x00")
            + section(3, b"\x01\x01") + section(5, b"\x01\x00\x01")
            + section(7, b"\x01" + string("run") + b"\x00\x01")
            + section(10, b"\x01" + leb(len(code)) + code)
            + (section(11, b"\x01\x00\x41\x80\x01\x0b" + leb(len(data)) + data) if data else b""))


def seq_module(calls, data=b"", read_back=None):
    # run ruft nacheinander WASI-Imports (name, Parametertypen, Argumente) auf und liefert das Ergebnis des letzten
    # (oder das u32 an read_back danach). Daten liegen ab Adresse 128.
    def const(kind, value):
        if kind == "I":
            return b"\x42" + leb(value, True)
        return b"\x41" + leb(value if value < 0x80000000 else value - 0x100000000, True)
    names = []
    for name, params, _ in calls:
        if name not in [item[0] for item in names]:
            names.append((name, params))
    code = b"\x00"
    for index, (name, params, args) in enumerate(calls):
        code += b"".join(const(kind, value) for kind, value in zip(params, args))
        code += b"\x10" + leb([item[0] for item in names].index(name))
        if index < len(calls) - 1 or read_back is not None:
            code += b"\x1a"
    if read_back is not None:
        code += const("i", read_back) + b"\x28\x02\x00"
    code += b"\x0b"
    types = b""
    for name, params in names:
        types += b"\x60" + leb(len(params)) + bytes(0x7e if kind == "I" else 0x7f for kind in params) + b"\x01\x7f"
    types += b"\x60\x00\x01\x7f"
    imports = b"".join(string("wasi_snapshot_preview1") + string(name) + b"\x00" + leb(index)
                       for index, (name, _) in enumerate(names))
    return (b"\x00asm\x01\x00\x00\x00"
            + section(1, leb(len(names) + 1) + types)
            + section(2, leb(len(names)) + imports)
            + section(3, b"\x01" + leb(len(names))) + section(5, b"\x01\x00\x01")
            + section(7, b"\x01" + string("run") + b"\x00" + leb(len(names)))
            + section(10, b"\x01" + leb(len(code)) + code)
            + (section(11, b"\x01\x00\x41\x80\x01\x0b" + leb(len(data)) + data) if data else b""))


def poll_module(subs, count=None, open_first=False, in_pointer=128, out_pointer=4096, nevents=8192):
    # run ruft poll_oneoff auf (optional nach path_open von asset.txt -> fd 5) und liefert
    # errno | nevents << 8 | Fehler des ersten Ereignisses << 16 | (nbytes & 0xff) << 24.
    def const(value):
        return b"\x41" + leb(value if value < 0x80000000 else value - 0x100000000, True)
    count = len(subs) // 48 if count is None else count
    code = b"\x01\x01\x7f"
    if open_first:
        code += b"\x41\x03\x41\x00\x41\x10\x41\x09\x41\x00\x42\x00\x42\x00\x41\x00\x41\xc0\x00\x10\x01\x1a"
    code += const(in_pointer) + const(out_pointer) + const(count) + const(nevents) + b"\x10\x00\x21\x00"
    code += b"\x20\x00" + const(nevents) + b"\x28\x02\x00" + const(256) + b"\x6c\x6a"
    code += const(out_pointer + 8) + b"\x2f\x01\x00" + const(65536) + b"\x6c\x6a"
    code += const(out_pointer + 16) + b"\x2d\x00\x00" + const(1 << 24) + b"\x6c\x6a\x0b"
    run = 2 if open_first else 1
    imports = [("poll_oneoff", 0)] + ([("path_open", 2)] if open_first else [])
    return (b"\x00asm\x01\x00\x00\x00"
            + section(1, b"\x03\x60\x04" + b"\x7f" * 4 + b"\x01\x7f\x60\x00\x01\x7f"
                      + b"\x60\x09" + b"\x7f" * 5 + b"\x7e\x7e\x7f\x7f\x01\x7f")
            + section(2, bytes([len(imports)]) + b"".join(
                string("wasi_snapshot_preview1") + string(name) + b"\x00" + bytes([kind]) for name, kind in imports))
            + section(3, b"\x01\x01") + section(5, b"\x01\x00\x01")
            + section(7, b"\x01" + string("run") + b"\x00" + bytes([run]))
            + section(10, b"\x01" + leb(len(code)) + code)
            + section(11, b"\x02\x00" + const(16) + b"\x0b" + leb(9) + b"asset.txt"
                      + b"\x00" + const(128) + b"\x0b" + leb(len(subs)) + subs))


def clock_sub(milliseconds, clock=1, absolute=False, userdata=9, base_ns=0):
    # Abo-Layout (48 Byte): userdata u64, tag u8 @8, Uhr u32 @16, timeout u64 @24, precision u64 @32, flags u16 @40.
    return struct.pack("<QB7xIxxxxQQHxxxxxx", userdata, 0, clock, base_ns + milliseconds * 1000000, 0, 1 if absolute else 0)


def fd_sub(fd, tag, userdata=5):
    return struct.pack("<QB7xI", userdata, tag, fd).ljust(48, b"\0")


def decode_poll(value):
    value &= 0xffffffff
    return value & 255, (value >> 8) & 255, (value >> 16) & 255, value >> 24


class NativeInputTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        compiler = shutil.which("gcc")
        if compiler is None:
            raise RuntimeError("gcc fuer die native ABI-Pruefung erforderlich")
        work = ROOT / ".tools" / "wasm-input-native"
        work.mkdir(parents=True, exist_ok=True)
        cls.temp = tempfile.TemporaryDirectory(dir=work, ignore_cleanup_errors=True)
        source = ROOT / "ProjektZeh/ThirdParty/wasm3/source"
        library = Path(cls.temp.name) / ("input.dll" if os.name == "nt" else "input.so")
        # Die C-Dateien einzeln und parallel uebersetzen (ein gcc-Aufruf braucht sequenziell ~20 s), dann linken.
        sources = [*(ROOT / "ProjektZeh/Native/Wasm/host" / name for name in ("zehos_host_core.c", "zehos_host_wasm3.c")),
                   ROOT / "scripts/tests/wasm-input-host.c", *sorted(source.glob("*.c"))]
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

    @classmethod
    def tearDownClass(cls):
        # Windows haelt geladene DLLs bis Prozessende offen; Reste bleiben im ignorierten Testordner.
        cls.temp.cleanup()
        if os.name == "nt":
            cls.dll_directory.close()

    def run_guest(self, pointer, available=1, read_frame=False):
        self.lib.input_test_reset(available)
        return self.execute(module(pointer, read_frame))

    def execute(self, data):
        value = ctypes.c_int32()
        message = ctypes.create_string_buffer(512)
        status = self.lib.zehos_wasm_run(data, len(data), b"run", 100000, 65536, 65536,
                                         ctypes.byref(value), message, len(message))
        return status, value.value, message.value.decode()

    def test_network_checks_guest_ranges_before_host_call(self):
        cases = (
            ("net_open", (65535, 2, 80, 0, 128)),
            ("net_open", (0, 4, 80, 0, 65533)),
            ("net_send", (1, 65535, 2, 128)),
            ("net_send", (1, 0, 4, 0xfffffffe)),
            ("net_recv", (1, 0xffffffff, 2, 128)),
            ("net_recv", (1, 0, 4, 65533)),
        )
        for name, args in cases:
            with self.subTest(name=name, args=args):
                self.lib.net_test_reset()
                status, _, message = self.execute(net_module(name, args))
                self.assertEqual(status, 5, message)
                self.assertIn("out of bounds", message.lower())
                self.assertEqual(self.lib.net_test_calls(), 0)

    def test_network_unaligned_valid_buffers_reach_host(self):
        for name, args, expected in (("net_open", (1, 4, 80, 0, 7), 2),
                                     ("net_send", (1, 1, 4, 7), 8),
                                     ("net_recv", (1, 1, 4, 7), 8)):
            with self.subTest(name=name):
                self.lib.net_test_reset()
                status, value, message = self.execute(net_module(name, args))
                self.assertEqual(status, 0, message)
                self.assertEqual(value, expected)
                self.assertEqual(self.lib.net_test_calls(), 1)

    def test_valid_unaligned_and_last_sixteen_bytes_copy_all_fields(self):
        for pointer in (1, 65520):
            with self.subTest(pointer=pointer):
                status, value, message = self.run_guest(pointer, read_frame=True)
                self.assertEqual(status, 0, message)
                self.assertEqual(value, 151340)
                self.assertEqual(self.lib.input_test_dequeues(), 1)

    def test_invalid_pointer_traps_before_calling_host(self):
        for pointer in (65521, 0xfffffff8):
            with self.subTest(pointer=pointer):
                status, _, message = self.run_guest(pointer)
                self.assertEqual(status, 5, message)
                self.assertIn("out of bounds", message.lower())
                self.assertEqual(self.lib.input_test_calls(), 0)
                self.assertEqual(self.lib.input_test_dequeues(), 0)

    def test_empty_and_host_error_return_status_without_dequeue(self):
        for available in (0, -1):
            with self.subTest(available=available):
                status, value, message = self.run_guest(1, available)
                self.assertEqual(status, 0, message)
                self.assertEqual(value, available)
                self.assertEqual(self.lib.input_test_dequeues(), 0)
                status, value, message = self.run_guest(1, available, read_frame=True)
                self.assertEqual(status, 0, message)
                self.assertEqual(value, 0, "Ohne Ereignis muss Gastspeicher unveraendert bleiben")

    def test_path_stat_reads_file_directory_and_root_at_memory_end(self):
        for path, expected in ((b"asset.txt", 71), (b"res", 48), (b".", 48)):
            with self.subTest(path=path):
                status, value, message = self.execute(path_module(path, read_stat=True))
                self.assertEqual(status, 0, message)
                self.assertEqual(value, expected)

    def test_path_queries_preserve_error_cause_and_reject_escape(self):
        for name in ("path_filestat_get", "path_readlink"):
            for path, expected in ((b"missing", 44), (b"denied", 2), (b"io", 29),
                                   (b"../asset.txt", 2), (b"/asset.txt", 2), (b"res/../asset.txt", 2),
                                   (b"res\\asset.txt", 2), (b"x:asset", 2), (b"x\x00asset", 2)):
                with self.subTest(name=name, path=path):
                    self.lib.stat_test_reset()
                    status, value, message = self.execute(path_module(path, name=name))
                    self.assertEqual(status, 0, message)
                    self.assertEqual(value, expected)
                    if expected == 2 and path != b"denied":
                        self.assertEqual(self.lib.stat_test_calls(), 0)

    def test_path_queries_trap_before_host_when_guest_memory_is_invalid(self):
        for kwargs in ({"output":65473}, {"path_pointer":0xfffffff8},
                       {"name":"path_readlink", "buffer":65535},
                       {"name":"path_readlink", "used":65533}):
            with self.subTest(kwargs=kwargs):
                self.lib.stat_test_reset()
                status, _, message = self.execute(path_module(b"asset.txt", **kwargs))
                self.assertEqual(status, 5, message)
                self.assertIn("out of bounds", message.lower())
                self.assertEqual(self.lib.stat_test_calls(), 0)

    def test_readlink_returns_einval_for_existing_nonlink(self):
        status, value, message = self.execute(path_module(b"asset.txt", name="path_readlink"))
        self.assertEqual(status, 0, message)
        self.assertEqual(value, 28)

    def test_path_stat_rejects_other_directory_handles_and_unknown_flags(self):
        # fd 4 ist nur mit Speicherfreigabe /save (Etappe 13.4); ohne sie bleibt er ungueltig.
        self.lib.save_test_reset(0)
        for kwargs, expected in (({"dir_fd":4},8), ({"dir_fd":5},8), ({"flags":2},28)):
            self.lib.stat_test_reset()
            status, value, message = self.execute(path_module(b"asset.txt", **kwargs))
            self.assertEqual(status, 0, message)
            self.assertEqual(value, expected)
            self.assertEqual(self.lib.stat_test_calls(), 0)


    def test_random_get_fills_buffer_on_success(self):
        self.lib.random_test_reset(0)
        status, value, message = self.execute(random_module(65520, 16))
        self.assertEqual(status, 0, message)
        self.assertEqual(value, 0)
        status, value, message = self.execute(random_module(65520, 16, read_first=True))
        self.assertEqual(status, 0, message)
        self.assertEqual(value, 0xa5)

    def test_random_get_reports_host_error_instead_of_success(self):
        # Ohne Hardwarezufall darf der Gast keinen Erfolg sehen (#61).
        self.lib.random_test_reset(29)
        status, value, message = self.execute(random_module(1, 16))
        self.assertEqual(status, 0, message)
        self.assertEqual(value, 29)
        self.assertEqual(self.lib.random_test_calls(), 1)

    def test_random_get_accepts_empty_buffer_at_memory_end(self):
        self.lib.random_test_reset(0)
        status, value, message = self.execute(random_module(65536, 0))
        self.assertEqual(status, 0, message)
        self.assertEqual(value, 0)

    def test_random_get_traps_before_host_when_buffer_is_invalid(self):
        self.lib.random_test_reset(0)
        status, _, message = self.execute(random_module(65530, 16))
        self.assertEqual(status, 5, message)
        self.assertIn("out of bounds", message.lower())
        self.assertEqual(self.lib.random_test_calls(), 0)

    def test_open_files_are_closed_on_every_exit_path(self):
        # Trap, proc_exit, regulaeres Ende und erschoepftes Gasbudget schliessen die offene Datei in der C-Schicht.
        for name, after, expected in (("trap", b"\x00", (5, 0)), ("proc_exit", b"\x41\x07\x10\x01", (0, 7)),
                                      ("return", b"\x41\x00\x0f", (0, 0)), ("gas", b"\x03\x40\x0c\x00\x0b", (6, 0))):
            with self.subTest(name=name):
                self.lib.file_test_reset()
                status, value, message = self.execute(open_module(after))
                self.assertEqual((status, value), expected, message)
                self.assertEqual(self.lib.file_test_closes(), 1)
                self.assertEqual(self.lib.file_test_open(), 0)

    # Etappe 13.4: App-Speicher /save (fd 4)
    def save_call(self, name, params, args, data=b"", read_back=None):
        status, value, message = self.execute(wasi_module(name, params, args, data, read_back))
        self.assertEqual(status, 0, message)
        return value

    def test_save_is_absent_without_capability(self):
        self.lib.save_test_reset(0)
        self.assertEqual(self.save_call("fd_prestat_get", "ii", (4, 512)), 8)
        self.assertEqual(self.save_call("path_create_directory", "iii", (4, 128, 4), b"maps"), 8)
        self.assertEqual(self.save_call("path_open", "iiiiiIIii", (4, 0, 128, 4, 1, 64, 0, 0, 512), b"maps"), 8)
        self.assertEqual(self.lib.save_test_calls(), 0)

    def test_save_prestat_names_save(self):
        self.lib.save_test_reset(1)
        self.assertEqual(self.save_call("fd_prestat_get", "ii", (4, 512), read_back=516), 5)
        self.assertEqual(self.save_call("fd_prestat_dir_name", "iii", (4, 512, 5), read_back=512),
                         int.from_bytes(b"/sav", "little"))
        self.assertEqual(self.save_call("fd_prestat_dir_name", "iii", (4, 512, 4)), 28, "Puffer zu klein")

    def test_package_stays_read_only_and_links_are_refused(self):
        self.lib.save_test_reset(1)
        for name, params, args in (("path_create_directory", "iii", (3, 128, 4)), ("path_unlink_file", "iii", (3, 128, 4)),
                                   ("path_remove_directory", "iii", (3, 128, 4)), ("path_rename", "iiiiii", (3, 128, 4, 4, 128, 4)),
                                   ("path_rename", "iiiiii", (4, 128, 4, 3, 128, 4))):
            with self.subTest(name=name, args=args):
                self.assertEqual(self.save_call(name, params, args, b"maps"), 76)
        self.assertEqual(self.save_call("path_symlink", "iiiii", (128, 4, 4, 128, 4), b"maps"), 76)
        self.assertEqual(self.save_call("path_link", "iiiiiii", (4, 0, 128, 4, 4, 128, 4), b"maps"), 76)
        self.assertEqual(self.save_call("fd_write", "iiii", (3, 512, 0, 520)), 8, "Paket-Preopen ist kein Schreibziel")
        self.assertEqual(self.lib.save_test_calls(), 0)

    def test_save_paths_are_checked_before_the_host(self):
        self.lib.save_test_reset(1)
        for path in (b"../x", b"/abs", b"a/../b", b"a\\b", b"a:b"):
            with self.subTest(path=path):
                self.assertEqual(self.save_call("path_create_directory", "iii", (4, 128, len(path)), path), 2)
                self.assertEqual(self.save_call("path_rename", "iiiiii", (4, 128, len(path), 4, 128, 1), path), 2)
        self.assertEqual(self.lib.save_test_calls(), 0)
        status, _, message = self.execute(wasi_module("path_create_directory", "iii", (4, 0xfffffff8, 16)))
        self.assertEqual(status, 5, message)
        self.assertEqual(self.lib.save_test_calls(), 0, "Speichertrap vor dem Hostaufruf")
        self.assertEqual(self.save_call("path_create_directory", "iii", (4, 128, 4), b"maps"), 0)
        self.assertEqual(self.lib.save_test_calls(), 1)

    def test_save_open_passes_flags_and_returns_a_file_descriptor(self):
        self.lib.save_test_reset(1)
        # O_CREAT|O_TRUNC mit fd_write-Recht -> SAVE_CREATE|SAVE_TRUNCATE|SAVE_WRITE = 25; erster freier fd ist 5.
        self.assertEqual(self.save_call("path_open", "iiiiiIIii", (4, 0, 128, 4, 9, 64, 0, 0, 512), b"w.cw", read_back=512), 5)
        self.assertEqual(self.lib.save_test_flags(), 25)
        self.assertEqual(self.save_call("path_open", "iiiiiIIii", (4, 0, 128, 4, 0x10, 64, 0, 0, 512), b"w.cw"), 28, "unbekanntes oflag")

    # Etappe 12.8 (P4-C): poll_oneoff
    def poll(self, subs, exit_after=-1, **kwargs):
        self.lib.poll_test_reset(exit_after)
        self.lib.file_test_reset()
        status, value, message = self.execute(poll_module(subs, **kwargs))
        self.assertEqual(status, 0, message)
        return decode_poll(value)

    def test_poll_clock_relative_sleeps_and_reports_event(self):
        errno, events, error, _ = self.poll(clock_sub(25))
        self.assertEqual((errno, events, error), (0, 1, 0))
        self.assertEqual(self.lib.poll_test_sleep_total_ms(), 25)
        self.assertEqual(self.lib.poll_test_sleeps(), 3)
        self.assertLessEqual(self.lib.poll_test_sleep_max_ms(), 10)

    def test_poll_clock_abstime(self):
        now = 1000000000000
        errno, events, _, _ = self.poll(clock_sub(15, absolute=True, base_ns=now))
        self.assertEqual((errno, events), (0, 1))
        self.assertEqual(self.lib.poll_test_sleep_total_ms(), 15)
        # Ein bereits verstrichener Zeitpunkt wartet nicht.
        errno, events, _, _ = self.poll(clock_sub(0, absolute=True, base_ns=now - 1))
        self.assertEqual((errno, events), (0, 1))
        self.assertEqual(self.lib.poll_test_sleeps(), 0)

    def test_poll_fd_ready_does_not_sleep(self):
        errno, events, error, nbytes = self.poll(fd_sub(5, 1) + clock_sub(10000, userdata=7), open_first=True)
        self.assertEqual((errno, events, error, nbytes), (0, 1, 0, 7))
        self.assertEqual(self.lib.poll_test_sleeps(), 0)
        for fd, tag in ((1, 2), (2, 2)):
            errno, events, error, nbytes = self.poll(fd_sub(fd, tag))
            self.assertEqual((errno, events, error, nbytes), (0, 1, 0, 0))
        self.assertEqual(self.lib.poll_test_sleeps(), 0)

    def test_poll_unknown_fd_reports_ebadf_event(self):
        for fd in (0, 3, 77, 0xffffffff):
            with self.subTest(fd=fd):
                errno, events, error, _ = self.poll(fd_sub(fd, 1))
                self.assertEqual((errno, events, error), (0, 1, 8))
                self.assertEqual(self.lib.poll_test_sleeps(), 0)

    def test_poll_invalid_clock_reports_einval_event(self):
        errno, events, error, _ = self.poll(clock_sub(10, clock=9))
        self.assertEqual((errno, events, error), (0, 1, 28))
        self.assertEqual(self.lib.poll_test_sleeps(), 0)

    def test_poll_zero_subscriptions_is_einval(self):
        self.assertEqual(self.poll(clock_sub(1), count=0)[0], 28)
        self.assertEqual(self.lib.poll_test_sleeps(), 0)

    def test_poll_65_subscriptions_is_einval(self):
        self.assertEqual(self.poll(fd_sub(1, 2) * 65)[0], 28)
        self.assertEqual(self.poll(fd_sub(1, 2) * 64)[:2], (0, 64))

    def test_poll_should_exit_returns_within_10ms(self):
        errno, events, _, _ = self.poll(clock_sub(3600000), exit_after=1)
        self.assertEqual((errno, events), (0, 0))
        self.assertEqual(self.lib.poll_test_sleeps(), 1)
        self.assertLessEqual(self.lib.poll_test_sleep_max_ms(), 10)

    def test_poll_long_sleep_is_not_capped(self):
        errno, events, _, _ = self.poll(clock_sub(5000))
        self.assertEqual((errno, events), (0, 1))
        self.assertEqual(self.lib.poll_test_sleep_total_ms(), 5000)

    def test_poll_traps_before_host_when_guest_memory_is_invalid(self):
        for kwargs in ({"in_pointer": 65500}, {"out_pointer": 65530}, {"nevents": 65534}):
            with self.subTest(kwargs=kwargs):
                self.lib.poll_test_reset(-1)
                status, _, message = self.execute(poll_module(clock_sub(50), **kwargs))
                self.assertEqual(status, 5, message)
                self.assertIn("out of bounds", message.lower())
                self.assertEqual(self.lib.poll_test_sleeps(), 0)

    # Etappe 12.8 (P4-B): Paketordner im Host-Kern
    OPEN = "iiiiiIIii"

    def package(self, calls, data=b"", read_back=None):
        self.lib.pkg_test_reset()
        self.lib.file_test_reset()
        self.lib.save_test_reset(1)
        status, value, message = self.execute(seq_module(calls, data, read_back))
        self.assertEqual(status, 0, message)
        return value

    def open_dir(self, dir_fd=3, path=b"sub", out=512):
        return ("path_open", self.OPEN, (dir_fd, 0, 128, len(path), 2, 0, 0, 0, out))

    def test_package_opendir_and_readdir_lists_files(self):
        value = self.package([self.open_dir(), ("fd_readdir", "iiiIi", (5, 1024, 64, 0, 600))], b"sub", read_back=600)
        self.assertEqual(value, 3)
        self.assertEqual(self.lib.pkg_test_last_readdir_handle(), 91)
        self.assertEqual(self.lib.pkg_test_last_dir(), -1, "fd 3 ist die Paketwurzel")

    def test_readdir_on_preopen_fd3(self):
        value = self.package([("fd_readdir", "iiiIi", (3, 1024, 64, 7, 600))], read_back=600)
        self.assertEqual(value, 3)
        self.assertEqual(self.lib.pkg_test_last_cookie(), 7)
        self.assertEqual((self.lib.pkg_test_opendirs(), self.lib.pkg_test_closedirs(), self.lib.pkg_test_open_dirs()), (1, 1, 0))

    def test_readdir_on_save_fd4(self):
        self.package([("fd_readdir", "iiiIi", (4, 1024, 64, 0, 600))], read_back=600)
        self.assertEqual(self.lib.save_test_calls(), 3, "open, readdir, close im App-Speicher")
        self.assertEqual(self.lib.save_test_flags(), 2)
        self.assertEqual(self.lib.pkg_test_opendirs(), 0)
        self.lib.save_test_reset(0)
        status, value, _ = self.execute(seq_module([("fd_readdir", "iiiIi", (4, 1024, 64, 0, 600))]))
        self.assertEqual((status, value), (0, 8), "ohne Freigabe ungueltig")

    def test_readdir_rights_have_bit_14_on_fd3_and_fd4(self):
        for fd in (3, 4):
            self.package([("fd_fdstat_get", "ii", (fd, 512))], read_back=520)
            status, value, _ = self.execute(seq_module([("fd_fdstat_get", "ii", (fd, 512))], read_back=520))
            self.assertEqual(status, 0)
            self.assertTrue(value & (1 << 14), fd)

    def test_package_opendir_with_creat_is_enotcapable(self):
        for flags in (3, 1, 6, 10):
            with self.subTest(flags=flags):
                value = self.package([("path_open", self.OPEN, (3, 0, 128, 3, flags, 0, 0, 0, 512))], b"sub")
                self.assertEqual(value, 76)
        value = self.package([("path_open", self.OPEN, (3, 0, 128, 3, 2, 64, 0, 0, 512))], b"sub")
        self.assertEqual(value, 76, "Schreibrecht")
        self.assertEqual(self.lib.pkg_test_opendirs(), 0)

    def test_open_relative_to_package_dir_fd(self):
        value = self.package([self.open_dir(),
                              ("path_open", self.OPEN, (5, 0, 140, 9, 0, 0, 0, 0, 520))], b"sub" + b"\0" * 9 + b"asset.txt", read_back=520)
        self.assertEqual(value, 6)
        self.assertEqual(self.lib.pkg_test_openat_dir(), 91)
        self.assertEqual(self.lib.file_test_closes(), 1, "Datei wird beim Laufende geschlossen")

    def test_stat_relative_to_package_dir_fd(self):
        value = self.package([self.open_dir(), ("path_filestat_get", "iiiii", (5, 0, 140, 9, 600))],
                             b"sub" + b"\0" * 9 + b"asset.txt", read_back=616)
        self.assertEqual(value & 0xff, 4)
        self.assertEqual(self.lib.pkg_test_last_dir(), 91)
        value = self.package([("path_filestat_get", "iiiii", (5, 0, 128, 3, 600))], b"sub")
        self.assertEqual(value, 8, "fd 5 ist nicht offen")

    def test_read_on_package_dir_is_eisdir(self):
        self.assertEqual(self.package([self.open_dir(), ("fd_read", "iiii", (5, 1024, 0, 600))], b"sub"), 31)
        self.assertEqual(self.package([self.open_dir(), ("fd_pread", "iiiIi", (5, 1024, 0, 0, 600))], b"sub"), 31)
        self.assertEqual(self.package([self.open_dir(), ("fd_seek", "iIii", (5, 0, 0, 600))], b"sub"), 31)

    def test_close_package_dir_calls_closedir(self):
        self.assertEqual(self.package([self.open_dir(), ("fd_close", "i", (5,))], b"sub"), 0)
        self.assertEqual((self.lib.pkg_test_closedirs(), self.lib.pkg_test_open_dirs()), (1, 0))
        self.assertEqual(self.lib.save_test_calls(), 0, "nie save_close")

    def test_open_package_dir_is_closed_on_every_exit_path(self):
        value = self.package([self.open_dir()], b"sub")
        self.assertEqual(value, 0)
        self.assertEqual((self.lib.pkg_test_closedirs(), self.lib.pkg_test_open_dirs()), (1, 0))

    def test_package_dir_fd_counts_against_limit(self):
        calls = [self.open_dir(out=512 + 4 * index) for index in range(16)] + [self.open_dir(out=700)]
        self.assertEqual(self.package(calls, b"sub"), 33, "17. Handle: EMFILE")
        self.assertEqual(self.lib.pkg_test_opendirs(), 16, "ohne freien Platz kein Hostaufruf")


    # Etappe 12.8 (P4-D): Uhren, dev/ino/Zeitstempel, fehlende Imports
    MTIME = 1700000000123456789
    OPEN_ASSET = ("path_open", "iiiiiIIii", (3, 0, 128, 9, 0, 0, 0, 0, 512))

    def u32(self, calls, data, read_back):
        return self.package(calls, data, read_back=read_back) & 0xffffffff

    def test_clock_res_get_invalid_id_is_einval(self):
        for clock in (4, 9, -1):
            with self.subTest(clock=clock):
                self.assertEqual(self.package([("clock_res_get", "ii", (clock, 512))]), 28)
        self.assertEqual(self.u32([("clock_res_get", "ii", (0, 512))], b"", 512), 1000000)
        self.assertEqual(self.u32([("clock_res_get", "ii", (1, 512))], b"", 512), 1000, "Stopwatch-Aufloesung vom Host")

    def test_filestat_dev_ino_stable_and_distinct(self):
        stat = [("path_filestat_get", "iiiii", (3, 0, 128, 9, 600))]
        first = self.u32(stat, b"asset.txt", 608)
        self.assertEqual(first, self.u32(stat, b"asset.txt", 608), "stabil")
        self.assertEqual(self.u32(stat, b"asset.txt", 600), 1, "dev 1 = Paket")
        other = self.u32([("path_filestat_get", "iiiii", (3, 0, 128, 3, 600))], b"sub", 608)
        self.assertNotEqual(first, other, "verschiedene Pfade, verschiedene ino")
        # "./asset.txt" ist derselbe normierte Pfad.
        dotted = self.u32([("path_filestat_get", "iiiii", (3, 0, 128, 11, 600))], b"./asset.txt", 608)
        self.assertEqual(first, dotted)
        # Der offene Datei-fd meldet dieselbe ino wie der Pfad.
        by_fd = self.u32([self.OPEN_ASSET, ("fd_filestat_get", "ii", (5, 600))], b"asset.txt", 608)
        self.assertEqual(first, by_fd)

    def test_filestat_ino_is_relative_to_package_root_through_dir_fd(self):
        direct = self.u32([("path_filestat_get", "iiiii", (3, 0, 128, 13, 600))], b"sub/asset.txt", 608)
        # Der Teststub kennt unterhalb des Ordner-Handles nur "asset.txt": derselbe Pfad "sub/asset.txt" wie oben.
        via_dir = self.u32([self.open_dir(), ("path_filestat_get", "iiiii", (5, 0, 140, 9, 600))],
                           b"sub" + b"\0" * 9 + b"asset.txt", 608)
        self.assertEqual(via_dir, direct, "Ordner-fd rechnet ino weiter")
        self.assertNotEqual(via_dir, self.u32([("path_filestat_get", "iiiii", (3, 0, 128, 9, 600))], b"asset.txt", 608))

    def test_filestat_has_mtime(self):
        low = self.MTIME & 0xffffffff
        stat = [("path_filestat_get", "iiiii", (3, 0, 128, 9, 600))]
        for offset in (40, 48, 56):
            with self.subTest(offset=offset):
                self.assertEqual(self.u32(stat, b"asset.txt", 600 + offset), low)
        self.assertEqual(self.u32(stat, b"asset.txt", 652), self.MTIME >> 32, "obere Haelfte von mtim")
        self.assertEqual(self.u32([self.OPEN_ASSET, ("fd_filestat_get", "ii", (5, 600))], b"asset.txt", 648), low)
        self.assertEqual(self.u32([("path_filestat_get", "iiiii", (3, 0, 128, 3, 600))], b"sub", 648), 0, "unbekannt = 0")

    def test_fd_renumber_moves_file(self):
        self.assertEqual(self.package([self.OPEN_ASSET, ("fd_renumber", "ii", (5, 6)),
                                       ("fd_filestat_get", "ii", (6, 600))], b"asset.txt"), 0)
        self.assertEqual(self.package([self.OPEN_ASSET, ("fd_renumber", "ii", (5, 6)),
                                       ("fd_filestat_get", "ii", (5, 600))], b"asset.txt"), 8, "alter fd ist frei")
        self.assertEqual(self.lib.file_test_closes(), 1, "genau einmal geschlossen, am Laufende")

    def test_fd_renumber_over_open_target_closes_it(self):
        calls = [self.OPEN_ASSET, ("path_open", "iiiiiIIii", (3, 0, 128, 9, 0, 0, 0, 0, 516)), ("fd_renumber", "ii", (5, 6))]
        self.assertEqual(self.package(calls, b"asset.txt"), 0)
        self.assertEqual(self.lib.file_test_closes(), 2, "Ziel sofort, Quelle am Laufende")

    def test_fd_renumber_same_fd_is_noop(self):
        self.assertEqual(self.package([self.OPEN_ASSET, ("fd_renumber", "ii", (5, 5)),
                                       ("fd_filestat_get", "ii", (5, 600))], b"asset.txt"), 0)
        self.assertEqual(self.package([("fd_renumber", "ii", (7, 7))]), 8, "nicht offen")

    def test_fd_renumber_preopen_is_ebadf(self):
        for source, target in ((3, 6), (4, 6), (1, 6), (5, 6)):
            with self.subTest(source=source):
                self.assertEqual(self.package([("fd_renumber", "ii", (source, target))]), 8)
        for target in (3, 4, 0, 21, -1):
            with self.subTest(target=target):
                self.assertEqual(self.package([self.OPEN_ASSET, ("fd_renumber", "ii", (5, target))], b"asset.txt"), 8)

    def test_fd_datasync_flushes_like_sync(self):
        save_open = ("path_open", "iiiiiIIii", (4, 0, 128, 1, 0, 1 << 6, 0, 0, 512))
        self.assertEqual(self.package([save_open, ("fd_datasync", "i", (5,))], b"x"), 0)
        calls_datasync = self.lib.save_test_calls()
        self.assertEqual(self.package([save_open, ("fd_sync", "i", (5,))], b"x"), 0)
        self.assertEqual(calls_datasync, self.lib.save_test_calls(), "datasync erreicht den Host wie sync")
        self.assertEqual(calls_datasync, 3, "open, sync, close")
        self.assertEqual(self.package([("fd_datasync", "i", (9,))]), 8)

    def test_proc_raise_returns_enosys(self):
        self.assertEqual(self.package([("proc_raise", "i", (6,))]), 52)

    def test_fd_allocate_is_enotsup(self):
        self.assertEqual(self.package([("fd_allocate", "iII", (1, 0, 10))]), 58)
        self.assertEqual(self.package([self.OPEN_ASSET, ("fd_allocate", "iII", (5, 0, 10))], b"asset.txt"), 58)


if __name__ == "__main__":
    unittest.main()
