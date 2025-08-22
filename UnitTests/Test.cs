// <copyright file="Test.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon.DynamoDBv2;
    using Amazon.DynamoDBv2.Model;
    using Amazon.Runtime;

    [TestFixture]
    public class Test
    {
        public Test()
        {
        }

        [Test]
        [Category("Unit")]
        public void EndpointProviderConstructorTest()
        {
            // Test EndpointProvider constructor
            var uri = new Uri("http://127.0.0.1:8080");
            var datacenter = "dc1";
            var rack = "rack1";

            var provider = new EndpointProvider(uri, datacenter, rack);

            Assert.That(provider, Is.Not.Null);
        }

        [Test]
        [Category("Unit")]
        public void EndpointProviderConstructorWithEmptyValuesTest()
        {
            // Test EndpointProvider constructor with empty datacenter and rack
            var uri = new Uri("http://127.0.0.1:8080");
            var datacenter = string.Empty;
            var rack = string.Empty;

            var provider = new EndpointProvider(uri, datacenter, rack);

            Assert.That(provider, Is.Not.Null);
        }

        [Test]
        [Category("Unit")]
        public void AlternatorLiveNodesConstructorTest()
        {
            // Test AlternatorLiveNodes constructor
            var uri = new Uri("http://127.0.0.1:8080");
            var datacenter = "dc1";
            var rack = "rack1";
            var nodes = new AlternatorLiveNodes(uri, datacenter, rack);

            Assert.That(nodes, Is.Not.Null);
        }
    }
}