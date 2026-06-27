// <copyright file="UnitTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon.DynamoDBv2;
    using Amazon.DynamoDBv2.Model;
    using Amazon.Runtime;

    [TestFixture]
    [Category("Unit")]
    public class HelperOptionsBuilderUnitTests
    {
        public HelperOptionsBuilderUnitTests()
        {
        }

        [Test]
        public void HelperOptionsBuilderBasicTest()
        {
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("https://127.0.0.1:8181"))
                .WithDatacenter("dc1")
                .WithRack("rack1")
                .Build();

            Assert.That(options, Is.Not.Null);
            Assert.That(options.InitialNodes, Is.EqualTo(new List<string> { "127.0.0.1" }));
            Assert.That(options.Schema, Is.EqualTo("https"));
            Assert.That(options.Port, Is.EqualTo(8181));
            Assert.That(options.Datacenter, Is.EqualTo("dc1"));
            Assert.That(options.Rack, Is.EqualTo("rack1"));
            Assert.That(options.ValidateOnInitialization, Is.True);
            Assert.That(options.StartImmediately, Is.True);
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderWithValidationDisabledTest()
        {
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("http://127.0.0.1:8080"))
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            Assert.That(options, Is.Not.Null);
            Assert.That(options.ValidateOnInitialization, Is.False);
            Assert.That(options.StartImmediately, Is.False);
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderThrowsWhenSeedUriNotSetTest()
        {
            var builder = HelperOptionsBuilder.Create()
                .WithDatacenter("dc1")
                .WithRack("rack1");

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Test]
        [Category("Unit")]
        public void HelperConstructorWithOptionsTest()
        {
            var uri = new Uri("http://127.0.0.1:8080");
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(uri)
                .WithDatacenter("dc1")
                .WithRack("rack1")
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            var helper = new Helper(options);

            Assert.That(helper, Is.Not.Null);
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderSupportsCustomHeaderOptimizerTest()
        {
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("http://127.0.0.1:8080"))
                .WithCustomOptimizeHeaders(config => config.RequiredHeaders.Concat(new[] { "X-Helper-Trace" }))
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            using var wrapper = AlternatorDynamoDBClient.builder()
                .WithOptions(options)
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.OptimizeHeaders, Is.True);
            Assert.That(wrapper.Config.HeadersWhitelist, Does.Contain("X-Helper-Trace"));
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderSupportsResponseCompressionConfigurationTest()
        {
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("http://127.0.0.1:8080"))
                .WithResponseCompression(ResponseCompressionAlgorithm.GZIP)
                .WithoutValidation()
                .WithDeferredStart()
                .Build();
            var disabledOptions = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("http://127.0.0.1:8081"))
                .WithoutResponseCompression()
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            using var wrapper = AlternatorDynamoDBClient.builder()
                .WithOptions(options)
                .buildWithAlternatorAPI();
            using var disabledWrapper = AlternatorDynamoDBClient.builder()
                .WithOptions(disabledOptions)
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.ResponseCompressionAlgorithms, Is.EqualTo(new[] { ResponseCompressionAlgorithm.Gzip }));
            Assert.That(disabledWrapper.Config.ResponseCompressionAlgorithms, Is.Empty);
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderSupportsConnectionTuningAliasesTest()
        {
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("http://127.0.0.1:8080"))
                .WithMaxIdleHttpConnections(71)
                .WithMaxIdleHttpConnectionsPerHost(72)
                .WithIdleHttpConnectionTimeoutMs(73000)
                .WithConnectionTimeToLiveMs(74000)
                .WithConnectionAcquisitionTimeoutMs(75000)
                .WithConnectionTimeoutMs(76000)
                .WithHttpClientTimeoutMs(77000)
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            using var wrapper = AlternatorDynamoDBClient.builder()
                .WithOptions(options)
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.MaxConnections, Is.EqualTo(72));
            Assert.That(wrapper.Config.ConnectionMaxIdleTimeMs, Is.EqualTo(73000));
            Assert.That(wrapper.Config.ConnectionTimeToLiveMs, Is.EqualTo(74000));
            Assert.That(wrapper.Config.ConnectionAcquisitionTimeoutMs, Is.EqualTo(75000));
            Assert.That(wrapper.Config.ConnectionTimeoutMs, Is.EqualTo(76000));
            Assert.That(wrapper.Config.HttpClientTimeoutMs, Is.EqualTo(77000));
            Assert.That(wrapper.getClient().Config.Timeout, Is.EqualTo(TimeSpan.FromMilliseconds(77000)));
        }
    }
}
