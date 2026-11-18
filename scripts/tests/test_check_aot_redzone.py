from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
import importlib.util
import io
import shutil
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts' / 'check-aot-redzone.py'
FIXTURES = ROOT / 'ProjektZeh' / 'Native' / 'AotProbe'
spec = importlib.util.spec_from_file_location('check_aot_redzone', SCRIPT)
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)

# mov [rsp-0x8],rax (Red Zone), mov [rsp+0x8],rax (eigener Rahmen), ret
BELOW = bytes.fromhex('48894424f8')
ABOVE = bytes.fromhex('4889442408')
RET = bytes.fromhex('c3')


class CountTests(unittest.TestCase):
    def test_counts_only_negative_rsp_offsets(self):
        text = ('   0:\t48 89 44 24 f8\tmov    QWORD PTR [rsp-0x8],rax\n'
                '   5:\t48 89 44 24 08\tmov    QWORD PTR [rsp+0x8],rax\n'
                '   a:\t48 8b 45 f8\tmov    rax,QWORD PTR [rbp-0x8]\n')
        self.assertEqual(tool.count_below_rsp(text), 1)

    def test_c_file_without_array_is_an_error(self):
        with tempfile.TemporaryDirectory() as scratch:
            empty = Path(scratch) / 'leer.c'
            empty.write_text('int x;\n', encoding='utf-8')
            with self.assertRaises(ValueError):
                tool.blobs(empty)

    def test_c_arrays_are_split_per_field(self):
        with tempfile.TemporaryDirectory() as scratch:
            source = Path(scratch) / 'f.c'
            source.write_text('const uint8_t a[] = {\n    0x48, 0x89,\n};\nconst uint8_t b[] = { 0xc3 };\n', encoding='utf-8')
            self.assertEqual(tool.blobs(source), [(f'{source}:a', b'\x48\x89'), (f'{source}:b', b'\xc3')])


@unittest.skipUnless(shutil.which('objdump'), 'objdump fehlt (GNU Binutils)')
class ObjdumpTests(unittest.TestCase):
    def run_main(self, *paths):
        stdout, stderr = io.StringIO(), io.StringIO()
        with redirect_stdout(stdout), redirect_stderr(stderr):
            code = tool.main([str(path) for path in paths])
        return code, stdout.getvalue(), stderr.getvalue()

    def write(self, data):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        path = Path(scratch.name) / 'app.aot'
        path.write_bytes(data)
        return path

    def test_red_zone_access_fails_with_count(self):
        code, out, error = self.run_main(self.write(ABOVE + BELOW + BELOW + RET))
        self.assertEqual(code, 1)
        self.assertIn('2 Zugriffe unter RSP', out)
        self.assertIn('Red Zone benutzt', error)

    def test_own_frame_access_passes(self):
        code, out, _ = self.run_main(self.write(ABOVE + RET))
        self.assertEqual(code, 0)
        self.assertIn('0 Zugriffe unter RSP', out)

    def test_committed_probe_fixtures_have_no_red_zone(self):
        # Die Probe-Fixtures stammen vom zugelassenen wamrc (fixture/README.md); ein Rueckfall auf ein wamrc ohne Patch
        # oder ein falsch erzeugtes Fixture faellt hier auf.
        code, out, error = self.run_main(FIXTURES / 'aot_fixture_data.c', FIXTURES / 'aot_runtime_fixture_data.c')
        self.assertEqual(code, 0, out + error)
        self.assertEqual(out.count(': 0 Zugriffe unter RSP'), 3, out)

    def test_missing_file_is_an_input_error(self):
        code, _, error = self.run_main(Path(tempfile.gettempdir()) / 'gibt-es-nicht.aot')
        self.assertEqual(code, 2)
        self.assertIn('Fehler', error)


if __name__ == '__main__':
    unittest.main()
