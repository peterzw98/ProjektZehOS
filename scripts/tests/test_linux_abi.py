import importlib.util
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("abi", Path(__file__).resolve().parents[1] / "test-linux-abi.py")
abi = importlib.util.module_from_spec(spec)
spec.loader.exec_module(abi)


class ParseTests(unittest.TestCase):
    def test_parse_and_compare(self):
        log = ("boot\n[SCHED] x LINUXRUN BEGIN hello\nLINUXRUN OUT 6869\nLINUXRUN OUT 0a\nLINUXRUN END exit 0\r\n"
               "LINUXRUN BEGIN bad\nLINUXRUN OUT 78\nLINUXRUN END signal 11\nLINUXRUN BEGIN hang\nLINUXRUN OUT 79\n")
        r = abi.parse_log(log)
        self.assertEqual(r["hello"], (b"hi\n", "exit 0"))
        self.assertEqual(r["bad"], (b"x", "signal 11"))
        self.assertEqual(r["hang"], (b"y", None))
        with tempfile.TemporaryDirectory() as d:
            e = Path(d)
            (e / "hello.txt").write_bytes(b"hi\n"); (e / "hello.status").write_text("exit 0\n")
            (e / "bad.txt").write_bytes(b"x"); (e / "bad.status").write_text("exit 0\n")
            (e / "hang.txt").write_bytes(b"y"); (e / "hang.status").write_text("exit 0\n")
            p = abi.compare(r, e, ["hello", "bad", "hang", "gone"])
        self.assertEqual(len(p), 3)
        self.assertIn("bad: Status 'signal 11'", p[0])
        self.assertIn("END fehlt", p[1])
        self.assertIn("Erwartung fehlt", p[2])

    def test_output_difference(self):
        with tempfile.TemporaryDirectory() as d:
            e = Path(d)
            (e / "a.txt").write_bytes(b"1\n2\n"); (e / "a.status").write_text("exit 0\n")
            p = abi.compare({"a": (b"1\n3\n", "exit 0")}, e, ["a"])
        self.assertIn("Zeile 2", p[0])


if __name__ == "__main__":
    unittest.main()
