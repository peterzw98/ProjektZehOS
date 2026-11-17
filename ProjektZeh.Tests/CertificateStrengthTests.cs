using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cosmos.Network.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Asn1.Oiw;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.X509;
using ProjektZeh.Network;

namespace ProjektZeh.Tests;

/// <summary>Etappe 12.2 PR A (Spec 2026-11-17-tls-12-2-audit.md §2.1): schwache Verfahren und Schluessel, kein CN-Rueckfall.</summary>
[TestClass]
public sealed class CertificateStrengthTests
{
    private static readonly SecureRandom Random = new();
    private static readonly DateTime Now = new(2026, 11, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly AsymmetricCipherKeyPair RootKey = Rsa(2048);
    private static readonly X509Certificate Root = Issue("CN=Test Root", RootKey, "CN=Test Root", RootKey.Private, "SHA256WITHRSA", ca: true);

    private static AsymmetricCipherKeyPair Rsa(int bits)
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(65537), Random, bits, 25));
        return generator.GenerateKeyPair();
    }

    private static AsymmetricCipherKeyPair Ec(DerObjectIdentifier curve)
    {
        var generator = new ECKeyPairGenerator();
        generator.Init(new ECKeyGenerationParameters(curve, Random));
        return generator.GenerateKeyPair();
    }

    private static X509Certificate Issue(string subject, AsymmetricCipherKeyPair subjectKey, string issuer, AsymmetricKeyParameter issuerKey,
        string algorithm, bool ca, string? dnsName = null)
    {
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(BigInteger.ProbablePrime(64, Random));
        generator.SetSubjectDN(new X509Name(subject));
        generator.SetIssuerDN(new X509Name(issuer));
        generator.SetNotBefore(Now.AddDays(-1));
        generator.SetNotAfter(Now.AddDays(30));
        generator.SetPublicKey(subjectKey.Public);
        generator.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(ca));
        if (dnsName != null)
            generator.AddExtension(X509Extensions.SubjectAlternativeName, false, new GeneralNames(new GeneralName(GeneralName.DnsName, dnsName)));
        return generator.Generate(new Asn1SignatureFactory(algorithm, issuerKey, Random));
    }

    private static string? Verify(X509Certificate leaf, params X509Certificate[] intermediates) =>
        CertificateVerifier.Verify([leaf, .. intermediates], "www.example.com", [Root], Now);

    private static X509Certificate Leaf(string algorithm = "SHA256WITHRSA", AsymmetricCipherKeyPair? key = null) =>
        Issue("CN=www.example.com", key ?? Rsa(2048), "CN=Test Root", RootKey.Private, algorithm, ca: false, "www.example.com");

    [TestMethod]
    public void StrongChainIsAccepted()
    {
        Assert.IsNull(Verify(Leaf()));
        Assert.IsNull(Verify(Leaf("SHA384WITHRSA")));
        Assert.IsNull(Verify(Leaf(key: Ec(SecObjectIdentifiers.SecP256r1))), "P-256-Blatt");
        Assert.IsNull(Verify(Leaf("SHA256WITHRSAANDMGF1")), "RSASSA-PSS mit SHA-256");
        Assert.IsNull(Verify(Leaf(key: Ec(SecObjectIdentifiers.SecP384r1))), "P-384-Blatt");
        Assert.IsNull(Verify(Leaf(key: Ec(SecObjectIdentifiers.SecP521r1))), "P-521-Blatt");
        var edGenerator = new Ed25519KeyPairGenerator();
        edGenerator.Init(new Ed25519KeyGenerationParameters(Random));
        Assert.IsNull(Verify(Leaf(key: edGenerator.GenerateKeyPair())), "Ed25519-Blatt");
    }

    [TestMethod]
    public void Sha1SelfSignedCopyOfTheRootAfterATrustedPathStillPasses()
    {
        // Server haengen oft die Wurzel (auch mit SHA-1-Selbstsignatur) an; sie wird nie erreicht, wenn die Wurzel vertraut ist.
        var appended = Issue("CN=Test Root", RootKey, "CN=Test Root", RootKey.Private, "SHA1WITHRSA", ca: true);
        Assert.IsNull(Verify(Leaf(), appended));
    }

    [TestMethod]
    public void PssNeedsSha2AndMatchingMgf1()
    {
        var sha256 = new AlgorithmIdentifier(NistObjectIdentifiers.IdSha256, DerNull.Instance);
        var sha1 = new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1, DerNull.Instance);
        Assert.IsTrue(CertificateVerifier.HasStrongSignature(WithAlgorithm(Pss(sha256, sha256))));
        Assert.IsFalse(CertificateVerifier.HasStrongSignature(WithAlgorithm(Pss(sha256, sha1))), "MGF1 mit SHA-1");
        Assert.IsFalse(CertificateVerifier.HasStrongSignature(WithAlgorithm(Pss(sha1, sha1))), "SHA-1");
        Assert.IsFalse(CertificateVerifier.HasStrongSignature(WithAlgorithm(new AlgorithmIdentifier(PkcsObjectIdentifiers.IdRsassaPss))), "ohne Parameter = SHA-1");
        Assert.IsFalse(CertificateVerifier.HasStrongSignature(WithAlgorithm(new AlgorithmIdentifier(PkcsObjectIdentifiers.IdRsassaPss, DerNull.Instance))), "NULL-Parameter");
    }

    private static AlgorithmIdentifier Pss(AlgorithmIdentifier hash, AlgorithmIdentifier maskHash) =>
        new(PkcsObjectIdentifiers.IdRsassaPss, new RsassaPssParameters(hash, new AlgorithmIdentifier(PkcsObjectIdentifiers.IdMgf1, maskHash),
            DerInteger.ValueOf(32), DerInteger.ValueOf(1)));

    // Nur die Algorithmusangabe zaehlt fuer HasStrongSignature; die Signatur selbst stammt von einem echten SHA-256-Signierer.
    private static X509Certificate WithAlgorithm(AlgorithmIdentifier algorithm)
    {
        var generator = new X509V3CertificateGenerator();
        generator.SetSerialNumber(BigInteger.One);
        generator.SetSubjectDN(new X509Name("CN=x"));
        generator.SetIssuerDN(new X509Name("CN=Test Root"));
        generator.SetNotBefore(Now.AddDays(-1));
        generator.SetNotAfter(Now.AddDays(1));
        generator.SetPublicKey(RootKey.Public);
        return generator.Generate(new ClaimedAlgorithm(algorithm, new Asn1SignatureFactory("SHA256WITHRSA", RootKey.Private, Random)));
    }

    private sealed class ClaimedAlgorithm(AlgorithmIdentifier algorithm, ISignatureFactory signer) : ISignatureFactory
    {
        public object AlgorithmDetails => algorithm;
        public IStreamCalculator<IBlockResult> CreateCalculator() => signer.CreateCalculator();
    }

    [TestMethod]
    [DataRow("SHA1WITHRSA")]
    [DataRow("MD5WITHRSA")]
    [DataRow("SHA1WITHRSAANDMGF1")]
    public void WeakSignatureOnTheLeafIsRejected(string algorithm)
    {
        string? error = Verify(Leaf(algorithm));
        Assert.IsNotNull(error, algorithm);
        StringAssert.Contains(error, "weak or unknown algorithm");
    }

    [TestMethod]
    public void WeakSignatureOnAnIntermediateIsRejected()
    {
        var intermediateKey = Rsa(2048);
        var intermediate = Issue("CN=Intermediate", intermediateKey, "CN=Test Root", RootKey.Private, "SHA1WITHRSA", ca: true);
        var leaf = Issue("CN=www.example.com", Rsa(2048), "CN=Intermediate", intermediateKey.Private, "SHA256WITHRSA", ca: false, "www.example.com");
        StringAssert.Contains(Verify(leaf, intermediate), "Intermediate");

        var good = Issue("CN=Intermediate", intermediateKey, "CN=Test Root", RootKey.Private, "SHA256WITHRSA", ca: true);
        Assert.IsNull(Verify(leaf, good), "Gegenprobe mit SHA-256");
    }

    [TestMethod]
    public void WeakKeysAreRejected()
    {
        StringAssert.Contains(Verify(Leaf(key: Rsa(1024))), "weak key");
        StringAssert.Contains(Verify(Leaf(key: Ec(SecObjectIdentifiers.SecP256k1))), "weak key", "secp256k1 ist keine erlaubte Kurve");

        var weakIntermediateKey = Rsa(1024);
        var intermediate = Issue("CN=Intermediate", weakIntermediateKey, "CN=Test Root", RootKey.Private, "SHA256WITHRSA", ca: true);
        var leaf = Issue("CN=www.example.com", Rsa(2048), "CN=Intermediate", weakIntermediateKey.Private, "SHA256WITHRSA", ca: false, "www.example.com");
        StringAssert.Contains(Verify(leaf, intermediate), "weak key");
    }

    [TestMethod]
    public void CommonNameAloneNoLongerNamesTheHost()
    {
        var leaf = Issue("CN=www.example.com", Rsa(2048), "CN=Test Root", RootKey.Private, "SHA256WITHRSA", ca: false);
        StringAssert.Contains(Verify(leaf), "doesn't name");
        Assert.IsFalse(CertificateVerifier.NamesHost(leaf, "www.example.com"));
    }

    [TestMethod]
    public void WeakRootsAreNotLoadedButTheShippedBundleKeepsAll()
    {
        var weakKey = Rsa(1024);
        var weakRoot = Issue("CN=Weak Root", weakKey, "CN=Weak Root", weakKey.Private, "SHA256WITHRSA", ca: true);
        string pem = ToPem(weakRoot) + ToPem(Root);
        var anchors = TlsTransport.LoadAnchors(Encoding.ASCII.GetBytes(pem));
        Assert.AreEqual(1, anchors.Count, "nur die starke Wurzel");

        // Gepruefter Stand 17.11.2026: 121 Wurzeln, keine faellt durch die Regeln heraus (Spec §5.2).
        var shipped = new X509CertificateParser().ReadCertificates(TlsRoots.Load()).Count;
        Assert.AreEqual(shipped, TlsTransport.LoadAnchors(TlsRoots.Load()).Count,
            "Stolperdraht: eine neue Wurzel faellt heraus (Schluessel, kritische Erweiterung oder Zweck) - Bundle pruefen, Regel nicht still lockern.");
    }

    [TestMethod]
    public void HandshakeListKeepsCommonSchemesAndDropsWeakOnes()
    {
        var offered = new List<SignatureAndHashAlgorithm>
        {
            SignatureAndHashAlgorithm.ed25519,
            SignatureAndHashAlgorithm.rsa_pss_rsae_sha256,
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa),
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.rsa),
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.dsa),
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha1, SignatureAlgorithm.rsa),
            SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha224, SignatureAlgorithm.ecdsa),
        };
        var kept = TlsTransport.StrongHandshakeSignatures(offered);
        CollectionAssert.AreEqual(offered.Take(4).ToList(), kept.ToList());
    }

    [TestMethod]
    public void HandshakeSignaturesWithoutSha1()
    {
        Assert.IsFalse(TlsTransport.IsStrongHandshakeSignature(SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha1, SignatureAlgorithm.rsa)));
        Assert.IsFalse(TlsTransport.IsStrongHandshakeSignature(SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha1, SignatureAlgorithm.ecdsa)));
        Assert.IsFalse(TlsTransport.IsStrongHandshakeSignature(SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha224, SignatureAlgorithm.rsa)));
        Assert.IsTrue(TlsTransport.IsStrongHandshakeSignature(SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha256, SignatureAlgorithm.ecdsa)));
        Assert.IsTrue(TlsTransport.IsStrongHandshakeSignature(SignatureAndHashAlgorithm.ed25519));
        Assert.IsTrue(TlsTransport.IsStrongHandshakeSignature(SignatureAndHashAlgorithm.rsa_pss_rsae_sha256));
    }

    private static string ToPem(X509Certificate certificate)
    {
        string base64 = Convert.ToBase64String(certificate.GetEncoded(), Base64FormattingOptions.InsertLineBreaks);
        return "-----BEGIN CERTIFICATE-----\n" + base64 + "\n-----END CERTIFICATE-----\n";
    }
}
