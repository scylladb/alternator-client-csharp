// <copyright file="AlternatorDynamoDBClientBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon;
    using Amazon.DynamoDBv2;
    using Amazon.Runtime;
    using Amazon.Runtime.Endpoints;
    using ScyllaDB.Alternator.KeyRouting;
    using ScyllaDB.Alternator.Routing;

    public class AlternatorDynamoDBClientBuilder
    {
        private readonly HelperOptionsBuilder optionsBuilder;
        private readonly AlternatorConfigBuilder configBuilder;
        private AWSCredentials? credentials;
        private AmazonDynamoDBConfig? config;
        private Action<AmazonDynamoDBConfig>? configureAws;
        private HelperOptions? options;
        private string datacenter = string.Empty;
        private string rack = string.Empty;
        private bool validateOnInitialization = true;
        private bool startImmediately = true;
        private CancellationToken cancellationToken = CancellationToken.None;
        private Func<string, string?>? userAgentTransformer;
        private bool defaultUserAgentTokenEnabled = true;
        private Action<HttpClientHandler>? configureHttpClientHandler;
        private Action<SocketsHttpHandler>? configureSocketsHttpHandler;
        private HttpClientType httpClientType = HttpClientType.Auto;
        private bool httpClientTypeSet;
        private bool httpClientFactorySet;
        private bool disableCertificateChecks;
        private bool apacheHttpClientCustomizerSet;
        private bool crtHttpClientCustomizerSet;
        private bool systemNetHttpClientCustomizerSet;

        public AlternatorDynamoDBClientBuilder()
        {
            this.optionsBuilder = HelperOptionsBuilder.Create();
            this.configBuilder = AlternatorConfig.Builder();
        }

        public HttpClientType HttpClientType => this.httpClientType;

        public AlternatorDynamoDBClientBuilder WithCredentials(AWSCredentials credentials)
        {
            this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithCredentials(string accessKey, string secretKey)
        {
            return this.WithCredentials(new BasicAWSCredentials(accessKey, secretKey));
        }

        public AlternatorDynamoDBClientBuilder WithCredentials(string accessKey, string secretKey, string token)
        {
            return this.WithCredentials(new SessionAWSCredentials(accessKey, secretKey, token));
        }

        public AlternatorDynamoDBClientBuilder WithOptions(HelperOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithAlternatorConfig(AlternatorConfig? config)
        {
            if (config == null)
            {
                return this;
            }

            this.options = null;
            this.ApplyAlternatorConfig(config);
            return this;
        }

        public AlternatorDynamoDBClientBuilder EndpointOverride(Uri endpointOverride)
        {
            this.optionsBuilder.WithInitialNodeUri(endpointOverride);
            this.configBuilder.WithSeedNode(endpointOverride);
            return this;
        }

        public AlternatorDynamoDBClientBuilder EndpointOverride(string endpointOverride)
        {
            return this.EndpointOverride(new Uri(endpointOverride));
        }

        public AlternatorDynamoDBClientBuilder WithInitialSeeds(IEnumerable<string> seedHosts)
        {
            if (seedHosts == null)
            {
                throw new ArgumentNullException(nameof(seedHosts));
            }

            return this.ConfigureInitialSeedHosts(seedHosts, nameof(seedHosts));
        }

        public AlternatorDynamoDBClientBuilder WithInitialSeeds(params string[] seedHosts)
        {
            if (seedHosts == null)
            {
                throw new ArgumentNullException(nameof(seedHosts));
            }

            return this.ConfigureInitialSeedHosts(seedHosts, nameof(seedHosts));
        }

        public AlternatorDynamoDBClientBuilder WithScheme(string scheme)
        {
            return this.WithSchema(scheme);
        }

#pragma warning disable SA1300, IDE1006
        public AlternatorDynamoDBClientBuilder endpointOverride(Uri endpointOverride)
        {
            return this.EndpointOverride(endpointOverride);
        }

        public AlternatorDynamoDBClientBuilder endpointOverride(string endpointOverride)
        {
            return this.EndpointOverride(endpointOverride);
        }

        public AlternatorDynamoDBClientBuilder withInitialSeeds(IEnumerable<string> seedHosts)
        {
            return this.WithInitialSeeds(seedHosts);
        }

        public AlternatorDynamoDBClientBuilder withInitialSeeds(params string[] seedHosts)
        {
            return this.WithInitialSeeds(seedHosts);
        }

        public AlternatorDynamoDBClientBuilder withScheme(string scheme)
        {
            return this.WithScheme(scheme);
        }

        public AlternatorDynamoDBClientBuilder withSchema(string schema)
        {
            return this.WithSchema(schema);
        }

        public AlternatorDynamoDBClientBuilder withPort(int port)
        {
            return this.WithPort(port);
        }

        public AlternatorDynamoDBClientBuilder credentialsProvider(AWSCredentials credentials)
        {
            return this.WithCredentials(credentials);
        }

        public AlternatorDynamoDBClientBuilder credentialsProvider(string accessKey, string secretKey)
        {
            return this.WithCredentials(accessKey, secretKey);
        }

        public AlternatorDynamoDBClientBuilder credentialsProvider(string accessKey, string secretKey, string token)
        {
            return this.WithCredentials(accessKey, secretKey, token);
        }

        public AlternatorDynamoDBClientBuilder region(RegionEndpoint regionEndpoint)
        {
            return this.WithRegionEndpoint(regionEndpoint);
        }

        public AlternatorDynamoDBClientBuilder region(string regionSystemName)
        {
            return this.WithRegionEndpoint(regionSystemName);
        }

        public AlternatorDynamoDBClientBuilder withAlternatorConfig(AlternatorConfig? config)
        {
            return this.WithAlternatorConfig(config);
        }
#pragma warning restore SA1300, IDE1006

        public AlternatorDynamoDBClientBuilder WithInitialNodeUri(Uri uri)
        {
            return this.EndpointOverride(uri);
        }

        public AlternatorDynamoDBClientBuilder WithInitialNodeUri(string uri)
        {
            return this.EndpointOverride(uri);
        }

        public AlternatorDynamoDBClientBuilder WithInitialNodes(List<string> initialNodes)
        {
            this.optionsBuilder.WithInitialNodes(initialNodes);
            this.configBuilder.WithSeedHosts(initialNodes);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithInitialNodes(params string[] initialNodes)
        {
            this.optionsBuilder.WithInitialNodes(initialNodes);
            this.configBuilder.WithSeedHosts(initialNodes);
            return this;
        }

        public AlternatorDynamoDBClientBuilder AddInitialNode(string node)
        {
            this.optionsBuilder.AddInitialNode(node);
            this.configBuilder.WithSeedHosts(this.optionsBuilder.Build().InitialNodes);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithSchema(string schema)
        {
            this.optionsBuilder.WithSchema(schema);
            this.configBuilder.WithScheme(schema);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithPort(int port)
        {
            this.optionsBuilder.WithPort(port);
            this.configBuilder.WithPort(port);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithDatacenter(string datacenter)
        {
            this.datacenter = datacenter ?? string.Empty;
            this.optionsBuilder.WithDatacenter(this.datacenter);
            this.ApplyLegacyRoutingScope();
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithRack(string rack)
        {
            this.rack = rack ?? string.Empty;
            this.optionsBuilder.WithRack(this.rack);
            this.ApplyLegacyRoutingScope();
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithDatacenterAndRack(string datacenter, string rack)
        {
            this.datacenter = datacenter ?? string.Empty;
            this.rack = rack ?? string.Empty;
            this.optionsBuilder.WithDatacenterAndRack(this.datacenter, this.rack);
            this.ApplyLegacyRoutingScope();
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithRoutingScope(RoutingScope? routingScope)
        {
            this.optionsBuilder.WithRoutingScope(routingScope);
            this.configBuilder.WithRoutingScope(routingScope);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithCompressionAlgorithm(RequestCompressionAlgorithm algorithm)
        {
            this.configBuilder.WithCompressionAlgorithm(algorithm);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithCompressionAlgorithm(RequestCompressionAlgorithm? algorithm)
        {
            this.configBuilder.WithCompressionAlgorithm(algorithm);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithMinCompressionSizeBytes(int minCompressionSizeBytes)
        {
            this.configBuilder.WithMinCompressionSizeBytes(minCompressionSizeBytes);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithOptimizeHeaders(bool optimizeHeaders)
        {
            this.configBuilder.WithOptimizeHeaders(optimizeHeaders);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithHeadersWhitelist(IEnumerable<string> headers)
        {
            this.configBuilder.WithHeadersWhitelist(headers);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithUserAgent(string userAgent)
        {
            this.userAgentTransformer = AlternatorUserAgent.ReplaceWith(userAgent);
            this.defaultUserAgentTokenEnabled = false;
            this.configBuilder.WithUserAgentEnabled(true);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithUserAgent(Func<string, string?> userAgentTransformer)
        {
            this.userAgentTransformer = AlternatorUserAgent.RequireUserAgentTransformer(userAgentTransformer);
            this.defaultUserAgentTokenEnabled = true;
            this.configBuilder.WithUserAgentEnabled(true);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithoutUserAgent()
        {
            this.userAgentTransformer = AlternatorUserAgent.Disable();
            this.defaultUserAgentTokenEnabled = false;
            this.configBuilder.WithUserAgentEnabled(false);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithKeyRouteAffinity(KeyRouteAffinityConfig keyRouteAffinityConfig)
        {
            this.configBuilder.WithKeyRouteAffinity(keyRouteAffinityConfig);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithKeyRouteAffinity(KeyRouteAffinity type)
        {
            this.configBuilder.WithKeyRouteAffinity(type);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithKeyRouteAffinity(KeyRouteAffinity? type)
        {
            this.configBuilder.WithKeyRouteAffinity(type);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithActiveRefreshIntervalMs(long intervalMs)
        {
            this.optionsBuilder.WithActiveRefreshIntervalMs(intervalMs);
            this.configBuilder.WithActiveRefreshIntervalMs(intervalMs);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithIdleRefreshIntervalMs(long intervalMs)
        {
            this.optionsBuilder.WithIdleRefreshIntervalMs(intervalMs);
            this.configBuilder.WithIdleRefreshIntervalMs(intervalMs);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithMaxConnections(int maxConnections)
        {
            this.configBuilder.WithMaxConnections(maxConnections);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithConnectionMaxIdleTimeMs(long connectionMaxIdleTimeMs)
        {
            this.configBuilder.WithConnectionMaxIdleTimeMs(connectionMaxIdleTimeMs);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithConnectionTimeToLiveMs(long connectionTimeToLiveMs)
        {
            this.configBuilder.WithConnectionTimeToLiveMs(connectionTimeToLiveMs);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithConnectionAcquisitionTimeoutMs(long connectionAcquisitionTimeoutMs)
        {
            this.configBuilder.WithConnectionAcquisitionTimeoutMs(connectionAcquisitionTimeoutMs);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithConnectionTimeoutMs(long connectionTimeoutMs)
        {
            this.configBuilder.WithConnectionTimeoutMs(connectionTimeoutMs);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithTlsConfig(TlsConfig? tlsConfig)
        {
            this.configBuilder.WithTlsConfig(tlsConfig);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithDisableCertificateChecks()
        {
            this.disableCertificateChecks = true;
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithValidation(bool validate)
        {
            this.validateOnInitialization = validate;
            this.optionsBuilder.WithValidation(validate);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithoutValidation()
        {
            this.validateOnInitialization = false;
            this.optionsBuilder.WithoutValidation();
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithImmediateStart(bool startImmediately)
        {
            this.startImmediately = startImmediately;
            this.optionsBuilder.WithImmediateStart(startImmediately);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithDeferredStart()
        {
            this.startImmediately = false;
            this.optionsBuilder.WithDeferredStart();
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithCancellationToken(CancellationToken cancellationToken)
        {
            this.cancellationToken = cancellationToken;
            this.optionsBuilder.WithCancellationToken(cancellationToken);
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithAmazonDynamoDBConfig(AmazonDynamoDBConfig config)
        {
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.httpClientFactorySet = this.config.HttpClientFactory != null;
            return this;
        }

        public AmazonDynamoDBConfig OverrideConfiguration()
        {
            return this.GetOrCreateDynamoDbConfig();
        }

        public AlternatorDynamoDBClientBuilder OverrideConfiguration(AmazonDynamoDBConfig config)
        {
            return this.WithAmazonDynamoDBConfig(config);
        }

        public AlternatorDynamoDBClientBuilder OverrideConfiguration(Action<AmazonDynamoDBConfig> configure)
        {
            (configure ?? throw new ArgumentNullException(nameof(configure))).Invoke(this.GetOrCreateDynamoDbConfig());
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithRegionEndpoint(RegionEndpoint regionEndpoint)
        {
            if (regionEndpoint == null)
            {
                throw new ArgumentNullException(nameof(regionEndpoint));
            }

            this.GetOrCreateDynamoDbConfig().RegionEndpoint = regionEndpoint;
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithRegionEndpoint(string regionSystemName)
        {
            if (regionSystemName == null)
            {
                throw new ArgumentNullException(nameof(regionSystemName));
            }

            return this.WithRegionEndpoint(RegionEndpoint.GetBySystemName(regionSystemName));
        }

        public AlternatorDynamoDBClientBuilder WithHttpClientHandlerCustomizer(Action<HttpClientHandler> customizer)
        {
            this.configureHttpClientHandler += customizer ?? throw new ArgumentNullException(nameof(customizer));
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithSocketsHttpHandlerCustomizer(Action<SocketsHttpHandler> customizer)
        {
            this.configureSocketsHttpHandler += customizer ?? throw new ArgumentNullException(nameof(customizer));
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithApacheHttpClientCustomizer(Action<HttpClientHandler> customizer)
        {
            if (customizer == null)
            {
                throw new ArgumentNullException(nameof(customizer));
            }

            this.apacheHttpClientCustomizerSet = true;
            return this.WithHttpClientHandlerCustomizer(customizer);
        }

        public AlternatorDynamoDBClientBuilder WithCrtHttpClientCustomizer(Action<HttpClientHandler> customizer)
        {
            if (customizer == null)
            {
                throw new ArgumentNullException(nameof(customizer));
            }

            this.crtHttpClientCustomizerSet = true;
            return this.WithHttpClientHandlerCustomizer(customizer);
        }

        public AlternatorDynamoDBClientBuilder WithSystemNetHttpClientCustomizer(Action<HttpClientHandler> customizer)
        {
            if (customizer == null)
            {
                throw new ArgumentNullException(nameof(customizer));
            }

            this.systemNetHttpClientCustomizerSet = true;
            return this.WithHttpClientHandlerCustomizer(customizer);
        }

        public AlternatorDynamoDBClientBuilder WithHttpClientType(HttpClientType httpClientType)
        {
            this.httpClientType = httpClientType;
            this.httpClientTypeSet = true;
            return this;
        }

        public AlternatorDynamoDBClientBuilder WithHttpClientType(HttpClientType? httpClientType)
        {
            if (httpClientType == null)
            {
                throw new ArgumentNullException(nameof(httpClientType));
            }

            return this.WithHttpClientType(httpClientType.Value);
        }

        public AlternatorDynamoDBClientBuilder WithHttpClientFactory(HttpClientFactory httpClientFactory)
        {
            this.GetOrCreateDynamoDbConfig().HttpClientFactory =
                httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            this.httpClientFactorySet = true;
            return this;
        }

        public AlternatorDynamoDBClientBuilder HttpClientFactory(HttpClientFactory httpClientFactory)
        {
            return this.WithHttpClientFactory(httpClientFactory);
        }

        public AlternatorDynamoDBClientBuilder AccountIdEndpointMode(AccountIdEndpointMode accountIdEndpointMode)
        {
            this.GetOrCreateDynamoDbConfig().AccountIdEndpointMode = accountIdEndpointMode;
            return this;
        }

        public AlternatorDynamoDBClientBuilder EndpointProvider(IEndpointProvider endpointProvider)
        {
            throw new NotSupportedException(
                "AlternatorDynamoDBClient does not support custom endpoint providers. Use EndpointOverride(Uri) for one seed, or WithInitialSeeds(...) with WithScheme(...) and WithPort(...) for multiple seeds.");
        }

        public AlternatorDynamoDBClientBuilder EndpointDiscoveryEnabled(bool endpointDiscoveryEnabled)
        {
            throw new NotSupportedException(
                "AlternatorDynamoDBClient does not support AWS endpoint discovery. Node discovery is handled automatically via the /localnodes API.");
        }

        public AlternatorDynamoDBClientBuilder EnableEndpointDiscovery()
        {
            throw new NotSupportedException(
                "AlternatorDynamoDBClient does not support AWS endpoint discovery. Node discovery is handled automatically via the /localnodes API.");
        }

        public AlternatorDynamoDBClientBuilder FipsEnabled(bool? fipsEnabled)
        {
            throw new NotSupportedException("AlternatorDynamoDBClient does not support FIPS mode.");
        }

        public AlternatorDynamoDBClientBuilder DualstackEnabled(bool? dualstackEnabled)
        {
            throw new NotSupportedException("AlternatorDynamoDBClient does not support dual-stack networking.");
        }

        public AlternatorDynamoDBClientBuilder ConfigureHttpClientHandler(Action<HttpClientHandler> configure)
        {
            return this.WithHttpClientHandlerCustomizer(configure);
        }

        public AlternatorDynamoDBClientBuilder ConfigureSocketsHttpHandler(Action<SocketsHttpHandler> configure)
        {
            return this.WithSocketsHttpHandlerCustomizer(configure);
        }

        public AlternatorDynamoDBClientBuilder ConfigureAws(Action<AmazonDynamoDBConfig> configure)
        {
            this.configureAws += configure ?? throw new ArgumentNullException(nameof(configure));
            return this;
        }

        public AmazonDynamoDBClient Build()
        {
            return this.BuildClientArtifacts().Client;
        }

        public AlternatorDynamoDBClientWrapper BuildWithAlternatorAPI()
        {
            var artifacts = this.BuildClientArtifacts();
            return new AlternatorDynamoDBClientWrapper(
                artifacts.Client,
                artifacts.EndpointProvider,
                artifacts.AlternatorConfig,
                artifacts.PartitionKeyResolver);
        }

#pragma warning disable SA1300, IDE1006
        public AlternatorDynamoDBClientBuilder withRoutingScope(RoutingScope? routingScope)
        {
            return this.WithRoutingScope(routingScope);
        }

        public AlternatorDynamoDBClientBuilder withCompressionAlgorithm(RequestCompressionAlgorithm algorithm)
        {
            return this.WithCompressionAlgorithm(algorithm);
        }

        public AlternatorDynamoDBClientBuilder withCompressionAlgorithm(RequestCompressionAlgorithm? algorithm)
        {
            return this.WithCompressionAlgorithm(algorithm);
        }

        public AlternatorDynamoDBClientBuilder withMinCompressionSizeBytes(int minCompressionSizeBytes)
        {
            return this.WithMinCompressionSizeBytes(minCompressionSizeBytes);
        }

        public AlternatorDynamoDBClientBuilder withOptimizeHeaders(bool optimizeHeaders)
        {
            return this.WithOptimizeHeaders(optimizeHeaders);
        }

        public AlternatorDynamoDBClientBuilder withHeadersWhitelist(IEnumerable<string> headers)
        {
            return this.WithHeadersWhitelist(headers);
        }

        public AlternatorDynamoDBClientBuilder withUserAgent(string userAgent)
        {
            return this.WithUserAgent(userAgent);
        }

        public AlternatorDynamoDBClientBuilder withUserAgent(Func<string, string?> userAgentTransformer)
        {
            return this.WithUserAgent(userAgentTransformer);
        }

        public AlternatorDynamoDBClientBuilder withoutUserAgent()
        {
            return this.WithoutUserAgent();
        }

        public AlternatorDynamoDBClientBuilder withKeyRouteAffinity(KeyRouteAffinityConfig keyRouteAffinityConfig)
        {
            return this.WithKeyRouteAffinity(keyRouteAffinityConfig);
        }

        public AlternatorDynamoDBClientBuilder withKeyRouteAffinity(KeyRouteAffinity type)
        {
            return this.WithKeyRouteAffinity(type);
        }

        public AlternatorDynamoDBClientBuilder withKeyRouteAffinity(KeyRouteAffinity? type)
        {
            return this.WithKeyRouteAffinity(type);
        }

        public AlternatorDynamoDBClientBuilder withTlsConfig(TlsConfig? tlsConfig)
        {
            return this.WithTlsConfig(tlsConfig);
        }

        public AlternatorDynamoDBClientBuilder withActiveRefreshIntervalMs(long intervalMs)
        {
            return this.WithActiveRefreshIntervalMs(intervalMs);
        }

        public AlternatorDynamoDBClientBuilder withIdleRefreshIntervalMs(long intervalMs)
        {
            return this.WithIdleRefreshIntervalMs(intervalMs);
        }

        public AlternatorDynamoDBClientBuilder withMaxConnections(int maxConnections)
        {
            return this.WithMaxConnections(maxConnections);
        }

        public AlternatorDynamoDBClientBuilder withConnectionMaxIdleTimeMs(long connectionMaxIdleTimeMs)
        {
            return this.WithConnectionMaxIdleTimeMs(connectionMaxIdleTimeMs);
        }

        public AlternatorDynamoDBClientBuilder withConnectionTimeToLiveMs(long connectionTimeToLiveMs)
        {
            return this.WithConnectionTimeToLiveMs(connectionTimeToLiveMs);
        }

        public AlternatorDynamoDBClientBuilder withConnectionAcquisitionTimeoutMs(long connectionAcquisitionTimeoutMs)
        {
            return this.WithConnectionAcquisitionTimeoutMs(connectionAcquisitionTimeoutMs);
        }

        public AlternatorDynamoDBClientBuilder withConnectionTimeoutMs(long connectionTimeoutMs)
        {
            return this.WithConnectionTimeoutMs(connectionTimeoutMs);
        }

        public AlternatorDynamoDBClientBuilder withDisableCertificateChecks()
        {
            return this.WithDisableCertificateChecks();
        }

        public AlternatorDynamoDBClientBuilder withHttpClientHandlerCustomizer(Action<HttpClientHandler> customizer)
        {
            return this.WithHttpClientHandlerCustomizer(customizer);
        }

        public AlternatorDynamoDBClientBuilder withSocketsHttpHandlerCustomizer(Action<SocketsHttpHandler> customizer)
        {
            return this.WithSocketsHttpHandlerCustomizer(customizer);
        }

        public AlternatorDynamoDBClientBuilder withApacheHttpClientCustomizer(Action<HttpClientHandler> customizer)
        {
            return this.WithApacheHttpClientCustomizer(customizer);
        }

        public AlternatorDynamoDBClientBuilder withCrtHttpClientCustomizer(Action<HttpClientHandler> customizer)
        {
            return this.WithCrtHttpClientCustomizer(customizer);
        }

        public AlternatorDynamoDBClientBuilder withSystemNetHttpClientCustomizer(Action<HttpClientHandler> customizer)
        {
            return this.WithSystemNetHttpClientCustomizer(customizer);
        }

        public AlternatorDynamoDBClientBuilder withHttpClientType(HttpClientType httpClientType)
        {
            return this.WithHttpClientType(httpClientType);
        }

        public AlternatorDynamoDBClientBuilder withHttpClientType(HttpClientType? httpClientType)
        {
            return this.WithHttpClientType(httpClientType);
        }

        public AmazonDynamoDBConfig overrideConfiguration()
        {
            return this.OverrideConfiguration();
        }

        public AlternatorDynamoDBClientBuilder overrideConfiguration(Action<AmazonDynamoDBConfig> configure)
        {
            return this.OverrideConfiguration(configure);
        }

        public AlternatorDynamoDBClientBuilder overrideConfiguration(AmazonDynamoDBConfig config)
        {
            return this.OverrideConfiguration(config);
        }

        public AlternatorDynamoDBClientBuilder accountIdEndpointMode(AccountIdEndpointMode accountIdEndpointMode)
        {
            return this.AccountIdEndpointMode(accountIdEndpointMode);
        }

        public AlternatorDynamoDBClientBuilder httpClientFactory(HttpClientFactory httpClientFactory)
        {
            return this.WithHttpClientFactory(httpClientFactory);
        }

        public AlternatorDynamoDBClientBuilder httpClient(HttpClientFactory httpClientFactory)
        {
            return this.WithHttpClientFactory(httpClientFactory);
        }

        public AlternatorDynamoDBClientBuilder httpClientBuilder(HttpClientFactory httpClientFactory)
        {
            return this.WithHttpClientFactory(httpClientFactory);
        }

        public AlternatorDynamoDBClientBuilder endpointProvider(IEndpointProvider endpointProvider)
        {
            return this.EndpointProvider(endpointProvider);
        }

        public AlternatorDynamoDBClientBuilder endpointDiscoveryEnabled(bool endpointDiscoveryEnabled)
        {
            return this.EndpointDiscoveryEnabled(endpointDiscoveryEnabled);
        }

        public AlternatorDynamoDBClientBuilder enableEndpointDiscovery()
        {
            return this.EnableEndpointDiscovery();
        }

        public AlternatorDynamoDBClientBuilder fipsEnabled(bool? fipsEnabled)
        {
            return this.FipsEnabled(fipsEnabled);
        }

        public AlternatorDynamoDBClientBuilder dualstackEnabled(bool? dualstackEnabled)
        {
            return this.DualstackEnabled(dualstackEnabled);
        }

        public AmazonDynamoDBClient build()
        {
            return this.Build();
        }

        public AlternatorDynamoDBClientWrapper buildWithAlternatorAPI()
        {
            return this.BuildWithAlternatorAPI();
        }
#pragma warning restore SA1300, IDE1006

        private static AlternatorConfig CreateConfigWithTlsConfig(AlternatorConfig config, TlsConfig tlsConfig)
        {
            return AlternatorConfig.Builder()
                .WithSeedHosts(config.SeedHosts)
                .WithScheme(config.Scheme)
                .WithPort(config.Port)
                .WithRoutingScope(config.RoutingScope)
                .WithCompressionAlgorithm(config.CompressionAlgorithm)
                .WithMinCompressionSizeBytes(config.MinCompressionSizeBytes)
                .WithOptimizeHeaders(config.OptimizeHeaders)
                .WithHeadersWhitelist(config.HeadersWhitelist)
                .WithUserAgentEnabled(config.UserAgentEnabled)
                .WithAuthenticationEnabled(config.AuthenticationEnabled)
                .WithTlsConfig(tlsConfig)
                .WithKeyRouteAffinity(config.KeyRouteAffinityConfig)
                .WithActiveRefreshIntervalMs(config.ActiveRefreshIntervalMs)
                .WithIdleRefreshIntervalMs(config.IdleRefreshIntervalMs)
                .WithMaxConnections(config.MaxConnections)
                .WithConnectionMaxIdleTimeMs(config.ConnectionMaxIdleTimeMs)
                .WithConnectionTimeToLiveMs(config.ConnectionTimeToLiveMs)
                .WithConnectionAcquisitionTimeoutMs(config.ConnectionAcquisitionTimeoutMs)
                .WithConnectionTimeoutMs(config.ConnectionTimeoutMs)
                .Build();
        }

        private static List<string> NormalizeInitialSeedHosts(IEnumerable<string> seedHosts, string paramName)
        {
            var hosts = new List<string>();
            foreach (var seedHost in seedHosts)
            {
                if (string.IsNullOrWhiteSpace(seedHost))
                {
                    throw new ArgumentException("Initial seed host cannot be null or empty.", paramName);
                }

                var host = seedHost.Trim();
                if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
                {
                    throw new ArgumentException(
                        "Initial seed must be a DNS name or IP address without scheme or port.",
                        paramName);
                }

                hosts.Add(host);
            }

            return hosts;
        }

        private ClientArtifacts BuildClientArtifacts()
        {
            this.configBuilder.WithAuthenticationEnabled(this.credentials != null);
            if (this.disableCertificateChecks)
            {
                this.configBuilder.WithTlsConfig(TlsConfig.TrustAll());
            }

            var alternatorConfig = this.options?.ToAlternatorConfig() ?? this.configBuilder.Build();
            if (this.disableCertificateChecks && this.options != null)
            {
                alternatorConfig = CreateConfigWithTlsConfig(alternatorConfig, TlsConfig.TrustAll());
            }

            this.ValidateSeedConfiguration(alternatorConfig);
            var dynamoDbConfig = this.GetOrCreateDynamoDbConfig();
            this.ValidateHttpClientConfiguration(dynamoDbConfig);
            var endpointProvider = this.options != null
                ? new Helper(this.options)
                : new Helper(alternatorConfig, this.validateOnInitialization, this.startImmediately, this.cancellationToken);

            if (alternatorConfig.ConnectionTimeoutMs > 0)
            {
                dynamoDbConfig.Timeout = TimeSpan.FromMilliseconds(alternatorConfig.ConnectionTimeoutMs);
            }

            dynamoDbConfig.MaxConnectionsPerServer = alternatorConfig.MaxConnections;
            dynamoDbConfig.HttpClientFactory ??= new AlternatorHttpClientFactory(
                alternatorConfig,
                this.configureHttpClientHandler,
                this.configureSocketsHttpHandler);
            this.configureAws?.Invoke(dynamoDbConfig);
            dynamoDbConfig.EndpointProvider = endpointProvider;
            var partitionKeyResolver = alternatorConfig.KeyRouteAffinityConfig?.IsEnabled == true
                ? new PartitionKeyResolver(alternatorConfig.KeyRouteAffinityConfig.PkInfoPerTable.ToDictionary(item => item.Key, item => item.Value))
                : null;
            var client = AlternatorAmazonDynamoDBClient.Create(
                this.credentials ?? new AnonymousAWSCredentials(),
                dynamoDbConfig,
                alternatorConfig,
                endpointProvider,
                partitionKeyResolver,
                this.userAgentTransformer,
                this.defaultUserAgentTokenEnabled && alternatorConfig.UserAgentEnabled);
            partitionKeyResolver?.SetClientForDiscovery(client);
            return new ClientArtifacts(client, endpointProvider, alternatorConfig, partitionKeyResolver);
        }

        private AmazonDynamoDBConfig GetOrCreateDynamoDbConfig()
        {
            this.config ??= new AmazonDynamoDBConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.USEast1,
            };
            return this.config;
        }

        private void ValidateSeedConfiguration(AlternatorConfig alternatorConfig)
        {
            if (alternatorConfig.SeedHosts.Count == 0)
            {
                throw new InvalidOperationException(
                    "endpointOverride or withInitialSeeds must be set when using AlternatorDynamoDBClientBuilder. Call endpointOverride(Uri) for one seed, or withInitialSeeds(...) with DNS/IP seed hosts.");
            }

            if (string.IsNullOrWhiteSpace(alternatorConfig.Scheme))
            {
                throw new InvalidOperationException(
                    "Alternator scheme must be set. Call endpointOverride(Uri) or withScheme(string).");
            }

            if (alternatorConfig.Port <= 0)
            {
                throw new InvalidOperationException(
                    "Alternator port must be set. Call endpointOverride(Uri) or withPort(int).");
            }
        }

        private AlternatorDynamoDBClientBuilder ConfigureInitialSeedHosts(IEnumerable<string> seedHosts, string paramName)
        {
            var hosts = NormalizeInitialSeedHosts(seedHosts, paramName);
            if (hosts.Count == 0)
            {
                throw new ArgumentException("At least one initial seed host must be provided.", paramName);
            }

            this.optionsBuilder.WithInitialNodes(hosts);
            this.configBuilder.WithSeedHosts(hosts);
            return this;
        }

        private void ValidateHttpClientConfiguration(AmazonDynamoDBConfig dynamoDbConfig)
        {
            var hasCustomHttpClientFactory = this.httpClientFactorySet
                || (dynamoDbConfig.HttpClientFactory != null
                    && dynamoDbConfig.HttpClientFactory.GetType() != typeof(AlternatorHttpClientFactory));

            if (hasCustomHttpClientFactory
                && (this.configureHttpClientHandler != null || this.configureSocketsHttpHandler != null))
            {
                throw new InvalidOperationException(
                    "Cannot use httpClient()/httpClientFactory() together with HTTP handler customizers. Use one transport configuration approach.");
            }

            if (hasCustomHttpClientFactory && this.httpClientTypeSet)
            {
                throw new InvalidOperationException(
                    "Cannot use httpClient()/httpClientFactory() together with withHttpClientType(). Use one transport configuration approach.");
            }

            if (this.configureHttpClientHandler != null && this.configureSocketsHttpHandler != null)
            {
                throw new InvalidOperationException(
                    "HttpClientHandler and SocketsHttpHandler customizers cannot be used together.");
            }

            if (this.apacheHttpClientCustomizerSet && this.crtHttpClientCustomizerSet)
            {
                throw new InvalidOperationException(
                    "Cannot use Apache and CRT HTTP client customizers together. Use one HTTP client customizer.");
            }

            if (this.apacheHttpClientCustomizerSet && this.systemNetHttpClientCustomizerSet)
            {
                throw new InvalidOperationException(
                    "Cannot use Apache and System.Net HTTP client customizers together. Use one HTTP client customizer.");
            }

            if (this.crtHttpClientCustomizerSet && this.systemNetHttpClientCustomizerSet)
            {
                throw new InvalidOperationException(
                    "Cannot use CRT and System.Net HTTP client customizers together. Use one HTTP client customizer.");
            }

            if (this.httpClientType == HttpClientType.Netty)
            {
                throw new NotSupportedException(
                    "HttpClientType.NETTY does not apply to the synchronous AmazonDynamoDBClient. Use HttpClientType.SYSTEM_NET_HTTP, APACHE, CRT, or AUTO.");
            }

            if (this.httpClientType == HttpClientType.Apache
                && (this.crtHttpClientCustomizerSet || this.systemNetHttpClientCustomizerSet))
            {
                throw new InvalidOperationException(
                    "HttpClientType.APACHE cannot be used with CRT or System.Net HTTP client customizers.");
            }

            if (this.httpClientType == HttpClientType.Crt
                && (this.apacheHttpClientCustomizerSet || this.systemNetHttpClientCustomizerSet))
            {
                throw new InvalidOperationException(
                    "HttpClientType.CRT cannot be used with Apache or System.Net HTTP client customizers.");
            }

            if (this.httpClientType == HttpClientType.SystemNetHttp
                && (this.apacheHttpClientCustomizerSet || this.crtHttpClientCustomizerSet))
            {
                throw new InvalidOperationException(
                    "HttpClientType.SYSTEM_NET_HTTP cannot be used with Apache or CRT HTTP client customizers.");
            }
        }

        private void ApplyAlternatorConfig(AlternatorConfig config)
        {
            this.configBuilder
                .WithRoutingScope(config.RoutingScope)
                .WithCompressionAlgorithm(config.CompressionAlgorithm)
                .WithMinCompressionSizeBytes(config.MinCompressionSizeBytes)
                .WithOptimizeHeaders(config.OptimizeHeaders)
                .WithHeadersWhitelist(config.HeadersWhitelist)
                .WithUserAgentEnabled(config.UserAgentEnabled)
                .WithAuthenticationEnabled(config.AuthenticationEnabled)
                .WithTlsConfig(config.TlsConfig)
                .WithKeyRouteAffinity(config.KeyRouteAffinityConfig)
                .WithActiveRefreshIntervalMs(config.ActiveRefreshIntervalMs)
                .WithIdleRefreshIntervalMs(config.IdleRefreshIntervalMs)
                .WithMaxConnections(config.MaxConnections)
                .WithConnectionMaxIdleTimeMs(config.ConnectionMaxIdleTimeMs)
                .WithConnectionTimeToLiveMs(config.ConnectionTimeToLiveMs)
                .WithConnectionAcquisitionTimeoutMs(config.ConnectionAcquisitionTimeoutMs)
                .WithConnectionTimeoutMs(config.ConnectionTimeoutMs);
        }

        private void ApplyLegacyRoutingScope()
        {
            RoutingScope? routingScope = null;
            if (!string.IsNullOrEmpty(this.datacenter) && !string.IsNullOrEmpty(this.rack))
            {
                routingScope = RackScope.Of(
                    this.datacenter,
                    this.rack,
                    DatacenterScope.Of(this.datacenter, ClusterScope.Create()));
            }
            else if (!string.IsNullOrEmpty(this.datacenter))
            {
                routingScope = DatacenterScope.Of(this.datacenter, ClusterScope.Create());
            }

            this.configBuilder.WithRoutingScope(routingScope);
        }

        private sealed class ClientArtifacts
        {
            internal ClientArtifacts(
                AmazonDynamoDBClient client,
                Helper endpointProvider,
                AlternatorConfig alternatorConfig,
                PartitionKeyResolver? partitionKeyResolver)
            {
                this.Client = client;
                this.EndpointProvider = endpointProvider;
                this.AlternatorConfig = alternatorConfig;
                this.PartitionKeyResolver = partitionKeyResolver;
            }

            internal AmazonDynamoDBClient Client { get; }

            internal Helper EndpointProvider { get; }

            internal AlternatorConfig AlternatorConfig { get; }

            internal PartitionKeyResolver? PartitionKeyResolver { get; }
        }
    }
}
