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
    public class UnitTests
    {
        public UnitTests()
        {
        }

        [Test]
        public void HelperConstructorTest()
        {
            var uri = new Uri("http://127.0.0.1:8080");
            var datacenter = "dc1";
            var rack = "rack1";

            var provider = new Helper(uri, datacenter, rack);

            Assert.That(provider, Is.Not.Null);
        }

        [Test]
        [Category("Unit")]
        public void HelperConstructorWithEmptyValuesTest()
        {
            var uri = new Uri("http://127.0.0.1:8080");
            var datacenter = string.Empty;
            var rack = string.Empty;

            var provider = new Helper(uri, datacenter, rack);

            Assert.That(provider, Is.Not.Null);
        }

        [Test]
        [Category("Unit")]
        public void AlternatorLiveNodesConstructorTest()
        {
            var uri = new Uri("http://127.0.0.1:8080");
            var datacenter = "dc1";
            var rack = "rack1";
            var nodes = new AlternatorLiveNodes(uri, datacenter, rack);

            Assert.That(nodes, Is.Not.Null);
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderBasicTest()
        {
            var uri = new Uri("http://127.0.0.1:8080");
            var options = HelperOptionsBuilder.Create()
                .WithSeedUri(uri)
                .WithDatacenter("dc1")
                .WithRack("rack1")
                .Build();

            Assert.That(options, Is.Not.Null);
            Assert.That(options.SeedUri, Is.EqualTo(uri));
            Assert.That(options.Datacenter, Is.EqualTo("dc1"));
            Assert.That(options.Rack, Is.EqualTo("rack1"));
            Assert.That(options.ValidateOnInitialization, Is.True);
            Assert.That(options.StartImmediately, Is.True);
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderWithStringUriTest()
        {
            var uriString = "http://127.0.0.1:8080";
            var options = HelperOptionsBuilder.Create()
                .WithSeedUri(uriString)
                .WithDatacenterAndRack("dc1", "rack1")
                .Build();

            Assert.That(options, Is.Not.Null);
            Assert.That(options.SeedUri.ToString(), Is.EqualTo(uriString));
            Assert.That(options.Datacenter, Is.EqualTo("dc1"));
            Assert.That(options.Rack, Is.EqualTo("rack1"));
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderWithValidationDisabledTest()
        {
            var uri = new Uri("http://127.0.0.1:8080");
            var options = HelperOptionsBuilder.Create()
                .WithSeedUri(uri)
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
        public void HelperOptionsBuilderThrowsWhenSeedUriIsNullTest()
        {
            var builder = HelperOptionsBuilder.Create();

            Assert.Throws<ArgumentNullException>(() => builder.WithSeedUri((Uri)null));
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderThrowsWhenSeedUriStringIsEmptyTest()
        {
            var builder = HelperOptionsBuilder.Create();

            Assert.Throws<ArgumentException>(() => builder.WithSeedUri(string.Empty));
        }

        [Test]
        [Category("Unit")]
        public void HelperConstructorWithOptionsTest()
        {
            var uri = new Uri("http://127.0.0.1:8080");
            var options = HelperOptionsBuilder.Create()
                .WithSeedUri(uri)
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
        public void HelperConstructorWithNullOptionsTest()
        {
            Assert.Throws<ArgumentNullException>(() => new Helper((HelperOptions)null));
        }
    }
}