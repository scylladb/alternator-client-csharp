// <copyright file="AlternatorConfig.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using ScyllaDB.Alternator.KeyRouting;
    using ScyllaDB.Alternator.Routing;

    public class AlternatorConfig
    {
        public const int DefaultMinCompressionSizeBytes = 1024;
        public const long DefaultActiveRefreshIntervalMs = 1000;
        public const long DefaultIdleRefreshIntervalMs = 60000;
        public const int DefaultMaxConnections = 400;
        public const long DefaultConnectionMaxIdleTimeMs = 600000;
        public const long DefaultConnectionTimeToLiveMs = 0;
        public const long DefaultConnectionAcquisitionTimeoutMs = 10000;
        public const long DefaultConnectionTimeoutMs = 15000;
        public const int RecommendedPartitionKeyDiscoveryMaxRetries = 3;
        public const long RecommendedPartitionKeyDiscoveryInitialDelayMs = 100;
        public const long RecommendedPartitionKeyDiscoveryMaxDelayMs = 2000;
        public const long RecommendedPartitionKeyDiscoveryCooldownMs = 5 * 60 * 1000;

#pragma warning disable SA1310, IDE1006
        public const int DEFAULT_MIN_COMPRESSION_SIZE_BYTES = DefaultMinCompressionSizeBytes;
        public const long DEFAULT_ACTIVE_REFRESH_INTERVAL_MS = DefaultActiveRefreshIntervalMs;
        public const long DEFAULT_IDLE_REFRESH_INTERVAL_MS = DefaultIdleRefreshIntervalMs;
        public const int DEFAULT_MAX_CONNECTIONS = DefaultMaxConnections;
        public const long DEFAULT_CONNECTION_MAX_IDLE_TIME_MS = DefaultConnectionMaxIdleTimeMs;
        public const long DEFAULT_CONNECTION_TIME_TO_LIVE_MS = DefaultConnectionTimeToLiveMs;
        public const long DEFAULT_CONNECTION_ACQUISITION_TIMEOUT_MS = DefaultConnectionAcquisitionTimeoutMs;
        public const long DEFAULT_CONNECTION_TIMEOUT_MS = DefaultConnectionTimeoutMs;
        public const int RECOMMENDED_PARTITION_KEY_DISCOVERY_MAX_RETRIES = RecommendedPartitionKeyDiscoveryMaxRetries;
        public const long RECOMMENDED_PARTITION_KEY_DISCOVERY_INITIAL_DELAY_MS = RecommendedPartitionKeyDiscoveryInitialDelayMs;
        public const long RECOMMENDED_PARTITION_KEY_DISCOVERY_MAX_DELAY_MS = RecommendedPartitionKeyDiscoveryMaxDelayMs;
        public const long RECOMMENDED_PARTITION_KEY_DISCOVERY_COOLDOWN_MS = RecommendedPartitionKeyDiscoveryCooldownMs;
#pragma warning restore SA1310, IDE1006

        public static readonly IReadOnlySet<string> BaseRequiredHeaders = CreateReadOnlyHeaderSet(new[]
        {
            "Host",
            "X-Amz-Target",
            "Content-Type",
            "Content-Length",
            "Accept-Encoding",
            "Connection",
        });

        public static readonly IReadOnlySet<string> UserAgentHeaders = CreateReadOnlyHeaderSet(new[]
        {
            "User-Agent",
        });

        public static readonly IReadOnlySet<string> CompressionHeaders = CreateReadOnlyHeaderSet(new[]
        {
            "Content-Encoding",
        });

        public static readonly IReadOnlySet<string> AuthenticationHeaders = CreateReadOnlyHeaderSet(new[]
        {
            "Authorization",
            "X-Amz-Date",
        });

#pragma warning disable SA1310, IDE1006
        public static readonly IReadOnlySet<string> BASE_REQUIRED_HEADERS = BaseRequiredHeaders;
        public static readonly IReadOnlySet<string> USER_AGENT_HEADERS = UserAgentHeaders;
        public static readonly IReadOnlySet<string> COMPRESSION_HEADERS = CompressionHeaders;
        public static readonly IReadOnlySet<string> AUTHENTICATION_HEADERS = AuthenticationHeaders;
#pragma warning restore SA1310, IDE1006

        internal AlternatorConfig(
            List<string> seedHosts,
            string scheme,
            int port,
            RoutingScope routingScope,
            RequestCompressionAlgorithm compressionAlgorithm,
            int minCompressionSizeBytes,
            bool optimizeHeaders,
            ISet<string>? headersWhitelist,
            bool userAgentEnabled,
            bool authenticationEnabled,
            KeyRouteAffinityConfig? keyRouteAffinityConfig,
            long activeRefreshIntervalMs,
            long idleRefreshIntervalMs,
            int maxConnections,
            long connectionMaxIdleTimeMs,
            long connectionTimeToLiveMs,
            long connectionAcquisitionTimeoutMs,
            long connectionTimeoutMs,
            TlsConfig tlsConfig)
        {
            this.SeedHosts = seedHosts.AsReadOnly();
            this.Scheme = scheme;
            this.Port = port;
            this.RoutingScope = routingScope;
            this.CompressionAlgorithm = compressionAlgorithm;
            this.MinCompressionSizeBytes = minCompressionSizeBytes >= 0 ? minCompressionSizeBytes : DefaultMinCompressionSizeBytes;
            this.OptimizeHeaders = optimizeHeaders;
            this.UserAgentEnabled = userAgentEnabled;
            this.AuthenticationEnabled = authenticationEnabled;
            this.HeadersWhitelist = this.CreateHeadersWhitelist(headersWhitelist);
            this.KeyRouteAffinityConfig = keyRouteAffinityConfig;
            this.ActiveRefreshIntervalMs = activeRefreshIntervalMs > 0 ? activeRefreshIntervalMs : DefaultActiveRefreshIntervalMs;
            this.IdleRefreshIntervalMs = idleRefreshIntervalMs > 0 ? idleRefreshIntervalMs : DefaultIdleRefreshIntervalMs;
            this.MaxConnections = maxConnections;
            this.ConnectionMaxIdleTimeMs = connectionMaxIdleTimeMs;
            this.ConnectionTimeToLiveMs = connectionTimeToLiveMs;
            this.ConnectionAcquisitionTimeoutMs = connectionAcquisitionTimeoutMs;
            this.ConnectionTimeoutMs = connectionTimeoutMs;
            this.TlsConfig = tlsConfig;
        }

        public IReadOnlyList<string> SeedHosts { get; }

        public string Scheme { get; }

        public int Port { get; }

        public RoutingScope RoutingScope { get; }

        public RequestCompressionAlgorithm CompressionAlgorithm { get; }

        public int MinCompressionSizeBytes { get; }

        public bool OptimizeHeaders { get; }

        public IReadOnlySet<string> HeadersWhitelist { get; }

        public bool UserAgentEnabled { get; }

        public bool AuthenticationEnabled { get; }

        public KeyRouteAffinityConfig? KeyRouteAffinityConfig { get; }

        public long ActiveRefreshIntervalMs { get; }

        public long IdleRefreshIntervalMs { get; }

        public int MaxConnections { get; }

        public long ConnectionMaxIdleTimeMs { get; }

        public long ConnectionTimeToLiveMs { get; }

        public long ConnectionAcquisitionTimeoutMs { get; }

        public long ConnectionTimeoutMs { get; }

        public TlsConfig TlsConfig { get; }

        public IReadOnlySet<string> RequiredHeaders => this.GetRequiredHeaders();

        public static AlternatorConfigBuilder Builder()
        {
            return new AlternatorConfigBuilder();
        }

#pragma warning disable SA1300, IDE1006
        public static AlternatorConfigBuilder builder()
        {
            return Builder();
        }
#pragma warning restore SA1300, IDE1006

        public IReadOnlySet<string> GetRequiredHeaders()
        {
            var required = new HashSet<string>(BaseRequiredHeaders, StringComparer.OrdinalIgnoreCase);
            if (this.CompressionAlgorithm.IsEnabled())
            {
                required.UnionWith(CompressionHeaders);
            }

            if (this.UserAgentEnabled)
            {
                required.UnionWith(UserAgentHeaders);
            }

            if (this.AuthenticationEnabled)
            {
                required.UnionWith(AuthenticationHeaders);
            }

            return CreateReadOnlyHeaderSet(required);
        }

#pragma warning disable SA1300, IDE1006
        public IReadOnlyList<string> getSeedHosts()
        {
            return this.SeedHosts;
        }

        public string getScheme()
        {
            return this.Scheme;
        }

        public int getPort()
        {
            return this.Port;
        }

        public RoutingScope getRoutingScope()
        {
            return this.RoutingScope;
        }

        public RequestCompressionAlgorithm getCompressionAlgorithm()
        {
            return this.CompressionAlgorithm;
        }

        public int getMinCompressionSizeBytes()
        {
            return this.MinCompressionSizeBytes;
        }

        public bool isOptimizeHeaders()
        {
            return this.OptimizeHeaders;
        }

        public IReadOnlySet<string> getHeadersWhitelist()
        {
            return this.HeadersWhitelist;
        }

        public bool isUserAgentEnabled()
        {
            return this.UserAgentEnabled;
        }

        public bool isAuthenticationEnabled()
        {
            return this.AuthenticationEnabled;
        }

        public TlsConfig getTlsConfig()
        {
            return this.TlsConfig;
        }

        public KeyRouteAffinityConfig? getKeyRouteAffinityConfig()
        {
            return this.KeyRouteAffinityConfig;
        }

        public long getActiveRefreshIntervalMs()
        {
            return this.ActiveRefreshIntervalMs;
        }

        public long getIdleRefreshIntervalMs()
        {
            return this.IdleRefreshIntervalMs;
        }

        public int getMaxConnections()
        {
            return this.MaxConnections;
        }

        public long getConnectionMaxIdleTimeMs()
        {
            return this.ConnectionMaxIdleTimeMs;
        }

        public long getConnectionTimeToLiveMs()
        {
            return this.ConnectionTimeToLiveMs;
        }

        public long getConnectionAcquisitionTimeoutMs()
        {
            return this.ConnectionAcquisitionTimeoutMs;
        }

        public long getConnectionTimeoutMs()
        {
            return this.ConnectionTimeoutMs;
        }

        public IReadOnlySet<string> getRequiredHeaders()
        {
            return this.GetRequiredHeaders();
        }
#pragma warning restore SA1300, IDE1006

        internal static ReadOnlySet<string> CreateReadOnlyHeaderSet(IEnumerable<string> headers)
        {
            return new ReadOnlySet<string>(headers, StringComparer.OrdinalIgnoreCase);
        }

        private IReadOnlySet<string> CreateHeadersWhitelist(ISet<string>? headersWhitelist)
        {
            if (headersWhitelist != null)
            {
                return CreateReadOnlyHeaderSet(headersWhitelist);
            }

            return this.GetRequiredHeaders();
        }
    }
}
