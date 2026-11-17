"""Prueft poll_event mit der echten C-Bruecke und wasm3 (benoetigt gcc)."""
import ctypes
import os
from pathlib import Path
import shutil
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
        subprocess.run([compiler, "-shared", "-fPIC", "-O2", "-I", str(source),
                        *(str(ROOT / "ProjektZeh/Native/Wasm/host" / name) for name in ("zehos_host_core.c", "zehos_host_wasm3.c")),
                        str(ROOT / "scripts/tests/wasm-input-host.c"),
                        *map(str, sorted(source.glob("*.c"))), "-lm", "-o", str(library)], check=True)
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
        for kwargs, expected in (({"dir_fd":4},8), ({"flags":2},28)):
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

if __name__ == "__main__":
    unittest.main()
