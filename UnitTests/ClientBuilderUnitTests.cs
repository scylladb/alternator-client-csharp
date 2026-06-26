// <copyright file="ClientBuilderUnitTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon;
    using Amazon.DynamoDBv2;
    using Amazon.DynamoDBv2.Model;
    using Amazon.Runtime;
    using ScyllaDB.Alternator.KeyRouting;
    using ScyllaDB.Alternator.Routing;

    [TestFixture]
    [Category("Unit")]
    public class ClientBuilderUnitTests
    {
        [Test]
        public void AlternatorDynamoDBClientBuilderFactoryTest()
        {
            var builder = AlternatorDynamoDBClient.builder();

            Assert.That(builder, Is.Not.Null);
            Assert.That(builder, Is.InstanceOf<AlternatorDynamoDBClientBuilder>());
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderBuildsClientTest()
        {
            var credentials = new BasicAWSCredentials("user", "password");

            using var client = AlternatorDynamoDBClient.Builder()
                .WithCredentials(credentials)
                .WithInitialNodeUri(new Uri("http://127.0.0.1:8080"))
                .WithDatacenterAndRack("dc1", "rack1")
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            Assert.That(client, Is.Not.Null);
            Assert.That(client, Is.InstanceOf<AmazonDynamoDBClient>());
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderJavaStyleBuildReturnsAwsDynamoDbClientTest()
        {
            using var client = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .WithoutValidation()
                .WithDeferredStart()
                .build();

            Assert.That(client, Is.InstanceOf<AmazonDynamoDBClient>());
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsJavaStyleEndpointOverrideTest()
        {
            using var client = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .WithRoutingScope(DatacenterScope.of("dc1", ClusterScope.create()))
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            Assert.That(client, Is.Not.Null);
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsInitialSeedsTest()
        {
            using var wrapper = AlternatorDynamoDBClient.builder()
                .withScheme("https")
                .withPort(8043)
                .withInitialSeeds(
                    "dc1-seed.example.com",
                    "dc2-seed.example.com")
                .withRoutingScope(ClusterScope.create())
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.getSeedHosts(), Is.EqualTo(new[] { "dc1-seed.example.com", "dc2-seed.example.com" }));
            Assert.That(wrapper.Config.getScheme(), Is.EqualTo("https"));
            Assert.That(wrapper.Config.getPort(), Is.EqualTo(8043));
            Assert.That(wrapper.getLiveNodes(), Is.EqualTo(new[]
            {
                new Uri("https://dc1-seed.example.com:8043"),
                new Uri("https://dc2-seed.example.com:8043"),
            }));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsIpv6InitialSeedsTest()
        {
            using var wrapper = AlternatorDynamoDBClient.builder()
                .withScheme("http")
                .withPort(8080)
                .withInitialSeeds("::1")
                .withRoutingScope(ClusterScope.create())
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.getSeedHosts(), Is.EqualTo(new[] { "::1" }));
            Assert.That(wrapper.getLiveNodes(), Is.EqualTo(new[]
            {
                new Uri("http://[::1]:8080"),
            }));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderRejectsInitialSeedsThatAreNotHostsTest()
        {
            var uriException = Assert.Throws<ArgumentException>(() =>
                AlternatorDynamoDBClient.builder()
                    .withInitialSeeds("https://dc1-seed.example.com:8043"));
            Assert.That(uriException!.Message, Does.Contain("DNS name or IP address"));

            var hostPortException = Assert.Throws<ArgumentException>(() =>
                AlternatorDynamoDBClient.builder()
                    .WithInitialSeeds("dc1-seed.example.com:8043"));
            Assert.That(hostPortException!.Message, Does.Contain("without scheme or port"));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderRequiresSchemeAndPortForInitialSeedsTest()
        {
            var schemeException = Assert.Throws<InvalidOperationException>(() =>
                AlternatorDynamoDBClient.builder()
                    .withPort(8043)
                    .withInitialSeeds("dc1-seed.example.com")
                    .WithoutValidation()
                    .WithDeferredStart()
                    .build());
            Assert.That(schemeException!.Message, Does.Contain("scheme must be set"));

            var portException = Assert.Throws<InvalidOperationException>(() =>
                AlternatorDynamoDBClient.builder()
                    .withScheme("https")
                    .withInitialSeeds("dc1-seed.example.com")
                    .WithoutValidation()
                    .WithDeferredStart()
                    .build());
            Assert.That(portException!.Message, Does.Contain("port must be set"));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderBuildsWrapperWithAlternatorApiTest()
        {
            using var wrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(wrapper, Is.Not.Null);
            Assert.That(wrapper.getClient(), Is.InstanceOf<AmazonDynamoDBClient>());
            Assert.That(wrapper.getLiveNodes(), Is.EqualTo(new List<Uri> { new Uri("http://127.0.0.1:8080") }));
            Assert.That(wrapper.nextAsURI(), Is.EqualTo(new Uri("http://127.0.0.1:8080")));
            Assert.That(wrapper.getAlternatorLiveNodes(), Is.SameAs(wrapper.GetAlternatorLiveNodes()));
            Assert.That(wrapper.getAlternatorLiveNodes().getLiveNodes(), Is.EqualTo(new List<Uri> { new Uri("http://127.0.0.1:8080") }));
            Assert.That(wrapper.getAlternatorLiveNodes().nextAsURI("/localnodes", "rack=r1"), Is.EqualTo(new Uri("http://127.0.0.1:8080/localnodes?rack=r1")));
            Assert.DoesNotThrow(() => wrapper.Close());
            Assert.DoesNotThrow(() => wrapper.close());
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderRequiresEndpointOverrideTest()
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                AlternatorDynamoDBClient.builder()
                    .WithoutValidation()
                    .WithDeferredStart()
                    .build());

            Assert.That(exception!.Message, Does.Contain("endpointOverride or withInitialSeeds must be set"));
        }

        [Test]
        public void AlternatorDynamoDBClientWrapperSupportsJavaStyleConstructorsTest()
        {
            var config = AlternatorConfig.builder()
                .withSeedNode("http://127.0.0.1:8080")
                .build();
            var clientConfig = new AmazonDynamoDBConfig
            {
                RegionEndpoint = RegionEndpoint.USEast1,
                ServiceURL = "http://127.0.0.1:8080",
            };
            var client = new AmazonDynamoDBClient(new AnonymousAWSCredentials(), clientConfig);
            var liveNodes = new AlternatorLiveNodes(config);
            using var wrapper = new AlternatorDynamoDBClientWrapper(client, liveNodes);

            Assert.That(wrapper.getClient(), Is.SameAs(client));
            Assert.That(wrapper.getAlternatorLiveNodes(), Is.SameAs(liveNodes));
            Assert.That(wrapper.getAlternatorConfig(), Is.Null);
            Assert.Throws<InvalidOperationException>(() => _ = wrapper.Config);

            var clientWithConfig = new AmazonDynamoDBClient(new AnonymousAWSCredentials(), clientConfig);
            var liveNodesWithConfig = new AlternatorLiveNodes(config);
            using var wrapperWithConfig = new AlternatorDynamoDBClientWrapper(clientWithConfig, liveNodesWithConfig, config);

            Assert.That(wrapperWithConfig.getClient(), Is.SameAs(clientWithConfig));
            Assert.That(wrapperWithConfig.getAlternatorLiveNodes(), Is.SameAs(liveNodesWithConfig));
            Assert.That(wrapperWithConfig.getAlternatorConfig(), Is.SameAs(config));
            Assert.That(wrapperWithConfig.Config, Is.SameAs(config));
        }

        [Test]
        public void AlternatorDynamoDBClientWrapperCloseWaitsForLiveNodesShutdownTest()
        {
            var handler = new TrackingHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8080)
                .withRoutingScope(ClusterScope.create())
                .build();
            var clientConfig = new AmazonDynamoDBConfig
            {
                RegionEndpoint = RegionEndpoint.USEast1,
                ServiceURL = "http://127.0.0.1:8080",
            };
            var client = new AmazonDynamoDBClient(new AnonymousAWSCredentials(), clientConfig);
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);
            var wrapper = new AlternatorDynamoDBClientWrapper(client, liveNodes, config);

            liveNodes.start().Wait(TimeSpan.FromSeconds(5));
            Assert.That(
                SpinWait.SpinUntil(() => handler.SendCount > 0, TimeSpan.FromSeconds(5)),
                Is.True);

            wrapper.close();

            Assert.That(liveNodes.isRunning(), Is.False);
            Assert.That(handler.DisposeCount, Is.EqualTo(0));
            Assert.That(handler.SendCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task AlternatorLiveNodesShutdownStopsCanceledStartTest()
        {
            using var wrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var liveNodes = wrapper.getAlternatorLiveNodes();

            await liveNodes.Start(cancellation.Token);
            await WaitUntilAsync(() => !liveNodes.IsRunning());
            liveNodes.shutdown();

            Assert.That(liveNodes.isRunning(), Is.False);
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsJavaStyleBuildAndConfigCopyTest()
        {
            var config = AlternatorConfig.builder()
                .withSeedNode("http://127.0.0.1:8080")
                .withCompressionAlgorithm(RequestCompressionAlgorithm.NONE)
                .withMaxConnections(50)
                .build();

            using var client = AlternatorDynamoDBClient.builder()
                .withAlternatorConfig(null)
                .endpointOverride("http://127.0.0.1:8080")
                .WithoutValidation()
                .WithDeferredStart()
                .build();

            var noEndpointBuilder = AlternatorDynamoDBClient.builder()
                .withAlternatorConfig(config)
                .WithoutValidation()
                .WithDeferredStart();
            Assert.Throws<InvalidOperationException>(() => noEndpointBuilder.buildWithAlternatorAPI());

            using var wrapper = AlternatorDynamoDBClient.builder()
                .withAlternatorConfig(config)
                .endpointOverride("http://127.0.0.1:8080")
                .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP)
                .withCompressionAlgorithm(null)
                .withMaxConnections(75)
                .region(RegionEndpoint.USWest2)
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(client, Is.InstanceOf<AmazonDynamoDBClient>());
            Assert.That(wrapper.Config.getSeedHosts(), Is.EqualTo(new[] { "127.0.0.1" }));
            Assert.That(wrapper.Config.getScheme(), Is.EqualTo("http"));
            Assert.That(wrapper.Config.getPort(), Is.EqualTo(8080));
            Assert.That(wrapper.Config.getCompressionAlgorithm(), Is.EqualTo(RequestCompressionAlgorithm.None));
            Assert.That(wrapper.Config.getMaxConnections(), Is.EqualTo(75));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderCopiesAlternatorConfigHeadersWhitelistLikeJavaTest()
        {
            var config = AlternatorConfig.builder()
                .withSeedNode("http://127.0.0.1:8080")
                .withCompressionAlgorithm(RequestCompressionAlgorithm.NONE)
                .build();

            var builder = AlternatorDynamoDBClient.builder()
                .withAlternatorConfig(config)
                .endpointOverride("http://127.0.0.1:8080")
                .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP)
                .WithoutValidation()
                .WithDeferredStart();

            var exception = Assert.Throws<ArgumentException>(() => builder.buildWithAlternatorAPI());
            Assert.That(exception!.Message, Does.Contain("Content-Encoding"));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderDisableCertificateChecksWinsAtBuildLikeJavaTest()
        {
            var strictTlsConfig = TlsConfig.systemDefault();
            var config = AlternatorConfig.builder()
                .withSeedNode("https://127.0.0.1:8181")
                .withTlsConfig(strictTlsConfig)
                .build();

            using var wrapper = AlternatorDynamoDBClient.builder()
                .withDisableCertificateChecks()
                .withAlternatorConfig(config)
                .endpointOverride("https://127.0.0.1:8181")
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.getTlsConfig().isTrustAllCertificates(), Is.True);
            Assert.That(wrapper.Config.getTlsConfig().isVerifyHostname(), Is.False);
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsAwsBuilderParityMethodsTest()
        {
            var builder = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .WithoutValidation()
                .WithDeferredStart();

            Assert.That(builder.region("us-west-2"), Is.SameAs(builder));
            Assert.That(builder.overrideConfiguration().RegionEndpoint, Is.EqualTo(RegionEndpoint.USWest2));
            Assert.That(builder.region(RegionEndpoint.USEast1), Is.SameAs(builder));
            Assert.That(builder.overrideConfiguration().RegionEndpoint, Is.EqualTo(RegionEndpoint.USEast1));
            Assert.That(builder.overrideConfiguration(config => config.Timeout = TimeSpan.FromSeconds(3)), Is.SameAs(builder));
            Assert.That(builder.overrideConfiguration().Timeout, Is.EqualTo(TimeSpan.FromSeconds(3)));
            var replacementConfig = new AmazonDynamoDBConfig
            {
                RegionEndpoint = RegionEndpoint.USWest2,
                Timeout = TimeSpan.FromSeconds(4),
            };
            Assert.That(builder.overrideConfiguration(replacementConfig), Is.SameAs(builder));
            Assert.That(builder.overrideConfiguration(), Is.SameAs(replacementConfig));
            Assert.That(builder.accountIdEndpointMode(AccountIdEndpointMode.DISABLED), Is.SameAs(builder));
            Assert.Throws<NotSupportedException>(() => builder.endpointProvider(null!));
            Assert.Throws<NotSupportedException>(() => builder.endpointDiscoveryEnabled(true));
            Assert.Throws<NotSupportedException>(() => builder.enableEndpointDiscovery());
            Assert.Throws<NotSupportedException>(() => builder.fipsEnabled(true));
            Assert.Throws<NotSupportedException>(() => builder.dualstackEnabled(true));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsCredentialConvenienceOverloadsTest()
        {
            using var basicWrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .credentialsProvider("access-key", "secret-key")
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();
            using var sessionWrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8081")
                .credentialsProvider("access-key", "secret-key", "session-token")
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(basicWrapper.Config.isAuthenticationEnabled(), Is.True);
            Assert.That(sessionWrapper.Config.isAuthenticationEnabled(), Is.True);
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsHttpClientHandlerCustomizerTest()
        {
            var builder = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080");

            Assert.That(builder.withHttpClientHandlerCustomizer(_ => { }), Is.SameAs(builder));
            Assert.That(builder.withApacheHttpClientCustomizer(_ => { }), Is.SameAs(builder));
            Assert.That(builder.withCrtHttpClientCustomizer(_ => { }), Is.SameAs(builder));
            Assert.That(builder.withSystemNetHttpClientCustomizer(_ => { }), Is.SameAs(builder));
            Assert.That(builder.withHttpClientType(HttpClientType.APACHE), Is.SameAs(builder));
            Assert.That(builder.HttpClientType, Is.EqualTo(HttpClientType.Apache));
            Assert.That(builder.withHttpClientType(HttpClientType.CRT), Is.SameAs(builder));
            Assert.That(builder.HttpClientType, Is.EqualTo(HttpClientType.Crt));
            Assert.Throws<ArgumentNullException>(() => builder.withHttpClientType(null));
            Assert.Throws<ArgumentNullException>(() => builder.withHttpClientHandlerCustomizer(null!));
            Assert.Throws<ArgumentNullException>(() => builder.withApacheHttpClientCustomizer(null!));
            Assert.Throws<ArgumentNullException>(() => builder.withCrtHttpClientCustomizer(null!));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsHttpClientFactoryTest()
        {
            var factory = new TestHttpClientFactory();
            var builder = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .WithoutValidation()
                .WithDeferredStart();

            Assert.That(builder.httpClientFactory(factory), Is.SameAs(builder));
            Assert.That(builder.overrideConfiguration().HttpClientFactory, Is.SameAs(factory));

            using var client = builder.build();
            Assert.That(client, Is.Not.Null);
            Assert.Throws<ArgumentNullException>(() => AlternatorDynamoDBClient.builder().httpClientFactory(null!));
            Assert.Throws<InvalidOperationException>(() => AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .httpClient(factory)
                .withHttpClientType(HttpClientType.APACHE)
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI());
            Assert.Throws<InvalidOperationException>(() => AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .httpClientBuilder(factory)
                .withSocketsHttpHandlerCustomizer(_ => { })
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI());
            Assert.Throws<NotSupportedException>(() => AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .withHttpClientType(HttpClientType.NETTY)
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI());
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderValidatesJavaStyleHttpClientCustomizerConflictsTest()
        {
            Assert.Throws<InvalidOperationException>(() => AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .withApacheHttpClientCustomizer(_ => { })
                .withCrtHttpClientCustomizer(_ => { })
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI());

            Assert.Throws<InvalidOperationException>(() => AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .withHttpClientType(HttpClientType.APACHE)
                .withCrtHttpClientCustomizer(_ => { })
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI());

            Assert.Throws<InvalidOperationException>(() => AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .withHttpClientType(HttpClientType.CRT)
                .withApacheHttpClientCustomizer(_ => { })
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI());

            using var apacheWrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .withHttpClientType(HttpClientType.APACHE)
                .withApacheHttpClientCustomizer(_ => { })
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();
            using var crtWrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8081")
                .withHttpClientType(HttpClientType.CRT)
                .withCrtHttpClientCustomizer(_ => { })
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(apacheWrapper.Config, Is.Not.Null);
            Assert.That(crtWrapper.Config, Is.Not.Null);
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsSocketsHttpHandlerCustomizerTest()
        {
            var builder = AlternatorDynamoDBClient.builder()
                .endpointOverride("https://127.0.0.1:8181");

            Assert.That(builder.withSocketsHttpHandlerCustomizer(_ => { }), Is.SameAs(builder));
            Assert.Throws<ArgumentNullException>(() => builder.withSocketsHttpHandlerCustomizer(null!));

            using var wrapper = builder
                .withAlternatorConfig(AlternatorConfig.builder()
                    .withSeedNode("https://127.0.0.1:8181")
                    .withMaxConnections(321)
                    .withConnectionMaxIdleTimeMs(30000)
                    .withConnectionTimeToLiveMs(60000)
                    .withConnectionTimeoutMs(7000)
                    .withTlsConfig(TlsConfig.trustAll())
                    .build())
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.getMaxConnections(), Is.EqualTo(321));
            Assert.That(wrapper.Config.getConnectionMaxIdleTimeMs(), Is.EqualTo(30000));
            Assert.That(wrapper.Config.getConnectionTimeToLiveMs(), Is.EqualTo(60000));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsCompressionAndHeaderOptimizationTest()
        {
            using var wrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP)
                .withMinCompressionSizeBytes(1)
                .withOptimizeHeaders(true)
                .withHeadersWhitelist(new[]
                {
                    "Host",
                    "X-Amz-Target",
                    "Content-Type",
                    "Content-Length",
                    "Accept-Encoding",
                    "Connection",
                    "User-Agent",
                    "Content-Encoding",
                })
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.CompressionAlgorithm, Is.EqualTo(RequestCompressionAlgorithm.Gzip));
            Assert.That(wrapper.Config.MinCompressionSizeBytes, Is.EqualTo(1));
            Assert.That(wrapper.Config.OptimizeHeaders, Is.True);
            Assert.That(wrapper.Config.AuthenticationEnabled, Is.False);
            Assert.That(wrapper.Config.HeadersWhitelist, Does.Contain("Content-Encoding"));
            Assert.That(wrapper.Config.HeadersWhitelist, Does.Not.Contain("Authorization"));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsUserAgentConfigurationTest()
        {
            var builder = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080");

            Assert.That(builder.withUserAgent("custom/1"), Is.SameAs(builder));
            Assert.That(builder.withUserAgent(userAgent => userAgent + " app/1"), Is.SameAs(builder));
            Assert.That(builder.withoutUserAgent(), Is.SameAs(builder));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderRejectsInvalidUserAgentConfigurationTest()
        {
            var builder = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080");

            Assert.Throws<ArgumentException>(() => builder.withUserAgent((string)null!));
            Assert.Throws<ArgumentException>(() => builder.withUserAgent(" "));
            var nullTransformer = Assert.Throws<ArgumentException>(() =>
                builder.withUserAgent((Func<string, string?>)null!));
            Assert.That(nullTransformer!.Message, Does.Contain("userAgentTransformer cannot be null"));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderWithoutUserAgentUpdatesHeaderRequirementsTest()
        {
            using var wrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .withOptimizeHeaders(true)
                .withoutUserAgent()
                .withHeadersWhitelist(AlternatorConfig.BaseRequiredHeaders)
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.UserAgentEnabled, Is.False);
            Assert.That(wrapper.Config.RequiredHeaders, Does.Not.Contain("User-Agent"));
            Assert.That(wrapper.Config.HeadersWhitelist, Does.Not.Contain("User-Agent"));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderValidatesAuthenticationHeadersTest()
        {
            var credentials = new BasicAWSCredentials("user", "password");
            var builder = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .credentialsProvider(credentials)
                .withOptimizeHeaders(true)
                .withHeadersWhitelist(AlternatorConfig.BaseRequiredHeaders)
                .WithoutValidation()
                .WithDeferredStart();

            Assert.Throws<ArgumentException>(() => builder.buildWithAlternatorAPI());
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderSupportsKeyRouteAffinityTest()
        {
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE)
                .withPkInfo("users", "user_id")
                .build();

            using var wrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .withKeyRouteAffinity(affinity)
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.KeyRouteAffinityConfig, Is.Not.Null);
            Assert.That(wrapper.Config.KeyRouteAffinityConfig!.Type, Is.EqualTo(KeyRouteAffinity.AnyWrite));
            Assert.That(wrapper.Config.KeyRouteAffinityConfig.PkInfoPerTable["users"], Is.EqualTo("user_id"));
        }

        [Test]
        public void AlternatorDynamoDBClientBuilderCreatesPartitionKeyResolverForAffinityAutodiscoveryTest()
        {
            using var wrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride("http://127.0.0.1:8080")
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Assert.That(wrapper.PartitionKeyResolver, Is.Not.Null);
            Assert.That(wrapper.getAlternatorConfig(), Is.SameAs(wrapper.Config));
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await Task.Delay(20, cancellation.Token);
            }
        }

        private sealed class TrackingHttpMessageHandler : HttpMessageHandler
        {
            private int disposeCount;
            private int sendCount;

            internal int DisposeCount => this.disposeCount;

            internal int SendCount => this.sendCount;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref this.sendCount);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("[\"127.0.0.2\"]", System.Text.Encoding.UTF8, "application/json"),
                });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Interlocked.Increment(ref this.disposeCount);
                }

                base.Dispose(disposing);
            }
        }

        private sealed class TestHttpClientFactory : HttpClientFactory
        {
            public override HttpClient CreateHttpClient(IClientConfig clientConfig)
            {
                return new HttpClient(new HttpClientHandler());
            }
        }
    }
}
