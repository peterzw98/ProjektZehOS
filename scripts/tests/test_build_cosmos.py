import json
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts' / 'build-cosmos.sh'
VERSION = (ROOT / 'deploy' / 'cosmos' / 'VERSION').read_text().strip()


def plan(*args, feed=None):
    """Ruft das Bauskript mit --print-plan auf; nichts wird gebaut, geklont oder installiert."""
    with tempfile.TemporaryDirectory() as temp:
        arguments = ['bash', str(SCRIPT), '--print-plan', '--feed', feed or temp, *args]
        return subprocess.run(arguments, capture_output=True, text=True, cwd=ROOT)


class BuildCosmosTests(unittest.TestCase):
    def test_version_files_agree(self):
        sdks = json.loads((ROOT / 'global.json').read_text())['msbuild-sdks']
        self.assertEqual(VERSION, sdks['Cosmos.Sdk'])

    def test_workflows_use_source_build_and_pinned_sdk(self):
        sdk = (ROOT / 'deploy' / 'cosmos' / 'SDK').read_text().strip()
        for name in ('ci.yml', 'release.yml'):
            text = (ROOT / '.github' / 'workflows' / name).read_text()
            # Keine Upstream-Werkzeuge mehr: sie wuerden die selbst gebauten Pakete mit fremdem Patcher verarbeiten.
            self.assertNotIn('dotnet tool install --global Cosmos', text, name)
            self.assertEqual(text.count('scripts/build-cosmos.sh --global'), text.count("dotnet-version: '" + sdk + "'"), name)
            if name == 'ci.yml':
                self.assertRegex(text, r'actions/cache@[0-9a-f]{40}')
            else:
                self.assertNotIn('actions/cache', text, 'Release baut Cosmos immer frisch aus dem Commit')
    def test_official_version_needs_explicit_feed(self):
        result = subprocess.run(['bash', str(SCRIPT), '--print-plan', '--version', '3.0.97'], capture_output=True, text=True, cwd=ROOT)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('--feed', result.stderr)
        self.assertEqual(0, plan('--version', '3.0.97').returncode)

    def test_runs_from_repository_root(self):
        with tempfile.TemporaryDirectory() as temp:
            result = subprocess.run(['bash', str(SCRIPT), '--print-plan', '--feed', 'f'], capture_output=True, text=True, cwd=temp)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertTrue((Path(temp) / 'f').is_dir())

    def test_commit_change_requires_version_change(self):
        # Gleiche Version bei anderem Commit liesse den NuGet-Cache still den alten Stand liefern.
        base = subprocess.run(['git', 'merge-base', 'HEAD', 'origin/master'], capture_output=True, text=True, cwd=ROOT)
        if base.returncode != 0:
            self.skipTest('origin/master nicht verfuegbar')
        changed = subprocess.run(['git', 'diff', '--quiet', base.stdout.strip(), '--', 'deploy/cosmos/COMMIT'], cwd=ROOT).returncode != 0
        if changed:
            version_changed = subprocess.run(['git', 'diff', '--quiet', base.stdout.strip(), '--', 'deploy/cosmos/VERSION'], cwd=ROOT).returncode != 0
            self.assertTrue(version_changed, 'deploy/cosmos/COMMIT geaendert, VERSION nicht')

    def test_commit_is_full_sha(self):
        commit = (ROOT / 'deploy' / 'cosmos' / 'COMMIT').read_text().strip()
        self.assertRegex(commit, r'^[0-9a-f]{40}$')

    def test_plan_contains_no_user_level_side_effects(self):
        result = plan()
        self.assertEqual(0, result.returncode, result.stderr)
        for forbidden in ('nuget locals', 'nuget remove source', 'nuget add source', '.nuget/packages', 'postCreateCommand'):
            self.assertNotIn(forbidden, result.stdout)

    def test_plan_is_x64_only(self):
        result = plan()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn('arm64', result.stdout.lower())

    def test_plan_sets_version_and_pathmap(self):
        result = plan()
        self.assertIn(f'VersionPrefix={VERSION}', result.stdout)
        self.assertIn('PathMap=', result.stdout)
        self.assertIn('/home/runner/work/Cosmos/Cosmos', result.stdout)

    def test_trial_version_from_commit(self):
        result = plan('--commit', '0123456789abcdef0123456789abcdef01234567')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('VersionPrefix=3.0.97-zehos.0.g0123456', result.stdout)

    def test_plan_installs_tools_locally_by_default(self):
        local = plan()
        self.assertIn('--tool-path', local.stdout)
        self.assertIn(f'.tools/cosmos-tools/{VERSION}', local.stdout)
        self.assertNotIn('--global', local.stdout)
        global_plan = plan('--global')
        self.assertIn('--global', global_plan.stdout)
        self.assertNotIn('--tool-path', global_plan.stdout)

    def test_no_tools_skips_tool_installation(self):
        result = plan('--no-tools')
        self.assertNotIn('dotnet tool', result.stdout)

    def test_same_version_other_commit_is_refused(self):
        with tempfile.TemporaryDirectory() as feed:
            (Path(feed) / f'{VERSION}.commit').write_text('f' * 40 + '\n')
            result = plan(feed=feed)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('VERSION erhoehen', result.stderr)

    def test_same_version_same_commit_skips_the_build(self):
        commit = (ROOT / 'deploy' / 'cosmos' / 'COMMIT').read_text().strip()
        with tempfile.TemporaryDirectory() as feed:
            (Path(feed) / f'{VERSION}.commit').write_text(commit + '\n')
            result = plan(feed=feed)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertNotIn('dotnet build', result.stdout)
        self.assertIn('dotnet tool install', result.stdout)


if __name__ == '__main__':
    unittest.main()
