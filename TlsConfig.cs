// <copyright file="TlsConfig.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.Net.Security;
    using System.Security.Cryptography.X509Certificates;

    public sealed class TlsConfig
    {
        private static readonly TlsConfig TrustAllInstance = new TlsConfig(
            new List<string>(),
            new List<X509Certificate2>(),
            null,
            null,
            new List<X509Certificate2>(),
            null,
            false,
            true,
            false,
            true);

        private static readonly TlsConfig SystemDefaultInstance = new TlsConfig(
            new List<string>(),
            new List<X509Certificate2>(),
            null,
            null,
            new List<X509Certificate2>(),
            null,
            true,
            false,
            true,
            true);

        internal TlsConfig(
            IReadOnlyList<string> customCaCertPaths,
            IReadOnlyList<X509Certificate2> customCaCertificates,
            string? clientCertificatePath,
            string? clientPrivateKeyPath,
            IReadOnlyList<X509Certificate2> clientCertificates,
            RemoteCertificateValidationCallback? certificateValidationCallback,
            bool trustSystemCaCerts,
            bool trustAllCertificates,
            bool verifyHostname,
            bool tlsSessionResumptionEnabled)
        {
            this.CustomCaCertPaths = new List<string>(customCaCertPaths ?? Array.Empty<string>()).AsReadOnly();
            this.CustomCaCertificates = CopyCertificates(customCaCertificates);
            this.ClientCertificatePath = clientCertificatePath;
            this.ClientPrivateKeyPath = clientPrivateKeyPath;
            this.ClientCertificates = CopyCertificates(clientCertificates);
            this.CertificateValidationCallback = certificateValidationCallback;
            this.TrustSystemCaCerts = trustSystemCaCerts;
            this.TrustAllCertificates = trustAllCertificates;
            this.VerifyHostname = verifyHostname;
            this.TlsSessionResumptionEnabled = tlsSessionResumptionEnabled;
        }

        public IReadOnlyList<string> CustomCaCertPaths { get; }

        public IReadOnlyList<X509Certificate2> CustomCaCertificates { get; }

        public string? ClientCertificatePath { get; }

        public string? ClientPrivateKeyPath { get; }

        public IReadOnlyList<X509Certificate2> ClientCertificates { get; }

        public bool HasClientCertificate =>
            (this.ClientCertificatePath != null && this.ClientPrivateKeyPath != null) || this.ClientCertificates.Count > 0;

        public RemoteCertificateValidationCallback? CertificateValidationCallback { get; }

        public bool TrustSystemCaCerts { get; }

        public bool TrustAllCertificates { get; }

        public bool VerifyHostname { get; }

        public bool TlsSessionResumptionEnabled { get; }

        public static TlsConfig TrustAll()
        {
            return TrustAllInstance;
        }

        public static TlsConfig SystemDefault()
        {
            return SystemDefaultInstance;
        }

        public static TlsConfigBuilder Builder()
        {
            return new TlsConfigBuilder();
        }

#pragma warning disable SA1300, IDE1006
        public static TlsConfig trustAll()
        {
            return TrustAll();
        }

        public static TlsConfig systemDefault()
        {
            return SystemDefault();
        }

        public static TlsConfigBuilder builder()
        {
            return Builder();
        }

        public IReadOnlyList<string> getCustomCaCertPaths()
        {
            return this.CustomCaCertPaths;
        }

        public string? getClientCertificatePath()
        {
            return this.ClientCertificatePath;
        }

        public string? getClientPrivateKeyPath()
        {
            return this.ClientPrivateKeyPath;
        }

        public IReadOnlyList<X509Certificate2> getCustomCaCertificates()
        {
            return this.CustomCaCertificates;
        }

        public IReadOnlyList<X509Certificate2> getClientCertificates()
        {
            return this.ClientCertificates;
        }

        public RemoteCertificateValidationCallback? getCertificateValidationCallback()
        {
            return this.CertificateValidationCallback;
        }

        public bool hasClientCertificate()
        {
            return this.HasClientCertificate;
        }

        public bool isTrustSystemCaCerts()
        {
            return this.TrustSystemCaCerts;
        }

        public bool isTrustAllCertificates()
        {
            return this.TrustAllCertificates;
        }

        public bool isVerifyHostname()
        {
            return this.VerifyHostname;
        }

        public bool isTlsSessionResumptionEnabled()
        {
            return this.TlsSessionResumptionEnabled;
        }

#pragma warning restore SA1300, IDE1006

        public override string ToString()
        {
            return "TlsConfig{"
                + "customCaCertPaths="
                + "["
                + string.Join(", ", this.CustomCaCertPaths)
                + "]"
                + ", customCaCertificates="
                + this.CustomCaCertificates.Count
                + ", clientCertificatePath="
                + (this.ClientCertificatePath ?? "null")
                + ", clientPrivateKeyPath="
                + (this.ClientPrivateKeyPath ?? "null")
                + ", clientCertificates="
                + this.ClientCertificates.Count
                + ", certificateValidationCallback="
                + (this.CertificateValidationCallback == null ? "null" : "configured")
                + ", trustSystemCaCerts="
                + this.TrustSystemCaCerts.ToString().ToLowerInvariant()
                + ", trustAllCertificates="
                + this.TrustAllCertificates.ToString().ToLowerInvariant()
                + ", verifyHostname="
                + this.VerifyHostname.ToString().ToLowerInvariant()
                + ", tlsSessionResumptionEnabled="
                + this.TlsSessionResumptionEnabled.ToString().ToLowerInvariant()
                + "}";
        }

        public override bool Equals(object? obj)
        {
            return obj is TlsConfig other
                && this.TrustSystemCaCerts == other.TrustSystemCaCerts
                && this.TrustAllCertificates == other.TrustAllCertificates
                && this.VerifyHostname == other.VerifyHostname
                && this.TlsSessionResumptionEnabled == other.TlsSessionResumptionEnabled
                && this.CustomCaCertPaths.SequenceEqual(other.CustomCaCertPaths)
                && CertificatesEqual(this.CustomCaCertificates, other.CustomCaCertificates)
                && string.Equals(this.ClientCertificatePath, other.ClientCertificatePath, StringComparison.Ordinal)
                && string.Equals(this.ClientPrivateKeyPath, other.ClientPrivateKeyPath, StringComparison.Ordinal)
                && CertificatesEqual(this.ClientCertificates, other.ClientCertificates)
                && Equals(this.CertificateValidationCallback, other.CertificateValidationCallback);
        }

        public override int GetHashCode()
        {
            var hash = default(HashCode);
            foreach (var path in this.CustomCaCertPaths)
            {
                hash.Add(path, StringComparer.Ordinal);
            }

            hash.Add(this.ClientCertificatePath, StringComparer.Ordinal);
            hash.Add(this.ClientPrivateKeyPath, StringComparer.Ordinal);
            AddCertificateHashes(hash, this.CustomCaCertificates);
            AddCertificateHashes(hash, this.ClientCertificates);
            hash.Add(this.CertificateValidationCallback);
            hash.Add(this.TrustSystemCaCerts);
            hash.Add(this.TrustAllCertificates);
            hash.Add(this.VerifyHostname);
            hash.Add(this.TlsSessionResumptionEnabled);
            return hash.ToHashCode();
        }

        private static IReadOnlyList<X509Certificate2> CopyCertificates(IEnumerable<X509Certificate2>? certificates)
        {
            if (certificates == null)
            {
                return Array.Empty<X509Certificate2>();
            }

            return certificates.Select(certificate => new X509Certificate2(certificate)).ToList().AsReadOnly();
        }

        private static bool CertificatesEqual(
            IReadOnlyList<X509Certificate2> left,
            IReadOnlyList<X509Certificate2> right)
        {
            return left.Count == right.Count
                && left.Zip(right, (leftCertificate, rightCertificate) =>
                    string.Equals(leftCertificate.Thumbprint, rightCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                    .All(equal => equal);
        }

        private static void AddCertificateHashes(HashCode hash, IEnumerable<X509Certificate2> certificates)
        {
            foreach (var certificate in certificates)
            {
                hash.Add(certificate.Thumbprint, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
