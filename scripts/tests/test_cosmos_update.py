import importlib.util
from pathlib import Path
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('cosmos_update', SCRIPTS / 'cosmos-update.py')
update = importlib.util.module_from_spec(spec)
spec.loader.exec_module(update)

OLD = '0' * 40
NEW = 'a' * 40


def make_root(directory, version='3.0.97-zehos.6', commit=OLD, sdk=None):
    root = Path(directory)
    (root / 'deploy/cosmos').mkdir(parents=True)
    (root / 'deploy/cosmos/COMMIT').write_bytes(commit.encode() + b'\r\n')
    (root / 'deploy/cosmos/VERSION').write_bytes(version.encode() + b'\r\n')
    (root / 'global.json').write_text('{\n  "msbuild-sdks": {\n    "Cosmos.Sdk": "%s"\n  }\n}\n' % (sdk or version))
    return root


class CosmosUpdateTests(unittest.TestCase):
    def test_verify_consistent_and_mismatch(self):
        with tempfile.TemporaryDirectory() as d:
            self.assertEqual([], update.verify(make_root(d)))
        with tempfile.TemporaryDirectory() as d:
            problems = update.verify(make_root(d, sdk='3.0.97-zehos.5', commit='abc'))
        self.assertEqual(2, len(problems))

    def test_pin_sets_all_places_and_keeps_line_endings(self):
        with tempfile.TemporaryDirectory() as d:
            root = make_root(d)
            update.write_pin(root, '3.0.99-zehos.0', NEW)
            self.assertEqual([], update.verify(root))
            self.assertEqual(NEW.encode() + b'\r\n', (root / 'deploy/cosmos/COMMIT').read_bytes())
            self.assertEqual(update.read_pin(root)['global.json'], '3.0.99-zehos.0')

    def test_pin_refuses_new_commit_with_same_version(self):
        with tempfile.TemporaryDirectory() as d:
            root = make_root(d)
            with self.assertRaises(ValueError):
                update.write_pin(root, '3.0.97-zehos.6', NEW)
            with self.assertRaises(ValueError):
                update.write_pin(root, '3.0.99', 'kurz')
            self.assertEqual(OLD, update.read_pin(root)['COMMIT'])

    def test_latest_stable_ignores_prereleases_and_sorts_numerically(self):
        self.assertEqual('3.0.100', update.latest_stable(['3.0.99', '3.0.100', '3.0.101-preview', '3.0.9']))

    def test_patch_files_and_affected(self):
        text = '# x\n\n- **Commit:** Squash\n- **Dateien:** `src/A/Ext2Directory.cs` (`Rmdir`), `Ext2Tests.cs`; `Rmdir`\n'
        files = update.patch_files(text)
        self.assertEqual(['Ext2Tests.cs', 'src/A/Ext2Directory.cs'], files)
        hits = update.affected({'p': files, 'q': ['Other.cs']}, ['src/B/Ext2Tests.cs', 'src/A/Ext2Directory.cs', 'README.md'])
        self.assertEqual({'p': ['src/A/Ext2Directory.cs', 'src/B/Ext2Tests.cs'], 'q': []}, hits)


if __name__ == '__main__':
    unittest.main()
