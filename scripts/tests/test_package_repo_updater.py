import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


UPDATER = Path(__file__).resolve().parents[2] / 'deploy/package-repo/updater.sh'


@unittest.skipUnless(sys.platform.startswith('linux'), 'Paket-Updater läuft unter Linux')
class PackageRepoUpdaterTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.repo = Path(scratch.name) / 'source'
        self.repo.mkdir()
        self.root = Path(scratch.name) / 'served'
        self.current = self.root / 'current'
        self.git('init', '-q', '-b', 'packages')

    def git(self, *args):
        return subprocess.check_output(['git', '-C', str(self.repo), '-c', 'user.name=Test',
                                        '-c', 'user.email=test@example.invalid', *args], text=True).strip()

    def commit(self, **files):
        for name, content in files.items():
            (self.repo / name).write_text(content, encoding='utf-8')
        self.git('add', '.')
        self.git('commit', '-qm', 'Teststand')
        return self.git('rev-parse', 'HEAD')

    def update(self):
        return subprocess.run(['sh', str(UPDATER), '--once'], text=True, capture_output=True, timeout=15,
                              env={**os.environ, 'PACKAGES_ROOT': str(self.root),
                                   'PACKAGES_GIT_URL': self.repo.as_uri(), 'PACKAGES_BRANCH': 'packages'})

    def test_legacy_invalid_and_complete_v2(self):
        legacy = self.commit(catalog='Legacy')
        first = self.update()
        self.assertEqual(first.returncode, 0, first.stderr)
        self.assertEqual(self.current.readlink(), Path('releases') / legacy)

        for invalid in ({'catalog2': 'Katalog'}, {'catalog2.sig': ''}):
            with self.subTest(invalid=invalid):
                rejected = self.commit(**invalid)
                failure = self.update()
                self.assertNotEqual(failure.returncode, 0)
                self.assertIn('nichtleeres Dateipaar', failure.stderr)
                self.assertEqual(self.current.readlink(), Path('releases') / legacy)
                self.assertFalse((self.root / 'releases' / rejected).exists())
                self.assertFalse(list(self.root.glob('.publish.*')))

        complete = self.commit(**{'catalog2.sig': 'Signatur'})
        final = self.update()
        self.assertEqual(final.returncode, 0, final.stderr)
        self.assertEqual(self.current.readlink(), Path('releases') / complete)
        self.assertEqual((self.current / 'catalog2').read_text(), 'Katalog')
        self.assertEqual((self.current / 'catalog2.sig').read_text(), 'Signatur')
        self.assertEqual((self.root / 'releases' / legacy / 'catalog').read_text(), 'Legacy')

        orphan = self.root / '.publish.orphan'
        orphan.mkdir()
        (orphan / 'archive.tar').touch()
        self.git('rm', 'catalog2', 'catalog2.sig')
        downgrade = self.commit(catalog='Fallback')
        failure = self.update()
        self.assertNotEqual(failure.returncode, 0)
        self.assertIn('reinen v1-Stand', failure.stderr)
        self.assertEqual(self.current.readlink(), Path('releases') / complete)
        self.assertFalse((self.root / 'releases' / downgrade).exists())
        self.assertFalse(orphan.exists())


if __name__ == '__main__':
    unittest.main()
