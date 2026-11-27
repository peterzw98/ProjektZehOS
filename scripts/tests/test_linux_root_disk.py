"""Tests fuer scripts/linux-root-disk.py (Ladybird V28): Buendel aus tar auf eine neue FAT32-Platte.

Die Platte wird mit fsck.fat (dosfstools) und mtools unabhaengig vom Schreiber geprueft; fehlen die Werkzeuge, werden diese
Tests uebersprungen (Windows-Host)."""
import importlib.util
import io
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest

SCRIPTS = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("linux_root_disk", SCRIPTS / "linux-root-disk.py")
lrd = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lrd)

LONG = "x" * 200 + ".so"
TOOLS = all(shutil.which(tool) for tool in ("fsck.fat", "mcopy", "mdir"))


def add_file(archive, name, data=b"", mode=0o755):
    info = tarfile.TarInfo(name)
    info.size, info.mode, info.mtime = len(data), mode, 1764201600
    archive.addfile(info, io.BytesIO(data))


def add_link(archive, name, target, hard=False):
    info = tarfile.TarInfo(name)
    info.type = tarfile.LNKTYPE if hard else tarfile.SYMTYPE
    info.linkname = target
    archive.addfile(info)


def add_dir(archive, name):
    info = tarfile.TarInfo(name)
    info.type = tarfile.DIRTYPE
    archive.addfile(info)


class LinuxRootDiskTests(unittest.TestCase):
    def setUp(self):
        self.temp = Path(tempfile.mkdtemp(prefix="lrd-test-"))
        self.addCleanup(shutil.rmtree, self.temp)

    def bundle(self, fill) -> Path:
        path = self.temp / "b.tar.gz"
        with tarfile.open(path, "w:gz") as archive:
            fill(archive)
        return path

    def build(self, fill, **options):
        image = self.temp / "disk.img"
        result = lrd.build(self.bundle(fill), image, cluster_size=512, free_mib=1, work=self.temp, **options)
        return image, result

    def ladybird_like(self, archive):
        add_dir(archive, "lb-99fb/")
        add_file(archive, "lb-99fb/bin/Ladybird", b"\x7fELF hub" * 1000)
        add_file(archive, "lb-99fb/libexec/WebContent", b"\x7fELF wc")
        add_file(archive, "lb-99fb/lib/libc.musl-x86_64.so.1", b"musl")
        add_link(archive, "lb-99fb/lib/ld-musl-x86_64.so.1", "libc.musl-x86_64.so.1")
        add_file(archive, "lb-99fb/lib/libstdc++.so.6.0.33", b"stdc" * 3000)
        add_link(archive, "lb-99fb/lib/libstdc++.so.6", "libstdc++.so.6.0.33")
        add_link(archive, "lb-99fb/lib/libgcc_s.so.1", "/usr/lib/libgcc_s.so.1")
        add_file(archive, "lb-99fb/usr/lib/libgcc_s.so.1", b"gcc_s")
        add_file(archive, "lb-99fb/share/fonts/notiz.txt", b"klein")
        add_file(archive, "lb-99fb/share/fonts/" + LONG, b"lang")
        add_link(archive, "lb-99fb/etc/fonts", "../share/fonts")
        add_file(archive, "lb-99fb/share/leer", b"")
        add_link(archive, "lb-99fb/bin/hart", "lb-99fb/bin/Ladybird", hard=True)

    def test_links_resolved_and_tree_matches(self):
        image, result = self.build(self.ladybird_like, strip=1)
        self.assertEqual(result["links"], 4)
        self.assertEqual(len(result["longest_name"]), len(LONG))
        if not TOOLS:
            self.skipTest("fsck.fat/mtools fehlen")
        part = self.temp / "part.img"
        with image.open("rb") as source, part.open("wb") as target:
            source.seek(result["partition_offset"])
            shutil.copyfileobj(source, target)
        check = subprocess.run(["fsck.fat", "-n", "-v", str(part)], capture_output=True, text=True)
        self.assertEqual(check.returncode, 0, check.stdout + check.stderr)
        self.assertIn("32 bit entries", check.stdout)
        out = self.temp / "out"
        subprocess.run(["mcopy", "-s", "-n", "-i", f"{image}@@{result['partition_offset']}", "::lb", str(out)],
                       check=True, capture_output=True)
        root = out  # mcopy -s ::lb out legt den Inhalt von lb direkt nach out
        self.assertEqual((root / "lib/ld-musl-x86_64.so.1").read_bytes(), b"musl")
        self.assertEqual((root / "lib/libstdc++.so.6").read_bytes(), b"stdc" * 3000)
        self.assertEqual((root / "lib/libgcc_s.so.1").read_bytes(), b"gcc_s")
        self.assertEqual((root / "etc/fonts" / LONG).read_bytes(), b"lang")
        self.assertEqual((root / "bin/hart").read_bytes(), b"\x7fELF hub" * 1000)
        self.assertEqual((root / "share/leer").read_bytes(), b"")
        self.assertFalse((root / "lib/ld-musl-x86_64.so.1").is_symlink())
        # Kleinschreibung bleibt erhalten (langer Eintrag statt NT-Flag, COSMOS_SUPPORT.md).
        listing = subprocess.run(["mdir", "-b", "-/", "-i", f"{image}@@{result['partition_offset']}", "::lb/share/fonts"],
                                 check=True, capture_output=True, text=True).stdout
        self.assertIn("notiz.txt", listing)
        self.assertNotIn("NOTIZ.TXT", listing)

    def test_dangling_link_fails_or_is_skipped(self):
        def fill(archive):
            add_file(archive, "bin/a", b"a")
            add_link(archive, "lib/weg.so", "nicht-da.so")
        with self.assertRaises(lrd.BundleError) as caught:
            self.build(fill)
        self.assertIn("Link ohne Ziel: /lib/weg.so", caught.exception.problems[0])
        self.assertFalse((self.temp / "disk.img").exists())
        _, result = self.build(fill, skip_dangling=True)
        self.assertTrue(any("weg.so" in w for w in result["warnings"]))

    def test_directory_link_cycle_fails(self):
        def fill(archive):
            add_file(archive, "a/b/f", b"f")
            add_link(archive, "a/b/zurueck", "..")
        with self.assertRaises(lrd.BundleError) as caught:
            self.build(fill)
        self.assertIn("Kreis", caught.exception.problems[0])

    def test_name_problems_are_all_listed(self):
        def fill(archive):
            add_file(archive, "share/Font.ttf", b"1")
            add_file(archive, "share/font.TTF", b"2")
            add_file(archive, "share/" + "y" * 256, b"3")
            add_file(archive, "share/frage?", b"4")
            add_file(archive, "share/punkt.", b"5")
            add_file(archive, "share/.zehos-x", b"6")
        with self.assertRaises(lrd.BundleError) as caught:
            self.build(fill)
        text = "\n".join(caught.exception.problems)
        for expected in ("Schreibweise", "UTF-16", "verbotene Zeichen", "am Ende", ".zehos"):
            self.assertIn(expected, text)

    def test_unsafe_member_and_existing_image_rejected(self):
        with self.assertRaises(lrd.BundleError):
            self.build(lambda archive: add_file(archive, "../raus", b"x"))
        image = self.temp / "disk.img"
        image.write_bytes(b"vorhanden")
        with self.assertRaises(lrd.BundleError):
            lrd.build(self.bundle(lambda a: add_file(a, "bin/a", b"a")), image)
        self.assertEqual(image.read_bytes(), b"vorhanden")

    def test_add_file_overrides_bundle(self):
        page = self.temp / "static.html"
        page.write_text("<p>hi</p>")
        _, result = self.build(lambda a: add_file(a, "bin/a", b"a"), adds=[f"/www/static.html={page}"])
        self.assertEqual(result["files"], 2)

    def test_entry_below_link_or_file_fails(self):
        def fill(archive):
            add_file(archive, "usr/lib/a.so", b"a")
            add_link(archive, "lib", "usr/lib")
            add_file(archive, "lib/x.so", b"x")
        with self.assertRaises(lrd.BundleError) as caught:
            self.build(fill)
        self.assertIn("Eintrag unter Link /lib wird nie erreicht: /lib/x.so", caught.exception.problems)
        page = self.temp / "p.html"
        page.write_text("p")
        with self.assertRaises(lrd.BundleError) as caught:
            self.build(lambda a: add_file(a, "bin/a", b"a"), adds=[f"/bin/a/p.html={page}"])
        self.assertIn("Datei /bin/a", caught.exception.problems[0])
        self.assertFalse((self.temp / "disk.img").exists())

    def test_non_utf8_name_is_reported(self):
        path = self.temp / "b.tar"
        with tarfile.open(path, "w", format=tarfile.GNU_FORMAT, encoding="latin-1") as archive:
            add_file(archive, "share/k\xe4se", b"k")
        with self.assertRaises(lrd.BundleError) as caught:
            lrd.build(path, self.temp / "disk.img", cluster_size=512, free_mib=1, work=self.temp)
        self.assertIn("Name ist kein UTF-8: /share/k\\xe4se", caught.exception.problems)

    def test_failed_write_leaves_no_image(self):
        original = lrd.write_fat32

        def broken(tree, image, **options):
            image.write_bytes(b"halb")
            raise OSError("Platte voll")
        lrd.write_fat32 = broken
        self.addCleanup(setattr, lrd, "write_fat32", original)
        with self.assertRaises(OSError):
            self.build(lambda a: add_file(a, "bin/a", b"a"))
        self.assertFalse((self.temp / "disk.img").exists())
        lrd.write_fat32 = original
        self.build(lambda a: add_file(a, "bin/a", b"a"))
        self.assertTrue((self.temp / "disk.img").exists())


if __name__ == "__main__":
    unittest.main()
