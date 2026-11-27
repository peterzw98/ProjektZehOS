"""Hosttests für die Archivübergabe vom Ladybird-Demo-Runner an den Plattenbau."""

import gzip
import io
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest


SCRIPTS = Path(__file__).resolve().parents[1]
ABSOLUTE = b"<dir>/share/fonts/dejavu</dir><dir>/share/Lagom/fonts</dir>\n"
CACHEDIR = b"<cachedir>/tmp/fontconfig</cachedir>\n"
RELATIVE = (
    b'<dir prefix="relative">share/fonts/dejavu</dir>'
    b'<dir prefix="relative">share/Lagom/fonts</dir>\n'
)


@unittest.skipUnless(
    os.name == "posix" and shutil.which("bash") and shutil.which("tar"),
    "Bash und tar auf einem POSIX-Host erforderlich",
)
class LadybirdDemoRunTests(unittest.TestCase):
    def setUp(self):
        work = tempfile.TemporaryDirectory()
        self.addCleanup(work.cleanup)
        self.root = Path(work.name)
        self.home = self.root / "home"
        self.bundle = self.home / "ladybird-bundle/ladybird-static-with-images.tar.gz"
        self.bundle.parent.mkdir(parents=True)
        self.runner = self.root / "scripts/ladybird-demo-run.sh"
        self.runner.parent.mkdir()
        self.runner.write_bytes((SCRIPTS / self.runner.name).read_bytes())
        version = self.root / "deploy/cosmos/VERSION"
        version.parent.mkdir(parents=True)
        version.write_text("test\n")
        self.iso = self.root / "test.iso"
        self.iso.touch()
        self.output = self.root / "output"
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.stub("git", "echo test\n")
        self.stub("pgrep", "exit 1\n")
        self.stub("file", "exit 0\n")
        self.stub(
            "python3",
            'case "$1" in\n'
            '  scripts/linux-root-disk.py) printf "%s\\n" "$@" > "$HOME/disk-args" ;;\n'
            '  scripts/test-linux-root-disk-guest.py) printf test > "${@: -1}" ;;\n'
            '  -I) exit 0 ;;\n'
            '  *) exit 1 ;;\n'
            'esac\n',
        )

    def stub(self, name, body):
        path = self.bin / name
        path.write_text("#!/bin/bash\n" + body)
        path.chmod(0o755)

    def make_bundle(self, prefix, fonts, rules=True):
        with tarfile.open(self.bundle, "w:gz") as archive:
            members = {f"{prefix}fonts.conf": fonts}
            if rules:
                members[f"{prefix}conf.d/10-test.conf"] = b"font rule"
            for name, content in members.items():
                member = tarfile.TarInfo(name)
                member.size = len(content)
                archive.addfile(member, io.BytesIO(content))

    def run_demo(self, use_default=True):
        args = [] if use_default else ["-b", str(self.bundle)]
        return subprocess.run(
            ["bash", str(self.runner), "--iso", str(self.iso), "-o", str(self.output), *args],
            env={"HOME": str(self.home), "DOTNET_ROOT": str(self.bin),
                 "PATH": f"{self.bin}{os.pathsep}{os.environ['PATH']}"},
            capture_output=True,
            text=True,
            timeout=15,
        )

    def disk_args(self, result):
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return (self.home / "disk-args").read_text().splitlines()

    def test_prepared_fonts_in_default_bundle(self):
        self.make_bundle("etc/fonts/", ABSOLUTE)
        args = self.disk_args(self.run_demo())
        self.assertIn(str(self.bundle), args)
        self.assertFalse(any(arg.startswith("/etc/fonts/") for arg in args), args)
        self.assertNotIn("--strip", args)

    def test_explicit_prepared_bundle(self):
        self.make_bundle("./etc/fonts/", ABSOLUTE + CACHEDIR)
        result = self.run_demo(use_default=False)
        args = self.disk_args(result)
        self.assertFalse(any(arg.startswith("/etc/fonts/") for arg in args), args)
        self.assertNotIn("WARNUNG", result.stdout)

    @unittest.skipUnless(shutil.which("zstd"), "zstd erforderlich")
    def test_prepared_fonts_under_bundle_directory_in_zstd(self):
        self.make_bundle("./ladybird-static/etc/fonts/", ABSOLUTE + CACHEDIR)
        raw = gzip.decompress(self.bundle.read_bytes())
        self.bundle = self.bundle.with_suffix(".zst")
        subprocess.run(["zstd", "-q", "-o", str(self.bundle)], input=raw, check=True, timeout=15)
        args = self.disk_args(self.run_demo(use_default=False))
        self.assertEqual(args[args.index("--strip") + 1], "1")
        self.assertFalse(any(arg.startswith("/etc/fonts/") for arg in args), args)

    def test_prepared_fonts_with_xdg_cache_are_accepted(self):
        self.make_bundle("./etc/fonts/", ABSOLUTE + b'<cachedir prefix="xdg">fontconfig</cachedir>\n')
        result = self.run_demo()
        self.disk_args(result)
        self.assertNotIn("WARNUNG", result.stdout)

    def test_prepared_fonts_without_cache_warn(self):
        self.make_bundle("./etc/fonts/", ABSOLUTE)
        result = self.run_demo()
        self.disk_args(result)
        self.assertIn("WARNUNG", result.stdout)
        self.assertIn("<cachedir>", result.stdout)

    def test_legacy_relative_fonts(self):
        self.make_bundle("./", RELATIVE)
        args = self.disk_args(self.run_demo(use_default=False))
        self.assertIn(f"/etc/fonts/conf.d/10-test.conf={self.output}/confd/10-test.conf", args)
        written = (self.output / "fonts.conf").read_bytes()
        self.assertIn(b"<dir>/share/fonts/dejavu</dir>", written)
        self.assertIn(b"<cachedir>/tmp/fontconfig</cachedir>", written)

    def test_prepared_fonts_without_rules_are_rejected(self):
        self.make_bundle("./etc/fonts/", ABSOLUTE, rules=False)
        result = self.run_demo()
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("conf.d", result.stdout)
        self.assertFalse((self.home / "disk-args").exists())


if __name__ == "__main__":
    unittest.main()
