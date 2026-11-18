using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Cosmos.Network.Http;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;

namespace ProjektZeh.Network;

// Der Transport muss jede Ein- und Ausgabe einschliesslich Handshake zeitlich begrenzen.
/// <summary>Die eigene Zertifikatspruefung hat abgelehnt; Apps unterscheiden das von Protokollfehlern.</summary>
internal sealed class TlsCertificateRejectedException(short alert, string? detail) : TlsFatalAlert(alert, detail);

internal static class TlsTransport
{
    internal static Stream Open(Stream transport, string host, Action<byte[]> fillRandom, byte[] trustedPem, DateTime utcNow)
    {
        CheckClock(utcNow);
        var protocol = new TlsClientProtocol(transport);
        protocol.Connect(CreateClient(host, fillRandom, CachedAnchors(trustedPem), utcNow, false));
        return protocol.Stream;
    }

    // Ein Verweis statt eines Werttupels: Lesen und Ersetzen sind so atomar, kein Thread sieht Pem und Anker zweier Staende.
    private sealed record AnchorCache(byte[] Pem, IList<X509Certificate> Anchors);
    private static AnchorCache? cachedAnchors;

    // Das Parsen aller Wurzeln je Verbindung erzeugte den Grossteil des Muells eines Downloads; der Cosmos-GC laeuft
    // erst, wenn der Seitenallokator erschoepft ist. Gilt fuer dasselbe Bundle-Array (TlsRoots.Load liefert stets dasselbe).
    /// <summary>
    /// Startet die Abruffrist eines Netzabrufs. Fuer HTTPS werden die Wurzeln vorher einmal geparst: Beim ersten Abruf nach
    /// dem Boot dauerte das laenger als die ganze Frist (CosmosNetworkService.FetchTimeoutMilliseconds), danach traf der
    /// Abruf den Cache.
    /// </summary>
    internal static System.Diagnostics.Stopwatch StartDeadline(Uri url, byte[] trustedPem)
    {
        if (url.Scheme == Uri.UriSchemeHttps) CachedAnchors(trustedPem);
        return System.Diagnostics.Stopwatch.StartNew();
    }

    internal static IList<X509Certificate> CachedAnchors(byte[] trustedPem)
    {
        var cached = Volatile.Read(ref cachedAnchors);
        if (cached != null && ReferenceEquals(cached.Pem, trustedPem)) return cached.Anchors;
        var anchors = LoadAnchors(trustedPem);
        Volatile.Write(ref cachedAnchors, new AnchorCache(trustedPem, anchors));
        return anchors;
    }

    internal static void CheckClock(DateTime utcNow)
    {
        // Etappe 12.2: nicht vor dem Stand des Wurzelbundles, hoechstens 15 Jahre danach (vorher nur Jahr 2026-2100).
        if (!ProjektZeh.SystemServices.ClockCorrection.IsPlausible(utcNow, TlsRoots.MinimumTime))
            throw new InvalidOperationException("Ungueltige UTC-Uhr; TLS ist gesperrt.");
    }

    internal static IList<X509Certificate> LoadAnchors(byte[] trustedPem)
    {
        var roots = new X509CertificateParser().ReadCertificates(trustedPem);
        var trusted = new List<X509Certificate>();
        foreach (X509Certificate root in roots)
        {
            if (IsUsableAnchor(root)) trusted.Add(root);
        }
        if (trusted.Count == 0) throw new InvalidOperationException("Keine vertrauenswuerdigen TLS-Wurzeln.");
        return trusted;
    }

    // Shell und Wasm-Apps teilen Suiten, Grenzen und Pruefung. Apps verlangen close_notify, damit eine Antwort ohne
    // Laenge nicht unbemerkt abgeschnitten wird; die Shell liest nur laengengepruefte Antworten.
    internal static TlsClient CreateClient(string host, Action<byte[]> fillRandom, IList<X509Certificate> anchors, DateTime utcNow,
        bool requireCloseNotify) =>
        new Client(new BcTlsCrypto(new HardwareSecureRandom(fillRandom)), host, anchors, utcNow, requireCloseNotify);

    private static bool IsUsableAnchor(X509Certificate root)
    {
        // Etappe 12.2: Wurzeln mit schwachem Schluessel (RSA < 2048, fremde Kurve) nicht laden.
        if (!CertificateVerifier.HasStrongKey(root)) return false;
        var critical = root.GetCriticalExtensionOids();
        if (critical != null)
        {
            foreach (string oid in critical)
            {
                if (oid != X509Extensions.BasicConstraints.Id && oid != X509Extensions.KeyUsage.Id &&
                    oid != X509Extensions.ExtendedKeyUsage.Id && oid != X509Extensions.SubjectAlternativeName.Id &&
                    oid != X509Extensions.CertificatePolicies.Id && oid != X509Extensions.NameConstraints.Id) return false;
            }
        }
        return AllowsServerPurpose(root);
    }

    private static bool AllowsServerPurpose(X509Certificate certificate)
    {
        var purposes = certificate.GetExtendedKeyUsage();
        if (purposes == null) return true;
        foreach (var purpose in purposes)
            if (purpose.Equals(KeyPurposeID.id_kp_serverAuth) || purpose.Equals(KeyPurposeID.AnyExtendedKeyUsage)) return true;
        return false;
    }

    private sealed class HardwareSecureRandom(Action<byte[]> fill) : SecureRandom(new HardwareGenerator(fill))
    {
        public override byte[] GenerateSeed(int length)
        {
            byte[] bytes = new byte[length];
            fill(bytes);
            return bytes;
        }

        public override void GenerateSeed(Span<byte> seed)
        {
            byte[] bytes = new byte[seed.Length];
            fill(bytes);
            bytes.CopyTo(seed);
            Array.Clear(bytes);
        }
    }

    private sealed class HardwareGenerator(Action<byte[]> fill) : IRandomGenerator
    {
        public void AddSeedMaterial(byte[] seed) { }
        public void AddSeedMaterial(ReadOnlySpan<byte> seed) { }
        public void AddSeedMaterial(long seed) { }
        public void NextBytes(byte[] bytes) => fill(bytes);
        public void NextBytes(byte[] bytes, int start, int len) => NextBytes(bytes.AsSpan(start, len));
        public void NextBytes(Span<byte> bytes)
        {
            byte[] temporary = new byte[bytes.Length];
            fill(temporary);
            temporary.CopyTo(bytes);
            Array.Clear(temporary);
        }
    }

    private sealed class Client(BcTlsCrypto crypto, string host, IList<X509Certificate> roots, DateTime utcNow, bool requireCloseNotify)
        : DefaultTlsClient(crypto)
    {
        protected override int[] GetSupportedCipherSuites() => TlsUtilities.GetSupportedCipherSuites(Crypto,
        [CipherSuite.TLS_CHACHA20_POLY1305_SHA256, CipherSuite.TLS_AES_128_GCM_SHA256,
         CipherSuite.TLS_AES_256_GCM_SHA384, CipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
         CipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256, CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
         CipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256, CipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
         CipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384]);
        public override int GetMaxCertificateChainLength() => 8;
        public override int GetMaxHandshakeMessageSize() => 32768;
        protected override ProtocolVersion[] GetSupportedVersions() => [ProtocolVersion.TLSv13, ProtocolVersion.TLSv12];

        // Etappe 12.2: auch die Handshake-Signatur (TLS 1.2 ServerKeyExchange, TLS 1.3 CertificateVerify) nie mit SHA-1
        // oder schwaecher. BouncyCastle prueft beide gegen diese Liste.
        protected override IList<SignatureAndHashAlgorithm> GetSupportedSignatureAlgorithms() =>
            StrongHandshakeSignatures(base.GetSupportedSignatureAlgorithms());
        protected override IList<ServerName>? GetSniServerNames() => CertificateVerifier.ParseIPv4(host) == null
            ? [new ServerName(NameType.host_name, Encoding.ASCII.GetBytes(host))] : null;
        public override TlsAuthentication GetAuthentication() => new Authentication(host, roots, utcNow);
        public override bool RequiresCloseNotify() => requireCloseNotify;
    }

    /// <summary>
    /// Behaelt RSA (PKCS#1 v1.5 und PSS) und ECDSA mit SHA-2 sowie EdDSA. DSA entfaellt, weil der Pruefer keine DSA-Schluessel
    /// liest; ML-DSA (Hash-Byte 9) ebenso, weil der Pruefer solche Zertifikate nicht annimmt.
    /// </summary>
    internal static IList<SignatureAndHashAlgorithm> StrongHandshakeSignatures(IList<SignatureAndHashAlgorithm> offered)
    {
        var strong = new List<SignatureAndHashAlgorithm>();
        foreach (var algorithm in offered)
            if (IsStrongHandshakeSignature(algorithm)) strong.Add(algorithm);
        return strong;
    }

    internal static bool IsStrongHandshakeSignature(SignatureAndHashAlgorithm algorithm) =>
        algorithm.Signature != SignatureAlgorithm.dsa &&
        (algorithm.Hash is HashAlgorithm.sha256 or HashAlgorithm.sha384 or HashAlgorithm.sha512 || algorithm.Hash == HashAlgorithm.Intrinsic);

    private sealed class Authentication(string host, IList<X509Certificate> roots, DateTime utcNow) : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var chain = serverCertificate.Certificate;
            if (chain == null || chain.Length == 0 || chain.Length > 8)
                throw new TlsCertificateRejectedException(AlertDescription.bad_certificate, null);
            var certificates = new X509Certificate[chain.Length];
            for (int i = 0; i < certificates.Length; i++)
            {
                // Ein unlesbares Zertifikat ist eine Ablehnung der Pruefung, kein Protokollfehler.
                try { certificates[i] = new X509Certificate(chain.GetCertificateAt(i).GetEncoded()); }
                catch (Exception) { throw new TlsCertificateRejectedException(AlertDescription.bad_certificate, "Zertifikat nicht lesbar"); }
                if (!CertificateVerifier.HasReadableKey(certificates[i]) || (i > 0 && !AllowsServerPurpose(certificates[i])))
                    throw new TlsCertificateRejectedException(AlertDescription.unsupported_certificate, null);
            }
            string? error = CertificateVerifier.Verify(certificates, host, roots, utcNow);
            if (error != null) throw new TlsCertificateRejectedException(AlertDescription.bad_certificate, error);
        }

        public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
    }
}
