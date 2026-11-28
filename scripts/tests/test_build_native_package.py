import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock


sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
SPEC = importlib.util.spec_from_file_location('build_native_package', Path(sys.path[0]) / 'build-native-package.py')
native = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(native)


IMAGE = 'registry.example/toolchain@sha256:' + 'a' * 64
SOURCE = b'Quellinhalt\n'
MANIFEST = '''id = "hello"
version = "1.0.0"
release = 1
runtime = "linux-x64-v1"
license = "MIT"
exec = ["usr/bin/hello"]
'''


class BuildNativePackageTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.root = Path(scratch.name)
        self.recipe = self.root / 'hello'
        self.recipe.mkdir()
        (self.recipe / 'build.sh').write_text('#!/bin/sh\n', encoding='utf-8')
        (self.recipe / 'manifest.toml').write_text(MANIFEST, encoding='utf-8')
        (self.recipe / 'hello.S').write_bytes(b'Quelldatei\n')
        (self.recipe / 'privat.pem').write_text('Nicht in den Container übernehmen.\n', encoding='utf-8')
        (self.recipe / 'patches').mkdir()
        (self.recipe / 'patches' / 'change.patch').write_bytes(b'Patchinhalt\n')

    def write_recipe(self, image=IMAGE, source_name='hello.tar.gz', digest=None):
        sha256 = digest or hashlib.sha256(SOURCE).hexdigest()
        (self.recipe / 'package.toml').write_text(
            f'image = "{image}"\nlocal = ["hello.S"]\n[[source]]\nname = "{source_name}"\n'
            'url = "https://example.test/hello.tar.gz"\n'
            f'sha256 = "{sha256}"\n', encoding='utf-8')

    def run_container(self, command, *, check):
        self.assertTrue(check)
        self.assertEqual(command[:2], ['podman', 'run'])
        self.assertIn('--network=none', command)
        self.assertIn('--pull=never', command)
        self.assertIn('--read-only', command)
        self.assertEqual(command[-4:], ['--entrypoint', '/bin/sh', IMAGE, '/recipe/build.sh'])
        mounts = [command[index + 1] for index, argument in enumerate(command) if argument == '--volume']
        self.assertEqual(len(mounts), 3)
        self.assertTrue(mounts[0].endswith(':/recipe:ro'))
        self.assertTrue(mounts[1].endswith(':/sources:ro'))
        recipe = Path(mounts[0].removesuffix(':/recipe:ro'))
        self.assertEqual({p.name for p in recipe.iterdir()},
                         {'package.toml', 'manifest.toml', 'build.sh', 'hello.S', 'patches'})
        sources = Path(mounts[1].removesuffix(':/sources:ro'))
        self.assertEqual((sources / 'hello.tar.gz').read_bytes(), SOURCE)
        output = Path(mounts[2].removesuffix(':/output:rw'))
        executable = output / 'files' / 'usr' / 'bin' / 'hello'
        executable.parent.mkdir(parents=True)
        executable.write_bytes(b'fixture\n')

    def test_repeated_builds_produce_the_same_packaging_inputs(self):
        self.write_recipe()
        first, second = self.root / 'one', self.root / 'two'
        with mock.patch.object(native, 'open_source', side_effect=lambda url: io.BytesIO(SOURCE)), \
                mock.patch.object(native.subprocess, 'run', side_effect=self.run_container):
            native.build(self.recipe, first, 'podman')
            native.build(self.recipe, second, 'podman')
        first_files = {p.relative_to(first): p.read_bytes() for p in first.rglob('*') if p.is_file()}
        second_files = {p.relative_to(second): p.read_bytes() for p in second.rglob('*') if p.is_file()}
        self.assertEqual(first_files, second_files)
        self.assertEqual((first / 'manifest.toml').read_text(encoding='utf-8'), MANIFEST)
        info = json.loads((first / 'build-info.json').read_text(encoding='utf-8'))
        self.assertEqual(info['image'], IMAGE)
        self.assertEqual(info['license'], 'MIT')
        self.assertEqual(info['files']['usr/bin/hello'], hashlib.sha256(b'fixture\n').hexdigest())
        self.assertEqual(info['recipe']['patches/change.patch'], hashlib.sha256(b'Patchinhalt\n').hexdigest())
        self.assertNotIn('privat.pem', info['recipe'])

    def test_hash_mismatch_never_starts_a_build(self):
        self.write_recipe(digest='0' * 64)
        output = self.root / 'output'
        with mock.patch.object(native, 'open_source', return_value=io.BytesIO(SOURCE)), \
                mock.patch.object(native.subprocess, 'run') as run:
            with self.assertRaisesRegex(native.package2.PackageError, 'SHA-256'):
                native.build(self.recipe, output, 'podman')
        run.assert_not_called()
        self.assertFalse(output.exists())

    def test_failed_build_leaves_no_output_and_existing_output_is_preserved(self):
        self.write_recipe()
        output = self.root / 'output'
        with mock.patch.object(native, 'open_source', return_value=io.BytesIO(SOURCE)), \
                mock.patch.object(native.subprocess, 'run', side_effect=subprocess.CalledProcessError(42, ['podman'])):
            with self.assertRaises(subprocess.CalledProcessError):
                native.build(self.recipe, output, 'podman')
        self.assertFalse(output.exists())
        output.mkdir()
        with mock.patch.object(native, 'open_source') as download:
            with self.assertRaises(native.package2.PackageError):
                native.build(self.recipe, output, 'podman')
        download.assert_not_called()
        self.assertTrue(output.is_dir())

    def test_imprecise_image_and_unsafe_source_name_are_rejected(self):
        with mock.patch.object(native, 'open_source') as download:
            for fields in ({'image': 'registry.example/toolchain:latest'}, {'source_name': '../escape'}):
                with self.subTest(fields=fields):
                    self.write_recipe(**fields)
                    with self.assertRaises(native.package2.PackageError):
                        native.build(self.recipe, self.root / 'output', 'podman')
        download.assert_not_called()

    def test_interrupt_removes_container_before_releasing_scratch(self):
        self.write_recipe()
        output = self.root / 'output'
        name = None

        def interrupted(command, **options):
            nonlocal name
            self.assertTrue(list(self.root.glob('.native-*')))
            if command[1] == 'run':
                name = command[command.index('--name') + 1]
                raise KeyboardInterrupt
            self.assertEqual(command, ['podman', 'rm', '--force', '--ignore', name])
            return subprocess.CompletedProcess(command, 0, '', '')

        with mock.patch.object(native, 'open_source', return_value=io.BytesIO(SOURCE)), \
                mock.patch.object(native.subprocess, 'run', side_effect=interrupted) as run:
            with self.assertRaises(KeyboardInterrupt):
                native.build(self.recipe, output, 'podman')
        self.assertEqual(run.call_count, 2)
        self.assertFalse(list(self.root.glob('.native-*')))
        self.assertFalse(output.exists())

    def test_declared_pem_never_reaches_the_container(self):
        self.write_recipe()
        (self.recipe / 'hello.S').write_bytes(b'-----BEGIN PRIVATE KEY-----\n')
        with mock.patch.object(native, 'open_source') as download, mock.patch.object(native.subprocess, 'run') as run:
            with self.assertRaisesRegex(native.package2.PackageError, 'privaten Schluessel'):
                native.build(self.recipe, self.root / 'output', 'podman')
        download.assert_not_called()
        run.assert_not_called()

    def test_unreadable_output_directory_cannot_be_published(self):
        self.write_recipe()
        output = self.root / 'output'
        original = Path.iterdir

        def deny_directory(path):
            if path.name == 'usr' and path.parent.name == 'files':
                raise PermissionError('Test: Paket-Unterverzeichnis unlesbar')
            return original(path)

        with mock.patch.object(native, 'open_source', return_value=io.BytesIO(SOURCE)), \
                mock.patch.object(native.subprocess, 'run', side_effect=self.run_container), \
                mock.patch.object(Path, 'iterdir', deny_directory):
            with self.assertRaises(PermissionError):
                native.build(self.recipe, output, 'podman')
        self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
