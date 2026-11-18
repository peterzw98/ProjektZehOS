"""Echte Testsignaturen prÃ¼fen, ohne den privaten Bridge-SchlÃ¼ssel oder PrÃ¼f-Ausnahmen."""
import asyncio
import base64
import datetime
import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import types
import unittest
from unittest.mock import AsyncMock, Mock, patch

ROOT = Path(__file__).resolve().parents[2]
GPG = shutil.which('gpg')
if not GPG and Path('C:/Program Files/Git/usr/bin/gpg.exe').exists():
    GPG = 'C:/Program Files/Git/usr/bin/gpg.exe'


@unittest.skipUnless(GPG, 'GPG wird fÃ¼r echte Testsignaturen benÃ¶tigt')
class BridgeSignatureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.home = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls.home.cleanup)
        cls.gpg_home = Path(cls.home.name).as_posix()
        cygpath = Path(GPG).with_name('cygpath.exe')
        if os.name == 'nt' and cygpath.exists():
            cls.gpg_home = subprocess.check_output([str(cygpath), '-u', cls.home.name], text=True).strip()
        cls.environment = dict(os.environ, GNUPGHOME=cls.gpg_home)
        cls.environment['PATH'] = str(Path(GPG).parent) + os.pathsep + cls.environment.get('PATH', '')
        cls.addClassCleanup(cls.stop_agent)
        generated = subprocess.run([GPG, '--batch', '--pinentry-mode', 'loopback', '--passphrase', '',
                        '--quick-generate-key', 'Bridge split tests <bridge-tests@example.invalid>',
                        'ed25519', 'sign', '1d'], env=cls.environment, capture_output=True, timeout=30)
        if generated.returncode:
            raise RuntimeError(generated.stderr.decode(errors="replace"))
        os.environ.setdefault('IRC_PASS', 'x')
        os.environ.setdefault('DISCORD_GUILD_ID', '1')
        sys.modules.setdefault('discord', types.SimpleNamespace(Client=object))
        spec = importlib.util.spec_from_file_location('bridge_signature_test', ROOT / 'deploy/discord-bridge/bridge.py')
        cls.bridge = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.bridge)
        cls.bridge.SIGN_KEY = 'bridge-tests@example.invalid'

    @classmethod
    def stop_agent(cls):
        command = shutil.which('gpgconf', path=cls.environment['PATH'])
        if command:
            subprocess.run([command, '--kill', 'gpg-agent'], env=cls.environment,
                           capture_output=True, timeout=10)

    def sign(self, payload):
        with patch.dict(os.environ, self.environment):
            return asyncio.run(self.bridge.sign(payload))

    def verify(self, payload, signature):
        path = Path(self.home.name) / 'part.sig'
        path.write_bytes(base64.b64decode(signature))
        return subprocess.run([GPG, '--batch', '--verify', self.gpg_home + '/part.sig', '-'], input=payload,
                              env=self.environment, capture_output=True, timeout=30).returncode

    def test_each_part_is_signed_and_cannot_be_reassembled_or_relabelled(self):
        parts = self.bridge.irc_lines('agent-x: first\nsecond\nthird', 123)
        payloads = [self.bridge.signed_payload('#general', 1, 2, part) for part in parts]
        signatures = [self.sign(payload) for payload in payloads]
        for payload, signature in zip(payloads, signatures):
            self.assertEqual(0, self.verify(payload, signature))
            self.assertNotEqual(0, self.verify(payload + b'x', signature))
        self.assertNotEqual(0, self.verify(payloads[1], signatures[0]))
        self.assertNotEqual(0, self.verify(b'\n'.join(payloads), signatures[0]))
        for changed in (self.bridge.signed_payload('#second', 1, 2, parts[0]),
                        self.bridge.signed_payload('#general', 3, 2, parts[0]),
                        self.bridge.signed_payload('#general', 1, 3, parts[0]),
                        self.bridge.signed_payload('#general', 1, 2, parts[0].replace('m=123', 'm=124'))):
            self.assertNotEqual(0, self.verify(changed, signatures[0]))

    def test_long_rsa_signature_is_rejected_before_any_send(self):
        subprocess.run([GPG, '--batch', '--pinentry-mode', 'loopback', '--passphrase', '',
                        '--quick-generate-key', 'Bridge RSA tests <bridge-rsa@example.invalid>',
                        'rsa2048', 'sign', '1d'], env=self.environment, capture_output=True, check=True, timeout=30)
        client = object.__new__(self.bridge.Bridge)
        client.channels = {'#general': types.SimpleNamespace(id=1)}
        client.writer = types.SimpleNamespace(write=Mock(), drain=AsyncMock())
        client.message_lock, client.last_message_sent = asyncio.Lock(), None
        message = types.SimpleNamespace(author=types.SimpleNamespace(bot=False, id=1, display_name='Test'),
                                        guild=object(), channel=types.SimpleNamespace(id=1), clean_content='RSA test',
                                        attachments=[], id=123, created_at=datetime.datetime.now(datetime.timezone.utc),
                                        add_reaction=AsyncMock(), reply=AsyncMock())
        with patch.object(self.bridge, 'SIGN_KEY', 'bridge-rsa@example.invalid'):
            signature = self.sign(self.bridge.signed_payload('#general', 1, 2, 'test'))
            self.assertGreater(len(signature), self.bridge.IRC_PAYLOAD_BYTES)
            with patch.dict(os.environ, self.environment):
                asyncio.run(client.on_message(message))
        client.writer.write.assert_not_called()
        message.add_reaction.assert_awaited_once()
        message.reply.assert_awaited_once()

    def test_single_valid_test_part_does_not_bypass_bridge_fingerprint(self):
        shell = shutil.which('sh', path=self.environment['PATH'])
        if not shell:
            self.skipTest('verify.sh braucht sh')
        part = self.bridge.irc_lines('agent-x: first\nsecond', 123)[0]
        stamp = int(time.time())
        signature = self.sign(self.bridge.signed_payload('#general', 1, stamp, part))
        line = f'<Test> {part} [id=1 t={stamp} sig={signature}]\n'
        result = subprocess.run([shell, str(ROOT / 'deploy/discord-bridge/verify.sh'), '#general'],
                                input=line, text=True, env=self.environment, capture_output=True, timeout=30)
        self.assertEqual(1, result.returncode)
        self.assertEqual('INVALID', result.stdout.strip())


if __name__ == '__main__':
    unittest.main()
