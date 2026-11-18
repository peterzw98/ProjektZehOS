import contextlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
from unittest import mock

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('measure', SCRIPTS / 'measure-wasm-frametimes.py')
measure = importlib.util.module_from_spec(spec)
spec.loader.exec_module(measure)


class ArgumentTests(unittest.TestCase):
    def test_defaults_measure_published_doom_once(self):
        args = measure.parse_args(['--iso', 'x.iso'])
        self.assertEqual((args.id, args.version, args.aot, args.runs, args.package), ('doom', '1.1.0', 'off', 1, None))

    def test_package_options_need_each_other(self):
        for argv in (['--package', 'p'], ['--key', 'k'], ['--aot-file', 'a.aot'], ['--network'], ['--runs', '0']):
            with self.subTest(argv=argv), self.assertRaises(SystemExit), contextlib.redirect_stderr(io.StringIO()):
                measure.parse_args(['--iso', 'x.iso', *argv])

    def test_last_median_uses_last_window(self):
        lines = ['Bildzeit n=10 min=1 median=40 p95=90 max=99 ms pausen=0', 'Anteile (ms, median/p95/max) app=1',
                 'Bildzeit n=12 min=1 median=33 p95=80 max=95 ms pausen=0']
        self.assertEqual(measure.last_median(lines), 33)
        self.assertIsNone(measure.last_median(['Anteile (ms, median/p95/max) app=1']))

    def test_last_average_fps_counts_frames_of_last_window(self):
        lines = ['Bildzeit n=1462 min=6 median=23 p95=120 max=200 ms pausen=0',
                 'Bildzeit n=1800 min=6 median=20 p95=99 max=150 ms pausen=0']
        self.assertEqual(measure.last_average_fps(lines), 30.0)
        self.assertIsNone(measure.last_average_fps([]))

    def test_memory_defaults_to_1g_for_classicube_only(self):
        self.assertEqual(measure.parse_args(['--iso', 'x.iso']).memory, '512M')
        self.assertEqual(measure.parse_args(['--iso', 'x.iso', '--id', 'classicube']).memory, '1G')
        self.assertEqual(measure.parse_args(['--iso', 'x.iso', '--id', 'classicube', '-m', '2G']).memory, '2G')

    def test_start_qemu_replaces_only_memory_value(self):
        tools = measure.tools
        with (tempfile.TemporaryDirectory() as work,
              mock.patch.object(tools, 'qemu_tool', return_value=Path(work) / 'qemu'),
              mock.patch.object(tools.subprocess, 'Popen') as popen):
            tools.start_qemu(Path('x.iso'), Path('d.qcow2'), Path(work), 1, 'kvm', None, '1G')
            arguments = popen.call_args.args[0]
        index = arguments.index('-m')
        self.assertEqual(arguments[index + 1], '1G')
        self.assertEqual(arguments.count('-m'), 1)
        self.assertNotIn('512M', arguments)
        self.assertIn('q35', arguments)

    def test_move_is_off_by_default(self):
        self.assertFalse(measure.parse_args(['--iso', 'x.iso']).move)
        self.assertTrue(measure.parse_args(['--iso', 'x.iso', '--move']).move)


if __name__ == '__main__':
    unittest.main()
