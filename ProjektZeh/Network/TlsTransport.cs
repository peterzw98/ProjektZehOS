using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cosmos.Network.Http;
using Org.BouncyCastle.Crypto.Prng;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;

namespace ProjektZeh.Network;

// Der Transport muss jede Ein- und Ausgabe einschliesslich Handshake zeitlich begrenzen.
internal static class TlsTransport
{
    internal static Stream Open(Stream transport, string host, Action<byte[]> fillRandom, byte[] trustedPem, DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc || utcNow.Year is < 2026 or > 2100)
            throw new InvalidOperationException("Ungueltige UTC-Uhr; TLS ist gesperrt.");
        var roots = new X509CertificateParser().ReadCertificates(trustedPem);
        var trusted = new List<X509Certificate>();
        foreach (X509Certificate root in roots)
        {
            if (IsUsableAnchor(root)) trusted.Add(root);
        }
        if (trusted.Count == 0) throw new InvalidOperationException("Keine vertrauenswuerdigen TLS-Wurzeln.");
        var protocol = new TlsClientProtocol(transport);
        protocol.Connect(new Client(new BcTlsCrypto(new HardwareSecureRandom(fillRandom)), host, trusted, utcNow));
        return protocol.Stream;
    }

    private static bool IsUsableAnchor(X509Certificate root)
    {
        if (!CertificateVerifier.HasReadableKey(root)) return false;
        var critical = root.GetCriticalExtensionOids();
        if (critical != null)
        {
            foreach (string oid in critical)
            {
                if (oid != X509Extensions.BasicConstraints.Id && oid != X509Extensions.KeyUsage.Id &&
                    oid != X509Extensions.ExtendedKeyUsage.Id && oid != X509Extensions.SubjectAlternativeName.Id &&
                    oid != X509Extensions.CertificatePolicies.Id) return false;
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

    private sealed class Client(BcTlsCrypto crypto, string host, IList<X509Certificate> roots, DateTime utcNow) : DefaultTlsClient(crypto)
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
        protected override IList<ServerName>? GetSniServerNames() => CertificateVerifier.ParseIPv4(host) == null
            ? [new ServerName(NameType.host_name, Encoding.ASCII.GetBytes(host))] : null;
        public override TlsAuthentication GetAuthentication() => new Authentication(host, roots, utcNow);
        public override bool RequiresCloseNotify() => false;
    }

    private sealed class Authentication(string host, IList<X509Certificate> roots, DateTime utcNow) : TlsAuthentication
    {
        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var chain = serverCertificate.Certificate;
            if (chain == null || chain.Length == 0 || chain.Length > 8)
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            var certificates = new X509Certificate[chain.Length];
            for (int i = 0; i < certificates.Length; i++)
            {
                certificates[i] = new X509Certificate(chain.GetCertificateAt(i).GetEncoded());
                if (!CertificateVerifier.HasReadableKey(certificates[i]) || (i > 0 && !AllowsServerPurpose(certificates[i])))
                    throw new TlsFatalAlert(AlertDescription.unsupported_certificate);
            }
            string? error = CertificateVerifier.Verify(certificates, host, roots, utcNow);
            if (error != null) throw new TlsFatalAlert(AlertDescription.bad_certificate, error);
        }

        public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
    }
}
