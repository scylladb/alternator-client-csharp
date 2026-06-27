// <copyright file="TlsConfigBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.Net.Security;
    using System.Security.Cryptography.X509Certificates;

    public sealed class TlsConfigBuilder
    {
        private List<string> customCaCertPaths = new List<string>();
        private List<X509Certificate2> customCaCertificates = new List<X509Certificate2>();
        private string? clientCertificatePath;
        private string? clientPrivateKeyPath;
        private List<X509Certificate2> clientCertificates = new List<X509Certificate2>();
        private RemoteCertificateValidationCallback? certificateValidationCallback;
        private bool trustSystemCaCerts = true;
        private bool trustAllCertificates;
        private bool verifyHostname = true;
        private bool tlsSessionResumptionEnabled = true;

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

        public TlsConfigBuilder WithCaCertificate(X509Certificate2? certificate)
        {
            if (certificate == null)
            {
                throw new ArgumentException("CA certificate cannot be null", nameof(certificate));
            }

            this.customCaCertificates.Add(certificate);
            return this;
        }

        public TlsConfigBuilder WithCaCertificates(IEnumerable<X509Certificate2>? certificates)
        {
            if (certificates == null)
            {
                this.customCaCertificates = new List<X509Certificate2>();
                return this;
            }

            this.customCaCertificates = new List<X509Certificate2>();
            foreach (var certificate in certificates)
            {
                this.WithCaCertificate(certificate);
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

        public TlsConfigBuilder WithClientCertificate(X509Certificate2? certificate)
        {
            if (certificate == null)
            {
                throw new ArgumentException("Client certificate cannot be null", nameof(certificate));
            }

            if (!certificate.HasPrivateKey)
            {
                throw new ArgumentException("Client certificate must include a private key", nameof(certificate));
            }

            this.clientCertificates.Add(certificate);
            return this;
        }

        public TlsConfigBuilder WithClientCertificates(IEnumerable<X509Certificate2>? certificates)
        {
            if (certificates == null)
            {
                this.clientCertificates = new List<X509Certificate2>();
                return this;
            }

            this.clientCertificates = new List<X509Certificate2>();
            foreach (var certificate in certificates)
            {
                this.WithClientCertificate(certificate);
            }

            return this;
        }

        public TlsConfigBuilder WithCertificateValidationCallback(RemoteCertificateValidationCallback? callback)
        {
            this.certificateValidationCallback = callback;
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

        public TlsConfigBuilder WithTlsSessionResumption(bool enabled)
        {
            this.tlsSessionResumptionEnabled = enabled;
            return this;
        }

        public TlsConfig Build()
        {
            if (!this.trustAllCertificates
                && !this.trustSystemCaCerts
                && this.customCaCertPaths.Count == 0
                && this.customCaCertificates.Count == 0
                && this.certificateValidationCallback == null)
            {
                throw new InvalidOperationException(
                    "Invalid TLS configuration: no trust source configured. Either enable trustSystemCaCerts, add custom CA certificates, or enable trustAllCertificates (for development only).");
            }

            return new TlsConfig(
                this.customCaCertPaths.AsReadOnly(),
                this.customCaCertificates.AsReadOnly(),
                this.clientCertificatePath,
                this.clientPrivateKeyPath,
                this.clientCertificates.AsReadOnly(),
                this.certificateValidationCallback,
                this.trustSystemCaCerts,
                this.trustAllCertificates,
                this.trustAllCertificates ? false : this.verifyHostname,
                this.tlsSessionResumptionEnabled);
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

        public TlsConfigBuilder withCaCertificate(X509Certificate2? certificate)
        {
            return this.WithCaCertificate(certificate);
        }

        public TlsConfigBuilder withCaCertificates(IEnumerable<X509Certificate2>? certificates)
        {
            return this.WithCaCertificates(certificates);
        }

        public TlsConfigBuilder withClientCertificate(string? certificatePath, string? privateKeyPath)
        {
            return this.WithClientCertificate(certificatePath, privateKeyPath);
        }

        public TlsConfigBuilder withClientCertificate(X509Certificate2? certificate)
        {
            return this.WithClientCertificate(certificate);
        }

        public TlsConfigBuilder withClientCertificates(IEnumerable<X509Certificate2>? certificates)
        {
            return this.WithClientCertificates(certificates);
        }

        public TlsConfigBuilder withCertificateValidationCallback(RemoteCertificateValidationCallback? callback)
        {
            return this.WithCertificateValidationCallback(callback);
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

        public TlsConfigBuilder withTlsSessionResumption(bool enabled)
        {
            return this.WithTlsSessionResumption(enabled);
        }

        public TlsConfig build()
        {
            return this.Build();
        }
#pragma warning restore SA1300, IDE1006
    }
}
