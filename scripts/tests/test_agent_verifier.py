"""Der Agentenstart darf nur die festgelegte Prüfkopie anlegen und keine lokalen Änderungen überschreiben."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
HELPER = ROOT / 'deploy/agent-team/prepare-bridge.sh'
BASH = shutil.which('bash')
if not BASH and Path('C:/Program Files/Git/bin/bash.exe').exists():
    BASH = 'C:/Program Files/Git/bin/bash.exe'


@unittest.skipUnless(BASH and shutil.which('git'), 'Bash und Git werden benötigt')
class AgentVerifierTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix='zehos-verifier-test-')
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.repo = self.root / 'repo with spaces'
        self.repo.mkdir()
        self.env = dict(os.environ, GIT_TERMINAL_PROMPT='0', GIT_CONFIG_NOSYSTEM='1', GIT_CONFIG_GLOBAL=os.devnull)
        self.git('init', '-q')
        self.git('config', 'user.name', 'Verifier test')
        self.git('config', 'user.email', 'verifier@example.invalid')
        self.git('config', 'commit.gpgsign', 'false')
        self.git('config', 'core.autocrlf', 'false')
        (self.repo / 'verify.sh').write_bytes(b'#!/bin/sh\necho test\n')
        (self.repo / 'bridge-pubkey.asc').write_bytes(b'test public key\n')
        self.git('add', 'verify.sh', 'bridge-pubkey.asc')
        self.git('commit', '-qm', 'verifier')
        self.pin = self.git('rev-parse', 'HEAD')
        (self.repo / 'main.txt').write_text('main unchanged\n', encoding='utf-8')
        self.git('add', 'main.txt')
        self.git('commit', '-qm', 'main')
        self.main = self.git('rev-parse', 'HEAD')
        self.tools = self.root / 'launcher'
        self.tools.mkdir()
        if HELPER.exists():
            shutil.copyfile(HELPER, self.tools / 'prepare-bridge.sh')
        (self.tools / 'BRIDGE_COMMIT').write_text(self.pin + '\n', encoding='ascii')
        self.target = self.repo / '.tools/disc-bridge'

    def git(self, *args, cwd=None):
        return subprocess.check_output(['git', '-C', str(cwd or self.repo), *args], env=self.env, text=True).strip()

    def prepare(self):
        return subprocess.run([BASH, (self.tools / 'prepare-bridge.sh').as_posix(), self.repo.as_posix()],
                              env=self.env, capture_output=True, text=True, timeout=30)

    def create(self):
        result = self.prepare()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.pin, self.git('rev-parse', 'HEAD', cwd=self.target))
        self.assertEqual(self.main, self.git('rev-parse', 'HEAD'))

    def test_creates_pinned_worktree_and_is_idempotent(self):
        self.create()
        self.create()
        self.assertIn('verify.sh', self.prepare().stdout)
        self.assertEqual('main unchanged\n', (self.repo / 'main.txt').read_text(encoding='utf-8'))

    def test_modified_verifier_is_rejected_and_preserved(self):
        self.create()
        path = self.target / 'verify.sh'
        path.write_text('changed verifier\n', encoding='utf-8')
        self.assertNotEqual(0, self.prepare().returncode)
        self.assertEqual('changed verifier\n', path.read_text(encoding='utf-8'))

    def test_modified_public_key_is_rejected(self):
        self.create()
        (self.target / 'bridge-pubkey.asc').write_text('changed key\n', encoding='utf-8')
        self.assertNotEqual(0, self.prepare().returncode)

    def test_other_head_is_not_silently_replaced(self):
        self.create()
        self.git('checkout', '--detach', '-q', self.main, cwd=self.target)
        self.assertNotEqual(0, self.prepare().returncode)
        self.assertEqual(self.main, self.git('rev-parse', 'HEAD', cwd=self.target))

    def assert_index_flag_rejected(self, filename, flag):
        self.create()
        self.git('update-index', flag, filename, cwd=self.target)
        path = self.target / filename
        path.write_text('changed but hidden from git diff\n', encoding='utf-8')
        self.assertNotEqual(0, self.prepare().returncode)
        self.assertEqual('changed but hidden from git diff\n', path.read_text(encoding='utf-8'))

    def test_assume_unchanged_cannot_hide_verifier(self):
        self.assert_index_flag_rejected('verify.sh', '--assume-unchanged')

    def test_skip_worktree_cannot_hide_verifier(self):
        self.assert_index_flag_rejected('verify.sh', '--skip-worktree')

    def test_assume_unchanged_cannot_hide_key(self):
        self.assert_index_flag_rejected('bridge-pubkey.asc', '--assume-unchanged')

    def test_skip_worktree_cannot_hide_key(self):
        self.assert_index_flag_rejected('bridge-pubkey.asc', '--skip-worktree')

    def test_new_copy_uses_lf_even_with_user_autocrlf(self):
        self.git('config', 'core.autocrlf', 'true')
        self.create()
        for name in ('verify.sh', 'bridge-pubkey.asc'):
            self.assertNotIn(b'\r\n', (self.target / name).read_bytes())

    def test_existing_non_worktree_directory_is_preserved(self):
        self.target.mkdir(parents=True)
        note = self.target / 'local.txt'
        note.write_text('keep', encoding='ascii')
        self.assertNotEqual(0, self.prepare().returncode)
        self.assertEqual('keep', note.read_text(encoding='ascii'))

    def test_invalid_or_unavailable_pin_does_not_create_copy(self):
        for pin in ('--invalid', '1' * 40):
            with self.subTest(pin=pin):
                (self.tools / 'BRIDGE_COMMIT').write_text(pin, encoding='ascii')
                self.assertNotEqual(0, self.prepare().returncode)
                self.assertFalse(self.target.exists())


if __name__ == '__main__':
    unittest.main()
