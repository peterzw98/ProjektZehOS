#if ZEHOS_SECURITY_PROBE
using System;
using System.IO;
using ProjektZeh.Storage;
using ProjektZeh.Shared;
using System.Security.Cryptography;
using Cosmos.Kernel.System.Diagnostics;
using ProjektZeh.Security;

namespace ProjektZeh.Runtime;

internal static class CosmosSecurityProbe
{
    public static bool Run()
    {
        try
        {
            byte[] salt = new byte[32];
            byte[] other = new byte[32];
            CosmosSaltSource.Fill(salt);
            CosmosSaltSource.Fill(other);
            if (CryptographicOperations.FixedTimeEquals(salt, other)) throw new InvalidOperationException("Zufallswerte identisch.");
            Log.WriteString("SECURITY_PROBE RNG completed\n");
            byte[] passwordBytes = "password"u8.ToArray();
            byte[] hash = PasswordHasher.DeriveAndClear(passwordBytes, "salt"u8.ToArray(), 1);
            foreach (byte value in passwordBytes) if (value != 0) throw new InvalidOperationException("Kennwortbytes nicht geloescht.");
            passwordBytes = "password"u8.ToArray();
            bool rejected = false;
            try { PasswordHasher.DeriveAndClear(passwordBytes, "salt"u8.ToArray(), 0); }
            catch (ArgumentException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("Ungueltige Iterationszahl akzeptiert.");
            foreach (byte value in passwordBytes) if (value != 0) throw new InvalidOperationException("Kennwortbytes nach Ausnahme nicht geloescht.");
            Log.WriteString("SECURITY_PROBE cleanup PASS success and exception\n");
            if (Convert.ToHexString(hash) != "120FB6CFFCF8B32C43E7225256C4F837A86548C92CCC35480805987CB70BE17B") throw new InvalidOperationException("PBKDF2 Testvektor falsch.");
            Log.WriteString("SECURITY_PROBE vector completed; production iterations starting\n");
            var hasher = new PasswordHasher(CosmosSaltSource.Fill);
            string encoded = hasher.Hash("probe-password-123");
            if (!PasswordHasher.Verify("probe-password-123", encoded) || PasswordHasher.Verify("wrong-password", encoded))
                throw new InvalidOperationException("Produktiver Kennwortvergleich falsch.");
#if ZEHOS_SECURITY_FILE_PROBE
            ProbeFiles(hasher);
#endif
            Log.WriteString("SECURITY_PROBE PASS PBKDF2-SHA256 vector, RNG and 600000 iterations\n");
            return true;
        }
        catch (Exception exception)
        {
            Log.WriteString($"SECURITY_PROBE FAIL {exception}\n");
            return false;
        }
    }
#if ZEHOS_SECURITY_FILE_PROBE
    private static void ProbeFiles(PasswordHasher hasher)
    {
        // Nur mit ausdruecklichem Probe-Build und isoliertem Datenimage starten.
        const string root = "/data/security-probe";
        if (Directory.Exists(root)) throw new InvalidOperationException("Probedaten existieren bereits.");
        Directory.CreateDirectory(root);
        var accounts = new AccountService(root, hasher);
        var alice = accounts.CreateSession();
        var bob = accounts.CreateSession();
        Require(!accounts.Load().IsOK(), "missing metadata");
        Require(accounts.PrepareRecovery(alice, "alice", "alice-password-123")().IsOK(), "recovery");
        Require(accounts.PrepareCreateUser(alice, "bob", false, "bob-password-123")().IsOK(), "create user");
        Require(accounts.PrepareLogin(bob, "bob", "bob-password-123")().IsOK(), "login");
        var own = new FileService(bob.HomeDirectory!, access: accounts.Access, session: bob);
        string path = Path.Combine(bob.HomeDirectory!, "own.txt");
        Require(own.WriteText("own.txt", "vorher").IsOK(), "own write");
        Require(own.WriteText("own.txt", "nachher").IsOK(), "own overwrite");
        RequireText(own.ReadText("own.txt"), "nachher", "own read");
        var unprivileged = accounts.CreateSession();
        Require(accounts.PrepareCreateUser(alice, "charlie", false, "charlie-password-123")().IsOK(), "second user");
        Require(accounts.PrepareLogin(unprivileged, "charlie", "charlie-password-123")().IsOK(), "second login");
        var foreign = new FileService(unprivileged.HomeDirectory!, access: accounts.Access, session: unprivileged);
        Require(!foreign.ReadText(path).IsOK(), "foreign denial");
        Require(accounts.Grant(alice, bob.HomeDirectory!, "charlie", FilePermission.Read).IsOK(), "parent grant");
        Require(accounts.Grant(bob, path, "charlie", FilePermission.Read).IsOK(), "read grant");
        RequireText(foreign.ReadText(path), "nachher", "shared read");
        Require(!foreign.WriteText(path, "forbidden").IsOK(), "foreign write denial");
        Require(!foreign.ReadText("../../../../outside").IsOK(), "traversal denial");
        string id = bob.UserId!;
        Require(accounts.Load().IsOK(), "reload");
        Require(accounts.PrepareLogin(bob, "bob", "bob-password-123")().IsOK() && bob.UserId == id, "stable identity");
        RequireText(own.ReadText("own.txt"), "nachher", "persisted file");
        File.WriteAllText(Path.Combine(root, ".zehos-security.db"), new string('x', 70000));
        Require(!accounts.Load().IsOK() && bob.UserId == null && !own.ReadText("own.txt").IsOK(), "corruption locks sessions");
        Require(accounts.PrepareRecovery(alice, "rescued", "rescued-password-123")().IsOK(), "corrupt recovery");
        string[] backups = Directory.GetFiles(root, ".zehos-security-recovery-*");
        Require(backups.Length == 1 && new FileInfo(backups[0]).Length == 70000, "recovery preserves bytes");
        Require(!accounts.Access.Check(alice, path, FilePermission.Read).IsOK(), "old file remains unmapped");
        Log.WriteString("SECURITY_FILES PASS users, ACL, overwrite, reload, corruption and recovery\n");
    }

    private static void RequireText(ResultObject<string> result, string expected, string stage) =>
        Require(result.IsOK() && result.Value == expected, stage);

    private static void Require(bool condition, string stage)
    {
        Log.WriteString($"SECURITY_FILES {stage}: {condition}\n");
        if (!condition) throw new InvalidOperationException(stage);
    }
#endif

}
#endif
