from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[2] / "experiments/StorageProbe/matrix.sh"
BASH = shutil.which("bash")
if BASH is None and Path("C:/Program Files/Git/bin/bash.exe").exists():
    BASH = "C:/Program Files/Git/bin/bash.exe"

# Externe Werkzeuge ersetzen: Diese Tests formatieren keine echten Images.
HARNESS = r'''
dotnet() {
    [[ "$1" == build ]] && return 0
    [[ "$FORMAT_STATUS" != 0 ]] && return "$FORMAT_STATUS"
    local next=0
    for argument in "$@"; do
        if [[ "$next" == 1 ]]; then
            (set -o noclobber; : > "$argument") || return 1
            break
        fi
        [[ "$argument" == format ]] && next=1
    done
    printf '{"ok":true}\n'
}
e2fsck() { return "$CHECK_STATUS"; }
dumpe2fs() {
    [[ "$BROKEN_HEADER" == 1 ]] && return 1
    [[ "$BROKEN_HEADER" == 2 ]] && return 0
    printf 'Block count: 1024\nBlocks per group: 2048\n'
}
export -f dotnet e2fsck dumpe2fs
"$BASH" "$1" "$2"
'''


@unittest.skipUnless(BASH, "Bash fuer den Linux-Matrixrunner nicht vorhanden")
class StorageMatrixTests(unittest.TestCase):
    def run_matrix(self, check_status=0, format_status=0, broken_header=0):
        directory = tempfile.TemporaryDirectory(dir=Path.cwd())
        self.addCleanup(directory.cleanup)
        base = Path(directory.name)
        sentinel = base / "cosmos-ext2-4194304-1024.img"
        sentinel.write_bytes(b"bestehendes Image erhalten")
        environment = dict(os.environ, CHECK_STATUS=str(check_status),
                           FORMAT_STATUS=str(format_status), BROKEN_HEADER=str(broken_header))
        result = subprocess.run([BASH, "-c", HARNESS, "matrix-test",
                                 os.path.relpath(SCRIPT).replace(os.sep, "/"),
                                 os.path.relpath(base).replace(os.sep, "/")],
                                capture_output=True, text=True, env=environment, check=False,
                                timeout=60)
        self.assertEqual(b"bestehendes Image erhalten", sentinel.read_bytes())
        return result

    def test_clean_matrix_succeeds(self):
        result = self.run_matrix()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(18, result.stdout.count("e2fsck=0"))

    def test_uncorrected_errors_fail_after_reporting_all_cases(self):
        result = self.run_matrix(check_status=4)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertEqual(18, result.stdout.count("e2fsck=4"))

    def test_format_failure_stops_without_deleting_existing_image(self):
        result = self.run_matrix(format_status=3)
        self.assertEqual(1, result.returncode)
        self.assertIn("Formatfehler", result.stderr)

    def test_missing_header_is_reported_as_failure(self):
        for broken_header in (1, 2):
            with self.subTest(broken_header=broken_header):
                result = self.run_matrix(broken_header=broken_header)
                self.assertEqual(1, result.returncode)
                self.assertIn("Header", result.stderr)


if __name__ == "__main__":
    unittest.main()
