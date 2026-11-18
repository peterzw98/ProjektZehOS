"""Versionsableitung aus Git (ProjektZeh.csproj, Ziel ZehOSVersionFromGit): Basissuche und Zaehlung in einem Temp-Repo.

Liest die git-Befehle aus der csproj, damit der Test genau die gebauten Aufrufe prueft.
"""
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ElementTree

CSPROJ = Path(__file__).resolve().parents[2] / "ProjektZeh/ProjektZeh.csproj"


def target_commands() -> tuple[str, str]:
    """Basissuche (git log ... -G) und Zaehlung (git rev-list ...) aus dem Ziel ZehOSVersionFromGit."""
    root = ElementTree.parse(CSPROJ).getroot()
    target = next(item for item in root.iter("Target") if item.get("Name") == "ZehOSVersionFromGit")
    commands = [item.get("Command") for item in target.iter("Exec")]
    base = next(command for command in commands if command.startswith("git log"))
    count = next(command for command in commands if command.startswith("git rev-list"))
    return base, count


def properties() -> dict[str, str]:
    root = ElementTree.parse(CSPROJ).getroot()
    return {item.tag: (item.text or "") for group in root.iter("PropertyGroup") for item in group
            if item.tag in ("ZehOSVersionPrefix", "ZehOSPatchBase")}


@unittest.skipUnless(shutil.which("git"), "git fehlt")
class VersionFromGitTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.repo = Path(self.folder.name)
        self.git("init", "-q", "-b", "master")
        self.git("config", "user.email", "test@example.invalid")
        self.git("config", "user.name", "Test")
        self.base_command, self.count_command = target_commands()

    def tearDown(self):
        self.folder.cleanup()

    def git(self, *arguments: str) -> str:
        return subprocess.run(["git", *arguments], cwd=self.repo, capture_output=True, text=True, check=True).stdout.strip()

    def write(self, text: str, message: str) -> None:
        (self.repo / "ProjektZeh.csproj").write_text(text, encoding="utf-8")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", message)

    def project(self, base: int, comment: str = "", version_line: str = "") -> str:
        return (f"<Project>\n  <!-- {comment} -->\n  <ZehOSVersionPrefix>0.5</ZehOSVersionPrefix>\n"
                f"  <ZehOSPatchBase>{base}</ZehOSPatchBase>\n  {version_line}\n</Project>\n")

    def version_base(self) -> str:
        output = subprocess.run(shlex.split(self.base_command), cwd=self.repo, capture_output=True, text=True,
                                check=True).stdout
        return output.split(" ")[0].strip()

    def commits_since(self, base: str) -> int:
        command = self.count_command.replace("$(ZehOSVersionBase)", base)
        return int(subprocess.run(shlex.split(command), cwd=self.repo, capture_output=True, text=True,
                                  check=True).stdout)

    def test_base_is_the_commit_that_set_the_elements_and_counts_from_there(self):
        self.write(self.project(123), "Basis")
        base = self.git("rev-parse", "HEAD")
        self.assertEqual(self.version_base(), base)
        self.assertEqual(self.commits_since(base), 0)
        (self.repo / "andere.txt").write_text("x", encoding="utf-8")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "andere Datei")
        self.assertEqual(self.version_base(), base)
        self.assertEqual(self.commits_since(base), 1)

    def test_comments_and_property_uses_do_not_reset_the_counter(self):
        # Review P2-1: Zeilen, die die Namen nur erwaehnen, duerfen keine neue Basis setzen.
        self.write(self.project(123), "Basis")
        base = self.git("rev-parse", "HEAD")
        self.write(self.project(123, comment="ZehOSVersionPrefix/ZehOSPatchBase erklaert"), "Kommentar")
        self.write(self.project(123, version_line="<Version>$(ZehOSVersionPrefix).$(ZehOSPatchBase)</Version>"), "Nutzung")
        self.assertEqual(self.version_base(), base)
        self.assertEqual(self.commits_since(base), 2)

    def test_merge_that_changes_the_base_becomes_the_new_base(self):
        self.write(self.project(123), "Basis")
        self.git("checkout", "-q", "-b", "zweig")
        self.write(self.project(0).replace(">0.5<", ">0.6<"), "0.6")
        self.git("checkout", "-q", "master")
        self.git("merge", "-q", "--no-ff", "zweig", "-m", "Merge 0.6")
        merge = self.git("rev-parse", "HEAD")
        self.assertEqual(self.version_base(), merge)
        self.assertEqual(self.commits_since(merge), 0)


class PropertyTests(unittest.TestCase):
    def test_prefix_and_patch_base_have_the_expected_form(self):
        values = properties()
        self.assertRegex(values["ZehOSVersionPrefix"], r"^(0|[1-9]\d*)\.(0|[1-9]\d*)$")
        self.assertRegex(values["ZehOSPatchBase"], r"^(0|[1-9]\d*)$")

    def test_only_the_two_element_lines_match_the_base_pattern(self):
        base, _ = target_commands()
        pattern = re.search(r'-G "([^"]+)"', base).group(1)
        hits = [line.strip() for line in CSPROJ.read_text(encoding="utf-8").splitlines() if re.search(pattern, line)]
        self.assertEqual(len(hits), 2, hits)
        self.assertTrue(all(hit.startswith(("<ZehOSVersionPrefix>", "<ZehOSPatchBase>")) for hit in hits), hits)


if __name__ == "__main__":
    unittest.main()
