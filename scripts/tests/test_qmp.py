import importlib.util
import io
from pathlib import Path
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('guest_test', SCRIPTS / 'test-wasm-doom-desktop.py')
guest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guest)


class FakeStream(io.BytesIO):
    """Nimmt Befehle an und liefert vorgegebene Antworten oder einen Fehler."""

    def __init__(self, replies: bytes = b'', error: Exception | None = None):
        super().__init__(replies)
        self.error = error

    def write(self, data):
        return len(data)

    def readline(self, *args):
        if self.error:
            raise self.error
        return super().readline(*args)


def qmp(stream: FakeStream):
    # Ohne __init__, damit kein QEMU noetig ist.
    instance = object.__new__(guest.Qmp)
    instance.stream = stream
    return instance


class QmpQuitTests(unittest.TestCase):
    def test_quit_accepts_reset(self):
        # Unter Linux mit QEMU 10.2 in 39 von 40 Laeufen beobachtet.
        qmp(FakeStream(error=ConnectionResetError(104, 'Connection reset by peer'))).quit()

    def test_quit_accepts_end_of_stream(self):
        qmp(FakeStream()).quit()

    def test_quit_accepts_return(self):
        qmp(FakeStream(b'{"return": {}}\n')).quit()

    def test_quit_reports_qmp_error(self):
        with self.assertRaises(RuntimeError):
            qmp(FakeStream(b'{"error": {"class": "GenericError"}}\n')).quit()

    def test_other_commands_report_end_of_stream(self):
        with self.assertRaises(ConnectionError):
            qmp(FakeStream()).command('stop')
        with self.assertRaises(ConnectionResetError):
            qmp(FakeStream(error=ConnectionResetError())).command('stop')


if __name__ == '__main__':
    unittest.main()
