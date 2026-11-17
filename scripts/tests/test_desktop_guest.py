import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('desktop_guest', SCRIPTS / 'test-desktop-guest.py')
guest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guest)


def ppm(width, height):
    # Jedes Pixel traegt seine Nummer in allen drei Kanaelen, damit der Ausschnitt eindeutig pruefbar ist.
    pixels = b''.join(bytes([index] * 3) for index in range(width * height))
    return f'P6\n{width} {height}\n255\n'.encode() + pixels


class RegionTests(unittest.TestCase):
    def test_region_crops_rows_and_columns(self):
        # 4x3: Zeilen 1-2, Spalten 1-2 -> Pixel 5, 6, 9, 10.
        self.assertEqual(guest.region(ppm(4, 3), (1, 1, 3, 3)), bytes([5] * 3 + [6] * 3 + [9] * 3 + [10] * 3))

    def test_region_rejects_box_outside_image(self):
        for box in ((0, 0, 5, 3), (0, 0, 4, 4), (2, 0, 2, 3), (-1, 0, 2, 2)):
            with self.subTest(box=box), self.assertRaises(ValueError):
                guest.region(ppm(4, 3), box)


class ErrorLineTests(unittest.TestCase):
    def test_error_lines_finds_only_error_level(self):
        serial = ('[12 ms] Information Desktop: Desktop ist bereit.\n'
                  '[13 ms] Warning Network: Kein Link.\n'
                  '[14 ms] Error Storage: Einbinden fehlgeschlagen.\n')
        self.assertEqual(guest.error_lines(serial), ['[14 ms] Error Storage: Einbinden fehlgeschlagen.'])

    def test_error_lines_ignores_split_debug_lines(self):
        serial = ('[LowLevelMonitor] Error while waiting: retry\n'
                  '[LowLevelMonitor] Acquire BEGIN\n'
                  '[15 ms] Information Shell: Error im Text ist kein Fehler.\n')
        self.assertEqual(guest.error_lines(serial), [])

    def test_missing_data_disk_is_expected_only_in_recovery(self):
        serial = ('[9 ms] Error Storage: Failure: cause=DeviceNotFound message=Keine Datenplatte gefunden.\n'
                  '[10 ms] Error Storage: Failure: cause=ReadFailed message=Lesefehler.\n')
        self.assertEqual(guest.unexpected_errors(serial, recovery=True),
                         ['[10 ms] Error Storage: Failure: cause=ReadFailed message=Lesefehler.'])
        self.assertEqual(len(guest.unexpected_errors(serial, recovery=False)), 2)


class RecoveryMachineTests(unittest.TestCase):
    def test_without_appdisk_keeps_other_devices(self):
        machine = guest.without_appdisk(guest.tools.MACHINE)
        self.assertFalse(any(value.startswith('ide-hd,drive=appdisk') for value in machine))
        for device in ('ahci,id=ahci', 'e1000e,netdev=net0', 'qemu-xhci,id=tabletbus'):
            self.assertIn(device, machine)
        self.assertEqual(len(machine), len(guest.tools.MACHINE) - 2)

    def test_without_appdisk_rejects_missing_device(self):
        with self.assertRaises(ValueError):
            guest.without_appdisk(['-device', 'ahci,id=ahci'])


class BootTimeoutTests(unittest.TestCase):
    def test_boot_timeout_stops_process_and_keeps_log(self):
        with tempfile.TemporaryDirectory() as directory:
            work = Path(directory)
            processes = []

            def start_vm(port):
                # Attrappe statt QEMU: schreibt ein serielles Protokoll ohne Marke und oeffnet kein QMP.
                (work / 'serial.log').write_text('[1 ms] Information Kernel: Start.\n')
                process = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)'])
                processes.append(process)
                return process

            started = time.monotonic()
            with self.assertRaises(TimeoutError):
                guest.run(work, start_vm, recovery=False, boot_timeout=2)
            self.assertLess(time.monotonic() - started, 30)
            self.assertIsNotNone(processes[0].poll(), 'Attrappenprozess laeuft noch')
            self.assertTrue((work / 'serial.log').is_file())


if __name__ == '__main__':
    unittest.main()
