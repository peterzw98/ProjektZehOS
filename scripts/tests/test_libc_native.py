"""Prueft Speicherfunktionen und Zahlenumwandlung der freistehenden C-Laufzeit (libc.c) auf dem Host (benoetigt gcc)."""
import ctypes
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
ERANGE, EINVAL = 34, 22
HEADERS = {
    "stddef.h": "typedef __SIZE_TYPE__ size_t;\ntypedef __PTRDIFF_TYPE__ ptrdiff_t;\n#define NULL ((void*)0)\n",
    "stdint.h": "typedef __UINT8_TYPE__ uint8_t;\ntypedef __UINT32_TYPE__ uint32_t;\ntypedef __UINT64_TYPE__ uint64_t;\n"
                "typedef __UINTPTR_TYPE__ uintptr_t;\n#define SIZE_MAX __SIZE_MAX__\n#define UINT32_MAX __UINT32_MAX__\n",
    "stdarg.h": "typedef __builtin_va_list va_list;\n#define va_start(v, l) __builtin_va_start(v, l)\n"
                "#define va_end(v) __builtin_va_end(v)\n#define va_arg(v, t) __builtin_va_arg(v, t)\n",
}


# Unter ELF wuerden Aufrufe innerhalb der Bibliothek (realloc -> free, calloc -> malloc) an die bereits geladene
# glibc gebunden (Interposition) und gaeben Arena-Zeiger an deren free; -Bsymbolic bindet sie an libc.c selbst.
LINK_LOCAL = [] if os.name == "nt" else ["-Wl,-Bsymbolic"]


@unittest.skipIf(shutil.which("gcc") is None, "gcc fuer die native libc-Pruefung erforderlich")
class NativeLibcTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        compiler = shutil.which("gcc")
        work = ROOT / ".tools" / "libc-native"
        work.mkdir(parents=True, exist_ok=True)
        cls.temp = tempfile.TemporaryDirectory(dir=work, ignore_cleanup_errors=True)
        library = Path(cls.temp.name) / ("libc.dll" if os.name == "nt" else "libc.so")
        # Wie im Kernel ohne Host-Header und Host-C-Laufzeit, damit nur libc.c und der Cosmos-Ersatz zaehlen. Die Compiler-
        # Header verweisen unter MinGW per include_next auf den Host (samt errno-Makro), daher eigene aus Compiler-Makros.
        include = Path(cls.temp.name) / "include"
        include.mkdir()
        for name, text in HEADERS.items():
            (include / name).write_text("#pragma once\n" + text)
        subprocess.run([compiler, "-shared", "-fPIC", "-O2", "-nostdinc", "-nostdlib", "-ffreestanding", "-fno-builtin",
                        "-I", str(ROOT / "ProjektZeh/Native/Wasm/libc/include"), "-isystem", str(include),
                        str(ROOT / "ProjektZeh/Native/Wasm/libc/libc.c"), str(ROOT / "scripts/tests/libc-host.c"),
                        "-lgcc", "-o", str(library)] + LINK_LOCAL, check=True)
        cls.lib = lib = ctypes.CDLL(str(library))
        for name in ("malloc", "calloc", "realloc", "heap_test_last_raw", "heap_test_last_freed"):
            getattr(lib, name).restype = ctypes.c_void_p
        lib.malloc.argtypes = [ctypes.c_size_t]
        lib.calloc.argtypes = [ctypes.c_size_t, ctypes.c_size_t]
        lib.realloc.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
        lib.free.argtypes = [ctypes.c_void_p]
        lib.heap_test_last_request.restype = ctypes.c_size_t
        lib.zehos_memory_begin.argtypes = [ctypes.c_size_t, ctypes.c_int, ctypes.c_void_p]
        lib.zehos_memory_begin.restype = ctypes.c_int
        lib.zehos_memory_end.restype = None
        for name in ("zehos_memory_used", "zehos_memory_peak"):
            getattr(lib, name).restype = ctypes.c_size_t
        cls.current_thread = ctypes.c_int(7)
        cls.thread_callback = ctypes.CFUNCTYPE(ctypes.c_int)(lambda: cls.current_thread.value)
        for name, result in (("strtoull", ctypes.c_ulonglong), ("strtoll", ctypes.c_longlong), ("strtol", ctypes.c_long)):
            getattr(lib, name).restype = result
            getattr(lib, name).argtypes = [ctypes.c_char_p, ctypes.c_void_p, ctypes.c_int]
        lib.strtod.restype = ctypes.c_double
        lib.strtod.argtypes = [ctypes.c_char_p, ctypes.c_void_p]

    @classmethod
    def tearDownClass(cls):
        # Windows haelt geladene DLLs bis Prozessende offen; Reste bleiben im ignorierten Testordner.
        cls.temp.cleanup()

    def setUp(self):
        self.lib.zehos_memory_end()
        self.lib.heap_test_reset()
        self.current_thread.value = 7

    def tearDown(self):
        self.lib.zehos_memory_end()
        self.assertEqual(self.lib.heap_test_host_depth(), 0)

    def begin_run(self, limit=0, owner=7):
        return self.lib.zehos_memory_begin(limit, owner, ctypes.cast(self.thread_callback, ctypes.c_void_p))

    def parse(self, name, text, base=10):
        """Liefert Wert, Anzahl gelesener Zeichen und errno."""
        buffer = ctypes.create_string_buffer(text.encode())
        end = ctypes.c_void_p()
        self.lib.libc_test_set_errno(0)
        value = getattr(self.lib, name)(buffer, ctypes.byref(end), base)
        return value, end.value - ctypes.addressof(buffer), self.lib.libc_test_errno()

    def test_blocks_are_16_byte_aligned_although_raw_blocks_are_not(self):
        for size in (0, 1, 7, 16, 33, 4096):
            pointer = self.lib.malloc(size)
            self.assertEqual(self.lib.heap_test_last_raw() % 16, 2)
            self.assertEqual(pointer % 16, 0, size)
        self.assertEqual(self.lib.calloc(3, 5) % 16, 0)
        self.assertEqual(self.lib.realloc(self.lib.malloc(4), 40) % 16, 0)

    def test_aligned_block_stays_inside_raw_block_for_every_offset(self):
        for offset in (0, 1, 2, 15):
            self.lib.heap_test_reset()
            self.lib.heap_test_set_offset(offset)
            for size in (0, 1, 15, 16, 17, 100):
                pointer = self.lib.malloc(size)
                raw = self.lib.heap_test_last_raw()
                self.assertEqual(raw % 16, offset)
                self.assertEqual(pointer % 16, 0, (offset, size))
                self.assertGreaterEqual(pointer - 16, raw, (offset, size))
                ctypes.memset(pointer, 0x5A, size)
                self.assertTrue(self.lib.heap_test_guard_intact(), (offset, size))
                self.lib.free(pointer)
                self.assertEqual(self.lib.heap_test_last_freed(), raw)

    def test_free_releases_the_raw_block(self):
        pointer = self.lib.malloc(24)
        raw = self.lib.heap_test_last_raw()
        self.lib.free(pointer)
        self.lib.free(None)
        self.assertEqual((self.lib.heap_test_allocs(), self.lib.heap_test_frees()), (1, 1))
        self.assertEqual(self.lib.heap_test_last_freed(), raw)

    def test_realloc_keeps_content_and_frees_old_block(self):
        pointer = self.lib.malloc(8)
        raw = self.lib.heap_test_last_raw()
        ctypes.memmove(pointer, b"ZehOS!!\0", 8)
        grown = self.lib.realloc(pointer, 64)
        self.assertEqual(ctypes.string_at(grown, 8), b"ZehOS!!\0")
        self.assertEqual(self.lib.heap_test_last_freed(), raw)
        shrunk = self.lib.realloc(grown, 3)
        self.assertEqual(ctypes.string_at(shrunk, 3), b"Zeh")
        self.assertIsNone(self.lib.realloc(shrunk, 0))
        self.assertEqual(self.lib.heap_test_allocs(), self.lib.heap_test_frees())

    def test_calloc_zeroes_memory(self):
        pointer = self.lib.malloc(32)
        ctypes.memset(pointer, 0xAB, 32)
        self.lib.free(pointer)
        self.lib.heap_test_reset()
        self.assertEqual(ctypes.string_at(self.lib.calloc(4, 8), 32), bytes(32))

    def test_sizes_beyond_32_bit_heap_limit_fail_without_heap_call(self):
        # Groesse/Rohzeiger plus zwei Arena-Zeiger und bis zu 15 Byte Ausrichtung.
        largest = 0xFFFFFFFF - 47
        for size in (largest + 1, 0xFFFFFFFF, 1 << 40, (1 << 64) - 1):
            self.assertIsNone(self.lib.malloc(size), hex(size))
        self.assertIsNone(self.lib.calloc(1 << 33, 1 << 33))
        # Produkt genau an bzw. knapp ueber der 32-Bit-Grenze; ohne Pruefung kuerzte der Heap es auf 0 bzw. 2^16.
        self.assertIsNone(self.lib.calloc(1 << 16, 1 << 16))
        self.assertIsNone(self.lib.calloc((1 << 16) + 1, 1 << 16))
        old = self.lib.malloc(4)
        ctypes.memmove(old, b"keep", 4)
        self.assertIsNone(self.lib.realloc(old, 1 << 33))
        self.assertEqual(self.lib.heap_test_frees(), 0)
        self.assertEqual(ctypes.string_at(old, 4), b"keep")
        self.assertEqual(self.lib.heap_test_allocs(), 1)
        # Die groesste zulaessige Anforderung erreicht den Heap ungekuerzt (der Ersatzheap lehnt sie dann ab).
        self.assertIsNone(self.lib.malloc(largest))
        self.assertEqual(self.lib.heap_test_last_request(), 0xFFFFFFFF)

    def test_run_budget_keeps_failed_allocation_and_releases_owner_blocks(self):
        self.assertEqual(self.begin_run(128), 1)
        try:
            pointer = self.lib.malloc(32)
            self.assertIsNotNone(pointer)
            used = self.lib.zehos_memory_used()
            self.assertEqual(used, self.lib.heap_test_last_request())
            self.assertIsNone(self.lib.malloc(100))
            self.assertEqual(self.lib.zehos_memory_used(), used)
            self.assertEqual(self.lib.zehos_memory_exceeded(), 1)
        finally:
            self.lib.zehos_memory_end()
        self.assertEqual(self.lib.zehos_memory_used(), 0)
        self.assertEqual(self.lib.heap_test_frees(), 1)

    def test_run_free_reopens_budget_and_keeps_peak(self):
        self.assertEqual(self.begin_run(128), 1)
        pointer = self.lib.malloc(32)
        used = self.lib.zehos_memory_used()
        self.lib.free(pointer)
        self.assertEqual(self.lib.zehos_memory_used(), 0)
        self.assertEqual(self.lib.zehos_memory_peak(), used)
        self.assertIsNotNone(self.lib.malloc(32))

    def test_run_keeps_foreign_thread_allocations_outside_arena(self):
        self.assertEqual(self.begin_run(128), 1)
        self.current_thread.value = 8
        foreign = self.lib.malloc(1024)
        self.assertIsNotNone(foreign)
        self.assertEqual(self.lib.zehos_memory_used(), 0)
        self.current_thread.value = 7
        self.assertIsNotNone(self.lib.malloc(32))
        self.current_thread.value = 8
        self.lib.zehos_memory_end()
        self.assertEqual(self.lib.heap_test_frees(), 1)
        self.lib.free(foreign)
        self.assertEqual(self.lib.heap_test_frees(), 2)

    def test_run_rejects_invalid_or_second_begin_without_reset(self):
        self.assertEqual(self.lib.zehos_memory_begin(128, 7, None), 0)
        self.assertEqual(self.begin_run(128, owner=8), 0)
        self.assertEqual(self.begin_run(128, owner=-1), 0)
        self.assertEqual(self.begin_run(128), 1)
        self.assertIsNotNone(self.lib.malloc(32))
        used = self.lib.zehos_memory_used()
        self.assertEqual(self.begin_run(1024), 0)
        self.assertEqual(self.lib.zehos_memory_used(), used)
        self.assertIsNone(self.lib.malloc(100))

    def test_run_realloc_rejection_preserves_old_content_and_charge(self):
        self.assertEqual(self.begin_run(128), 1)
        old = self.lib.malloc(32)
        ctypes.memmove(old, b"keep", 4)
        used = self.lib.zehos_memory_used()
        self.assertIsNone(self.lib.realloc(old, 64))
        self.assertEqual(ctypes.string_at(old, 4), b"keep")
        self.assertEqual(self.lib.zehos_memory_used(), used)
        self.assertEqual(self.lib.heap_test_frees(), 0)
        self.lib.free(old)
        self.assertIsNotNone(self.lib.malloc(64))

    def test_run_realloc_counts_both_blocks_during_copy(self):
        self.assertEqual(self.begin_run(), 1)
        pointer = self.lib.calloc(4, 8)
        first = self.lib.zehos_memory_used()
        grown = self.lib.realloc(pointer, 64)
        second = self.lib.heap_test_last_request()
        self.assertEqual(self.lib.zehos_memory_used(), second)
        self.assertEqual(self.lib.zehos_memory_peak(), first + second)
        self.lib.free(grown)
        self.assertEqual(self.lib.zehos_memory_used(), 0)

    def test_run_end_is_idempotent_and_next_begin_resets_diagnostics(self):
        self.assertEqual(self.begin_run(256), 1)
        for size in (16, 32, 48):
            self.assertIsNotNone(self.lib.malloc(size))
        peak = self.lib.zehos_memory_used()
        self.assertIsNone(self.lib.malloc(256))
        self.lib.zehos_memory_end()
        self.assertEqual(self.lib.heap_test_frees(), 3)
        self.assertEqual(self.lib.zehos_memory_used(), 0)
        self.assertEqual(self.lib.zehos_memory_peak(), peak)
        self.assertEqual(self.lib.zehos_memory_exceeded(), 1)
        self.lib.zehos_memory_end()
        self.assertEqual(self.lib.heap_test_frees(), 3)
        self.assertEqual(self.begin_run(), 1)
        self.assertEqual(self.lib.zehos_memory_peak(), 0)
        self.assertEqual(self.lib.zehos_memory_exceeded(), 0)

    def test_run_exact_raw_byte_limit_and_balanced_host_depth(self):
        self.assertEqual(self.begin_run(), 1)
        self.assertIsNotNone(self.lib.malloc(64))
        exact = self.lib.heap_test_last_request()
        self.lib.zehos_memory_end()
        self.assertEqual(self.begin_run(exact), 1)
        self.assertIsNotNone(self.lib.malloc(64))
        self.assertEqual(self.lib.zehos_memory_used(), exact)
        self.assertIsNone(self.lib.malloc(0))
        self.lib.zehos_memory_end()
        self.assertEqual(self.lib.heap_test_host_depth(), 0)

    def test_run_unlinks_middle_head_and_tail_without_double_free(self):
        self.assertEqual(self.begin_run(), 1)
        first, middle, third, last = [self.lib.malloc(32) for _ in range(4)]
        for pointer in (middle, first, last, third):
            self.lib.free(pointer)
        self.assertEqual(self.lib.zehos_memory_used(), 0)
        self.lib.zehos_memory_end()
        self.lib.zehos_memory_end()
        self.assertEqual(self.lib.heap_test_frees(), 4)

    def test_run_preserves_block_allocated_before_begin(self):
        before = self.lib.malloc(32)
        self.assertEqual(self.begin_run(), 1)
        self.assertIsNotNone(self.lib.malloc(32))
        self.lib.zehos_memory_end()
        self.assertEqual(self.lib.heap_test_frees(), 1)
        self.lib.free(before)
        self.assertEqual(self.lib.heap_test_frees(), 2)

    def test_run_heap_oom_and_calloc_rejection_leave_depth_balanced(self):
        self.assertEqual(self.begin_run(), 1)
        old = self.lib.malloc(32)
        used = self.lib.zehos_memory_used()
        self.assertIsNone(self.lib.realloc(old, 2 << 20))
        self.assertEqual(self.lib.zehos_memory_used(), used)
        self.assertEqual(self.lib.zehos_memory_exceeded(), 0)
        self.assertEqual(self.lib.heap_test_host_depth(), 0)
        self.lib.zehos_memory_end()
        self.assertEqual(self.begin_run(128), 1)
        self.assertIsNone(self.lib.calloc(2, 100))
        self.assertIsNone(self.lib.calloc(1 << 63, 3))
        self.assertEqual(self.lib.zehos_memory_used(), 0)
        self.assertEqual(self.lib.heap_test_host_depth(), 0)

    def test_strtoull_bases_prefixes_and_end(self):
        cases = [("  42xyz", 10, 42, 4), ("+17", 10, 17, 3), ("0x1fG", 0, 31, 4), ("0X1F", 16, 31, 4),
                 ("0755", 0, 493, 4), ("0", 0, 0, 1), ("0x", 0, 0, 1), ("0xg", 16, 0, 1), ("101", 2, 5, 3),
                 ("zZ", 36, 1295, 2), ("19", 8, 1, 1), ("-1", 10, (1 << 64) - 1, 2),
                 ("18446744073709551615", 10, (1 << 64) - 1, 20)]
        for text, base, value, length in cases:
            self.assertEqual(self.parse("strtoull", text, base), (value, length, 0), text)

    def test_missing_digits_leave_end_at_start(self):
        for name in ("strtoull", "strtoll", "strtol"):
            for text in ("", "   ", "-", "+x", "abc"):
                self.assertEqual(self.parse(name, text)[:2], (0, 0), (name, text))

    def test_invalid_base_sets_einval(self):
        for base in (-1, 1, 37):
            self.assertEqual(self.parse("strtoull", "12", base), (0, 0, EINVAL))

    def test_overflow_saturates_and_sets_erange(self):
        self.assertEqual(self.parse("strtoull", "18446744073709551616x"), ((1 << 64) - 1, 20, ERANGE))
        self.assertEqual(self.parse("strtoull", "0x10000000000000000", 0), ((1 << 64) - 1, 19, ERANGE))
        self.assertEqual(self.parse("strtoll", "9223372036854775807"), ((1 << 63) - 1, 19, 0))
        self.assertEqual(self.parse("strtoll", "-9223372036854775808"), (-(1 << 63), 20, 0))
        self.assertEqual(self.parse("strtoll", "9223372036854775808"), ((1 << 63) - 1, 19, ERANGE))
        self.assertEqual(self.parse("strtoll", "-9223372036854775809"), (-(1 << 63), 20, ERANGE))
        self.assertEqual(self.parse("strtoll", "-99999999999999999999"), (-(1 << 63), 21, ERANGE))

    def test_strtoll_and_strtol_signs(self):
        self.assertEqual(self.parse("strtoll", " -0x7f", 0), (-127, 6, 0))
        self.assertEqual(self.parse("strtol", "-123", 10), (-123, 4, 0))
        for name in ("strtoll", "strtol"):
            self.assertEqual(self.parse(name, "-0"), (0, 2, 0))
        # Unter MinGW (LLP64) ist long 32 Bit; strtol wird auf dem Host daher nur im 32-Bit-Bereich geprueft,
        # im Kernel (LP64) gilt der 64-Bit-Bereich wie bei strtoll.
        bits =ctypes.sizeof(ctypes.c_long) * 8
        maximum = (1 << (bits - 1)) - 1
        self.assertEqual(self.parse("strtol", str(maximum + 1))[::2], (maximum, ERANGE))
        self.assertEqual(self.parse("strtol", str(-maximum - 2))[::2], (-maximum - 1, ERANGE))

    def test_strtod_reports_missing_conversion(self):
        buffer = ctypes.create_string_buffer(b"1.5")
        end = ctypes.c_void_p()
        self.lib.libc_test_set_errno(0)
        self.assertEqual(self.lib.strtod(buffer, ctypes.byref(end)), 0.0)
        self.assertEqual(end.value, ctypes.addressof(buffer))
        self.assertEqual(self.lib.libc_test_errno(), EINVAL)


if __name__ == "__main__":
    unittest.main()
