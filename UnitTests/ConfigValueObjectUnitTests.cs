// <copyright file="ConfigValueObjectUnitTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using ScyllaDB.Alternator.KeyRouting;
    using ScyllaDB.Alternator.Routing;

    [TestFixture]
    [Category("Unit")]
    public class ConfigValueObjectUnitTests
    {
        [Test]
        public void RequestCompressionAlgorithmSupportsJavaStyleEnabledCheckTest()
        {
            Assert.That(RequestCompressionAlgorithm.NONE.isEnabled(), Is.False);
            Assert.That(RequestCompressionAlgorithm.GZIP.isEnabled(), Is.True);
        }

        [Test]
        public void HttpClientTypeSupportsJavaStyleCapabilityChecksTest()
        {
            Assert.That(HttpClientType.AUTO.supportsSync(), Is.True);
            Assert.That(HttpClientType.AUTO.supportsAsync(), Is.True);
            Assert.That(HttpClientType.APACHE.supportsSync(), Is.True);
            Assert.That(HttpClientType.APACHE.supportsAsync(), Is.False);
            Assert.That(HttpClientType.NETTY.supportsSync(), Is.False);
            Assert.That(HttpClientType.NETTY.supportsAsync(), Is.True);
            Assert.That(HttpClientType.CRT.supportsSync(), Is.True);
            Assert.That(HttpClientType.CRT.supportsAsync(), Is.True);
        }

        [Test]
        public void RoutingScopesSupportJavaStyleAccessorsAndValueEqualityTest()
        {
            var cluster = ClusterScope.create();
            Assert.That(cluster.getName(), Is.EqualTo("Cluster"));
            Assert.That(cluster.getDescription(), Is.EqualTo("Cluster (all nodes)"));
            Assert.That(cluster.getFallback(), Is.Null);
            Assert.That(cluster.getLocalNodesQuery(), Is.EqualTo(string.Empty));
            Assert.That(ClusterScope.create(), Is.SameAs(cluster));

            var datacenter = DatacenterScope.of("dc1", cluster);
            Assert.That(datacenter.getDatacenter(), Is.EqualTo("dc1"));
            Assert.That(datacenter.getName(), Is.EqualTo("Datacenter"));
            Assert.That(datacenter.getDescription(), Is.EqualTo("Datacenter dc1"));
            Assert.That(datacenter.getFallback(), Is.SameAs(cluster));
            Assert.That(datacenter.getLocalNodesQuery(), Is.EqualTo("dc=dc1"));
            Assert.That(DatacenterScope.of("dc1", ClusterScope.create()), Is.EqualTo(datacenter));
            Assert.That(DatacenterScope.of("dc2", ClusterScope.create()), Is.Not.EqualTo(datacenter));

            var rack = RackScope.of("dc1", "rack1", datacenter);
            Assert.That(rack.getRack(), Is.EqualTo("rack1"));
            Assert.That(rack.getName(), Is.EqualTo("Rack"));
            Assert.That(rack.getDescription(), Is.EqualTo("Rack rack1 in Datacenter dc1"));
            Assert.That(rack.getFallback(), Is.SameAs(datacenter));
            Assert.That(rack.getLocalNodesQuery(), Is.EqualTo("dc=dc1&rack=rack1"));
            Assert.That(RackScope.of("dc1", "rack1", DatacenterScope.of("dc1", ClusterScope.create())), Is.EqualTo(rack));
            Assert.That(RackScope.of("dc1", "rack2", datacenter), Is.Not.EqualTo(rack));

            Assert.Throws<ArgumentException>(() => DatacenterScope.of(string.Empty, null));
            Assert.Throws<ArgumentException>(() => RackScope.of("dc1", string.Empty, null));
        }

        [Test]
        public void TlsConfigSupportsJavaStyleBuildersTest()
        {
            var tls = TlsConfig.builder()
                .withCaCertPath("/tmp/ca.pem")
                .withClientCertificate("/tmp/client.crt", "/tmp/client.key")
                .withTrustSystemCaCerts(false)
                .withTlsSessionResumption(false)
                .build();

            Assert.That(tls.getCustomCaCertPaths(), Is.EqualTo(new[] { "/tmp/ca.pem" }));
            Assert.That(tls.hasClientCertificate(), Is.True);
            Assert.That(tls.isTrustSystemCaCerts(), Is.False);
            Assert.That(tls.isTrustAllCertificates(), Is.False);
            Assert.That(tls.isVerifyHostname(), Is.True);
            Assert.That(tls.isTlsSessionResumptionEnabled(), Is.False);
            Assert.That(TlsConfig.trustAll().isTrustAllCertificates(), Is.True);
            Assert.That(TlsConfig.trustAll().isTlsSessionResumptionEnabled(), Is.True);
            Assert.That(TlsConfig.systemDefault().isTrustSystemCaCerts(), Is.True);
            Assert.That(TlsConfig.systemDefault().isTlsSessionResumptionEnabled(), Is.True);
        }

        [Test]
        public void KeyRouteAffinityConfigSupportsJavaStyleBuilderAndReadOnlyMapTest()
        {
            var config = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.RMW)
                .withPkInfo("orders", "order_id")
                .withPkInfoMap(new Dictionary<string, string> { ["users"] = "user_id" })
                .build();

            Assert.That(config.isEnabled(), Is.True);
            Assert.That(config.getType(), Is.EqualTo(KeyRouteAffinity.Rmw));
            Assert.That(config.getPkInfoPerTable()["orders"], Is.EqualTo("order_id"));
            Assert.That(config.getPkInfoPerTable()["users"], Is.EqualTo("user_id"));
            Assert.Throws<NotSupportedException>(() =>
                ((IDictionary<string, string>)config.getPkInfoPerTable()).Add("products", "product_id"));
            Assert.That(KeyRouteAffinityConfig.of(null).isEnabled(), Is.False);
        }

        [Test]
        public void AlternatorConfigBuilderComputesRequiredHeadersAndValidatesScalarsTest()
        {
            var config = AlternatorConfig.builder()
                .withSeedNode("https://127.0.0.1:8043")
                .withRoutingScope(DatacenterScope.of("dc1", ClusterScope.create()))
                .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP)
                .withOptimizeHeaders(true)
                .withUserAgentEnabled(false)
                .withAuthenticationEnabled(false)
                .withMaxConnections(50)
                .build();

            Assert.That(config.getSeedHosts(), Is.EqualTo(new[] { "127.0.0.1" }));
            Assert.That(config.getScheme(), Is.EqualTo("https"));
            Assert.That(config.getPort(), Is.EqualTo(8043));
            Assert.That(config.getRoutingScope().getLocalNodesQuery(), Is.EqualTo("dc=dc1"));
            Assert.That(config.getRequiredHeaders(), Does.Contain("Content-Encoding"));
            Assert.That(config.getRequiredHeaders(), Does.Not.Contain("User-Agent"));
            Assert.That(config.getRequiredHeaders(), Does.Not.Contain("Authorization"));
            Assert.That(config.getMaxConnections(), Is.EqualTo(50));
            Assert.Throws<NotSupportedException>(() =>
                ((ISet<string>)config.getRequiredHeaders()).Add("X-Test"));

            Assert.Throws<ArgumentException>(() => AlternatorConfig.builder()
                .withMinCompressionSizeBytes(-1)
                .build());
            Assert.Throws<ArgumentException>(() => AlternatorConfig.builder()
                .withMaxConnections(0)
                .build());
            Assert.Throws<ArgumentException>(() => AlternatorConfig.builder()
                .withHeadersWhitelist(new[] { "Host" })
                .build());
        }
    }
}
