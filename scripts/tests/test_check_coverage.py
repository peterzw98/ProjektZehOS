import contextlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'check-coverage.py'
spec = importlib.util.spec_from_file_location('check_coverage', SCRIPT)
coverage = importlib.util.module_from_spec(spec)
spec.loader.exec_module(coverage)

ROOT = 'C:\\r\\ProjektZeh\\ProjektZeh\\'


def build_xml(classes):
    parts = ['<coverage><packages><package><classes>']
    for filename, lines in classes:
        parts.append(f'<class filename="{filename}"><lines>')
        parts.extend(f'<line number="{number}" hits="{hits}"/>' for number, hits in lines.items())
        parts.append('</lines></class>')
    parts.append('</classes></package></packages></coverage>')
    return ''.join(parts)


def lines(hit, missed):
    return {**{n: 1 for n in range(1, hit + 1)}, **{n: 0 for n in range(hit + 1, hit + missed + 1)}}


class CheckCoverageTests(unittest.TestCase):
    def run_main(self, classes, *options, exclusions=None, summary=None, raw_xml=None):
        with tempfile.TemporaryDirectory() as directory:
            report = Path(directory) / 'report.xml'
            report.write_text(raw_xml if raw_xml is not None else build_xml(classes), encoding='utf-8')
            arguments = [str(report), *options]
            if exclusions is not None:
                path = Path(directory) / 'exclusions.txt'
                path.write_text(exclusions, encoding='utf-8')
                arguments += ['--exclusions', str(path)]
            if summary:
                summary_path = Path(directory) / 'summary.md'
                arguments += ['--summary', str(summary_path)]
            out, err = io.StringIO(), io.StringIO()
            with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
                code = coverage.main(arguments)
            text = summary_path.read_text(encoding='utf-8') if summary else None
            return code, out.getvalue(), err.getvalue(), text

    def test_line_hit_in_any_class_counts_once(self):
        classes = [(ROOT + 'Shell\\A.cs', {5: 0}), (ROOT + 'Shell\\A.cs', {5: 3})]
        code, out, _, _ = self.run_main(classes, '--min-total', '100')
        self.assertEqual(0, code)
        self.assertIn('(1/1 Zeilen)', out)

    def test_only_product_sources_count(self):
        classes = [(ROOT + 'Shell\\A.cs', lines(2, 0)),
                   ('C:\\r\\ProjektZeh\\ProjektZeh.Tests\\T.cs', lines(0, 3)),
                   ('C:\\other\\B.cs', lines(0, 3))]
        _, out, _, _ = self.run_main(classes)
        self.assertIn('(2/2 Zeilen)', out)

    def test_total_below_minimum_fails(self):
        code, out, _, _ = self.run_main([(ROOT + 'Shell\\A.cs', lines(8, 2))], '--min-total', '90')
        self.assertEqual(1, code)
        self.assertIn('80.0', out)

    def test_file_below_minimum_fails_and_is_named(self):
        code, out, _, _ = self.run_main([(ROOT + 'Shell\\A.cs', lines(1, 9))], '--min-file', '50')
        self.assertEqual(1, code)
        self.assertIn('Shell/A.cs', out)

    def test_linux_runner_path_is_reduced_to_project_relative_name(self):
        runner = '/home/runner/work/ProjektZehOS/ProjektZehOS/ProjektZeh/Shell/A.cs'
        code, out, _, _ = self.run_main([(runner, lines(1, 9))], '--min-file', '50')
        self.assertEqual(1, code)
        self.assertIn('Unter Grenze: Shell/A.cs', out)

    def test_failure_message_shows_why_rounded_values_differ(self):
        # 91,96 % wird als 92.0 % gerundet; die Fehlzeile muss zeigen, dass der Wert trotzdem unter 92 liegt.
        code, out, _, _ = self.run_main([(ROOT + 'Shell\\A.cs', lines(2299, 201))], '--min-total', '92')
        self.assertEqual(1, code)
        self.assertIn('91.96', out)

    def test_exclusion_without_matching_file_warns(self):
        code, _, err, _ = self.run_main([(ROOT + 'Shell\\A.cs', lines(1, 0))], exclusions='Shell/Alt.cs # umbenannt\n')
        self.assertEqual(0, code)
        self.assertIn('Shell/Alt.cs', err)

    def test_sources_lists_product_files_missing_from_report(self):
        with tempfile.TemporaryDirectory() as directory:
            sources = Path(directory) / 'ProjektZeh'
            for name in ('Shell/A.cs', 'Storage/CosmosDisk.cs', 'obj/Generated.cs', 'bin/Copy.cs'):
                (sources / name).parent.mkdir(parents=True, exist_ok=True)
                (sources / name).write_text('', encoding='utf-8')
            code, out, _, text = self.run_main([(ROOT + 'Shell\\A.cs', lines(1, 0))], '--sources', str(sources), summary=True)
        self.assertEqual(0, code)
        self.assertIn('Ohne Messung: 1 von 2 Quelldateien', out)
        self.assertIn('Storage/CosmosDisk.cs', text)
        self.assertNotIn('Generated.cs', text)

    def test_excluded_file_is_ignored(self):
        classes = [(ROOT + 'Shell\\A.cs', lines(1, 9)), (ROOT + 'Shell\\B.cs', lines(10, 0))]
        code, _, _, _ = self.run_main(classes, '--min-file', '50', exclusions='Shell/A.cs # Konsole\n')
        self.assertEqual(0, code)

    def test_exclusion_without_reason_is_rejected(self):
        code, _, err, _ = self.run_main([(ROOT + 'Shell\\A.cs', lines(1, 0))], exclusions='Shell/A.cs\n')
        self.assertEqual(2, code)
        self.assertTrue(err)

    def test_missing_or_empty_report_fails(self):
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, coverage.main(['gibt-es-nicht.xml']))
        code, _, _, _ = self.run_main([], raw_xml='<coverage></coverage>')
        self.assertEqual(2, code)

    def test_summary_lists_worst_files(self):
        classes = [(ROOT + f'Shell\\F{n:02d}.cs', lines(1, n)) for n in range(12)]
        _, _, _, text = self.run_main(classes, summary=True)
        self.assertIn('| Datei | offen | Abdeckung |', text)
        self.assertIn('Shell/F11.cs', text)
        self.assertNotIn('Shell/F01.cs', text)
        self.assertLess(text.index('Shell/F11.cs'), text.index('Shell/F10.cs'))

    def test_multiple_reports_are_merged(self):
        # Die Hang-Erkennung des Testlaufs legt den Bericht ein zweites Mal ab; mehrere Testprojekte liefern je einen.
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / 'a.xml', Path(directory) / 'b.xml'
            first.write_text(build_xml([(ROOT + 'Shell\\A.cs', {1: 0, 2: 1})]), encoding='utf-8')
            second.write_text(build_xml([(ROOT + 'Shell\\A.cs', {1: 3}), (ROOT + 'Shell\\B.cs', {7: 0})]), encoding='utf-8')
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                code = coverage.main([str(first), str(second)])
        self.assertEqual(0, code)
        self.assertIn('(2/3 Zeilen)', out.getvalue())

    def test_relative_filename_is_accepted(self):
        _, out, _, _ = self.run_main([('ProjektZeh/Shell/A.cs', lines(1, 0))])
        self.assertIn('(1/1 Zeilen)', out)


if __name__ == '__main__':
    unittest.main()
