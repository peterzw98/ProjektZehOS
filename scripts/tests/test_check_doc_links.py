import importlib.util
from pathlib import Path
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('check_doc_links', SCRIPTS / 'check-doc-links.py')
links = importlib.util.module_from_spec(spec)
spec.loader.exec_module(links)


class CheckDocLinksTests(unittest.TestCase):
    def run_check(self, files):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name, text in files.items():
                path = root / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(text, encoding='utf-8')
            return links.check(root.resolve())

    def test_existing_file_and_anchor_pass(self):
        problems = self.run_check({'a.md': '[b](docs/b.md#zweiter-abschnitt) [c](#kopf)\n# Kopf\n',
                                   'docs/b.md': '# Titel\n## Zweiter Abschnitt\n'})
        self.assertEqual([], problems)

    def test_missing_file_is_reported_with_line(self):
        problems = self.run_check({'a.md': 'Text\n[x](fehlt.md)\n'})
        self.assertEqual(['a.md:2: Ziel fehlt: fehlt.md'], problems)

    def test_missing_anchor_is_reported(self):
        problems = self.run_check({'a.md': '[x](b.md#nicht-da)\n', 'b.md': '# Da\n'})
        self.assertEqual(['a.md:1: Anker fehlt: b.md#nicht-da'], problems)

    def test_heading_with_code_and_arrow_matches_github_slug(self):
        problems = self.run_check({'a.md': '[x](b.md#3-migration-legacy--root)\n',
                                   'b.md': '## 3. Migration `Legacy` → `Root`\n'})
        self.assertEqual([], problems)

    def test_duplicate_headings_get_numbered_anchors(self):
        problems = self.run_check({'a.md': '[x](b.md#abschnitt-1)\n', 'b.md': '# Abschnitt\n# Abschnitt\n'})
        self.assertEqual([], problems)

    def test_code_blocks_inline_code_external_and_images_are_ignored(self):
        text = '```\n[x](fehlt.md)\n```\n`[y](fehlt.md)` [z](https://example.org/x) ![i](bild.png) [m](mailto:a@b.c)\n'
        self.assertEqual([], self.run_check({'a.md': text}))

    def test_skipped_directories_are_not_scanned(self):
        self.assertEqual([], self.run_check({'ProjektZeh/ThirdParty/x/README.md': '[x](fehlt.md)\n'}))


if __name__ == '__main__':
    unittest.main()
