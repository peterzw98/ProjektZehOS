import importlib.util
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

    def test_qemu_img_bypasses_cosmos_wrapper(self):
        tools = self.cosmos_linux()
        command, environment = guest.qemu_img()
        self.assertEqual(command, [str(tools / 'qemu-img.real')])
        self.assertTrue(environment['LD_LIBRARY_PATH'].startswith(str(tools / 'lib')))

    def test_qemu_img_from_path_runs_unchanged(self):
        with mock.patch.object(guest.shutil, 'which', return_value='/usr/bin/qemu-img'):
            self.assertEqual(guest.qemu_img(), (['/usr/bin/qemu-img'], None))


if __name__ == '__main__':
    unittest.main()
