#nullable disable
// Cosmos: the certificate check mbedTLS runs natively for nanoFramework (mbedtls_x509_crt_verify): the chain the peer
// sent must lead to a trusted certificate through valid signatures, every certificate must be within its validity
// period, and a server's must name the host. BouncyCastle's TLS leaves this to its caller.

using System;
using System.Collections.Generic;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.EdEC;
using Org.BouncyCastle.Asn1.Pkcs;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Extension;
using BcCertificate = Org.BouncyCastle.X509.X509Certificate;

namespace Cosmos.Network.Http
{
    /// <summary>
    /// Checks a peer's certificate chain.
    /// </summary>
    internal static class CertificateVerifier
    {
        // Longer chains than a leaf, a few intermediates and a root are refused.
        private const int MaxDepth = 8;

        private const int KeyCertSign = 5;

        /// <summary>
        /// Checks a certificate chain.
        /// </summary>
        /// <param name="chain">The certificates the peer sent, its own first.</param>
        /// <param name="host">The host the leaf certificate must name, or null not to check it (a client certificate).</param>
        /// <param name="trusted">The trusted certificates.</param>
        /// <param name="now">The time the certificates must be valid at.</param>
        /// <returns>null if the chain is valid, else why it is not.</returns>
        internal static string Verify(BcCertificate[] chain, string host, IList<BcCertificate> trusted, DateTime now)
        {
            if (chain == null || chain.Length == 0)
            {
                return "The peer sent no certificate.";
            }

            BcCertificate current = chain[0];

            string error = CheckValidity(current, now);
            if (error != null)
            {
                return error;
            }

            if (host != null && !NamesHost(current, host))
            {
                return "The certificate of " + Name(current) + " doesn't name " + host + ".";
            }

            // A server's certificate must be one for servers, a client's one for clients, when it says what it is for.
            KeyPurposeID purpose = host != null ? KeyPurposeID.id_kp_serverAuth : KeyPurposeID.id_kp_clientAuth;
            if (!AllowsPurpose(current, purpose))
            {
                return "The certificate of " + Name(current) + " isn't meant for TLS " + (host != null ? "servers." : "clients.");
            }

            // ZehOS (Etappe 12.2): the authorities above the leaf, whose nameConstraints apply to it.
            var authorities = new List<BcCertificate>();

            for (int depth = 0; depth < MaxDepth; depth++)
            {
                // Refused as mbedTLS refuses it: an extension it can't honour that says it must be.
                error = CheckCriticalExtensions(current);
                if (error != null)
                {
                    return error;
                }

                // A certificate trusted as such: the end of the chain.
                if (Contains(trusted, current))
                {
                    return CheckNameConstraints(chain[0], authorities);
                }

                // ZehOS (Etappe 12.2): every certificate below the trust anchor is signed with a current algorithm and
                // carries a strong key; the anchors' keys are checked when they are loaded.
                error = CheckStrength(current);
                if (error != null)
                {
                    return error;
                }

                // Signed by a trusted certificate. Above the leaf, depth certificate authorities stand below it.
                BcCertificate anchor = FindIssuer(current, trusted, 0, now, depth, out error);
                if (anchor != null)
                {
                    authorities.Add(anchor);
                    return CheckNameConstraints(chain[0], authorities);
                }

                // Signed by an intermediate the peer sent.
                BcCertificate issuer = FindIssuer(current, chain, 1, now, depth, out string chainError);
                if (issuer == null || issuer.Equals(current))
                {
                    // No issuer, or a self-signed root the peer sent that isn't trusted.
                    return error ?? chainError ?? "The certificate of " + Name(current) + " was issued by " + current.IssuerDN + ", which isn't trusted.";
                }

                authorities.Add(issuer);
                current = issuer;
            }

            return "The certificate chain is longer than " + MaxDepth + " certificates.";
        }

        /// <summary>
        /// Whether a server certificate names a host, by its subjectAltName (dNSName or iPAddress); ZehOS: never by its
        /// common name.
        /// </summary>
        internal static bool NamesHost(BcCertificate certificate, string host)
        {
            host = host.TrimEnd('.');

            byte[] address = ParseIPv4(host);

            // ZehOS (Etappe 12.2): only the subjectAltName names a host; the common name no longer counts (as in current
            // browsers; RFC 6125 6.4.4 only allows it).
            Asn1OctetString extension = certificate.GetExtensionValue(X509Extensions.SubjectAlternativeName);
            if (extension != null)
            {
                // A malformed extension names nothing, as mbedTLS refuses the certificate.
                // Null for an empty one, which a peer may send: a null dereference is a kernel panic on Cosmos.
                GeneralName[] all;
                try
                {
                    all = GeneralNames.GetInstance(X509ExtensionUtilities.FromExtensionValue(extension))?.GetNames();
                }
                catch
                {
                    return false;
                }

                if (all == null)
                {
                    return false;
                }

                foreach (GeneralName name in all)
                {
                    if (name.TagNo == GeneralName.DnsName)
                    {
                        if (address == null && MatchesDnsName(DerIA5String.GetInstance(name.Name).GetString(), host))
                        {
                            return true;
                        }
                    }
                    else if (name.TagNo == GeneralName.IPAddress && address != null)
                    {
                        if (SameBytes(Asn1OctetString.GetInstance(name.Name).GetOctets(), address))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The four bytes of an IPv4 address literal, or null for a host name.
        /// </summary>
        /// <remarks>
        /// Parsed here, as Cosmos's IPAddress.TryParse throws and catches a FormatException within for a name of four
        /// labels (www.example.co.uk), on every handshake: an exception is slow on a Cosmos kernel, and two threads
        /// throwing at once halt it.
        /// </remarks>
        internal static byte[] ParseIPv4(string host)
        {
            byte[] address = new byte[4];
            int part = 0;
            int value = -1;

            for (int i = 0; i <= host.Length; i++)
            {
                char c = i < host.Length ? host[i] : '.';
                if (c >= '0' && c <= '9')
                {
                    value = (value < 0 ? 0 : value * 10) + (c - '0');
                    if (value > 255)
                    {
                        return null;
                    }
                }
                else if (c == '.' && value >= 0 && part < 4)
                {
                    address[part++] = (byte)value;
                    value = -1;
                }
                else
                {
                    return null;
                }
            }

            return part == 4 ? address : null;
        }

        /// <summary>
        /// Whether a certificate's DNS name matches a host: equal ignoring case, or a wildcard for its first label only.
        /// </summary>
        /// <remarks>
        /// ZehOS: wildcards follow RFC 6125 6.4.3 as OpenSSL checks them with X509_CHECK_FLAG_NO_PARTIAL_WILDCARDS, like the
        /// NetSurf probe host. The "*" is the whole leftmost label, at least
        /// two labels follow it ("*.com" names nothing), it never matches an IPv4 literal, and a "*" elsewhere or inside
        /// a label ("w*.example.com") is no wildcard; such a name can only match itself, which no host name does.
        /// </remarks>
        internal static bool MatchesDnsName(string pattern, string host)
        {
            pattern = pattern.TrimEnd('.');

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                // "*.example.com" names "www.example.com", not "example.com" nor "a.b.example.com".
                string suffix = pattern.Substring(1);
                int dot = host.IndexOf('.');
                if (dot <= 0 || suffix.Contains('*') || suffix.Contains("..") || suffix.IndexOf('.', 1) < 0 || ParseIPv4(host) != null)
                {
                    return false;
                }

                return string.Equals(suffix, host.Substring(dot), StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(pattern, host, StringComparison.OrdinalIgnoreCase);
        }

        private static string CheckValidity(BcCertificate certificate, DateTime now)
        {
            // The clock is in the message: a kernel's comes from the RTC, which may well be wrong.
            if (now < certificate.NotBefore)
            {
                return "The certificate of " + Name(certificate) + " isn't valid before " + certificate.NotBefore.ToString("u") + " (the clock says " + now.ToString("u") + ").";
            }

            if (now > certificate.NotAfter)
            {
                return "The certificate of " + Name(certificate) + " expired on " + certificate.NotAfter.ToString("u") + " (the clock says " + now.ToString("u") + ").";
            }

            return null;
        }

        // The extensions that may be critical: those the checks here honour, and the certificate policies, which
        // mbedTLS accepts too.
        private static readonly string[] s_handledExtensions =
        {
            X509Extensions.BasicConstraints.Id,
            X509Extensions.KeyUsage.Id,
            X509Extensions.ExtendedKeyUsage.Id,
            X509Extensions.SubjectAlternativeName.Id,
            X509Extensions.CertificatePolicies.Id,
            // ZehOS (Etappe 12.2): honoured by CheckNameConstraints.
            X509Extensions.NameConstraints.Id,
        };

        private static string CheckCriticalExtensions(BcCertificate certificate)
        {
            ISet<string> critical = certificate.GetCriticalExtensionOids();
            if (critical == null)
            {
                return null;
            }

            foreach (string oid in critical)
            {
                if (Array.IndexOf(s_handledExtensions, oid) < 0)
                {
                    return "The certificate of " + Name(certificate) + " has a critical extension that isn't supported (" + oid + ").";
                }
            }

            return null;
        }

        private static bool AllowsPurpose(BcCertificate certificate, KeyPurposeID purpose)
        {
            IList<DerObjectIdentifier> purposes = certificate.GetExtendedKeyUsage();
            if (purposes == null)
            {
                return true;
            }

            foreach (DerObjectIdentifier allowed in purposes)
            {
                if (allowed.Equals(purpose) || allowed.Equals(KeyPurposeID.AnyExtendedKeyUsage))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The certificate among <paramref name="candidates"/> (from <paramref name="first"/> on) that issued and signed
        /// <paramref name="certificate"/> and may sign certificates, or null.
        /// </summary>
        private static BcCertificate FindIssuer(BcCertificate certificate, IList<BcCertificate> candidates, int first, DateTime now, int authoritiesBelow, out string error)
        {
            error = null;

            for (int i = first; i < candidates.Count; i++)
            {
                BcCertificate candidate = candidates[i];
                if (!candidate.SubjectDN.Equivalent(certificate.IssuerDN) || !IsSignedBy(certificate, candidate))
                {
                    continue;
                }

                // A certificate authority's: basicConstraints cA, and keyCertSign when it has a key usage.
                bool[] keyUsage = candidate.GetKeyUsage();
                if (candidate.GetBasicConstraints() < 0 || (keyUsage != null && !keyUsage[KeyCertSign]))
                {
                    error = "The certificate of " + Name(certificate) + " was signed by " + Name(candidate) + ", which isn't a certificate authority.";
                    continue;
                }

                // Its basicConstraints' pathLenConstraint: how many certificate authorities may stand below it.
                if (candidate.GetBasicConstraints() < authoritiesBelow)
                {
                    error = "The certificate of " + Name(candidate) + " may not issue certificates through " + authoritiesBelow + " intermediate authorities.";
                    continue;
                }

                string validity = CheckValidity(candidate, now);
                if (validity != null)
                {
                    error = validity;
                    continue;
                }

                return candidate;
            }

            return null;
        }

        /// <summary>
        /// Whether BouncyCastle reads the certificate's public key without dereferencing null: an RSA, EC (on a named
        /// curve), Ed25519 or Ed448 one. It does for an EC key without its curve, or a DH, ElGamal or GOST key without
        /// parameters, which a peer may send: a kernel panic on Cosmos, which no catch stops. mbedTLS reads these
        /// kinds only, too.
        /// </summary>
        internal static bool HasReadableKey(BcCertificate certificate)
        {
            AlgorithmIdentifier algorithm = certificate.SubjectPublicKeyInfo?.Algorithm;
            if (algorithm == null)
            {
                return false;
            }

            DerObjectIdentifier oid = algorithm.Algorithm;
            if (oid.Equals(PkcsObjectIdentifiers.RsaEncryption) || oid.Equals(PkcsObjectIdentifiers.IdRsassaPss)
                || oid.Equals(EdECObjectIdentifiers.id_Ed25519) || oid.Equals(EdECObjectIdentifiers.id_Ed448))
            {
                return true;
            }

            // A named curve: an OID, as parsed, or as wrapped in X962Parameters in a certificate BouncyCastle made.
            return oid.Equals(X9ObjectIdentifiers.IdECPublicKey) && algorithm.Parameters?.ToAsn1Object() is DerObjectIdentifier;
        }

        // ZehOS (Etappe 12.2): signature algorithms accepted below a trust anchor. SHA-1, MD5 and anything unknown are
        // refused; RSASSA-PSS additionally needs a SHA-2 hash (checked in HasStrongSignature).
        private static readonly DerObjectIdentifier[] s_strongSignatures =
        {
            PkcsObjectIdentifiers.Sha256WithRsaEncryption,
            PkcsObjectIdentifiers.Sha384WithRsaEncryption,
            PkcsObjectIdentifiers.Sha512WithRsaEncryption,
            X9ObjectIdentifiers.ECDsaWithSha256,
            X9ObjectIdentifiers.ECDsaWithSha384,
            X9ObjectIdentifiers.ECDsaWithSha512,
            EdECObjectIdentifiers.id_Ed25519,
            EdECObjectIdentifiers.id_Ed448,
        };

        private static readonly DerObjectIdentifier[] s_strongHashes =
        {
            NistObjectIdentifiers.IdSha256,
            NistObjectIdentifiers.IdSha384,
            NistObjectIdentifiers.IdSha512,
        };

        private static readonly DerObjectIdentifier[] s_strongCurves =
        {
            SecObjectIdentifiers.SecP256r1,
            SecObjectIdentifiers.SecP384r1,
            SecObjectIdentifiers.SecP521r1,
        };

        private const int MinimumRsaBits = 2048;

        /// <summary>
        /// ZehOS (Etappe 12.2): why a certificate below the trust anchor is too weak, or null.
        /// </summary>
        internal static string CheckStrength(BcCertificate certificate)
        {
            if (!HasStrongSignature(certificate))
            {
                return "The certificate of " + Name(certificate) + " is signed with a weak or unknown algorithm (" + certificate.SigAlgName + ").";
            }

            if (!HasStrongKey(certificate))
            {
                return "The certificate of " + Name(certificate) + " has a weak key.";
            }

            return null;
        }

        internal static bool HasStrongSignature(BcCertificate certificate)
        {
            AlgorithmIdentifier algorithm = certificate.CertificateStructure.SignatureAlgorithm;
            if (Array.IndexOf(s_strongSignatures, algorithm.Algorithm) >= 0)
            {
                return true;
            }

            // RSASSA-PSS: a SHA-2 hash and MGF1 with the same hash (CA/B Baseline Requirements). Missing parameters
            // would mean SHA-1. Missing, NULL and malformed MGF1 parameters are refused without an exception (slow on
            // Cosmos; two at once on different threads halt it); only a malformed outer sequence is caught.
            if (!algorithm.Algorithm.Equals(PkcsObjectIdentifiers.IdRsassaPss) || algorithm.Parameters?.ToAsn1Object() is not Asn1Sequence sequence)
            {
                return false;
            }

            RsassaPssParameters parameters;
            try
            {
                parameters = RsassaPssParameters.GetInstance(sequence);
            }
            catch
            {
                // A malformed parameter sequence.
                return false;
            }

            DerObjectIdentifier hash = parameters.HashAlgorithm.Algorithm;
            AlgorithmIdentifier mask = parameters.MaskGenAlgorithm;
            return Array.IndexOf(s_strongHashes, hash) >= 0 && mask.Algorithm.Equals(PkcsObjectIdentifiers.IdMgf1) &&
                mask.Parameters?.ToAsn1Object() is Asn1Sequence maskHash && maskHash.Count is 1 or 2 &&
                maskHash[0] is DerObjectIdentifier maskOid && maskOid.Equals(hash);
        }

        /// <summary>
        /// ZehOS (Etappe 12.2): RSA of at least 2048 bits, EC on P-256, P-384 or P-521, Ed25519 or Ed448.
        /// </summary>
        internal static bool HasStrongKey(BcCertificate certificate)
        {
            if (!HasReadableKey(certificate))
            {
                return false;
            }

            AlgorithmIdentifier algorithm = certificate.SubjectPublicKeyInfo.Algorithm;
            if (algorithm.Algorithm.Equals(X9ObjectIdentifiers.IdECPublicKey))
            {
                return Array.IndexOf(s_strongCurves, (DerObjectIdentifier)algorithm.Parameters.ToAsn1Object()) >= 0;
            }

            if (algorithm.Algorithm.Equals(PkcsObjectIdentifiers.RsaEncryption) || algorithm.Algorithm.Equals(PkcsObjectIdentifiers.IdRsassaPss))
            {
                try
                {
                    return ((RsaKeyParameters)certificate.GetPublicKey()).Modulus.BitLength >= MinimumRsaBits;
                }
                catch
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// ZehOS (Etappe 12.2): the nameConstraints of every authority on the path (RFC 5280 4.2.1.10) for the leaf's
        /// subjectAltName dNSName and iPAddress (IPv4) entries, or why they are violated. Other name forms in a
        /// constraint can't be checked here, so such a chain is refused; so is a malformed extension.
        /// </summary>
        internal static string CheckNameConstraints(BcCertificate leaf, IList<BcCertificate> authorities)
        {
            List<string> dnsNames = null;
            List<byte[]> addresses = null;
            foreach (BcCertificate authority in authorities)
            {
                Asn1OctetString extension = authority.GetExtensionValue(X509Extensions.NameConstraints);
                if (extension == null)
                {
                    continue;
                }

                if (dnsNames == null)
                {
                    dnsNames = new List<string>();
                    addresses = new List<byte[]>();
                    if (!CollectNames(leaf, dnsNames, addresses))
                    {
                        return "The names of " + Name(leaf) + " can't be read.";
                    }
                }

                // A malformed extension, subtree or name in it refuses the chain; it only reaches this point when the
                // authority's signature was valid, so a peer can't make every handshake throw here.
                string error;
                try
                {
                    NameConstraints constraints = NameConstraints.GetInstance(X509ExtensionUtilities.FromExtensionValue(extension));
                    error = constraints == null || (constraints.PermittedSubtreesValue == null && constraints.ExcludedSubtreesValue == null)
                        ? "The name constraints of " + Name(authority) + " can't be read."
                        : CheckSubtrees(authority, Subtrees(constraints.PermittedSubtreesValue), true, dnsNames, addresses)
                            ?? CheckSubtrees(authority, Subtrees(constraints.ExcludedSubtreesValue), false, dnsNames, addresses);
                }
                catch
                {
                    error = "The name constraints of " + Name(authority) + " can't be read.";
                }

                if (error != null)
                {
                    return error;
                }
            }

            return null;
        }

        private static GeneralSubtree[] Subtrees(GeneralSubtrees subtrees)
        {
            if (subtrees?.ToAsn1Object() is not Asn1Sequence sequence)
            {
                return null;
            }

            // RFC 5280: SIZE (1..MAX), minimum 0, no maximum. Anything else is refused (null entry -> "can't be read").
            var result = new GeneralSubtree[sequence.Count == 0 ? 1 : sequence.Count];
            for (int i = 0; i < sequence.Count; i++)
            {
                GeneralSubtree subtree = GeneralSubtree.GetInstance(sequence[i]);
                result[i] = (subtree.MinimumObject == null || subtree.MinimumObject.Value.SignValue == 0) && subtree.MaximumObject == null ? subtree : null;
            }

            return result;
        }

        private static string CheckSubtrees(BcCertificate authority, GeneralSubtree[] subtrees, bool permitted, List<string> dnsNames, List<byte[]> addresses)
        {
            if (subtrees == null)
            {
                return null;
            }

            var dnsBases = new List<string>();
            var addressBases = new List<byte[]>();
            // RFC 5280: iPAddress is one name type; a permitted IPv6-only list permits no IPv4 address.
            bool addressSubtrees = false;
            foreach (GeneralSubtree subtree in subtrees)
            {
                if (subtree == null)
                {
                    return "The name constraints of " + Name(authority) + " can't be read.";
                }

                GeneralName name = subtree.Base;
                if (name.TagNo == GeneralName.DnsName)
                {
                    dnsBases.Add(DerIA5String.GetInstance(name.Name).GetString());
                }
                else if (name.TagNo == GeneralName.IPAddress)
                {
                    byte[] range = Asn1OctetString.GetInstance(name.Name).GetOctets();
                    addressSubtrees = true;
                    if (range.Length == 8)
                    {
                        addressBases.Add(range);
                    }
                    else if (range.Length != 32)
                    {
                        return "The name constraints of " + Name(authority) + " can't be read.";
                    }

                    // An IPv6 range (32 bytes) never contains an IPv4 address.
                }
                else
                {
                    return "The name constraints of " + Name(authority) + " use a name form that isn't supported.";
                }
            }

            foreach (string dnsName in dnsNames)
            {
                // An excluded subtree also catches a wildcard that could name a host in it (*.example.com against
                // excluded secret.example.com), as Chromium does; for permitted, the plain rule is already strict.
                bool inside = dnsBases.Exists(constraint => WithinDnsSubtree(dnsName, constraint) || (!permitted && WildcardReaches(dnsName, constraint)));
                // A permitted list of a form constrains names of that form only (RFC 5280 4.2.1.10).
                if (permitted ? dnsBases.Count > 0 && !inside : inside)
                {
                    return "The certificate of " + Name(authority) + " may not issue for " + dnsName + ".";
                }
            }

            foreach (byte[] address in addresses)
            {
                bool inside = addressBases.Exists(range => WithinAddressRange(address, range));
                if (permitted ? addressSubtrees && !inside : inside)
                {
                    return "The certificate of " + Name(authority) + " may not issue for that IP address.";
                }
            }

            return null;
        }

        private static bool CollectNames(BcCertificate leaf, List<string> dnsNames, List<byte[]> addresses)
        {
            Asn1OctetString extension = leaf.GetExtensionValue(X509Extensions.SubjectAlternativeName);
            if (extension == null)
            {
                return true;
            }

            try
            {
                GeneralName[] all = GeneralNames.GetInstance(X509ExtensionUtilities.FromExtensionValue(extension))?.GetNames();
                if (all == null)
                {
                    return false;
                }

                foreach (GeneralName name in all)
                {
                    if (name.TagNo == GeneralName.DnsName)
                    {
                        dnsNames.Add(DerIA5String.GetInstance(name.Name).GetString());
                    }
                    else if (name.TagNo == GeneralName.IPAddress)
                    {
                        byte[] address = Asn1OctetString.GetInstance(name.Name).GetOctets();
                        if (address.Length == 4)
                        {
                            addresses.Add(address);
                        }
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ZehOS (Etappe 12.2): RFC 5280 4.2.1.10: "example.com" covers itself and every name below it; ".example.com"
        /// only the names below it. "badexample.com" is not within "example.com".
        /// </summary>
        internal static bool WithinDnsSubtree(string name, string constraint)
        {
            name = name.TrimEnd('.');
            constraint = constraint.TrimEnd('.');
            if (constraint.Length == 0)
            {
                return true;
            }

            if (constraint[0] == '.')
            {
                return name.EndsWith(constraint, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(name, constraint, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("." + constraint, StringComparison.OrdinalIgnoreCase);
        }

        // "*.X" can name every host one label below X; it reaches a constraint at or below that level.
        private static bool WildcardReaches(string name, string constraint)
        {
            if (!name.StartsWith("*.", StringComparison.Ordinal))
            {
                return false;
            }

            string covered = name.Substring(2).TrimEnd('.');
            constraint = constraint.TrimEnd('.').TrimStart('.');
            return constraint.Length == 0 || string.Equals(constraint, covered, StringComparison.OrdinalIgnoreCase)
                || constraint.EndsWith("." + covered, StringComparison.OrdinalIgnoreCase);
        }

        private static bool WithinAddressRange(byte[] address, byte[] range)
        {
            for (int i = 0; i < 4; i++)
            {
                if ((address[i] & range[4 + i]) != (range[i] & range[4 + i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsSignedBy(BcCertificate certificate, BcCertificate issuer)
        {
            if (!HasReadableKey(issuer))
            {
                return false;
            }

            try
            {
                certificate.Verify(issuer.GetPublicKey());
                return true;
            }
            catch
            {
                // A different key, or a signature algorithm BouncyCastle doesn't know.
                return false;
            }
        }

        private static bool Contains(IList<BcCertificate> certificates, BcCertificate certificate)
        {
            // BouncyCastle compares the encodings, which it keeps.
            for (int i = 0; i < certificates.Count; i++)
            {
                if (certificates[i].Equals(certificate))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            return a.AsSpan().SequenceEqual(b);
        }

        private static string Name(BcCertificate certificate)
        {
            IList<string> commonNames = certificate.SubjectDN.GetValueList(X509Name.CN);
            return commonNames.Count > 0 ? commonNames[0] : certificate.SubjectDN.ToString();
        }
    }
}
