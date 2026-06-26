// <copyright file="TlsConfigBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public sealed class TlsConfigBuilder
    {
        private List<string> customCaCertPaths = new List<string>();
        private string? clientCertificatePath;
        private string? clientPrivateKeyPath;
        private bool trustSystemCaCerts = true;
        private bool trustAllCertificates;
        private bool verifyHostname = true;

        public TlsConfigBuilder WithCaCertPath(string? path)
        {
            if (path == null)
            {
                throw new ArgumentException("CA certificate path cannot be null", nameof(path));
            }

            this.customCaCertPaths.Add(path);
            return this;
        }

        public TlsConfigBuilder WithCaCertPaths(IEnumerable<string>? paths)
        {
            if (paths == null)
            {
                this.customCaCertPaths = new List<string>();
                return this;
            }

            this.customCaCertPaths = new List<string>();
            foreach (var path in paths)
            {
                this.WithCaCertPath(path);
            }

            return this;
        }

        public TlsConfigBuilder WithClientCertificate(string? certificatePath, string? privateKeyPath)
        {
            if (certificatePath == null)
            {
                throw new ArgumentException("Client certificate path cannot be null", nameof(certificatePath));
            }

            if (privateKeyPath == null)
            {
                throw new ArgumentException("Client private key path cannot be null", nameof(privateKeyPath));
            }

            this.clientCertificatePath = certificatePath;
            this.clientPrivateKeyPath = privateKeyPath;
            return this;
        }

        public TlsConfigBuilder WithTrustSystemCaCerts(bool trustSystemCaCerts)
        {
            this.trustSystemCaCerts = trustSystemCaCerts;
            return this;
        }

        public TlsConfigBuilder WithTrustAllCertificates(bool trustAllCertificates)
        {
            this.trustAllCertificates = trustAllCertificates;
            return this;
        }

        public TlsConfigBuilder WithVerifyHostname(bool verifyHostname)
        {
            this.verifyHostname = verifyHostname;
            return this;
        }

        public TlsConfig Build()
        {
            if (!this.trustAllCertificates && !this.trustSystemCaCerts && this.customCaCertPaths.Count == 0)
            {
                throw new InvalidOperationException(
                    "Invalid TLS configuration: no trust source configured. Either enable trustSystemCaCerts, add custom CA certificates, or enable trustAllCertificates (for development only).");
            }

            return new TlsConfig(
                this.customCaCertPaths.AsReadOnly(),
                this.clientCertificatePath,
                this.clientPrivateKeyPath,
                this.trustSystemCaCerts,
                this.trustAllCertificates,
                this.trustAllCertificates ? false : this.verifyHostname);
        }

#pragma warning disable SA1300, IDE1006
        public TlsConfigBuilder withCaCertPath(string? path)
        {
            return this.WithCaCertPath(path);
        }

        public TlsConfigBuilder withCaCertPaths(IEnumerable<string>? paths)
        {
            return this.WithCaCertPaths(paths);
        }

        public TlsConfigBuilder withClientCertificate(string? certificatePath, string? privateKeyPath)
        {
            return this.WithClientCertificate(certificatePath, privateKeyPath);
        }

        public TlsConfigBuilder withTrustSystemCaCerts(bool trustSystemCaCerts)
        {
            return this.WithTrustSystemCaCerts(trustSystemCaCerts);
        }

        public TlsConfigBuilder withTrustAllCertificates(bool trustAllCertificates)
        {
            return this.WithTrustAllCertificates(trustAllCertificates);
        }

        public TlsConfigBuilder withVerifyHostname(bool verifyHostname)
        {
            return this.WithVerifyHostname(verifyHostname);
        }

        public TlsConfig build()
        {
            return this.Build();
        }
#pragma warning restore SA1300, IDE1006
    }
}
