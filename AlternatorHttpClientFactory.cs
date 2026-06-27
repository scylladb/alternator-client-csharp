// <copyright file="AlternatorHttpClientFactory.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.Net;
    using System.Net.Http;
    using System.Net.Security;
    using System.Security.Cryptography.X509Certificates;
    using Amazon.Runtime;

    internal sealed class AlternatorHttpClientFactory : HttpClientFactory
    {
        private readonly AlternatorConfig config;
        private readonly Action<HttpClientHandler>? configureHttpClientHandler;
        private readonly Action<SocketsHttpHandler>? configureSocketsHttpHandler;

        internal AlternatorHttpClientFactory(
            AlternatorConfig config,
            Action<HttpClientHandler>? configureHttpClientHandler = null,
            Action<SocketsHttpHandler>? configureSocketsHttpHandler = null)
        {
            this.config = config;
            this.configureHttpClientHandler = configureHttpClientHandler;
            this.configureSocketsHttpHandler = configureSocketsHttpHandler;
        }

        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            HttpMessageHandler handler = CreatePrimaryHandler(
                this.config,
                this.configureHttpClientHandler,
                this.configureSocketsHttpHandler);
            if (this.config.OptimizeHeaders)
            {
                handler = new HeadersFilteringHttpMessageHandler(handler, this.config.HeadersWhitelist);
            }

            if (this.config.ResponseCompressionAlgorithms.Count > 0)
            {
                handler = new ResponseCompressionHttpMessageHandler(handler, this.config.ResponseCompressionAlgorithms);
            }

            return new HttpClient(handler);
        }

        internal static HttpMessageHandler CreatePrimaryHandler(
            AlternatorConfig config,
            Action<HttpClientHandler>? configureHttpClientHandler = null,
            Action<SocketsHttpHandler>? configureSocketsHttpHandler = null)
        {
            if (configureHttpClientHandler != null && configureSocketsHttpHandler != null)
            {
                throw new InvalidOperationException(
                    "HttpClientHandler and SocketsHttpHandler customizers cannot be used together.");
            }

            if (configureHttpClientHandler != null)
            {
                return CreateHandler(config.TlsConfig, config.MaxConnections, configureHttpClientHandler);
            }

            return CreateSocketsHandler(config, configureSocketsHttpHandler);
        }

        internal static HttpClientHandler CreateHandler(
            TlsConfig tlsConfig,
            int maxConnections,
            Action<HttpClientHandler>? configureHandler = null)
        {
            if (!tlsConfig.TlsSessionResumptionEnabled)
            {
                throw new NotSupportedException(
                    "Disabling TLS session resumption is only supported by the default SocketsHttpHandler transport.");
            }

            var handler = new HttpClientHandler
            {
                MaxConnectionsPerServer = maxConnections,
            };
            if (tlsConfig.hasClientCertificate())
            {
                handler.ClientCertificateOptions = ClientCertificateOption.Manual;
                handler.ClientCertificates.AddRange(LoadClientCertificates(tlsConfig));
            }

            if (NeedsCustomCertificateValidation(tlsConfig))
            {
                handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, sslPolicyErrors) =>
                    ValidateCertificate(tlsConfig, request, certificate, chain, sslPolicyErrors);
            }

            configureHandler?.Invoke(handler);
            return handler;
        }

        internal static SocketsHttpHandler CreateSocketsHandler(
            AlternatorConfig config,
            Action<SocketsHttpHandler>? configureHandler = null)
        {
            var handler = new SocketsHttpHandler
            {
                MaxConnectionsPerServer = config.MaxConnections,
                PooledConnectionIdleTimeout = ToTimeout(config.ConnectionMaxIdleTimeMs),
                PooledConnectionLifetime = ToTimeout(config.ConnectionTimeToLiveMs),
            };
            handler.SslOptions.AllowTlsResume = config.TlsConfig.TlsSessionResumptionEnabled;

            if (config.ConnectionTimeoutMs > 0)
            {
                handler.ConnectTimeout = TimeSpan.FromMilliseconds(config.ConnectionTimeoutMs);
            }

            if (config.TlsConfig.hasClientCertificate())
            {
                handler.SslOptions.ClientCertificates = LoadClientCertificates(config.TlsConfig);
            }

            if (NeedsCustomCertificateValidation(config.TlsConfig))
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) =>
                    ValidateCertificate(config.TlsConfig, sender, certificate, chain, sslPolicyErrors);
            }

            configureHandler?.Invoke(handler);
            return handler;
        }

        private static bool NeedsCustomCertificateValidation(TlsConfig tlsConfig)
        {
            return tlsConfig.CertificateValidationCallback != null
                || tlsConfig.TrustAllCertificates
                || tlsConfig.CustomCaCertPaths.Count > 0
                || tlsConfig.CustomCaCertificates.Count > 0
                || !tlsConfig.VerifyHostname;
        }

        private static X509CertificateCollection LoadClientCertificates(TlsConfig tlsConfig)
        {
            var certificates = new X509CertificateCollection();
            foreach (var certificate in tlsConfig.ClientCertificates)
            {
                certificates.Add(certificate);
            }

            if (tlsConfig.ClientCertificatePath != null && tlsConfig.ClientPrivateKeyPath != null)
            {
                certificates.Add(X509Certificate2.CreateFromPemFile(
                    tlsConfig.ClientCertificatePath,
                    tlsConfig.ClientPrivateKeyPath));
            }

            return certificates;
        }

        private static TimeSpan ToTimeout(long timeoutMs)
        {
            return timeoutMs == 0
                ? System.Threading.Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(timeoutMs);
        }

        private static bool ValidateCertificate(
            TlsConfig tlsConfig,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return ValidateCertificate(tlsConfig, null, certificate, chain, sslPolicyErrors);
        }

        private static bool ValidateCertificate(
            TlsConfig tlsConfig,
            object? sender,
            X509Certificate? certificate,
            X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            var certificate2 = certificate as X509Certificate2;
            X509Certificate2? convertedCertificate = null;
            if (certificate != null && certificate2 == null)
            {
                convertedCertificate = new X509Certificate2(certificate);
                certificate2 = convertedCertificate;
            }

            try
            {
                return ValidateCertificate(tlsConfig, sender, certificate2, chain, sslPolicyErrors);
            }
            finally
            {
                convertedCertificate?.Dispose();
            }
        }

        private static bool ValidateCertificate(
            TlsConfig tlsConfig,
            X509Certificate2? certificate,
            X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return ValidateCertificate(tlsConfig, null, certificate, chain, sslPolicyErrors);
        }

        private static bool ValidateCertificate(
            TlsConfig tlsConfig,
            object? sender,
            X509Certificate2? certificate,
            X509Chain? chain,
            SslPolicyErrors sslPolicyErrors)
        {
            if (tlsConfig.CertificateValidationCallback != null)
            {
                return tlsConfig.CertificateValidationCallback(sender ?? tlsConfig, certificate, chain, sslPolicyErrors);
            }

            if (tlsConfig.TrustAllCertificates)
            {
                return true;
            }

            if (sslPolicyErrors == SslPolicyErrors.None)
            {
                return true;
            }

            var hasNameMismatch = (sslPolicyErrors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0;
            var errorsWithoutNameMismatch = sslPolicyErrors & ~SslPolicyErrors.RemoteCertificateNameMismatch;
            if (!tlsConfig.VerifyHostname && errorsWithoutNameMismatch == SslPolicyErrors.None)
            {
                return true;
            }

            var unsupportedErrors = errorsWithoutNameMismatch & ~SslPolicyErrors.RemoteCertificateChainErrors;
            if (unsupportedErrors != SslPolicyErrors.None)
            {
                return false;
            }

            if (certificate == null || (tlsConfig.CustomCaCertPaths.Count == 0 && tlsConfig.CustomCaCertificates.Count == 0))
            {
                return false;
            }

            var hostnameAccepted = !tlsConfig.VerifyHostname || !hasNameMismatch;
            if (tlsConfig.TrustSystemCaCerts && chain != null && chain.Build(certificate))
            {
                return hostnameAccepted;
            }

            using var customChain = new X509Chain();
            customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            foreach (var certPath in tlsConfig.CustomCaCertPaths)
            {
                customChain.ChainPolicy.CustomTrustStore.Add(new X509Certificate2(certPath));
            }

            foreach (var customCaCertificate in tlsConfig.CustomCaCertificates)
            {
                customChain.ChainPolicy.CustomTrustStore.Add(customCaCertificate);
            }

            var chainValid = customChain.Build(certificate);
            if (chainValid)
            {
                return hostnameAccepted;
            }

            return false;
        }
    }
}
