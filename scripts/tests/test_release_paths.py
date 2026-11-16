import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / 'check-release-paths.py'
spec = importlib.util.spec_from_file_location('check_release_paths', SCRIPT)
scanner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scanner)


class ReleasePathTests(unittest.TestCase):
    def test_local_paths_in_supported_encodings(self):
        with tempfile.TemporaryDirectory() as directory:
            artifact = Path(directory) / 'artifact'
            for path in ('/home/alice/file', '/Users/alice/file', '/root/file', 'C:\\Users\\alice\\file'):
                for encoding in ('utf-8', 'utf-16-le', 'utf-16-be'):
                    with self.subTest(path=path, encoding=encoding):
                        artifact.write_bytes(b'prefix' + path.encode(encoding) + b'suffix')
                        self.assertEqual(1, scanner.main([str(artifact)]))

    def test_upstream_prefix_is_allowed_but_local_paths_are_not(self):
        with tempfile.TemporaryDirectory() as directory:
            artifact = Path(directory) / 'artifact'
            artifact.write_bytes('/home/runner/work/Cosmos/Cosmos/src/Cosmos.Kernel.Core/Scheduler/SchedulerManager.cs'.encode('utf-16-le') + b'\0\0')
            self.assertEqual(0, scanner.main([str(artifact)]))
            artifact.write_bytes(artifact.read_bytes() + b'/home/alice/secret')
            self.assertEqual(1, scanner.main([str(artifact)]))

    def test_upstream_prefix_with_suffix_is_not_allowed(self):
        upstream = '/home/runner/work/Cosmos/Cosmos/src/Cosmos.Kernel.Core/Scheduler/SchedulerManager.cs'
        with tempfile.TemporaryDirectory() as directory:
            artifact = Path(directory) / 'artifact'
            for encoding in ('utf-8', 'utf-16-le', 'utf-16-be'):
                with self.subTest(encoding=encoding):
                    artifact.write_bytes((upstream + '.private').encode(encoding))
                    self.assertEqual(1, scanner.main([str(artifact)]))

    def test_missing_file_fails_and_workspace_is_checked(self):
        with tempfile.TemporaryDirectory() as directory:
            artifact = Path(directory) / 'artifact'
            self.assertEqual(2, scanner.main([str(artifact)]))
            artifact.write_bytes(b'/build/private/work/source')
            with patch.dict('os.environ', {'GITHUB_WORKSPACE': '/build/private/work'}, clear=True):
                self.assertEqual(1, scanner.main([str(artifact)]))


if __name__ == '__main__':
    unittest.main()
