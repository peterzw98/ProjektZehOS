"""Schuetzt die Regel fuer den self-hosted Runner: das Repository ist oeffentlich, Fork-PRs duerfen nie auf Peters Rechner."""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / '.github' / 'workflows'
# Nur die beiden QEMU-Jobs laufen auf dem Runner MSI (Entscheid des Koordinators 19:21); alles andere bleibt auf GitHub-Runnern.
HEAVY = ('guest', 'guest-recovery')
GUARD = "github.event.pull_request.head.repo.full_name == github.repository"


def jobs(text):
    """Zerlegt den Abschnitt jobs: in {Name: Text}; Jobs stehen mit zwei Leerzeichen eingerueckt."""
    body = text[text.index('\njobs:\n'):]
    parts = re.split(r'\n  ([a-z][a-z-]*):\n', body)
    return {parts[i]: parts[i + 1] for i in range(1, len(parts), 2)}


def runs_on(job):
    return re.search(r'^    runs-on: (.*)$', job, re.MULTILINE).group(1)


class CiRunnerPolicy(unittest.TestCase):
    def setUp(self):
        self.jobs = jobs((WORKFLOWS / 'ci.yml').read_text(encoding='utf-8'))

    def test_heavy_jobs_use_self_hosted_only_for_own_repository(self):
        for name in HEAVY:
            runner = runs_on(self.jobs[name])
            self.assertIn('self-hosted', runner, name)
            # Ein Fork-PR faellt auf den gehosteten Runner zurueck; Push und manuelle Laeufe sind eigener Code.
            self.assertIn("github.event_name != 'pull_request'", runner, name)
            self.assertIn(GUARD, runner, name)
            self.assertTrue(runner.rstrip().endswith("|| 'ubuntu-24.04' }}"), name)

    def test_other_jobs_never_use_self_hosted(self):
        self.assertIn('kernel', self.jobs)
        for name, job in self.jobs.items():
            if name not in HEAVY:
                self.assertNotIn('self-hosted', runs_on(job), name)

    def test_only_the_probe_workflow_names_self_hosted_besides_ci(self):
        for path in WORKFLOWS.glob('*.yml'):
            if path.name in ('ci.yml', 'runner-probe.yml'):
                continue
            self.assertNotIn('self-hosted', path.read_text(encoding='utf-8'), path.name)

    def test_probe_never_runs_for_pull_requests(self):
        text = (WORKFLOWS / 'runner-probe.yml').read_text(encoding='utf-8')
        triggers = text[text.index('\non:\n'):text.index('\npermissions:')]
        self.assertNotIn('pull_request', triggers)

    def test_heavy_jobs_isolate_and_clean_runner_state(self):
        for name in HEAVY:
            job = self.jobs[name]
            self.assertIn('uses: ./.github/actions/runner-state', job, name)
            self.assertIn('rm -rf "${RUNNER_TEMP:?}/home"', job, name)
            # Die Isolierung steht direkt hinter dem Checkout und vor jeder Installation.
            self.assertLess(job.index('Check out source'), job.index('runner-state'), name)
            for install in ('build-cosmos.sh --global', 'cosmos install', 'setup-dotnet'):
                if install in job:
                    self.assertLess(job.index('runner-state'), job.index(install), name + ': ' + install)

    def test_no_system_package_install_on_self_hosted(self):
        for name in HEAVY:
            job = self.jobs[name]
            if 'apt-get' not in job:
                continue
            # Alle Schritte, die apt/sudo nutzen oder dafuer cachen, laufen nur auf gehosteten Runnern.
            for step in re.split(r'\n      - ', job)[1:]:
                if 'apt-get' in step or 'qemu-apt' in step or 'ImageOS' in step:
                    self.assertIn("runner.environment != 'self-hosted'", step, name)

    def test_action_isolates_home_only_on_self_hosted(self):
        text = (ROOT / '.github' / 'actions' / 'runner-state' / 'action.yml').read_text(encoding='utf-8')
        self.assertIn("if: runner.environment == 'self-hosted'", text)
        for variable in ('HOME=', 'DOTNET_CLI_HOME=', 'NUGET_PACKAGES='):
            self.assertIn(variable, text)
        self.assertNotIn('sudo', text)


if __name__ == '__main__':
    unittest.main()
