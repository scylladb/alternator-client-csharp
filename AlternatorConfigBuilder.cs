// <copyright file="AlternatorConfigBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using ScyllaDB.Alternator.KeyRouting;
    using ScyllaDB.Alternator.Routing;

    public class AlternatorConfigBuilder
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly List<string> seedHosts = new List<string>();
        private string scheme = string.Empty;
        private int port = -1;
        private RoutingScope? routingScope;
        private RequestCompressionAlgorithm compressionAlgorithm = RequestCompressionAlgorithm.None;
        private int minCompressionSizeBytes = AlternatorConfig.DefaultMinCompressionSizeBytes;
        private IReadOnlyList<ResponseCompressionAlgorithm> responseCompressionAlgorithms =
            AlternatorConfig.DefaultResponseCompressionAlgorithms;

        private bool optimizeHeaders;
        private ISet<string>? headersWhitelist;
        private bool headersWhitelistWasSet;
        private Func<AlternatorConfig, IEnumerable<string>>? customOptimizeHeaders;
        private bool userAgentEnabled = true;
        private bool authenticationEnabledValue = true;
        private KeyRouteAffinityConfig? keyRouteAffinityConfig;
        private long activeRefreshIntervalMs = AlternatorConfig.DefaultActiveRefreshIntervalMs;
        private long idleRefreshIntervalMs = AlternatorConfig.DefaultIdleRefreshIntervalMs;
        private int maxConnections = AlternatorConfig.DefaultMaxConnections;
        private long connectionMaxIdleTimeMs = AlternatorConfig.DefaultConnectionMaxIdleTimeMs;
        private bool connectionMaxIdleTimeMsSet;
        private long connectionTimeToLiveMs = AlternatorConfig.DefaultConnectionTimeToLiveMs;
        private long connectionAcquisitionTimeoutMs = AlternatorConfig.DefaultConnectionAcquisitionTimeoutMs;
        private long connectionTimeoutMs = AlternatorConfig.DefaultConnectionTimeoutMs;
        private long httpClientTimeoutMs = AlternatorConfig.DefaultHttpClientTimeoutMs;
        private TlsConfig? tlsConfig;

        public AlternatorConfigBuilder WithSeedNode(Uri? seedUri)
        {
            if (seedUri == null)
            {
                return this;
            }

            this.seedHosts.Clear();
            this.seedHosts.Add(seedUri.Host);
            this.scheme = seedUri.Scheme;
            this.port = seedUri.Port;
            return this;
        }

        public AlternatorConfigBuilder WithSeedNode(string? seedUri)
        {
            if (seedUri == null)
            {
                return this;
            }

            if (string.IsNullOrWhiteSpace(seedUri))
            {
                throw new ArgumentException("Seed URI cannot be null or empty.", nameof(seedUri));
            }

            return this.WithSeedNode(new Uri(seedUri));
        }

        public AlternatorConfigBuilder WithSeedHost(string? host)
        {
            if (host == null)
            {
                return this;
            }

            this.seedHosts.Clear();
            this.seedHosts.Add(host);
            return this;
        }

        public AlternatorConfigBuilder WithSeedHosts(IEnumerable<string>? hosts)
        {
            if (hosts == null)
            {
                return this;
            }

            this.seedHosts.Clear();
            this.seedHosts.AddRange(hosts);
            return this;
        }

        public AlternatorConfigBuilder WithScheme(string scheme)
        {
            this.scheme = scheme ?? string.Empty;
            return this;
        }

        public AlternatorConfigBuilder WithPort(int port)
        {
            this.port = port;
            return this;
        }

        public AlternatorConfigBuilder WithRoutingScope(RoutingScope? routingScope)
        {
            this.routingScope = routingScope;
            return this;
        }

        public AlternatorConfigBuilder WithCompressionAlgorithm(RequestCompressionAlgorithm algorithm)
        {
            this.compressionAlgorithm = algorithm;
            return this;
        }

        public AlternatorConfigBuilder WithCompressionAlgorithm(RequestCompressionAlgorithm? algorithm)
        {
            this.compressionAlgorithm = algorithm ?? RequestCompressionAlgorithm.None;
            return this;
        }

        public AlternatorConfigBuilder WithMinCompressionSizeBytes(int minCompressionSizeBytes)
        {
            this.minCompressionSizeBytes = minCompressionSizeBytes;
            return this;
        }

        public AlternatorConfigBuilder WithResponseCompression(params ResponseCompressionAlgorithm[] algorithms)
        {
            return this.WithResponseCompression((IEnumerable<ResponseCompressionAlgorithm>)algorithms);
        }

        public AlternatorConfigBuilder WithResponseCompression(IEnumerable<ResponseCompressionAlgorithm> algorithms)
        {
            this.responseCompressionAlgorithms = AlternatorConfig.NormalizeResponseCompressionAlgorithms(algorithms);
            return this;
        }

        public AlternatorConfigBuilder WithoutResponseCompression()
        {
            this.responseCompressionAlgorithms = Array.Empty<ResponseCompressionAlgorithm>();
            return this;
        }

        public AlternatorConfigBuilder WithOptimizeHeaders(bool optimizeHeaders)
        {
            this.optimizeHeaders = optimizeHeaders;
            return this;
        }

        public AlternatorConfigBuilder WithHeadersWhitelist(IEnumerable<string>? headers)
        {
            this.headersWhitelist = headers != null
                ? new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase)
                : null;
            this.headersWhitelistWasSet = true;
            this.customOptimizeHeaders = null;
            return this;
        }

        public AlternatorConfigBuilder WithCustomOptimizeHeaders(Func<AlternatorConfig, IEnumerable<string>> customOptimizeHeaders)
        {
            this.customOptimizeHeaders = customOptimizeHeaders ?? throw new ArgumentNullException(nameof(customOptimizeHeaders));
            this.headersWhitelist = null;
            this.headersWhitelistWasSet = false;
            this.optimizeHeaders = true;
            return this;
        }

        public AlternatorConfigBuilder WithUserAgentEnabled(bool userAgentEnabled)
        {
            this.userAgentEnabled = userAgentEnabled;
            return this;
        }

        public AlternatorConfigBuilder WithAuthenticationEnabled(bool authenticationEnabled)
        {
            this.authenticationEnabledValue = authenticationEnabled;
            return this;
        }

        public AlternatorConfigBuilder WithKeyRouteAffinity(KeyRouteAffinityConfig? keyRouteAffinityConfig)
        {
            this.keyRouteAffinityConfig = keyRouteAffinityConfig;
            return this;
        }

        public AlternatorConfigBuilder WithKeyRouteAffinity(KeyRouteAffinity type)
        {
            this.keyRouteAffinityConfig = KeyRouteAffinityConfig.Of(type);
            return this;
        }

        public AlternatorConfigBuilder WithKeyRouteAffinity(KeyRouteAffinity? type)
        {
            this.keyRouteAffinityConfig = KeyRouteAffinityConfig.Of(type);
            return this;
        }

        public AlternatorConfigBuilder WithActiveRefreshIntervalMs(long intervalMs)
        {
            this.activeRefreshIntervalMs = intervalMs;
            return this;
        }

        public AlternatorConfigBuilder WithIdleRefreshIntervalMs(long intervalMs)
        {
            this.idleRefreshIntervalMs = intervalMs;
            return this;
        }

        public AlternatorConfigBuilder WithMaxConnections(int maxConnections)
        {
            this.maxConnections = maxConnections;
            return this;
        }

        public AlternatorConfigBuilder WithMaxIdleHttpConnections(int maxIdleHttpConnections)
        {
            return this.WithMaxConnections(maxIdleHttpConnections);
        }

        public AlternatorConfigBuilder WithMaxIdleHttpConnectionsPerHost(int maxIdleHttpConnectionsPerHost)
        {
            return this.WithMaxConnections(maxIdleHttpConnectionsPerHost);
        }

        public AlternatorConfigBuilder WithConnectionMaxIdleTimeMs(long connectionMaxIdleTimeMs)
        {
            this.connectionMaxIdleTimeMs = connectionMaxIdleTimeMs;
            this.connectionMaxIdleTimeMsSet = true;
            return this;
        }

        public AlternatorConfigBuilder WithIdleHttpConnectionTimeoutMs(long idleHttpConnectionTimeoutMs)
        {
            return this.WithConnectionMaxIdleTimeMs(idleHttpConnectionTimeoutMs);
        }

        public AlternatorConfigBuilder WithConnectionTimeToLiveMs(long connectionTimeToLiveMs)
        {
            this.connectionTimeToLiveMs = connectionTimeToLiveMs;
            return this;
        }

        public AlternatorConfigBuilder WithConnectionAcquisitionTimeoutMs(long connectionAcquisitionTimeoutMs)
        {
            this.connectionAcquisitionTimeoutMs = connectionAcquisitionTimeoutMs;
            return this;
        }

        public AlternatorConfigBuilder WithConnectionTimeoutMs(long connectionTimeoutMs)
        {
            this.connectionTimeoutMs = connectionTimeoutMs;
            this.httpClientTimeoutMs = connectionTimeoutMs;
            return this;
        }

        public AlternatorConfigBuilder WithHttpClientTimeoutMs(long httpClientTimeoutMs)
        {
            this.httpClientTimeoutMs = httpClientTimeoutMs;
            return this;
        }

        public AlternatorConfigBuilder WithTlsConfig(TlsConfig? tlsConfig)
        {
            this.tlsConfig = tlsConfig;
            return this;
        }

#pragma warning disable SA1300, IDE1006
        public AlternatorConfigBuilder withSeedNode(Uri? seedUri)
        {
            return this.WithSeedNode(seedUri);
        }

        public AlternatorConfigBuilder withSeedNode(string? seedUri)
        {
            return this.WithSeedNode(seedUri);
        }

        public AlternatorConfigBuilder withSeedHost(string? host)
        {
            return this.WithSeedHost(host);
        }

        public AlternatorConfigBuilder withSeedHosts(IEnumerable<string>? hosts)
        {
            return this.WithSeedHosts(hosts);
        }

        public AlternatorConfigBuilder withScheme(string scheme)
        {
            return this.WithScheme(scheme);
        }

        public AlternatorConfigBuilder withPort(int port)
        {
            return this.WithPort(port);
        }

        public AlternatorConfigBuilder withRoutingScope(RoutingScope? routingScope)
        {
            return this.WithRoutingScope(routingScope);
        }

        public AlternatorConfigBuilder withCompressionAlgorithm(RequestCompressionAlgorithm algorithm)
        {
            return this.WithCompressionAlgorithm(algorithm);
        }

        public AlternatorConfigBuilder withCompressionAlgorithm(RequestCompressionAlgorithm? algorithm)
        {
            return this.WithCompressionAlgorithm(algorithm);
        }

        public AlternatorConfigBuilder withMinCompressionSizeBytes(int minCompressionSizeBytes)
        {
            return this.WithMinCompressionSizeBytes(minCompressionSizeBytes);
        }

        public AlternatorConfigBuilder withResponseCompression(params ResponseCompressionAlgorithm[] algorithms)
        {
            return this.WithResponseCompression(algorithms);
        }

        public AlternatorConfigBuilder withResponseCompression(IEnumerable<ResponseCompressionAlgorithm> algorithms)
        {
            return this.WithResponseCompression(algorithms);
        }

        public AlternatorConfigBuilder withoutResponseCompression()
        {
            return this.WithoutResponseCompression();
        }

        public AlternatorConfigBuilder withOptimizeHeaders(bool optimizeHeaders)
        {
            return this.WithOptimizeHeaders(optimizeHeaders);
        }

        public AlternatorConfigBuilder withHeadersWhitelist(IEnumerable<string>? headers)
        {
            return this.WithHeadersWhitelist(headers);
        }

        public AlternatorConfigBuilder withCustomOptimizeHeaders(Func<AlternatorConfig, IEnumerable<string>> customOptimizeHeaders)
        {
            return this.WithCustomOptimizeHeaders(customOptimizeHeaders);
        }

        public AlternatorConfigBuilder withUserAgentEnabled(bool userAgentEnabled)
        {
            return this.WithUserAgentEnabled(userAgentEnabled);
        }

        public AlternatorConfigBuilder withAuthenticationEnabled(bool authenticationEnabled)
        {
            return this.WithAuthenticationEnabled(authenticationEnabled);
        }

        public AlternatorConfigBuilder authenticationEnabled(bool authenticationEnabled)
        {
            return this.WithAuthenticationEnabled(authenticationEnabled);
        }

        public AlternatorConfigBuilder withKeyRouteAffinity(KeyRouteAffinityConfig? keyRouteAffinityConfig)
        {
            return this.WithKeyRouteAffinity(keyRouteAffinityConfig);
        }

        public AlternatorConfigBuilder withKeyRouteAffinity(KeyRouteAffinity type)
        {
            return this.WithKeyRouteAffinity(type);
        }

        public AlternatorConfigBuilder withKeyRouteAffinity(KeyRouteAffinity? type)
        {
            return this.WithKeyRouteAffinity(type);
        }

        public ISet<string> getRequiredHeaders()
        {
            return this.GetRequiredHeaders();
        }

        public AlternatorConfigBuilder withTlsConfig(TlsConfig? tlsConfig)
        {
            return this.WithTlsConfig(tlsConfig);
        }

        public AlternatorConfigBuilder withActiveRefreshIntervalMs(long intervalMs)
        {
            return this.WithActiveRefreshIntervalMs(intervalMs);
        }

        public AlternatorConfigBuilder withIdleRefreshIntervalMs(long intervalMs)
        {
            return this.WithIdleRefreshIntervalMs(intervalMs);
        }

        public AlternatorConfigBuilder withMaxConnections(int maxConnections)
        {
            return this.WithMaxConnections(maxConnections);
        }

        public AlternatorConfigBuilder withMaxIdleHttpConnections(int maxIdleHttpConnections)
        {
            return this.WithMaxIdleHttpConnections(maxIdleHttpConnections);
        }

        public AlternatorConfigBuilder withMaxIdleHttpConnectionsPerHost(int maxIdleHttpConnectionsPerHost)
        {
            return this.WithMaxIdleHttpConnectionsPerHost(maxIdleHttpConnectionsPerHost);
        }

        public AlternatorConfigBuilder withConnectionMaxIdleTimeMs(long connectionMaxIdleTimeMs)
        {
            return this.WithConnectionMaxIdleTimeMs(connectionMaxIdleTimeMs);
        }

        public AlternatorConfigBuilder withIdleHttpConnectionTimeoutMs(long idleHttpConnectionTimeoutMs)
        {
            return this.WithIdleHttpConnectionTimeoutMs(idleHttpConnectionTimeoutMs);
        }

        public AlternatorConfigBuilder withConnectionTimeToLiveMs(long connectionTimeToLiveMs)
        {
            return this.WithConnectionTimeToLiveMs(connectionTimeToLiveMs);
        }

        public AlternatorConfigBuilder withConnectionAcquisitionTimeoutMs(long connectionAcquisitionTimeoutMs)
        {
            return this.WithConnectionAcquisitionTimeoutMs(connectionAcquisitionTimeoutMs);
        }

        public AlternatorConfigBuilder withConnectionTimeoutMs(long connectionTimeoutMs)
        {
            return this.WithConnectionTimeoutMs(connectionTimeoutMs);
        }

        public AlternatorConfigBuilder withHttpClientTimeoutMs(long httpClientTimeoutMs)
        {
            return this.WithHttpClientTimeoutMs(httpClientTimeoutMs);
        }

        public AlternatorConfig build()
        {
            return this.Build();
        }
#pragma warning restore SA1300, IDE1006

        public ISet<string> GetRequiredHeaders()
        {
            var required = new HashSet<string>(AlternatorConfig.BaseRequiredHeaders, StringComparer.OrdinalIgnoreCase);
            if (this.compressionAlgorithm.IsEnabled())
            {
                required.UnionWith(AlternatorConfig.CompressionHeaders);
            }

            if (this.userAgentEnabled)
            {
                required.UnionWith(AlternatorConfig.UserAgentHeaders);
            }

            if (this.authenticationEnabledValue)
            {
                required.UnionWith(AlternatorConfig.AuthenticationHeaders);
            }

            return AlternatorConfig.CreateReadOnlyHeaderSet(required);
        }

        public AlternatorConfig Build()
        {
            this.ValidateScalarOptions();

            if (this.headersWhitelistWasSet)
            {
                this.ValidateHeadersWhitelist();
            }

            if (this.connectionMaxIdleTimeMsSet && this.connectionMaxIdleTimeMs == 0)
            {
                Logger.Warn(
                    "connectionMaxIdleTimeMs is set to 0, which disables idle connection eviction. This can lead to stale connections in long-running applications.");
            }

            var effectiveTlsConfig = this.CreateEffectiveTlsConfig();
            return new AlternatorConfig(
                new List<string>(this.seedHosts),
                this.scheme,
                this.port,
                this.routingScope ?? ClusterScope.Create(),
                this.compressionAlgorithm,
                this.minCompressionSizeBytes,
                this.responseCompressionAlgorithms,
                this.optimizeHeaders,
                this.headersWhitelist,
                this.headersWhitelistWasSet,
                this.customOptimizeHeaders,
                this.userAgentEnabled,
                this.authenticationEnabledValue,
                this.keyRouteAffinityConfig,
                this.activeRefreshIntervalMs,
                this.idleRefreshIntervalMs,
                this.maxConnections,
                this.connectionMaxIdleTimeMs,
                this.connectionTimeToLiveMs,
                this.connectionAcquisitionTimeoutMs,
                this.connectionTimeoutMs,
                this.httpClientTimeoutMs,
                effectiveTlsConfig);
        }

        private TlsConfig CreateEffectiveTlsConfig()
        {
            if (this.tlsConfig != null)
            {
                return this.tlsConfig;
            }

            return TlsConfig.TrustAll();
        }

        private void ValidateScalarOptions()
        {
            if (this.minCompressionSizeBytes < 0)
            {
                throw new ArgumentException(
                    "minCompressionSizeBytes must be non-negative, but was: " + this.minCompressionSizeBytes,
                    nameof(this.minCompressionSizeBytes));
            }

            if (this.maxConnections <= 0)
            {
                throw new ArgumentException(
                    "maxConnections must be positive, but was: " + this.maxConnections,
                    nameof(this.maxConnections));
            }

            if (this.connectionMaxIdleTimeMs < 0)
            {
                throw new ArgumentException(
                    "connectionMaxIdleTimeMs must be non-negative, but was: " + this.connectionMaxIdleTimeMs,
                    nameof(this.connectionMaxIdleTimeMs));
            }

            if (this.connectionTimeToLiveMs < 0)
            {
                throw new ArgumentException(
                    "connectionTimeToLiveMs must be non-negative, but was: " + this.connectionTimeToLiveMs,
                    nameof(this.connectionTimeToLiveMs));
            }

            if (this.connectionAcquisitionTimeoutMs < 0)
            {
                throw new ArgumentException(
                    "connectionAcquisitionTimeoutMs must be non-negative, but was: " + this.connectionAcquisitionTimeoutMs,
                    nameof(this.connectionAcquisitionTimeoutMs));
            }

            if (this.connectionTimeoutMs < 0)
            {
                throw new ArgumentException(
                    "connectionTimeoutMs must be non-negative, but was: " + this.connectionTimeoutMs,
                    nameof(this.connectionTimeoutMs));
            }

            if (this.httpClientTimeoutMs < 0)
            {
                throw new ArgumentException(
                    "httpClientTimeoutMs must be non-negative, but was: " + this.httpClientTimeoutMs,
                    nameof(this.httpClientTimeoutMs));
            }
        }

        private void ValidateHeadersWhitelist()
        {
            if (this.headersWhitelist == null || this.headersWhitelist.Count == 0)
            {
                throw new ArgumentException("Headers whitelist cannot be null or empty. To disable optimization, use WithOptimizeHeaders(false).", nameof(this.headersWhitelist));
            }

            var missing = new HashSet<string>(this.GetRequiredHeaders(), StringComparer.OrdinalIgnoreCase);
            missing.ExceptWith(this.headersWhitelist);
            if (missing.Count != 0)
            {
                throw new ArgumentException(
                    "Custom headers whitelist is missing required headers: " + string.Join(", ", missing),
                    nameof(this.headersWhitelist));
            }
        }
    }
}
