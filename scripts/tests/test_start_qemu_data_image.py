import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / 'start-qemu.ps1'
PWSH = shutil.which('pwsh') or shutil.which('powershell')
MIB = 1024 * 1024


@unittest.skipUnless(PWSH, 'PowerShell nicht installiert')
class DataImagePathTests(unittest.TestCase):
    def setUp(self):
        self.dir = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.dir)

    def run_script(self, path):
        return subprocess.run([PWSH, '-NoProfile', '-File', str(SCRIPT), '-PrepareDiskOnly',
                               '-DataImagePath', str(path)], capture_output=True, text=True)

    def image(self, name='data.img', content=b'\x5a' * 64 * MIB):
        path = self.dir / name
        path.write_bytes(content)
        return path

    def test_existing_image_is_only_checked(self):
        path = self.image('mit Leerzeichen.img')
        before = path.read_bytes()
        self.assertEqual(self.run_script(path).returncode, 0)
        self.assertEqual(path.read_bytes(), before)

    def test_larger_aligned_image_is_accepted(self):
        self.assertEqual(self.run_script(self.image(content=b'\0' * 65 * MIB)).returncode, 0)

    def test_rejections_create_nothing(self):
        cases = {'missing': self.dir / 'fehlt.img', 'comma': self.image('a,b.img'),
                 'small': self.image('klein.img', b'\0' * 32 * MIB),
                 'unaligned': self.image('schief.img', b'\0' * (64 * MIB + 1)),
                 'directory': self.dir}
        cases['directory'].joinpath('d').mkdir()
        cases['directory'] = self.dir / 'd'
        before = sorted(self.dir.rglob('*'))
        for name, path in cases.items():
            with self.subTest(name):
                self.assertNotEqual(self.run_script(path).returncode, 0)
        self.assertEqual(sorted(self.dir.rglob('*')), before)


if __name__ == '__main__':
    unittest.main()
