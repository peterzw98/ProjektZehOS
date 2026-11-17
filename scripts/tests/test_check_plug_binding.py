import importlib.util
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / 'check-plug-binding.py'
spec = importlib.util.spec_from_file_location('check_plug_binding', SCRIPT)
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)

PLUG_SOURCE = '''namespace ProjektZeh.Storage;

[Plug(TargetName = "Cosmos.Kernel.System.FileSystem.Ext2.Ext2DirectoryHelper")]
public static class RemovePlug
{
    [PlugMember]
    public static bool RemoveEntry(object sb, object dir, string name) => false;
}

internal static class Helper
{
    public static void NotAPlug() { }
}
'''

# Ausschnitt aus einem echten Log von `cosmos build --verbose`, samt Umbruch am Konsolenrand.
RESOLVED = '''             [DEBUG] [ResolveAndPatchMethod] Starting method resolution for
System.Boolean
ProjektZeh.Storage.RemovePlug::RemoveEntry(Cosmos.Kernel.System.FileSystem.Ext2.
Ext2Superblock,System.String)
             [DEBUG] [ResolveMethod] Found matching method: System.Boolean
'''
MISSED = '''             [DEBUG] [ResolveAndPatchMethod] Starting method resolution for
System.Boolean
ProjektZeh.Storage.RemovePlug::RemoveEntry(System.Object,System.Object,System.St
ring)
             [WARN] [ResolveMethod] Target method not found: RemoveEntry
'''
FOREIGN_MISS = '''             [DEBUG] [ResolveAndPatchMethod] Starting method resolution for
System.Void Cosmos.Kernel.Plugs.System.AppContextPlug::EnsureInitialized()
             [WARN] [ResolveMethod] Target method not found: EnsureInitialized
'''


class PlugBindingTests(unittest.TestCase):
    def run_gate(self, log_text):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'src').mkdir()
            (root / 'src' / 'RemovePlug.cs').write_text(PLUG_SOURCE, encoding='utf-8')
            log = root / 'kernel-build.log'
            log.write_text(log_text, encoding='utf-8')
            return gate.main([str(log), str(root / 'src')])

    def test_expected_plugs_come_from_plug_classes_only(self):
        with tempfile.TemporaryDirectory() as directory:
            (Path(directory) / 'RemovePlug.cs').write_text(PLUG_SOURCE, encoding='utf-8')
            self.assertEqual({'ProjektZeh.Storage.RemovePlug::RemoveEntry'}, gate.expected_plugs(directory))

    def test_bound_plug_passes_even_with_foreign_misses(self):
        self.assertEqual(0, self.run_gate(FOREIGN_MISS + RESOLVED))

    def test_unbound_plug_fails(self):
        self.assertEqual(1, self.run_gate(FOREIGN_MISS + MISSED))

    def test_miss_in_another_assembly_is_allowed_when_one_target_binds(self):
        # Wie FreeInode auf be097fe: dieselbe Signatur scheitert an einem gleichnamigen Typ und bindet am echten.
        same_signature_miss = RESOLVED.replace('[DEBUG] [ResolveMethod] Found matching method: System.Boolean',
                                               '[WARN] [ResolveMethod] Target method not found: RemoveEntry')
        self.assertEqual(0, self.run_gate(same_signature_miss + RESOLVED))

    def test_plug_absent_from_log_fails(self):
        self.assertEqual(1, self.run_gate(FOREIGN_MISS))

    def test_overload_that_does_not_bind_fails_even_if_another_binds(self):
        other = MISSED.replace('System.Object,System.Object,System.St\nring', 'System.Int32')
        self.assertEqual(1, self.run_gate(RESOLVED + other))

    def test_plug_member_forms(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / 'Plug.cs'
            source.write_text('namespace N { [Plug(typeof(T))] static class P {\n'
                              '[PlugMember(TargetName = "A")] static void A() { }\n'
                              '[PlugMember, Obsolete] static T G<T>(T v) => v; } }\n', encoding='utf-8')
            self.assertEqual({'N.P::A', 'N.P::G'}, gate.expected_plugs(directory))
            source.write_text('namespace N; [Plug(typeof(T))] static class P { [PlugMember] static int F { get; } }',
                              encoding='utf-8')
            with self.assertRaises(ValueError):
                gate.expected_plugs(directory)

    def test_missing_log_or_plugs_cannot_be_checked(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(2, gate.main([str(Path(directory) / 'fehlt.log'), directory]))
            log = Path(directory) / 'kernel-build.log'
            log.write_text(RESOLVED, encoding='utf-8')
            self.assertEqual(2, gate.main([str(log), directory]))


if __name__ == '__main__':
    unittest.main()
