import importlib.util
import shutil
from pathlib import Path
import tempfile
import unittest
from unittest import mock

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('guest_test', SCRIPTS / 'test-wasm-doom-desktop.py')
guest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guest)


class QemuToolTests(unittest.TestCase):
    def setUp(self):
        self.home = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.home)
        patches = [mock.patch.object(guest.shutil, 'which', return_value=None),
                   mock.patch.object(guest.Path, 'home', return_value=self.home)]
        for patch in patches:
            patch.start()
            self.addCleanup(patch.stop)

    def cosmos_linux(self):
        tools = self.home / '.cosmos/tools/qemu'
        (tools / 'lib').mkdir(parents=True)
        for name in ('qemu-img', 'qemu-img.real'):
            (tools / name).touch()
        return tools

    def test_missing_tool_reports_searched_places(self):
        with self.assertRaises(SystemExit) as raised:
            guest.qemu_tool('qemu-img')
        self.assertIn('~/.cosmos/tools/qemu/', str(raised.exception.code))

    def test_finds_cosmos_linux_tool(self):
        tools = self.cosmos_linux()
        self.assertEqual(guest.qemu_tool('qemu-img'), tools / 'qemu-img')

    def test_finds_cosmos_windows_tool(self):
        tool = self.home / 'AppData/Local/Cosmos/Tools/qemu/bin/qemu-img.exe'
        tool.parent.mkdir(parents=True)
        tool.touch()
        self.assertEqual(guest.qemu_tool('qemu-img'), tool)
        self.assertEqual(guest.qemu_img(), ([str(tool)], None))

    def test_qemu_img_bypasses_cosmos_wrapper(self):
        tools = self.cosmos_linux()
        command, environment = guest.qemu_img()
        self.assertEqual(command, [str(tools / 'qemu-img.real')])
        self.assertTrue(environment['LD_LIBRARY_PATH'].startswith(str(tools / 'lib')))

    def test_qemu_img_from_path_runs_unchanged(self):
        tool = str(self.home / 'qemu-img')
        with mock.patch.object(guest.shutil, 'which', return_value=tool):
            self.assertEqual(guest.qemu_img(), ([tool], None))

    def test_smp_arguments(self):
        loader_spec = importlib.util.spec_from_file_location('loader', SCRIPTS / 'test-wasm-loader.py')
        loader = importlib.util.module_from_spec(loader_spec)
        loader_spec.loader.exec_module(loader)
        tools = self.cosmos_linux()
        (tools / 'qemu-system-x86_64').touch()
        for value in ('', '4,cores=2,threads=2'):
            with self.subTest(value=value), mock.patch.dict('os.environ', {'ZEHOS_QEMU_SMP': value}), mock.patch.object(guest.subprocess, 'Popen') as popen:
                guest.start_qemu(Path('z.iso'), Path('d.qcow2'), self.home, 1234, 'tcg', None)
                for args in (popen.call_args.args[0], loader.build_arguments(Path('qemu'), Path('z.iso'), None, Path('serial'), 'tcg')):
                    if value:
                        self.assertEqual(args[args.index('-smp'):args.index('-smp') + 2], ['-smp', value])
                    else:
                        self.assertNotIn('-smp', args)

    def test_cpu_model_per_accelerator(self):
        # KVM braucht RDRAND/RDSEED fuer die Kennwortanlage (#56); WHPX bricht mit -cpu max/host ab.
        expected = {'tcg': ['-cpu', 'max'], 'kvm': ['-cpu', 'qemu64,+rdrand,+rdseed'], 'whpx': []}
        tools = self.cosmos_linux()
        (tools / 'qemu-system-x86_64').touch()
        for accel, cpu in expected.items():
            with self.subTest(accel=accel), mock.patch.object(guest.subprocess, 'Popen') as popen:
                guest.start_qemu(Path('z.iso'), Path('d.qcow2'), self.home, 1234, accel, None)
                self.assertTrue(popen.call_args.kwargs['stderr'].closed)
                arguments = popen.call_args.args[0]
                start = arguments.index('-accel') + 2
                self.assertEqual(arguments[start:start + len(cpu)], cpu)
                self.assertEqual(arguments.count('-cpu'), len(cpu) // 2)


if __name__ == '__main__':
    unittest.main()
