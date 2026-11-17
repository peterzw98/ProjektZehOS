using System;
using Cosmos.Network.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace ProjektZeh.Tests;

/// <summary>Etappe 12.2 PR B (Spec 2026-11-17-tls-12-2-audit.md §2.2): nameConstraints der Zertifizierungsstellen.</summary>
[TestClass]
public sealed class NameConstraintsTests
{
    private static readonly SecureRandom Random = new();
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AsymmetricCipherKeyPair RootKey = Rsa();
    private static readonly AsymmetricCipherKeyPair IntermediateKey = Rsa();
    private static readonly AsymmetricCipherKeyPair LeafKey = Rsa();

    private static AsymmetricCipherKeyPair Rsa()
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(65537), Random, 2048, 25));
        return generator.GenerateKeyPair();
    }

    private static X509Certificate Issue(string subject, AsymmetricKeyParameter key, string issuer, AsymmetricKeyParameter issuerKey,
        bool ca, NameConstraints? constraints = null, params GeneralName[] names)
    {
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(BigInteger.ProbablePrime(64, Random));
        generator.SetSubjectDN(new X509Name(subject));
        generator.SetIssuerDN(new X509Name(issuer));
        generator.SetNotBefore(Now.AddDays(-1));
        generator.SetNotAfter(Now.AddDays(30));
        generator.SetPublicKey(key);
        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(ca));
        // Kritisch wie bei echten eingeschraenkten Zwischenzertifikaten; frueher lehnte der Pruefer das grundsaetzlich ab.
        if (constraints != null) generator.AddExtension(X509Extensions.NameConstraints, true, constraints);
        if (names.Length > 0) generator.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(names));
        return generator.Generate(new Asn1SignatureFactory("SHA256WITHRSA", issuerKey, Random));
    }

    private static NameConstraints Permitted(params GeneralName[] names) => new(Subtrees(names), (GeneralSubtrees?)null);
    private static NameConstraints Excluded(params GeneralName[] names) => new((GeneralSubtrees?)null, Subtrees(names));

    private static GeneralSubtrees Subtrees(GeneralName[] names) =>
        GeneralSubtrees.GetInstance(new DerSequence(Array.ConvertAll(names, name => (Asn1Encodable)new GeneralSubtree(name))));

    private static GeneralName Dns(string name) => new(GeneralName.DnsName, name);
    private static GeneralName Ip(params byte[] bytes) => new(GeneralName.IPAddress, new DerOctetString(bytes));

    /// <summary>Wurzel -> eingeschraenktes Zwischenzertifikat -> Blatt; liefert Verify fuer den ersten Namen.</summary>
    private static string? Check(NameConstraints constraints, string host, params GeneralName[] leafNames)
    {
        var root = Issue("CN=Root", RootKey.Public, "CN=Root", RootKey.Private, ca: true);
        var intermediate = Issue("CN=Constrained", IntermediateKey.Public, "CN=Root", RootKey.Private, ca: true, constraints);
        var leaf = Issue("CN=leaf", LeafKey.Public, "CN=Constrained", IntermediateKey.Private, ca: false, null, leafNames);
        return CertificateVerifier.Verify([leaf, intermediate], host, [root], Now);
    }

    [TestMethod]
    public void PermittedDnsSubtreeAllowsItsNamesOnly()
    {
        var constraints = Permitted(Dns("example.com"));
        Assert.IsNull(Check(constraints, "www.example.com", Dns("www.example.com")));
        Assert.IsNull(Check(constraints, "example.com", Dns("example.com")));
        Assert.IsNull(Check(constraints, "a.example.com", Dns("*.example.com")), "Platzhalter unterhalb");
        StringAssert.Contains(Check(constraints, "www.other.org", Dns("www.other.org")), "may not issue");
        StringAssert.Contains(Check(constraints, "badexample.com", Dns("badexample.com")), "may not issue", "kein Teilstring-Treffer");
        StringAssert.Contains(Check(constraints, "www.example.com", Dns("www.example.com"), Dns("evil.org")), "evil.org", "jeder SAN zaehlt");
    }

    [TestMethod]
    public void ExcludedDnsSubtreeRefusesItsNames()
    {
        var constraints = Excluded(Dns(".example.com"));
        StringAssert.Contains(Check(constraints, "www.example.com", Dns("www.example.com")), "may not issue");
        Assert.IsNull(Check(constraints, "example.com", Dns("example.com")), "Punktform schliesst nur Unternamen aus");
        Assert.IsNull(Check(constraints, "www.other.org", Dns("www.other.org")));
    }

    [TestMethod]
    public void Ipv4RangesApplyToAddressesOnly()
    {
        var constraints = Permitted(Ip(10, 0, 0, 0, 255, 0, 0, 0));
        Assert.IsNull(Check(constraints, "10.1.2.3", Ip(10, 1, 2, 3)));
        StringAssert.Contains(Check(constraints, "192.168.1.1", Ip(192, 168, 1, 1)), "IP address");
        Assert.IsNull(Check(constraints, "www.example.com", Dns("www.example.com")), "nur-IP-Liste beschraenkt keine DNS-Namen");

        // iPAddress ist ein Namenstyp: eine nur aus IPv6 bestehende Erlaubtliste erlaubt keine IPv4-Adresse (RFC 5280, Chromium).
        var ipv6Only = Permitted(Ip(new byte[32]));
        StringAssert.Contains(Check(ipv6Only, "192.168.1.1", Ip(192, 168, 1, 1)), "IP address");

        var excluded = Excluded(Ip(192, 168, 0, 0, 255, 255, 0, 0));
        StringAssert.Contains(Check(excluded, "192.168.1.1", Ip(192, 168, 1, 1)), "IP address");
        Assert.IsNull(Check(excluded, "10.0.0.1", Ip(10, 0, 0, 1)));
        Assert.IsNull(Check(Excluded(Ip(new byte[32])), "192.168.1.1", Ip(192, 168, 1, 1)), "ausgeschlossener IPv6-Bereich trifft keine IPv4-Adresse");
        StringAssert.Contains(Check(Permitted(Ip(10, 0, 0, 0, 255)), "10.0.0.1", Ip(10, 0, 0, 1)), "can't be read", "5-Byte-Bereich");
    }

    [TestMethod]
    public void WildcardCannotEscapeAnExcludedSubtree()
    {
        StringAssert.Contains(Check(Excluded(Dns("secret.example.com")), "secret.example.com", Dns("*.example.com")), "may not issue");
        StringAssert.Contains(Check(Excluded(Dns(".example.com")), "a.example.com", Dns("*.example.com")), "may not issue");
        Assert.IsNull(Check(Excluded(Dns("other.org")), "a.example.com", Dns("*.example.com")));
        StringAssert.Contains(Check(Excluded(Dns("deep.secret.example.com")), "a.secret.example.com", Dns("*.secret.example.com")), "may not issue",
            "auch tiefer liegende Ausschluesse gelten als erreichbar (konservativ)");
        StringAssert.Contains(Check(Permitted(Dns("secret.example.com")), "secret.example.com", Dns("*.example.com")), "may not issue", "Erlaubtliste bleibt streng");
    }

    [TestMethod]
    public void EmptyExcludedDnsConstraintRefusesEveryName() =>
        StringAssert.Contains(Check(Excluded(Dns("")), "www.example.com", Dns("www.example.com")), "may not issue");

    [TestMethod]
    public void OutOfProfileConstraintsAreRefused()
    {
        var withMaximum = new NameConstraints(GeneralSubtrees.GetInstance(new DerSequence(new GeneralSubtree(Dns("example.com"), DerInteger.Zero, DerInteger.One))), (GeneralSubtrees?)null);
        StringAssert.Contains(Check(withMaximum, "www.example.com", Dns("www.example.com")), "can't be read");
        var withMinimum = new NameConstraints(GeneralSubtrees.GetInstance(new DerSequence(new GeneralSubtree(Dns("example.com"), DerInteger.One, (DerInteger?)null))), (GeneralSubtrees?)null);
        StringAssert.Contains(Check(withMinimum, "www.example.com", Dns("www.example.com")), "can't be read");
        StringAssert.Contains(Check(new NameConstraints((GeneralSubtrees?)null, (GeneralSubtrees?)null), "www.example.com", Dns("www.example.com")), "can't be read", "weder erlaubt noch ausgeschlossen");
    }

    [TestMethod]
    public void ConstraintsOfSeveralAuthoritiesIntersect()
    {
        var root = Issue("CN=Root", RootKey.Public, "CN=Root", RootKey.Private, ca: true, Permitted(Dns("example.com")));
        var intermediate = Issue("CN=Constrained", IntermediateKey.Public, "CN=Root", RootKey.Private, ca: true, Permitted(Dns("www.example.com"), Dns("other.org")));
        var inBoth = Issue("CN=leaf", LeafKey.Public, "CN=Constrained", IntermediateKey.Private, ca: false, null, Dns("www.example.com"));
        var onlyIntermediate = Issue("CN=leaf", LeafKey.Public, "CN=Constrained", IntermediateKey.Private, ca: false, null, Dns("other.org"));
        Assert.IsNull(CertificateVerifier.Verify([inBoth, intermediate], "www.example.com", [root], Now));
        StringAssert.Contains(CertificateVerifier.Verify([onlyIntermediate, intermediate], "other.org", [root], Now), "may not issue");
    }

    [TestMethod]
    public void TrustedIntermediateEndingTheChainKeepsItsConstraints()
    {
        var root = Issue("CN=Root", RootKey.Public, "CN=Root", RootKey.Private, ca: true);
        var intermediate = Issue("CN=Constrained", IntermediateKey.Public, "CN=Root", RootKey.Private, ca: true, Permitted(Dns("example.com")));
        var bad = Issue("CN=leaf", LeafKey.Public, "CN=Constrained", IntermediateKey.Private, ca: false, null, Dns("www.other.org"));
        StringAssert.Contains(CertificateVerifier.Verify([bad, intermediate], "www.other.org", [root, intermediate], Now), "may not issue");
        StringAssert.Contains(CertificateVerifier.Verify([bad], "www.other.org", [intermediate], Now), "may not issue", "Zwischenzertifikat selbst als Anker");
    }

    [TestMethod]
    public void ShippedBundleHasNoRootWithNameConstraints()
    {
        // Gepruefter Stand 17.11.2026 (Spec §5.2); eine kuenftige Wurzel mit Beschraenkung soll bewusst auffallen.
        foreach (var root in new X509CertificateParser().ReadCertificates(ProjektZeh.Network.TlsRoots.Load()))
            Assert.IsNull(root.GetExtensionValue(X509Extensions.NameConstraints), root.SubjectDN.ToString());
    }

    [TestMethod]
    public void UnsupportedNameFormIsRefused()
    {
        var constraints = Permitted(new GeneralName(new X509Name("O=Example")));
        StringAssert.Contains(Check(constraints, "www.example.com", Dns("www.example.com")), "name form");
    }

    [TestMethod]
    public void ConstraintsOnTheTrustAnchorApply()
    {
        var root = Issue("CN=Root", RootKey.Public, "CN=Root", RootKey.Private, ca: true, Permitted(Dns("example.com")));
        var good = Issue("CN=leaf", LeafKey.Public, "CN=Root", RootKey.Private, ca: false, null, Dns("www.example.com"));
        var bad = Issue("CN=leaf", LeafKey.Public, "CN=Root", RootKey.Private, ca: false, null, Dns("www.other.org"));
        Assert.IsNull(CertificateVerifier.Verify([good], "www.example.com", [root], Now));
        StringAssert.Contains(CertificateVerifier.Verify([bad], "www.other.org", [root], Now), "may not issue");
    }

    [TestMethod]
    [DataRow("www.example.com", "example.com", true)]
    [DataRow("example.com", "example.com", true)]
    [DataRow("EXAMPLE.COM.", "example.com", true)]
    [DataRow("example.com", ".example.com", false)]
    [DataRow("a.b.example.com", ".example.com", true)]
    [DataRow("badexample.com", "example.com", false)]
    [DataRow("anything.org", "", true)]
    public void DnsSubtreeFollowsRfc5280(string name, string constraint, bool within) =>
        Assert.AreEqual(within, CertificateVerifier.WithinDnsSubtree(name, constraint), name + " / " + constraint);
}
