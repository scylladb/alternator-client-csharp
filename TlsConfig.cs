// <copyright file="TlsConfig.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public sealed class TlsConfig
    {
        private static readonly TlsConfig TrustAllInstance = new TlsConfig(
            new List<string>(),
            null,
            null,
            false,
            true,
            false,
            TlsSessionCacheConfig.GetDefault());

        private static readonly TlsConfig SystemDefaultInstance = new TlsConfig(
            new List<string>(),
            null,
            null,
            true,
            false,
            true,
            TlsSessionCacheConfig.GetDefault());

        internal TlsConfig(
            IReadOnlyList<string> customCaCertPaths,
            string? clientCertificatePath,
            string? clientPrivateKeyPath,
            bool trustSystemCaCerts,
            bool trustAllCertificates,
            bool verifyHostname,
            TlsSessionCacheConfig sessionCacheConfig)
        {
            this.CustomCaCertPaths = new List<string>(customCaCertPaths ?? Array.Empty<string>()).AsReadOnly();
            this.ClientCertificatePath = clientCertificatePath;
            this.ClientPrivateKeyPath = clientPrivateKeyPath;
            this.TrustSystemCaCerts = trustSystemCaCerts;
            this.TrustAllCertificates = trustAllCertificates;
            this.VerifyHostname = verifyHostname;
            this.SessionCacheConfig = sessionCacheConfig ?? TlsSessionCacheConfig.GetDefault();
        }

        public IReadOnlyList<string> CustomCaCertPaths { get; }

        public string? ClientCertificatePath { get; }

        public string? ClientPrivateKeyPath { get; }

        public bool HasClientCertificate => this.ClientCertificatePath != null && this.ClientPrivateKeyPath != null;

        public bool TrustSystemCaCerts { get; }

        public bool TrustAllCertificates { get; }

        public bool VerifyHostname { get; }

        public TlsSessionCacheConfig SessionCacheConfig { get; }

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

        public TlsSessionCacheConfig getSessionCacheConfig()
        {
            return this.SessionCacheConfig;
        }
#pragma warning restore SA1300, IDE1006

        public override string ToString()
        {
            return "TlsConfig{"
                + "customCaCertPaths="
                + "["
                + string.Join(", ", this.CustomCaCertPaths)
                + "]"
                + ", clientCertificatePath="
                + (this.ClientCertificatePath ?? "null")
                + ", clientPrivateKeyPath="
                + (this.ClientPrivateKeyPath ?? "null")
                + ", trustSystemCaCerts="
                + this.TrustSystemCaCerts.ToString().ToLowerInvariant()
                + ", trustAllCertificates="
                + this.TrustAllCertificates.ToString().ToLowerInvariant()
                + ", verifyHostname="
                + this.VerifyHostname.ToString().ToLowerInvariant()
                + ", sessionCacheConfig="
                + this.SessionCacheConfig
                + "}";
        }

        public override bool Equals(object? obj)
        {
            return obj is TlsConfig other
                && this.TrustSystemCaCerts == other.TrustSystemCaCerts
                && this.TrustAllCertificates == other.TrustAllCertificates
                && this.VerifyHostname == other.VerifyHostname
                && this.CustomCaCertPaths.SequenceEqual(other.CustomCaCertPaths)
                && string.Equals(this.ClientCertificatePath, other.ClientCertificatePath, StringComparison.Ordinal)
                && string.Equals(this.ClientPrivateKeyPath, other.ClientPrivateKeyPath, StringComparison.Ordinal)
                && Equals(this.SessionCacheConfig, other.SessionCacheConfig);
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
            hash.Add(this.TrustSystemCaCerts);
            hash.Add(this.TrustAllCertificates);
            hash.Add(this.VerifyHostname);
            hash.Add(this.SessionCacheConfig);
            return hash.ToHashCode();
        }
    }
}
