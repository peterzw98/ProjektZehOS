import importlib.util
import os
import unittest
from pathlib import Path
from unittest import mock

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('thread_probe', SCRIPTS / 'test-thread-probe.py')
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)

MESSAGES = 'MESSAGES_PROBE PASS numbered=100 consumed=100 duplicates=0 order=ok close/cancel/timeout/locks=ok\n'


class ThreadProbeLogTests(unittest.TestCase):
    def test_stage_passes_are_not_the_final_pass(self):
        self.assertEqual(probe.classify('THREAD_PROBE Scheduler=ZehOS Klassen; Tick=10000000ns\n'
                                        'THREAD_PROBE preemption PASS first=3 second=3; sleep/stop/join PASS\n'), 'RUNNING')

    def test_pass_needs_both_probes(self):
        self.assertEqual(probe.classify(MESSAGES + 'THREAD_PROBE PASS\n'), 'PASS')
        self.assertEqual(probe.classify('THREAD_PROBE PASS\n'), 'FAIL')

    def test_marker_after_interleaved_smp_output_counts(self):
        # Unter SMP schreibt Cosmos manche Zeilen in mehreren Aufrufen; eine fremde Teilzeile steht dann vor der Marke.
        self.assertEqual(probe.classify(MESSAGES + '[SCHED] Running thread THREAD_PROBE PASS\n7\n'), 'PASS')
        self.assertEqual(probe.classify(MESSAGES + 'THREAD_PROBE PASS\r\n'), 'PASS')
        self.assertEqual(probe.classify('[SCHED] Thread THREAD_PROBE FAIL x\n'), 'FAIL')

    def test_any_fail_line_is_fail(self):
        self.assertEqual(probe.classify('THREAD_PROBE FAIL Zeitlimit\n'), 'FAIL')
        self.assertEqual(probe.classify('MESSAGES_PROBE FAIL order\nTHREAD_PROBE PASS\n'), 'FAIL')

    def test_command_matches_ps1_and_honours_smp(self):
        with mock.patch.dict(os.environ, {'ZEHOS_QEMU_SMP': '4,cores=2,threads=2'}):
            arguments = probe.command('/q/bin/qemu-system-x86_64', Path('/x.iso'), Path('/s.log'), 'tcg')
        self.assertEqual(arguments[-2:], ['-smp', '4,cores=2,threads=2'])
        for expected in (['-cpu', 'max'], ['-m', '512M'], ['-machine', 'q35'], ['-cdrom', '/x.iso']):
            self.assertIn(expected, [arguments[i:i + 2] for i in range(len(arguments) - 1)])
        self.assertIn('shutdown=poweroff,reboot=reset', arguments)
        with mock.patch.dict(os.environ, {'ZEHOS_QEMU_SMP': ''}):
            self.assertNotIn('-smp', probe.command('/q', Path('/x.iso'), Path('/s.log'), 'kvm'))


if __name__ == '__main__':
    unittest.main()
