// Copyright ScyllaDB, Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

namespace ScyllaDB.Alternator
{
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using Amazon.DynamoDBv2.Model;
    using Amazon.Runtime;
    using ScyllaDB.Alternator.KeyRouting;
    using ScyllaDB.Alternator.Routing;

    [TestFixture]
    [Category("Unit")]
    public class AlternatorLiveNodesUnitTests
    {
        public AlternatorLiveNodesUnitTests()
        {
        }

        [Test]
        public void NextAsUriStartsWithFirstNodeAndRoundRobinsTest()
        {
            var liveNodes = new AlternatorLiveNodes(
                new List<string>
                {
                    "127.0.0.1",
                    "127.0.0.2",
                    "127.0.0.3",
                },
                "http",
                8080,
                ClusterScope.create());

            Assert.That(liveNodes.nextAsURI(), Is.EqualTo(new Uri("http://127.0.0.1:8080")));
            Assert.That(liveNodes.nextAsURI(), Is.EqualTo(new Uri("http://127.0.0.2:8080")));
            Assert.That(liveNodes.nextAsURI(), Is.EqualTo(new Uri("http://127.0.0.3:8080")));
            Assert.That(liveNodes.nextAsURI(), Is.EqualTo(new Uri("http://127.0.0.1:8080")));
        }

        [Test]
        public void CheckIfRackDatacenterFeatureIsSupportedUsesOneBaseNodeForBothRequestsTest()
        {
            using var server = new LocalNodesServer(
                2,
                request => request.Query == "rack=fakeRack" ? "[]" : "[\"127.0.0.1\"]");
            var liveNodes = new AlternatorLiveNodes(
                new List<string>
                {
                    "127.0.0.1",
                    "127.0.0.2",
                },
                "http",
                server.Port,
                ClusterScope.create());

            Assert.That(liveNodes.checkIfRackDatacenterFeatureIsSupported(), Is.True);
            server.WaitForRequests();

            Assert.That(
                server.Requests.Select(request => request.Host.Split(':')[0]),
                Is.EqualTo(new[] { "127.0.0.1", "127.0.0.1" }));
            Assert.That(
                server.Requests.Select(request => request.Target),
                Is.EqualTo(new[] { "/localnodes?rack=fakeRack", "/localnodes" }));
        }

        [Test]
        public void CheckIfRackAndDatacenterSetCorrectlyDoesNotFallbackFromConfiguredScopeTest()
        {
            using var server = new LocalNodesServer(1, _ => "[]");
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", ClusterScope.create()))
                .build();
            var liveNodes = new AlternatorLiveNodes(config);

            var exception = Assert.Throws<AlternatorLiveNodes.ValidationError>(() =>
                liveNodes.checkIfRackAndDatacenterSetCorrectly());
            Assert.That(exception, Is.InstanceOf<ValidationError>());
            server.WaitForRequests();

            Assert.That(server.Requests, Has.Count.EqualTo(1));
            Assert.That(server.Requests[0].Target, Is.EqualTo("/localnodes?dc=dc1&rack=rack1"));
        }

        [Test]
        public void CheckIfRackDatacenterFeatureIsSupportedThrowsNestedFailedToCheckLikeJavaTest()
        {
            using var server = new LocalNodesServer(2, _ => "[]");
            var liveNodes = new AlternatorLiveNodes(
                new List<string>
                {
                    "127.0.0.1",
                },
                "http",
                server.Port,
                ClusterScope.create());

            var exception = Assert.Throws<AlternatorLiveNodes.FailedToCheck>(() =>
                liveNodes.checkIfRackDatacenterFeatureIsSupported());
            Assert.That(exception, Is.InstanceOf<FailedToCheck>());
            server.WaitForRequests();

            Assert.That(
                server.Requests.Select(request => request.Target),
                Is.EqualTo(new[] { "/localnodes?rack=fakeRack", "/localnodes" }));
        }

        [Test]
        public void UpdateLiveNodesReplacesSeedNodesWithDiscoveredNodesTest()
        {
            using var server = new LocalNodesServer(1, _ => "[\"127.0.0.2\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config);

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "127.0.0.2" }));
        }

        [Test]
        public void UpdateLiveNodesResolvesDnsEntrypointAndKeepsDnsNodeRecordsTest()
        {
            using var server = new LocalNodesServer(1, _ => "[\"localhost\",\"node-a.internal\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("localhost")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config);

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(server.Requests[0].Host, Is.EqualTo($"localhost:{server.Port}"));
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "localhost", "node-a.internal" }));
        }

        [Test]
        public void ClusterScopePollsAllSeedNodesAndMergesResultsTest()
        {
            var handler = new DiscoveryHttpMessageHandler(new Dictionary<string, string>
            {
                ["dc1-node1.example.com"] = "[\"dc1-node1.example.com\",\"dc1-node2.example.com\"]",
                ["dc2-node1.example.com"] = "[\"dc2-node1.example.com\",\"dc2-node2.example.com\"]",
            });
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "dc1-node1.example.com", "dc2-node1.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[]
                {
                    "dc1-node1.example.com",
                    "dc1-node2.example.com",
                    "dc2-node1.example.com",
                    "dc2-node2.example.com",
                }));
            Assert.That(
                handler.RequestedUris.Select(uri => uri.Host),
                Is.EqualTo(new[] { "dc1-node1.example.com", "dc2-node1.example.com" }));
            Assert.That(handler.RequestedUris.Select(uri => uri.AbsolutePath), Is.All.EqualTo("/localnodes"));
            Assert.That(handler.RequestedUris.Select(uri => uri.Query), Is.All.Empty);
        }

        [Test]
        public void ClusterScopePublishesSuccessfulSeedResponsesWhenAnotherSeedFailsTest()
        {
            var handler = new PartialClusterDiscoveryHttpMessageHandler("[\"partial.example.com\"]");
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "dc1-node1.example.com", "dc2-node1.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            var completeTopology = liveNodes.getLiveNodes();
            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                completeTopology.Select(node => node.Host),
                Is.EqualTo(new[]
                {
                    "dc1-discovered1.example.com",
                    "dc1-discovered2.example.com",
                    "dc2-discovered1.example.com",
                    "dc2-discovered2.example.com",
                }));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "partial.example.com" }));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Does.Not.Contain("dc1-node1.example.com"));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Does.Not.Contain("dc2-node1.example.com"));
            Assert.That(
                handler.RequestedUris.Select(uri => uri.Host),
                Is.EqualTo(new[]
                {
                    "dc1-node1.example.com",
                    "dc2-node1.example.com",
                    "dc1-node1.example.com",
                    "dc2-node1.example.com",
                }));
        }

        [Test]
        public void ClusterScopeReinjectsSeedsWhenOneSeedIsEmptyAndAnotherFailsTest()
        {
            var handler = new PartialClusterDiscoveryHttpMessageHandler("[]");
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "dc1-node1.example.com", "dc2-node1.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            var completeTopology = liveNodes.getLiveNodes();
            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(completeTopology.Concat(new[]
            {
                new Uri("http://dc1-node1.example.com:8000"),
                new Uri("http://dc2-node1.example.com:8000"),
            })));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Does.Contain("dc1-node1.example.com"));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Does.Contain("dc2-node1.example.com"));
        }

        [Test]
        public void ClusterScopeReinjectsSeedsWhenEverySeedFailsTest()
        {
            var handler = new PartialClusterDiscoveryHttpMessageHandler(null);
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "dc1-node1.example.com", "dc2-node1.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            var completeTopology = liveNodes.getLiveNodes();
            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(completeTopology.Concat(new[]
            {
                new Uri("http://dc1-node1.example.com:8000"),
                new Uri("http://dc2-node1.example.com:8000"),
            })));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Does.Contain("dc1-node1.example.com"));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Does.Contain("dc2-node1.example.com"));
        }

        [Test]
        public void RackScopeRetriesNextSeedWhenFirstSeedReportsNoNodesTest()
        {
            var handler = new DiscoveryHttpMessageHandler(new Dictionary<string, string>
            {
                ["dc2-node1.example.com"] = "[]",
                ["dc1-node1.example.com"] = "[\"dc1-rack1-node.example.com\"]",
            });
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "dc2-node1.example.com", "dc1-node1.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(RackScope.of("dc1", "rack1", DatacenterScope.of("dc1", ClusterScope.create())))
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "dc1-rack1-node.example.com" }));
            Assert.That(
                handler.RequestedUris.Select(uri => uri.Host),
                Is.EqualTo(new[] { "dc2-node1.example.com", "dc1-node1.example.com" }));
            Assert.That(handler.RequestedUris.Select(uri => uri.AbsolutePath), Is.All.EqualTo("/localnodes"));
            Assert.That(handler.RequestedUris.Select(uri => uri.Query), Is.All.EqualTo("?dc=dc1&rack=rack1"));
        }

        [Test]
        public void RackScopeAffinityUsesDatacenterNodesWhileBasicRoutingStaysLocalTest()
        {
            using var server = new LocalNodesServer(
                4,
                request => request.Query switch
                {
                    "dc=dc1&rack=rack1" => "[\"127.0.0.2\"]",
                    "dc=dc1&rack=rack2" => "[\"127.0.0.3\"]",
                    "dc=dc1" => "[\"127.0.0.2\",\"127.0.0.3\",\"127.0.0.4\"]",
                    var query when string.IsNullOrEmpty(query) => "[\"other-dc.example.com\"]",
                    _ => throw new InvalidOperationException("Unexpected discovery query: " + request.Query),
                });
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE)
                .withPkInfo("users", "id")
                .build();
            var rack1 = CreateRackScopedLiveNodes(server.Port, "rack1", affinity);
            var rack2 = CreateRackScopedLiveNodes(server.Port, "rack2", affinity);

            InvokeUpdateLiveNodes(rack1);
            InvokeUpdateLiveNodes(rack2);
            server.WaitForRequests();

            var request = new PutItemRequest
            {
                TableName = "users",
                Item = new Dictionary<string, AttributeValue>
                {
                    ["id"] = new AttributeValue { S = "same-partition" },
                },
            };
            var batchRequest = new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    ["users"] = new List<WriteRequest>
                    {
                        new WriteRequest
                        {
                            PutRequest = new PutRequest { Item = request.Item },
                        },
                    },
                },
            };
            var rack1Affinity = new AffinityQueryPlanInterceptor(affinity, rack1);
            var rack2Affinity = new AffinityQueryPlanInterceptor(affinity, rack2);

            var rack1BasicPlan = new BasicQueryPlanInterceptor(rack1)
                .GetOrCreateQueryPlan(new ListTablesRequest(), new Dictionary<string, object>())
                .ToList();
            var rack2BasicPlan = new BasicQueryPlanInterceptor(rack2)
                .GetOrCreateQueryPlan(new ListTablesRequest(), new Dictionary<string, object>())
                .ToList();
            var rack1WritePlan = rack1Affinity
                .GetOrCreateQueryPlan(request, new Dictionary<string, object>())
                .ToList();
            var rack2WritePlan = rack2Affinity
                .GetOrCreateQueryPlan(request, new Dictionary<string, object>())
                .ToList();
            var rack1BatchPlan = rack1Affinity
                .GetOrCreateQueryPlan(batchRequest, new Dictionary<string, object>())
                .ToList();
            var rack2BatchPlan = rack2Affinity
                .GetOrCreateQueryPlan(batchRequest, new Dictionary<string, object>())
                .ToList();

            Assert.That(rack1BasicPlan.Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.2" }));
            Assert.That(rack2BasicPlan.Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.3" }));
            Assert.That(rack1WritePlan, Has.Count.EqualTo(3));
            Assert.That(rack2WritePlan, Is.EqualTo(rack1WritePlan));
            Assert.That(rack1BatchPlan, Has.Count.EqualTo(3));
            Assert.That(rack2BatchPlan, Is.EqualTo(rack1BatchPlan));
            Assert.That(
                server.Requests.Select(item => item.Query),
                Is.EqualTo(new[]
                {
                    "dc=dc1&rack=rack1",
                    "dc=dc1",
                    "dc=dc1&rack=rack2",
                    "dc=dc1",
                }));
        }

        [Test]
        public void AffinityInterceptorEnablesDatacenterDiscoveryOnSeparatelyCreatedRackManagerTest()
        {
            using var server = new LocalNodesServer(
                2,
                request => request.Query == "dc=dc1"
                    ? "[\"127.0.0.2\",\"127.0.0.3\",\"127.0.0.4\"]"
                    : "[\"127.0.0.2\"]");
            var liveNodesConfig = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", ClusterScope.create()))
                .build();
            var liveNodes = new AlternatorLiveNodes(liveNodesConfig);
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE)
                .withPkInfo("users", "id")
                .build();
            liveNodes.start().Wait(TimeSpan.FromSeconds(5));
            try
            {
                var interceptor = new AffinityQueryPlanInterceptor(affinity, liveNodes);
                liveNodes.nextAsURI();
                server.WaitForRequests();
                Assert.That(
                    SpinWait.SpinUntil(
                        () => LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host)
                            .SequenceEqual(new[] { "127.0.0.2", "127.0.0.3", "127.0.0.4" }),
                        TimeSpan.FromSeconds(5)),
                    Is.True);

                var basicPlan = new BasicQueryPlanInterceptor(liveNodes)
                    .GetOrCreateQueryPlan(new ListTablesRequest(), new Dictionary<string, object>())
                    .ToList();
                var affinityPlan = interceptor.GetOrCreateQueryPlan(
                        new PutItemRequest
                        {
                            TableName = "users",
                            Item = new Dictionary<string, AttributeValue>
                            {
                                ["id"] = new AttributeValue { S = "same-partition" },
                            },
                        },
                        new Dictionary<string, object>())
                    .ToList();

                Assert.That(basicPlan.Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.2" }));
                Assert.That(
                    affinityPlan.Select(node => node.Host),
                    Is.EquivalentTo(new[] { "127.0.0.2", "127.0.0.3", "127.0.0.4" }));
                Assert.That(
                    server.Requests.Select(item => item.Query),
                    Is.EqualTo(new[] { "dc=dc1&rack=rack1", "dc=dc1" }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [TestCase("dc1", 2)]
        [TestCase("dc2", 3)]
        [TestCase("cluster", 4)]
        public void AffinityPreservesExplicitFallbackOrderTest(string availableScope, int expectedRequests)
        {
            using var server = new LocalNodesServer(
                expectedRequests,
                request => request.Query switch
                {
                    "dc=dc1&rack=rack1" => "[\"127.0.0.2\"]",
                    "dc=dc1" => availableScope == "dc1" ? "[\"127.0.0.3\"]" : "[]",
                    "dc=dc2" => availableScope == "dc2" ? "[\"127.0.0.4\"]" : "[]",
                    var query when string.IsNullOrEmpty(query) => "[\"127.0.0.5\"]",
                    _ => throw new InvalidOperationException("Unexpected discovery query: " + request.Query),
                });
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", RackScope.of("dc2", "rack2", ClusterScope.create())))
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .build();
            var liveNodes = new AlternatorLiveNodes(config);

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            var expectedHost = availableScope == "dc1" ? "127.0.0.3" : availableScope == "dc2" ? "127.0.0.4" : "127.0.0.5";
            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host), Is.EqualTo(new[] { expectedHost }));
            Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.2" }));
            Assert.That(
                server.Requests.Select(item => item.Query),
                Is.EqualTo(new[] { "dc=dc1&rack=rack1", "dc=dc1", "dc=dc2", string.Empty }.Take(expectedRequests)));
        }

        [TestCase("[]", false)]
        [TestCase("[]", true)]
        [TestCase(LocalNodesServer.Http500, true)]
        [TestCase("invalid JSON", true)]
        public void AffinityDiscoversDatacenterWhenRackHasNoNodesWithoutAddingFallbackTest(string rackResponse, bool datacenterAvailable)
        {
            using var server = new LocalNodesServer(
                2,
                request => request.Query switch
                {
                    "dc=dc1&rack=rack1" => rackResponse,
                    "dc=dc1" => datacenterAvailable ? "[\"127.0.0.3\"]" : "[]",
                    _ => throw new InvalidOperationException("Unexpected discovery query: " + request.Query),
                });
            var liveNodes = new AlternatorLiveNodes(AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", null))
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .build());

            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.1" }));
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(
                LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host),
                Is.EqualTo(new[] { datacenterAvailable ? "127.0.0.3" : "127.0.0.1" }));
            Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.1" }));
            Assert.That(server.Requests.Select(item => item.Query), Is.EqualTo(new[] { "dc=dc1&rack=rack1", "dc=dc1" }));
        }

        [Test]
        public void AffinityReusesOrdinaryDatacenterDiscoveryTest()
        {
            using var server = new LocalNodesServer(
                2,
                request => request.Query == "dc=dc1" ? "[\"127.0.0.3\"]" : "[]");
            var liveNodes = new AlternatorLiveNodes(AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", DatacenterScope.of("dc1", ClusterScope.create())))
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .build());

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes), Is.EqualTo(liveNodes.GetLiveNodes()));
            Assert.That(server.Requests.Select(item => item.Query), Is.EqualTo(new[] { "dc=dc1&rack=rack1", "dc=dc1" }));
        }

        [Test]
        public void AffinityTriesPreferredDatacenterBeforeReusingOrdinaryFallbackTest()
        {
            using var server = new LocalNodesServer(
                3,
                request => request.Query switch
                {
                    "dc=dc1&rack=rack1" => "[]",
                    "dc=dc2" => "[\"127.0.0.4\"]",
                    "dc=dc1" => "[\"127.0.0.3\"]",
                    _ => throw new InvalidOperationException("Unexpected discovery query: " + request.Query),
                });
            var liveNodes = new AlternatorLiveNodes(AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", DatacenterScope.of("dc2", null)))
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .build());

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.3" }));
            Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.4" }));
            Assert.That(server.Requests.Select(item => item.Query), Is.EqualTo(new[] { "dc=dc1&rack=rack1", "dc=dc2", "dc=dc1" }));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void AffinityDoesNotDuplicateDiscoveryWithoutDistinctViewTest(bool enabled, bool rackFallback)
        {
            using var server = new LocalNodesServer(1, _ => "[\"127.0.0.2\"]");
            var builder = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(enabled
                    ? DatacenterScope.of("dc1", rackFallback ? RackScope.of("dc2", "rack2", null) : ClusterScope.create())
                    : RackScope.of("dc1", "rack1", null));
            if (enabled)
            {
                builder.withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE);
            }

            var liveNodes = new AlternatorLiveNodes(builder.build());
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes), Is.EqualTo(liveNodes.GetLiveNodes()));
            Assert.That(server.Requests.Select(item => item.Query), Is.EqualTo(new[] { enabled ? "dc=dc1" : "dc=dc1&rack=rack1" }));
        }

        [TestCase("[]")]
        [TestCase(LocalNodesServer.Http500)]
        [TestCase("invalid JSON")]
        public void AffinityRetainsMembershipDuringFailedRefreshAndRecoversTest(string failedResponse)
        {
            var dcRequests = 0;
            using var server = new LocalNodesServer(
                6,
                request => request.Query == "dc=dc1"
                    ? ++dcRequests switch
                    {
                        1 => "[\"127.0.0.3\"]",
                        2 => failedResponse,
                        _ => "[\"127.0.0.4\"]",
                    }
                    : "[\"127.0.0.2\"]");
            var liveNodes = new AlternatorLiveNodes(AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", null))
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .build());

            InvokeUpdateLiveNodes(liveNodes);
            InvokeUpdateLiveNodes(liveNodes);
            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.3" }));
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.4" }));
            Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.2" }));
        }

        [Test]
        public void ConfiguredRackAffinityDiscoversDatacenterOnRequestActivityTest()
        {
            var datacenterNodes = new[]
            {
                "datacenter-node1.example.com",
                "datacenter-node2.example.com",
                "datacenter-node3.example.com",
            };
            var rack1Handler = new ScopeAwareDiscoveryHttpMessageHandler("rack1-node.example.com", datacenterNodes);
            var rack2Handler = new ScopeAwareDiscoveryHttpMessageHandler("rack2-node.example.com", datacenterNodes);
            using var rack1HttpClient = new HttpClient(rack1Handler);
            using var rack2HttpClient = new HttpClient(rack2Handler);
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE)
                .withPkInfo("users", "id")
                .build();
            var rack1 = new AlternatorLiveNodes(
                CreateRackScopedConfig(8043, "rack1", affinity, "rack1-seed.example.com"),
                rack1HttpClient);
            var rack2 = new AlternatorLiveNodes(
                CreateRackScopedConfig(8043, "rack2", affinity, "rack2-seed.example.com"),
                rack2HttpClient);

            rack1.start().Wait(TimeSpan.FromSeconds(5));
            rack2.start().Wait(TimeSpan.FromSeconds(5));
            try
            {
                rack1.nextAsURI();
                rack2.nextAsURI();
                Assert.That(
                    SpinWait.SpinUntil(
                        () => rack1Handler.HasRackAndDatacenterRequests && rack2Handler.HasRackAndDatacenterRequests
                            && LazyQueryPlan.sortedAffinityNodes(rack1).Select(node => node.Host).SequenceEqual(datacenterNodes)
                            && LazyQueryPlan.sortedAffinityNodes(rack2).Select(node => node.Host).SequenceEqual(datacenterNodes),
                        TimeSpan.FromSeconds(5)),
                    Is.True);
                var rack1Interceptor = new AffinityQueryPlanInterceptor(affinity, rack1);
                var rack2Interceptor = new AffinityQueryPlanInterceptor(affinity, rack2);

                var request = new PutItemRequest
                {
                    TableName = "users",
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["id"] = new AttributeValue { S = "same-partition" },
                    },
                };
                var rack1Plan = rack1Interceptor
                    .GetOrCreateQueryPlan(request, new Dictionary<string, object>())
                    .ToList();
                var rack2Plan = rack2Interceptor
                    .GetOrCreateQueryPlan(request, new Dictionary<string, object>())
                    .ToList();

                Assert.That(rack1.getLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "rack1-node.example.com" }));
                Assert.That(rack2.getLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "rack2-node.example.com" }));
                Assert.That(rack1Plan, Is.EqualTo(rack2Plan));
                Assert.That(rack1Plan.Select(node => node.Host), Is.EquivalentTo(datacenterNodes));
            }
            finally
            {
                rack1.shutdownAndWait();
                rack2.shutdownAndWait();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AffinityDoesNotRepeatFailedScopesWithinRefreshAndRetriesNextRefreshTest(bool ordinaryDiscoveryFails)
        {
            var failingHosts = new HashSet<string>();
            if (ordinaryDiscoveryFails)
            {
                failingHosts.Add("seed.example.com");
            }

            var failingQueries = new HashSet<string> { "?dc=dc1", string.Empty };
            var handler = new DiscoveryHttpMessageHandler(
                new Dictionary<string, string> { ["seed.example.com"] = "[\"recovered.example.com\"]" },
                failingHosts,
                failingQueries);
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(RackScope.of("dc1", "rack1", DatacenterScope.of("dc1", ClusterScope.create())))
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                handler.RequestedUris.Select(uri => uri.Query),
                Is.EqualTo(new[] { "?dc=dc1&rack=rack1", "?dc=dc1", string.Empty }));
            Assert.That(
                liveNodes.GetLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { ordinaryDiscoveryFails ? "seed.example.com" : "recovered.example.com" }));

            failingHosts.Clear();
            failingQueries.Clear();
            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                handler.RequestedUris.Skip(3).Select(uri => uri.Query),
                Is.EqualTo(new[] { "?dc=dc1&rack=rack1", "?dc=dc1" }));
            Assert.That(
                LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host),
                Is.EqualTo(new[] { "recovered.example.com" }));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void AffinityResolvesCustomFallbackOnlyWhenEnabledTest(bool configuredAffinity, bool attachInterceptor)
        {
            var handler = new DiscoveryHttpMessageHandler(
                new Dictionary<string, string> { ["seed.example.com"] = "[\"node.example.com\"]" });
            using var client = new HttpClient(handler);
            var scope = new TrackingRoutingScope(RackScope.of("dc1", "rack1", null));
            var builder = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8043)
                .withRoutingScope(scope);
            var affinity = KeyRouteAffinityConfig.builder().withType(KeyRouteAffinity.ANY_WRITE).build();
            if (configuredAffinity)
            {
                builder.withKeyRouteAffinity(affinity);
            }

            var liveNodes = new AlternatorLiveNodes(builder.build(), client);
            if (attachInterceptor)
            {
                _ = new AffinityQueryPlanInterceptor(affinity, liveNodes);
            }

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(scope.FallbackReads, Is.EqualTo(configuredAffinity || attachInterceptor ? 1 : 0));
            Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "node.example.com" }));
            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes), Is.EqualTo(liveNodes.GetLiveNodes()));
            Assert.That(handler.RequestedUris.Select(uri => uri.Query), Is.EqualTo(new[] { "?dc=custom" }));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AffinityRetryPlanKeepsCapturedDatacenterMembershipAcrossRefreshTest(bool batch)
        {
            var refreshed = false;
            using var server = new LocalNodesServer(
                4,
                request => request.Query.Contains("&rack=") ? "[\"127.0.0.2\"]"
                    : refreshed ? "[\"127.0.0.6\",\"127.0.0.7\"]" : "[\"127.0.0.3\",\"127.0.0.4\",\"127.0.0.5\"]");
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE).withPkInfo("users", "id").build();
            var liveNodes = CreateRackScopedLiveNodes(server.Port, "rack1", affinity);
            InvokeUpdateLiveNodes(liveNodes);
            var item = new Dictionary<string, AttributeValue> { ["id"] = new AttributeValue { S = "same-partition" } };
            AmazonWebServiceRequest request = batch
                ? new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>>
                    {
                        ["users"] = new List<WriteRequest>
                        {
                            new WriteRequest { PutRequest = new PutRequest { Item = item } },
                        },
                    },
                }
                : new PutItemRequest { TableName = "users", Item = item };
            var interceptor = new AffinityQueryPlanInterceptor(affinity, liveNodes);
            var context = new Dictionary<string, object>();
            var plan = interceptor.GetOrCreateQueryPlan(request, context);
            var observed = new List<Uri> { plan.Next() };

            refreshed = true;
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();
            observed.AddRange(plan);

            Assert.Multiple(() =>
            {
                Assert.That(observed.Select(node => node.Host), Is.EquivalentTo(new[] { "127.0.0.3", "127.0.0.4", "127.0.0.5" }));
                Assert.That(plan.HasNext, Is.False);
                Assert.That(() => plan.Next(), Throws.InvalidOperationException);
                Assert.That(plan.ToList(), Is.Empty);
                Assert.That(interceptor.GetOrCreateQueryPlan(request, context), Is.SameAs(plan));
                Assert.That(
                    interceptor.GetOrCreateQueryPlan(request, new Dictionary<string, object>()).Select(node => node.Host),
                    Is.EquivalentTo(new[] { "127.0.0.6", "127.0.0.7" }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AffinityResolvesFullChainOnceOnInitializationOrLateActivationTest(bool lateActivation)
        {
            var custom = new TrackingRoutingScope(ClusterScope.create(), () => "dc=dc2");
            var scope = RackScope.of(
                "dc1",
                "rack1",
                RackScope.of("dc1", "rack1", DatacenterScope.of("dc1", custom)));
            var handler = new ScopeAwareDiscoveryHttpMessageHandler("rack.example.com", Array.Empty<string>());
            using var client = new HttpClient(handler);
            var builder = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8043)
                .withRoutingScope(scope);
            var affinity = KeyRouteAffinityConfig.builder().withType(KeyRouteAffinity.ANY_WRITE).build();
            if (!lateActivation)
            {
                builder.withKeyRouteAffinity(affinity);
            }

            var liveNodes = new AlternatorLiveNodes(builder.build(), client);
            Assert.That(custom.FallbackReads, Is.EqualTo(lateActivation ? 0 : 1));
            _ = new AffinityQueryPlanInterceptor(affinity, liveNodes);
            _ = new AffinityQueryPlanInterceptor(affinity, liveNodes);
            Assert.That(custom.FallbackReads, Is.EqualTo(1));
            Assert.That(handler.RequestedUris, Is.Empty);

            InvokeUpdateLiveNodes(liveNodes);
            InvokeUpdateLiveNodes(liveNodes);

            Assert.Multiple(() =>
            {
                Assert.That(custom.FallbackReads, Is.EqualTo(1));
                Assert.That(liveNodes.GetRoutingScope(), Is.SameAs(scope));
                Assert.That(handler.RequestedUris.Select(uri => uri.Query), Is.EqualTo(new[]
                {
                    "?dc=dc1&rack=rack1", "?dc=dc1", "?dc=dc2",
                    "?dc=dc1&rack=rack1", "?dc=dc1", "?dc=dc2",
                }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AffinityRetainsCustomQueriesThatDivergeAfterEquivalentDiscoveryTest(bool matchesBuiltIn)
        {
            var phase = 0;
            var firstQueryReads = 0;
            var secondQueryReads = 0;
            var sharedQuery = matchesBuiltIn ? "dc=dc1" : "dc=custom";
            var second = new TrackingRoutingScope(ClusterScope.create(), () =>
            {
                secondQueryReads++;
                return phase == 0 ? sharedQuery : "dc=other";
            });
            var first = new TrackingRoutingScope(second, () =>
            {
                firstQueryReads++;
                return sharedQuery;
            });
            var scope = RackScope.of("dc1", "rack1", first);
            using var server = new LocalNodesServer(
                matchesBuiltIn ? 6 : 8,
                request => request.Query.Contains("&rack=") && (!matchesBuiltIn || phase == 0) ? "[\"127.0.0.2\"]"
                    : request.Query == "dc=other" ? "[\"127.0.0.3\"]" : "[]");
            var liveNodes = new AlternatorLiveNodes(AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(scope)
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .build());

            Assert.That(firstQueryReads + secondQueryReads, Is.Zero);
            var initialFirstFallbackReads = first.FallbackReads;
            var initialSecondFallbackReads = second.FallbackReads;
            InvokeUpdateLiveNodes(liveNodes);
            phase = 1;
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.Multiple(() =>
            {
                Assert.That(initialFirstFallbackReads, Is.EqualTo(1));
                Assert.That(initialSecondFallbackReads, Is.EqualTo(1));
                Assert.That(firstQueryReads, Is.EqualTo(2));
                Assert.That(secondQueryReads, Is.EqualTo(2));
                Assert.That(first.FallbackReads, Is.EqualTo(matchesBuiltIn ? 4 : 1));
                Assert.That(second.FallbackReads, Is.EqualTo(1));
                Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { matchesBuiltIn ? "127.0.0.3" : "127.0.0.2" }));
                Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.3" }));
                Assert.That(server.Requests.Select(request => request.Query), Is.EqualTo(matchesBuiltIn
                    ? new[] { "dc=dc1&rack=rack1", "dc=dc1", string.Empty, "dc=dc1&rack=rack1", "dc=dc1", "dc=other" }
                    : new[] { "dc=dc1&rack=rack1", "dc=dc1", "dc=custom", string.Empty, "dc=dc1&rack=rack1", "dc=dc1", "dc=custom", "dc=other" }));
            });
        }

        [TestCase(true, "rack.example.com")]
        [TestCase(false, "datacenter.example.com")]
        public void EquivalentCustomAndDatacenterScopesReuseDiscoveryTest(bool customFirst, string expectedNode)
        {
            var rack = RackScope.of("dc1", "rack1", null);
            var custom = new TrackingRoutingScope(customFirst ? rack : null, () => "dc=dc1");
            RoutingScope scope = customFirst ? custom : RackScope.of("dc1", "rack1", custom);
            var handler = new ScopeAwareDiscoveryHttpMessageHandler(
                customFirst ? new[] { "rack.example.com" } : Array.Empty<string>(),
                customFirst ? Array.Empty<string>() : new[] { "datacenter.example.com" });
            using var client = new HttpClient(handler);
            var liveNodes = new AlternatorLiveNodes(
                AlternatorConfig.builder()
                    .withSeedHost("seed.example.com")
                    .withScheme("http")
                    .withPort(8043)
                    .withRoutingScope(scope)
                    .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                    .build(),
                client);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.Multiple(() =>
            {
                Assert.That(
                    handler.RequestedUris.Select(uri => uri.Query),
                    Is.EqualTo(customFirst
                        ? new[] { "?dc=dc1", "?dc=dc1&rack=rack1" }
                        : new[] { "?dc=dc1&rack=rack1", "?dc=dc1" }));
                Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { expectedNode }));
                Assert.That(
                    LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host),
                    Is.EqualTo(new[] { customFirst ? "seed.example.com" : expectedNode }));
            });
        }

        [Test]
        public void CustomBareQueryDoesNotReplaceClusterSeedAggregationTest()
        {
            var bareRequests = 0;
            using var server = new LocalNodesServer(
                7,
                request =>
                {
                    if (request.Query == string.Empty)
                    {
                        bareRequests++;
                        return bareRequests <= 2 ? "[]"
                            : request.Host.StartsWith("127.0.0.1:", StringComparison.Ordinal)
                                ? "[\"127.0.0.4\"]" : "[\"127.0.0.5\"]";
                    }

                    return request.Query.Contains("&rack=") ? "[\"127.0.0.3\"]" : "[]";
                });
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
            };
            using var client = new HttpClient(handler);
            var scope = new TrackingRoutingScope(RackScope.of("dc1", "rack1", ClusterScope.create()), () => string.Empty);
            var liveNodes = new AlternatorLiveNodes(
                AlternatorConfig.builder()
                    .withSeedHosts(new[] { "127.0.0.1", "127.0.0.2" })
                    .withScheme("http")
                    .withPort(server.Port)
                    .withRoutingScope(scope)
                    .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                    .build(),
                client);

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.Multiple(() =>
            {
                Assert.That(bareRequests, Is.EqualTo(4));
                Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.3" }));
                Assert.That(
                    LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host),
                    Is.EqualTo(new[] { "127.0.0.4", "127.0.0.5" }));
            });
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CustomDiscoveryQueryIsEvaluatedOncePerRefreshTest(bool affinityEnabled)
        {
            var queryReads = 0;
            var scope = new TrackingRoutingScope(
                RackScope.of("dc1", "rack1", null),
                () => ++queryReads % 2 == 1 ? "dc=A" : "dc=B");
            using var server = new LocalNodesServer(
                2,
                request => request.Query == "dc=A" ? "[\"127.0.0.2\"]" : "[\"127.0.0.3\"]");
            using var pollingHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var liveNodes = new AlternatorLiveNodes(
                AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(scope)
                .withKeyRouteAffinity(affinityEnabled ? KeyRouteAffinity.ANY_WRITE : KeyRouteAffinity.NONE)
                .build(),
                pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            var firstNodes = liveNodes.GetLiveNodes();
            var firstAffinityNodes = LazyQueryPlan.SortedAffinityNodes(liveNodes);
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.Multiple(() =>
            {
                Assert.That(queryReads, Is.EqualTo(2));
                Assert.That(server.Requests.Select(request => request.Query), Is.EqualTo(new[] { "dc=A", "dc=B" }));
                Assert.That(firstNodes.Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.2" }));
                Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.3" }));
                Assert.That(firstAffinityNodes, Is.EqualTo(firstNodes));
                Assert.That(LazyQueryPlan.SortedAffinityNodes(liveNodes), Is.EqualTo(liveNodes.GetLiveNodes()));
                Assert.That(scope.FallbackReads, Is.EqualTo(affinityEnabled ? 1 : 0));
            });
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void AffinityRefreshPreservesRetainedHealthAndLiveObservationsTest(bool quarantined, bool observeDuringRefresh)
        {
            var phase = 0;
            Uri? firstNode = null;
            AlternatorLiveNodes? liveNodes = null;
            using var server = new LocalNodesServer(
                6,
                request =>
                {
                    if (request.Query.Contains("&rack="))
                    {
                        return phase == 0 ? "[\"127.0.0.2\"]" : "[\"127.0.0.3\"]";
                    }

                    if (phase == 1 && observeDuringRefresh)
                    {
                        liveNodes!.ReportNodeResult(firstNode!, NodeHealthObservation.Success);
                    }

                    return phase == 1 ? "[\"127.0.0.2\"]" : "[\"127.0.0.3\"]";
                });
            firstNode = new Uri($"http://127.0.0.2:{server.Port}");
            liveNodes = new AlternatorLiveNodes(AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", null))
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .withNodeHealth(NodeHealthStoreConfig.builder().withQuarantineSuccessThreshold(3).build())
                .build());
            InvokeUpdateLiveNodes(liveNodes);
            liveNodes.ReportNodeResult(firstNode, NodeHealthObservation.ConnectionFailure);
            if (quarantined)
            {
                liveNodes.ReportNodeResult(firstNode, NodeHealthObservation.Success);
            }

            var before = liveNodes.GetNodeStatus(firstNode) ?? throw new InvalidOperationException("Initial node health is missing.");
            phase = 1;
            InvokeUpdateLiveNodes(liveNodes);
            var after = liveNodes.GetNodeStatus(firstNode) ?? throw new InvalidOperationException("Retained node health is missing.");
            var plan = new LazyQueryPlan(liveNodes, 42L).ToList();
            phase = 2;
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.Multiple(() =>
            {
                Assert.That(after.State, Is.EqualTo(quarantined || observeDuringRefresh ? NodeHealthState.Quarantined : NodeHealthState.Down));
                Assert.That(after.ConsecutiveSuccesses, Is.EqualTo(quarantined || observeDuringRefresh ? 1 : 0));
                Assert.That(after.ConsecutiveServerErrors, Is.EqualTo(observeDuringRefresh ? 0 : before.ConsecutiveServerErrors));
                if (!observeDuringRefresh)
                {
                    Assert.That(after.UpdatedAt, Is.EqualTo(before.UpdatedAt));
                }

                if (!quarantined && !observeDuringRefresh)
                {
                    Assert.That(plan, Does.Not.Contain(firstNode));
                    Assert.That(plan.Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.3" }));
                }

                Assert.That(liveNodes.GetNodeStatus(firstNode), Is.Null);
                Assert.That(server.Requests.All(request => request.Host.StartsWith("127.0.0.1:", StringComparison.Ordinal)), Is.True);
            });
        }

        [TestCase("[]")]
        [TestCase("invalid JSON")]
        [TestCase("throwing fallback")]
        public void AffinityRefreshFinalizesHealthAfterUnsuccessfulDiscoveryTest(string response)
        {
            var phase = 0;
            var throwingFallback = response == "throwing fallback";
            using var server = new LocalNodesServer(
                throwingFallback ? 5 : 4,
                request => request.Query.Contains("&rack=")
                    ? phase == 0 ? "[\"127.0.0.2\"]" : "[\"127.0.0.4\"]"
                    : phase == 0 ? "[\"127.0.0.3\"]" : request.Query == "dc=custom" ? "[]" : response);
            var liveNodes = new AlternatorLiveNodes(AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", throwingFallback ? new TrackingRoutingScope() : null))
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .build());
            var departedNode = new Uri($"http://127.0.0.2:{server.Port}");
            var retainedNode = new Uri($"http://127.0.0.3:{server.Port}");
            InvokeUpdateLiveNodes(liveNodes);
            liveNodes.ReportNodeResult(departedNode, NodeHealthObservation.ConnectionFailure);
            liveNodes.ReportNodeResult(retainedNode, NodeHealthObservation.ConnectionFailure);
            phase = 1;
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.Multiple(() =>
            {
                Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.4" }));
                Assert.That(liveNodes.GetNodeStatus(departedNode), Is.Null);
                Assert.That(liveNodes.GetNodeStatus(retainedNode)?.State, Is.EqualTo(NodeHealthState.Down));
                Assert.That(liveNodes.GetDownNodes(), Is.EqualTo(new[] { retainedNode }));
            });
        }

        [Test]
        public void AuxiliaryCustomFallbackFailureKeepsSuccessfulOrdinaryDiscoveryTest()
        {
            var handler = new ScopeAwareDiscoveryHttpMessageHandler("ordinary.example.com", Array.Empty<string>());
            using var client = new HttpClient(handler);
            var queryReads = 0;
            var fallback = new TrackingRoutingScope(query: () =>
            {
                queryReads++;
                throw new InvalidOperationException("Auxiliary custom query is unavailable.");
            });
            var liveNodes = new AlternatorLiveNodes(
                AlternatorConfig.builder()
                    .withSeedHost("seed.example.com")
                    .withScheme("http")
                    .withPort(8043)
                    .withRoutingScope(RackScope.of("dc1", "rack1", fallback))
                    .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                    .build(),
                client);
            Exception? failure = null;
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
            }
            catch (System.Reflection.TargetInvocationException exception)
            {
                failure = exception.InnerException;
            }

            Assert.Multiple(() =>
            {
                Assert.That(failure, Is.Null);
                Assert.That(queryReads, Is.EqualTo(1));
                Assert.That(fallback.FallbackReads, Is.EqualTo(1));
                Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "ordinary.example.com" }));
                Assert.That(handler.RequestedUris.Select(uri => uri.Query), Is.EqualTo(new[] { "?dc=dc1&rack=rack1", "?dc=dc1" }));
            });
        }

        [Test]
        public void SuccessfulDatacenterAffinityResolvesButDoesNotDiscoverUnusedCustomFallbackTest()
        {
            var handler = new DiscoveryHttpMessageHandler(
                new Dictionary<string, string> { ["seed.example.com"] = "[\"node.example.com\"]" });
            using var client = new HttpClient(handler);
            var unusedScope = new TrackingRoutingScope();
            var liveNodes = new AlternatorLiveNodes(
                AlternatorConfig.builder()
                    .withSeedHost("seed.example.com")
                    .withScheme("http")
                    .withPort(8043)
                    .withRoutingScope(RackScope.of("dc1", "rack1", unusedScope))
                    .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                    .build(),
                client);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(unusedScope.FallbackReads, Is.EqualTo(1));
            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host), Is.EqualTo(new[] { "node.example.com" }));
            Assert.That(handler.RequestedUris.Select(uri => uri.Query), Is.EqualTo(new[] { "?dc=dc1&rack=rack1", "?dc=dc1" }));
        }

        [TestCase("[]")]
        [TestCase(LocalNodesServer.Http500)]
        public void CustomScopeFallbackToRackEnablesDatacenterAffinityTest(string customResponse)
        {
            using var server = new LocalNodesServer(
                3,
                request => request.Query switch
                {
                    "dc=custom" => customResponse,
                    "dc=dc1&rack=rack1" => "[\"127.0.0.2\"]",
                    "dc=dc1" => "[\"127.0.0.2\",\"127.0.0.3\"]",
                    _ => throw new InvalidOperationException("Unexpected discovery query: " + request.Query),
                });
            var scope = new TrackingRoutingScope(RackScope.of("dc1", "rack1", ClusterScope.create()));
            var liveNodes = new AlternatorLiveNodes(AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(scope)
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .build());

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(scope.FallbackReads, Is.GreaterThan(0));
            Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.2" }));
            Assert.That(
                LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host),
                Is.EqualTo(new[] { "127.0.0.2", "127.0.0.3" }));
            Assert.That(
                server.Requests.Select(request => request.Query),
                Is.EqualTo(new[] { "dc=custom", "dc=dc1&rack=rack1", "dc=dc1" }));
        }

        [Test]
        public void EmptyAffinityMembershipFallsBackToReinjectedSeedForSingleAndBatchWritesTest()
        {
            var failDiscovery = false;
            using var server = new LocalNodesServer(
                5,
                request => failDiscovery ? "invalid JSON" : request.Query == "dc=dc1"
                    ? "[\"127.0.0.2\",\"127.0.0.3\"]"
                    : "[\"127.0.0.2\"]");
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE)
                .withPkInfo("users", "id")
                .build();
            var liveNodes = CreateRackScopedLiveNodes(server.Port, "rack1", affinity);
            InvokeUpdateLiveNodes(liveNodes);
            foreach (var node in LazyQueryPlan.sortedAffinityNodes(liveNodes))
            {
                liveNodes.ReportNodeResult(node, NodeHealthObservation.ConnectionFailure);
            }

            failDiscovery = true;
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();
            var interceptor = new AffinityQueryPlanInterceptor(affinity, liveNodes);
            var item = new Dictionary<string, AttributeValue> { ["id"] = new AttributeValue { S = "same-partition" } };
            var single = new PutItemRequest { TableName = "users", Item = item };
            var batch = new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    ["users"] = new List<WriteRequest> { new WriteRequest { PutRequest = new PutRequest { Item = item } } },
                },
            };

            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes).Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.1" }));
            Assert.That(
                interceptor.GetOrCreateQueryPlan(single, new Dictionary<string, object>()).Select(node => node.Host),
                Is.EqualTo(new[] { "127.0.0.1" }));
            Assert.That(
                interceptor.GetOrCreateQueryPlan(batch, new Dictionary<string, object>()).Select(node => node.Host),
                Is.EqualTo(new[] { "127.0.0.1" }));
        }

        [TestCase("mixed")]
        [TestCase("quarantined")]
        [TestCase("empty")]
        public void SingleAffinityOrdinaryFallbackKeepsSeededSnapshotAcrossRefreshTest(string ordinaryHealth)
        {
            var refreshed = false;
            using var server = new LocalNodesServer(
                4,
                request => request.Query == "dc=dc1" ? "[\"127.0.0.5\"]"
                    : refreshed ? "[\"127.0.0.6\"]" : "[\"127.0.0.2\",\"127.0.0.3\",\"127.0.0.4\"]");
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE).withPkInfo("users", "id").build();
            var liveNodes = CreateRackScopedLiveNodes(server.Port, "rack1", affinity);
            InvokeUpdateLiveNodes(liveNodes);
            liveNodes.ReportNodeResult(new Uri($"http://127.0.0.5:{server.Port}"), NodeHealthObservation.ConnectionFailure);
            var ordinaryNodes = liveNodes.GetLiveNodes();
            foreach (var node in ordinaryNodes)
            {
                if (ordinaryHealth != "mixed" || node.Host == "127.0.0.4")
                {
                    liveNodes.ReportNodeResult(node, NodeHealthObservation.ConnectionFailure);
                    if (ordinaryHealth != "empty")
                    {
                        liveNodes.ReportNodeResult(node, NodeHealthObservation.Success);
                    }
                }
            }

            var partitionKey = new AttributeValue { S = "same-partition" };
            var seed = AttributeValueHasher.Hash(partitionKey);
            var expected = ordinaryHealth switch
            {
                "mixed" => new LazyQueryPlan(ordinaryNodes.Take(2), seed).Concat(ordinaryNodes.Skip(2)).ToList(),
                "quarantined" => new LazyQueryPlan(ordinaryNodes, seed).ToList(),
                _ => new List<Uri>(),
            };
            var interceptor = new AffinityQueryPlanInterceptor(affinity, liveNodes);
            var request = new PutItemRequest
            {
                TableName = "users",
                Item = new Dictionary<string, AttributeValue> { ["id"] = partitionKey },
            };
            var context = new Dictionary<string, object>();
            var plan = interceptor.GetOrCreateQueryPlan(request, context);
            var observed = new List<Uri>();
            if (plan.HasNext)
            {
                observed.Add(plan.Next());
            }

            refreshed = true;
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();
            observed.AddRange(plan);

            Assert.That(observed, Is.EqualTo(expected));
            Assert.That(plan.HasNext, Is.False);
            Assert.That(interceptor.GetOrCreateQueryPlan(request, context), Is.SameAs(plan));
            Assert.That(
                interceptor.GetOrCreateQueryPlan(request, new Dictionary<string, object>()).Select(node => node.Host),
                Is.EqualTo(new[] { "127.0.0.6" }));
        }

        [Test]
        public void BatchAffinityOrdinaryFallbackKeepsVotesAndSnapshotAcrossRefreshTest()
        {
            var refreshed = false;
            using var server = new LocalNodesServer(
                4,
                request => request.Query == "dc=dc1" ? "[\"127.0.0.5\"]"
                    : refreshed ? "[\"127.0.0.6\"]" : "[\"127.0.0.2\",\"127.0.0.3\",\"127.0.0.4\"]");
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE).withPkInfo("users", "id").build();
            var liveNodes = CreateRackScopedLiveNodes(server.Port, "rack1", affinity);
            InvokeUpdateLiveNodes(liveNodes);
            liveNodes.ReportNodeResult(new Uri($"http://127.0.0.5:{server.Port}"), NodeHealthObservation.ConnectionFailure);
            var ordinaryNodes = liveNodes.GetLiveNodes();
            var quarantined = ordinaryNodes.Last();
            liveNodes.ReportNodeResult(quarantined, NodeHealthObservation.ConnectionFailure);
            liveNodes.ReportNodeResult(quarantined, NodeHealthObservation.Success);
            var partitionKey = new AttributeValue { S = "same-partition" };
            var activeNodes = ordinaryNodes.Take(2).ToList();
            var preferred = new LazyQueryPlan(activeNodes, AttributeValueHasher.Hash(partitionKey)).Next();
            var expected = new[] { preferred }.Concat(activeNodes.Where(node => node != preferred)).Append(quarantined).ToList();
            var request = new BatchWriteItemRequest
            {
                RequestItems = new Dictionary<string, List<WriteRequest>>
                {
                    ["users"] = new List<WriteRequest>
                    {
                        new WriteRequest
                        {
                            PutRequest = new PutRequest
                            {
                                Item = new Dictionary<string, AttributeValue> { ["id"] = partitionKey },
                            },
                        },
                    },
                },
            };
            var interceptor = new AffinityQueryPlanInterceptor(affinity, liveNodes);
            var plan = interceptor.GetOrCreateQueryPlan(request, new Dictionary<string, object>());
            var observed = new List<Uri> { plan.Next() };

            refreshed = true;
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();
            observed.AddRange(plan);

            Assert.That(observed, Is.EqualTo(expected));
            Assert.That(plan.HasNext, Is.False);
            Assert.That(
                interceptor.GetOrCreateQueryPlan(request, new Dictionary<string, object>()).Select(node => node.Host),
                Is.EqualTo(new[] { "127.0.0.6" }));
        }

        [Test]
        public void PublicSeededAndPreferredPlansUseOrdinaryFallbackWhenAffinityIsDownTest()
        {
            using var server = new LocalNodesServer(
                2,
                request => request.Query == "dc=dc1" ? "[\"127.0.0.5\"]" : "[\"127.0.0.2\"]");
            var affinity = KeyRouteAffinityConfig.builder().withType(KeyRouteAffinity.ANY_WRITE).build();
            var liveNodes = CreateRackScopedLiveNodes(server.Port, "rack1", affinity);
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();
            liveNodes.ReportNodeResult(new Uri($"http://127.0.0.5:{server.Port}"), NodeHealthObservation.ConnectionFailure);
            var ordinaryNodes = liveNodes.GetLiveNodes();

            Assert.That(new LazyQueryPlan(liveNodes, 42L).ToList(), Is.EqualTo(ordinaryNodes));
            Assert.That(new LazyQueryPlan(liveNodes, ordinaryNodes).ToList(), Is.EqualTo(ordinaryNodes));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void HashHelperUsesOrdinaryFallbackWhenAffinityIsDownTest(bool quarantined)
        {
            using var server = new LocalNodesServer(
                2,
                request => request.Query == "dc=dc1" ? "[\"127.0.0.5\"]" : "[\"127.0.0.2\",\"127.0.0.3\"]");
            var affinity = KeyRouteAffinityConfig.builder().withType(KeyRouteAffinity.ANY_WRITE).build();
            var liveNodes = CreateRackScopedLiveNodes(server.Port, "rack1", affinity);
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();
            liveNodes.ReportNodeResult(new Uri($"http://127.0.0.5:{server.Port}"), NodeHealthObservation.ConnectionFailure);
            var ordinaryNodes = liveNodes.GetLiveNodes();
            if (quarantined)
            {
                foreach (var node in ordinaryNodes)
                {
                    liveNodes.ReportNodeResult(node, NodeHealthObservation.ConnectionFailure);
                    liveNodes.ReportNodeResult(node, NodeHealthObservation.Success);
                    Assert.That(liveNodes.GetNodeStatus(node)?.State, Is.EqualTo(NodeHealthState.Quarantined));
                }
            }

            var method = typeof(AlternatorLiveNodes).GetMethod(
                "GetNodeForHash",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            Assert.That(method!.Invoke(liveNodes, new object[] { -1L }), Is.EqualTo(ordinaryNodes.Last()));
            Assert.That(LazyQueryPlan.SortedAffinityNodes(liveNodes), Is.EqualTo(ordinaryNodes));
            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes), Is.EqualTo(ordinaryNodes));
            var expected = new LazyQueryPlan(liveNodes, 42L).First();
            Assert.That(LazyQueryPlan.PreferredNodeForHash(liveNodes, 42L), Is.EqualTo(expected));
            Assert.That(LazyQueryPlan.preferredNodeForHash(liveNodes, 42L), Is.EqualTo(expected));
        }

        [Test]
        public void OrdinarySelectionStaysLocalWhenAffinityActivatesDuringHealthReadTest()
        {
            var localNode = new Uri("http://local.example.com:8043");
            var remoteNode = new Uri("http://remote.example.com:8043");
            var handler = new ScopeAwareDiscoveryHttpMessageHandler(localNode.Host, new[] { localNode.Host, remoteNode.Host });
            using var client = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost(localNode.Host)
                .withScheme("http")
                .withPort(localNode.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", ClusterScope.create()))
                .build();
            var liveNodes = new AffinityActivatingLiveNodes(config, client);
            try
            {
                Assert.That(liveNodes.NextAsUri(), Is.EqualTo(localNode));
                Assert.That(liveNodes.GetLiveNodes(), Is.EqualTo(new[] { localNode }));
                Assert.That(liveNodes.GetActiveNodes(), Is.EqualTo(new[] { localNode, remoteNode }));
            }
            finally
            {
                Assert.That(liveNodes.ShutdownAndWait(), Is.True);
            }
        }

        [Test]
        public void OrdinaryRecoverySkipsAffinityOnlyDownNodesAndExplicitProbeRecoversThemTest()
        {
            var localNode = new Uri("http://local.example.com:8043");
            var remoteNode = new Uri("http://remote.example.com:8043");
            var handler = new ScopeAwareDiscoveryHttpMessageHandler(localNode.Host, new[] { localNode.Host, remoteNode.Host });
            using var client = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost(localNode.Host)
                .withScheme("http")
                .withPort(localNode.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", ClusterScope.create()))
                .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
                .withNodeHealth(NodeHealthStoreConfig.builder().withQuarantineSuccessThreshold(1).build())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, client);
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                liveNodes.ReportNodeResult(localNode, NodeHealthObservation.ConnectionFailure);
                liveNodes.ReportNodeResult(remoteNode, NodeHealthObservation.ConnectionFailure);
                var method = typeof(AlternatorLiveNodes).GetMethod(
                    "SelectNextUri",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                Assert.That(method, Is.Not.Null);

                Assert.That(method!.Invoke(liveNodes, Array.Empty<object>()), Is.EqualTo(localNode));

                Assert.That(handler.RequestedUris.Skip(2).Select(uri => uri.Host), Is.EqualTo(new[] { localNode.Host }));
                Assert.That(liveNodes.GetNodeStatus(localNode)?.State, Is.EqualTo(NodeHealthState.Active));
                Assert.That(liveNodes.GetDownNodes(), Is.EqualTo(new[] { remoteNode }));
                Assert.That(liveNodes.ProbeDownNodesAsync().GetAwaiter().GetResult(), Is.EqualTo(new[] { remoteNode }));
                Assert.That(liveNodes.GetNodeStatus(remoteNode)?.State, Is.EqualTo(NodeHealthState.Active));
                Assert.That(handler.RequestedUris.Last().Host, Is.EqualTo(remoteNode.Host));
                Assert.That(liveNodes.GetActiveNodes(), Is.EqualTo(new[] { localNode, remoteNode }));
                Assert.That(liveNodes.GetQuarantinedNodes(), Is.Empty);
                Assert.That(liveNodes.GetDownNodes(), Is.Empty);
                Assert.That(liveNodes.NextAsUri(), Is.EqualTo(localNode));
                Assert.That(new LazyQueryPlan(liveNodes).ToList(), Is.EqualTo(new[] { localNode }));
                Assert.That(
                    LazyQueryPlan.sortedAffinityNodes(liveNodes),
                    Is.EqualTo(new[] { localNode, remoteNode }));
            }
            finally
            {
                Assert.That(liveNodes.ShutdownAndWait(), Is.True);
            }
        }

        [Test]
        public void AffinityOnlyDownNodeIsProbedIntoQuarantineTest()
        {
            using var server = new LocalNodesServer(
                3,
                request => request.Query == "dc=dc1"
                    ? "[\"127.0.0.1\",\"localhost\"]"
                    : "[\"127.0.0.1\"]");
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE)
                .withPkInfo("users", "id")
                .build();
            var liveNodes = CreateRackScopedLiveNodes(server.Port, "rack1", affinity);
            var remoteNode = new Uri($"http://localhost:{server.Port}");

            InvokeUpdateLiveNodes(liveNodes);
            liveNodes.reportNodeResult(remoteNode, NodeHealthObservation.ConnectionFailure);

            Assert.That(liveNodes.getActiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.1" }));
            Assert.That(liveNodes.getDownNodes(), Is.EqualTo(new[] { remoteNode }));
            Assert.That(liveNodes.getNodeStatus(remoteNode)?.State, Is.EqualTo(NodeHealthState.Down));

            InvokeProbeDownNodesIfDue(liveNodes);
            server.WaitForRequests();

            Assert.That(liveNodes.getNodeStatus(remoteNode)?.State, Is.EqualTo(NodeHealthState.Quarantined));
            Assert.That(liveNodes.getQuarantinedNodes(), Is.EqualTo(new[] { remoteNode }));
            Assert.That(liveNodes.getDownNodes(), Is.Empty);
            Assert.That(
                new LazyQueryPlan(liveNodes).Select(node => node.Host),
                Is.EqualTo(new[] { "127.0.0.1" }));
            Assert.That(server.Requests[2].Host, Is.EqualTo($"localhost:{server.Port}"));
            Assert.That(server.Requests[2].Query, Is.EqualTo("dc=dc1&rack=rack1"));
        }

        [Test]
        public void TopologyAccessorsDoNotMixMembershipAndHealthRefreshGenerationsTest()
        {
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE)
                .withPkInfo("users", "id")
                .build();
            var normalLiveNodes = CreateRackScopedLiveNodes(8043, "rack1", affinity, "old-normal.example.com");
            var affinityLiveNodes = CreateRackScopedLiveNodes(8043, "rack1", affinity, "old-affinity.example.com");
            var newNormalNode = new Uri("http://new-normal.example.com:8043");
            var newAffinityNode = new Uri("http://new-affinity.example.com:8043");

            var normalResult = ReadNodesAcrossTopologyPublication(
                normalLiveNodes,
                normalLiveNodes.GetActiveNodes,
                newNormalNode);
            var affinityResult = ReadNodesAcrossTopologyPublication(
                affinityLiveNodes,
                () => LazyQueryPlan.sortedAffinityNodes(affinityLiveNodes),
                newAffinityNode);

            Assert.That(normalResult, Is.EqualTo(new[] { newNormalNode }));
            Assert.That(affinityResult, Is.EqualTo(new[] { newAffinityNode }));
        }

        [Test]
        public void CheckIfRackAndDatacenterSetCorrectlyRetriesNextSeedTest()
        {
            var handler = new DiscoveryHttpMessageHandler(new Dictionary<string, string>
            {
                ["dc2-node1.example.com"] = "[]",
                ["dc1-node1.example.com"] = "[\"dc1-rack1-node.example.com\"]",
            });
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "dc2-node1.example.com", "dc1-node1.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(RackScope.of("dc1", "rack1", DatacenterScope.of("dc1", ClusterScope.create())))
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            liveNodes.checkIfRackAndDatacenterSetCorrectly();

            Assert.That(
                handler.RequestedUris.Select(uri => uri.Host),
                Is.EqualTo(new[] { "dc2-node1.example.com", "dc1-node1.example.com" }));
            Assert.That(handler.RequestedUris.Select(uri => uri.Query), Is.All.EqualTo("?dc=dc1&rack=rack1"));
        }

        [Test]
        public void PollingRequestIncludesJavaStyleKeepAliveAndHostHeadersTest()
        {
            using var server = new LocalNodesServer(1, _ => "[\"127.0.0.2\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config);

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(server.Requests[0].Host, Is.EqualTo($"127.0.0.1:{server.Port}"));
            Assert.That(server.Requests[0].Connection, Does.Contain("keep-alive").IgnoreCase);
        }

        [Test]
        public void PollingRequestFormatsIpv6HostHeadersAndDiscoveredNodesTest()
        {
            var handler = new TrackingHttpMessageHandler("[\"::2\"]");
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("::1")
                .withScheme("http")
                .withPort(8080)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(handler.LastRequestUri, Is.EqualTo(new Uri("http://[::1]:8080/localnodes")));
            Assert.That(handler.LastHostHeader, Is.EqualTo("[::1]:8080"));
            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(new[]
            {
                new Uri("http://[::2]:8080"),
            }));
        }

        [Test]
        public void PollingRequestUsesIpv6LiteralEndpointTest()
        {
            using var server = new LocalNodesServer(
                1,
                _ => "[\"::1\"]",
                IPAddress.IPv6Loopback);
            var config = AlternatorConfig.builder()
                .withSeedHost("::1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config);

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(server.Requests[0].Host, Is.EqualTo($"[::1]:{server.Port}"));
            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(new[]
            {
                new Uri($"http://[::1]:{server.Port}"),
            }));
        }

        [Test]
        public void DualStackDnsFallsBackFromBrokenIpv6ToIpv4Test()
        {
            using var server = new LocalNodesServer(1, _ => "[\"127.0.0.1\"]", IPAddress.Loopback);
            var attempts = new List<IPAddress>();
            using var pollingHttpClient = CreateAddressMappedHttpClient(
                new[] { IPAddress.IPv6Loopback, IPAddress.Loopback },
                attempts);
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(attempts, Is.EqualTo(new[] { IPAddress.IPv6Loopback, IPAddress.Loopback }));
            Assert.That(server.Requests[0].Host, Is.EqualTo($"entrypoint.test:{server.Port}"));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.1" }));
        }

        [Test]
        public void DualStackDnsFallsBackFromBrokenIpv4ToIpv6Test()
        {
            using var server = new LocalNodesServer(1, _ => "[\"::1\"]", IPAddress.IPv6Loopback);
            var attempts = new List<IPAddress>();
            using var pollingHttpClient = CreateAddressMappedHttpClient(
                new[] { IPAddress.Loopback, IPAddress.IPv6Loopback },
                attempts);
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(attempts, Is.EqualTo(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }));
            Assert.That(server.Requests[0].Host, Is.EqualTo($"entrypoint.test:{server.Port}"));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "[::1]" }));
        }

        [Test]
        public void RecoveryRefreshReturnsToOriginalDnsEntrypointAndTriesLaterAddressTest()
        {
            var responses = new Queue<string>(new[]
            {
                "[\"learned-old.test\"]",
                "[\"learned-new.test\"]",
            });
            using var server = new LocalNodesServer(2, _ => responses.Dequeue(), IPAddress.Loopback);
            var addressAnswers = new Queue<IReadOnlyList<IPAddress>>(new[]
            {
                new[] { IPAddress.Loopback },
                new[] { IPAddress.IPv6Loopback, IPAddress.Loopback },
            });
            var attempts = new List<IPAddress>();

            IEnumerable<IPAddress> NextAddressAnswer()
            {
                foreach (var address in addressAnswers.Dequeue())
                {
                    yield return address;
                }
            }

            using var pollingHttpClient = CreateAddressMappedHttpClient(NextAddressAnswer(), attempts);
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            var learnedNode = new Uri($"http://learned-old.test:{server.Port}");
            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(new[] { learnedNode }));

            liveNodes.reportNodeResult(learnedNode, NodeHealthObservation.ConnectionFailure);
            Assert.That(liveNodes.getActiveNodes(), Is.Empty);
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(
                attempts,
                Is.EqualTo(new[]
                {
                    IPAddress.Loopback,
                    IPAddress.IPv6Loopback,
                    IPAddress.Loopback,
                }));
            Assert.That(addressAnswers, Is.Empty);
            Assert.That(
                server.Requests.Select(request => request.Host),
                Is.EqualTo(new[]
                {
                    $"entrypoint.test:{server.Port}",
                    $"entrypoint.test:{server.Port}",
                }));
            Assert.That(
                liveNodes.getLiveNodes(),
                Is.EqualTo(new[] { new Uri($"http://learned-new.test:{server.Port}") }));
        }

        [Test]
        public void FailedRefreshReinjectsIpv6SeedAlongsideKnownNodesTest()
        {
            using var server = new LocalNodesServer(1, _ => "[\"::2\"]", IPAddress.IPv6Loopback);
            var config = AlternatorConfig.builder()
                .withSeedHost("::1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config);

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "[::2]" }));

            server.Dispose();
            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "[::2]", "[::1]" }));
        }

        [Test]
        public void ConstructorValidatesConfigurationLikeJavaTest()
        {
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("bad scheme")
                .withPort(8080)
                .build();

            Assert.Throws<SystemException>(() => new AlternatorLiveNodes(config));
        }

        [Test]
        public void GetLiveNodesReturnsReadOnlySnapshotLikeJavaTest()
        {
            var liveNodes = new AlternatorLiveNodes(
                new List<string>
                {
                    "127.0.0.1",
                },
                "http",
                8080,
                ClusterScope.create());
            var snapshot = liveNodes.getLiveNodes();

            Assert.That(snapshot, Is.EqualTo(new[] { new Uri("http://127.0.0.1:8080") }));
            Assert.Throws<NotSupportedException>(() =>
                ((IList<Uri>)snapshot).Add(new Uri("http://127.0.0.2:8080")));
            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(new[] { new Uri("http://127.0.0.1:8080") }));
        }

        [Test]
        public void LazyQueryPlanCreatedBeforeRefreshUsesCurrentLiveNodesLikeJavaTest()
        {
            using var server = new LocalNodesServer(1, _ => "[\"127.0.0.2\",\"127.0.0.3\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config);
            var queryPlan = new LazyQueryPlan(liveNodes);

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            var hosts = new List<string>();
            while (queryPlan.hasNext())
            {
                hosts.Add(queryPlan.next().Host);
            }

            Assert.That(hosts, Is.EquivalentTo(new[] { "127.0.0.2", "127.0.0.3" }));
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void PublicAffinityPlanCapturesNodesOnFirstUseAndKeepsThemAcrossRefreshTest(bool preferred, bool affinityEnabled)
        {
            var refreshed = false;
            using var server = new LocalNodesServer(
                affinityEnabled ? 4 : 2,
                _ => refreshed ? "[\"127.0.0.4\"]" : "[\"127.0.0.2\",\"127.0.0.3\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(affinityEnabled ? RackScope.of("dc1", "rack1", null) : ClusterScope.create())
                .withKeyRouteAffinity(affinityEnabled ? KeyRouteAffinity.ANY_WRITE : KeyRouteAffinity.NONE)
                .build();
            var liveNodes = new AlternatorLiveNodes(config);
            var preferredNode = new Uri($"http://127.0.0.3:{server.Port}");
            var plan = preferred ? new LazyQueryPlan(liveNodes, new[] { preferredNode }) : new LazyQueryPlan(liveNodes, 42L);

            InvokeUpdateLiveNodes(liveNodes);
            var expectedPlan = preferred
                ? new LazyQueryPlan(liveNodes, new[] { preferredNode }).ToList()
                : new LazyQueryPlan(liveNodes, 42L).ToList();
            var first = plan.Next();
            refreshed = true;
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();
            var captured = new[] { first }.Concat(plan).ToList();

            Assert.That(captured, Is.EqualTo(expectedPlan));
            Assert.That(captured.Select(node => node.Host), Is.EquivalentTo(new[] { "127.0.0.2", "127.0.0.3" }));
            if (preferred)
            {
                Assert.That(first, Is.EqualTo(preferredNode));
            }

            Assert.That(plan.HasNext, Is.False);
            Assert.That(new LazyQueryPlan(liveNodes, 42L).Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.4" }));
        }

        [Test]
        public void PublicPreferredPlanKeepsCanonicalDatacenterQuarantineFallbackTest()
        {
            using var server = new LocalNodesServer(
                2,
                request => request.Query == "dc=dc1" ? "[\"127.0.0.3\",\"127.0.0.2\"]" : "[\"127.0.0.4\"]");
            var affinity = KeyRouteAffinityConfig.builder().withType(KeyRouteAffinity.ANY_WRITE).build();
            var liveNodes = CreateRackScopedLiveNodes(server.Port, "rack1", affinity);
            var preferredNode = new Uri($"http://127.0.0.3:{server.Port}");
            var plan = new LazyQueryPlan(liveNodes, new[] { preferredNode });
            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();
            foreach (var node in LazyQueryPlan.SortedAffinityNodes(liveNodes))
            {
                liveNodes.ReportNodeResult(node, NodeHealthObservation.ConnectionFailure);
                liveNodes.ReportNodeResult(node, NodeHealthObservation.Success);
                Assert.That(liveNodes.GetNodeStatus(node)?.State, Is.EqualTo(NodeHealthState.Quarantined));
            }

            Assert.That(liveNodes.GetLiveNodes().Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.4" }));
            Assert.That(plan.Select(node => node.Host), Is.EqualTo(new[] { "127.0.0.2", "127.0.0.3" }));
            Assert.That(
                new LazyQueryPlan(liveNodes, new[] { preferredNode }).Select(node => node.Host),
                Is.EqualTo(new[] { "127.0.0.2", "127.0.0.3" }));
        }

        [Test]
        public void UpdateLiveNodesContinuesFallbackAfterNonSuccessResponseTest()
        {
            using var server = new LocalNodesServer(
                2,
                request => request.Query.Contains("rack=", StringComparison.Ordinal) ? LocalNodesServer.Http500 : "[\"127.0.0.5\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(RackScope.of("dc1", "rack1", ClusterScope.create()))
                .build();
            var liveNodes = new AlternatorLiveNodes(config);

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(
                server.Requests.Select(request => request.Target),
                Is.EqualTo(new[] { "/localnodes?dc=dc1&rack=rack1", "/localnodes" }));
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "127.0.0.5" }));
        }

        [Test]
        public void ExternalPollingHttpClientIsReusedAndNotDisposedByShutdownTest()
        {
            var handler = new TrackingHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8080)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            InvokeUpdateLiveNodes(liveNodes);
            liveNodes.shutdown();

            Assert.That(handler.SendCount, Is.EqualTo(2));
            Assert.That(handler.DisposeCount, Is.EqualTo(0));
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "127.0.0.2" }));
        }

        [Test]
        public void ShutdownAndWaitStopsRefreshTaskAndPreservesExternalPollingClientTest()
        {
            var handler = new TrackingHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8080)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            liveNodes.start().Wait(TimeSpan.FromSeconds(5));
            liveNodes.nextAsURI();
            Assert.That(
                SpinWait.SpinUntil(() => handler.SendCount > 0, TimeSpan.FromSeconds(5)),
                Is.True);

            Assert.That(liveNodes.shutdownAndWait(), Is.True);

            Assert.That(liveNodes.isRunning(), Is.False);
            Assert.That(handler.DisposeCount, Is.EqualTo(0));
            Assert.That(handler.SendCount, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void JavaStyleRunBlocksUntilShutdownAndPreservesExternalPollingClientTest()
        {
            var handler = new TrackingHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8080)
                .withRoutingScope(ClusterScope.create())
                .withActiveRefreshIntervalMs(10)
                .withIdleRefreshIntervalMs(10)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            var runTask = Task.Run(() => liveNodes.run());
            Assert.That(
                SpinWait.SpinUntil(() => liveNodes.isRunning(), TimeSpan.FromSeconds(5)),
                Is.True);
            liveNodes.nextAsURI();
            Assert.That(
                SpinWait.SpinUntil(() => handler.SendCount > 0, TimeSpan.FromSeconds(5)),
                Is.True);
            Assert.That(liveNodes.isRunning(), Is.True);

            liveNodes.shutdown();

            Assert.That(runTask.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(liveNodes.isRunning(), Is.False);
            Assert.That(handler.DisposeCount, Is.EqualTo(0));
            Assert.That(handler.SendCount, Is.GreaterThanOrEqualTo(1));
        }

        [TestCase(false, "cluster")]
        [TestCase(true, "cluster")]
        [TestCase(false, "configured")]
        [TestCase(true, "configured")]
        [TestCase(false, "late")]
        [TestCase(true, "late")]
        public void StartDefersPollingUntilIdleRefreshIntervalTest(bool run, string affinityMode)
        {
            var handler = new TrackingHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8080)
                .withRoutingScope(affinityMode == "cluster" ? ClusterScope.create() : RackScope.of("dc1", "rack1", null))
                .withKeyRouteAffinity(affinityMode == "configured" ? KeyRouteAffinity.ANY_WRITE : KeyRouteAffinity.NONE)
                .withActiveRefreshIntervalMs(10)
                .withIdleRefreshIntervalMs(500)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);
            var runTask = run ? Task.Run(() => liveNodes.run()) : liveNodes.start();
            try
            {
                Assert.That(SpinWait.SpinUntil(() => liveNodes.isRunning(), TimeSpan.FromSeconds(5)), Is.True);
                if (affinityMode == "late")
                {
                    _ = new AffinityQueryPlanInterceptor(
                        KeyRouteAffinityConfig.builder().withType(KeyRouteAffinity.ANY_WRITE).build(),
                        liveNodes);
                }

                Thread.Sleep(100);
                Assert.That(handler.SendCount, Is.Zero);
                Assert.That(
                    SpinWait.SpinUntil(() => handler.SendCount >= (affinityMode == "cluster" ? 1 : 2), TimeSpan.FromSeconds(5)),
                    Is.True);
            }
            finally
            {
                Assert.That(liveNodes.shutdownAndWait(), Is.True);
                Assert.That(runTask.Wait(TimeSpan.FromSeconds(5)), Is.True);
            }
        }

        [Test]
        public void RequestSignalsDiscoveryBeforeIdleRefreshAndRateLimitsItTest()
        {
            var handler = new TrackingHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8080)
                .withRoutingScope(ClusterScope.create())
                .withActiveRefreshIntervalMs(200)
                .withIdleRefreshIntervalMs(10000)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            liveNodes.start().Wait(TimeSpan.FromSeconds(5));
            liveNodes.nextAsURI();
            Assert.That(
                SpinWait.SpinUntil(() => handler.SendCount == 1, TimeSpan.FromSeconds(5)),
                Is.True);
            liveNodes.nextAsURI();
            Thread.Sleep(100);
            Assert.That(handler.SendCount, Is.EqualTo(1));

            Assert.That(
                SpinWait.SpinUntil(
                    () =>
                    {
                        liveNodes.nextAsURI();
                        return handler.SendCount == 2;
                    },
                    TimeSpan.FromSeconds(5)),
                Is.True);

            liveNodes.shutdownAndWait();
        }

        [Test]
        public void NextAsUriStartsDeferredDiscoveryUpdaterTest()
        {
            var handler = new TrackingHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8080)
                .withRoutingScope(ClusterScope.create())
                .withActiveRefreshIntervalMs(10)
                .withIdleRefreshIntervalMs(10000)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            liveNodes.nextAsURI();
            Assert.That(
                SpinWait.SpinUntil(() => handler.SendCount == 1, TimeSpan.FromSeconds(5)),
                Is.True);

            liveNodes.shutdownAndWait();
        }

        [Test]
        public void ShutdownAndWaitOnUnstartedLiveNodesReturnsTrueTest()
        {
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8080)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config);

            Assert.That(liveNodes.ShutdownAndWait(0), Is.True);
            Assert.That(liveNodes.shutdownAndWait(0), Is.True);
            Assert.That(liveNodes.isRunning(), Is.False);
        }

        private static void InvokeUpdateLiveNodes(AlternatorLiveNodes liveNodes)
        {
            var method = typeof(AlternatorLiveNodes).GetMethod(
                "UpdateLiveNodes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method!.Invoke(liveNodes, Array.Empty<object>());
        }

        private static void InvokeProbeDownNodesIfDue(AlternatorLiveNodes liveNodes)
        {
            var method = typeof(AlternatorLiveNodes).GetMethod(
                "ProbeDownNodesIfDue",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method!.Invoke(liveNodes, new object[] { CancellationToken.None });
        }

        private static IReadOnlyList<Uri> ReadNodesAcrossTopologyPublication(
            AlternatorLiveNodes liveNodes,
            Func<IReadOnlyList<Uri>> readNodes,
            Uri newNode)
        {
            var bindingFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var lockField = typeof(AlternatorLiveNodes).GetField("liveNodesLock", bindingFlags);
            var liveNodesField = typeof(AlternatorLiveNodes).GetField("liveNodes", bindingFlags);
            var affinityNodesField = typeof(AlternatorLiveNodes).GetField("affinityNodes", bindingFlags);
            var healthStoreField = typeof(AlternatorLiveNodes).GetField("healthStore", bindingFlags);
            Assert.That(lockField, Is.Not.Null);
            Assert.That(liveNodesField, Is.Not.Null);
            Assert.That(affinityNodesField, Is.Not.Null);
            Assert.That(healthStoreField, Is.Not.Null);

            var topologyLock = lockField!.GetValue(liveNodes) as ReaderWriterLockSlim;
            Assert.That(topologyLock, Is.Not.Null);
            var healthStore = healthStoreField!.GetValue(liveNodes);
            Assert.That(healthStore, Is.Not.Null);
            var setKnownNodes = healthStore!.GetType().GetMethod(
                "SetKnownNodes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(setKnownNodes, Is.Not.Null);

            Task<IReadOnlyList<Uri>> readTask;
            topologyLock!.EnterWriteLock();
            try
            {
                readTask = Task.Run(readNodes);
                Assert.That(
                    SpinWait.SpinUntil(() => topologyLock.WaitingReadCount == 1, TimeSpan.FromSeconds(5)),
                    Is.True);
                var publishedNodes = new List<Uri> { newNode };
                liveNodesField!.SetValue(liveNodes, publishedNodes);
                affinityNodesField!.SetValue(liveNodes, new List<Uri>(publishedNodes));
                setKnownNodes!.Invoke(healthStore, new object[] { publishedNodes });
            }
            finally
            {
                topologyLock.ExitWriteLock();
            }

            Assert.That(readTask.Wait(TimeSpan.FromSeconds(5)), Is.True);
            return readTask.Result;
        }

        private static AlternatorLiveNodes CreateRackScopedLiveNodes(
            int port,
            string rack,
            KeyRouteAffinityConfig affinity,
            string seedHost = "127.0.0.1")
        {
            return new AlternatorLiveNodes(CreateRackScopedConfig(port, rack, affinity, seedHost));
        }

        private static AlternatorConfig CreateRackScopedConfig(
            int port,
            string rack,
            KeyRouteAffinityConfig affinity,
            string seedHost)
        {
            return AlternatorConfig.builder()
                .withSeedHost(seedHost)
                .withScheme("http")
                .withPort(port)
                .withRoutingScope(RackScope.of("dc1", rack, ClusterScope.create()))
                .withKeyRouteAffinity(affinity)
                .build();
        }

        private static HttpClient CreateAddressMappedHttpClient(
            IEnumerable<IPAddress> addresses,
            ICollection<IPAddress> attempts)
        {
            var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (context, cancellationToken) =>
                {
                    Exception? lastException = null;
                    foreach (var address in addresses)
                    {
                        attempts.Add(address);
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            await socket.ConnectAsync(
                                new IPEndPoint(address, context.DnsEndPoint.Port),
                                cancellationToken);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch (Exception exception)
                        {
                            socket.Dispose();
                            lastException = exception;
                        }
                    }

                    throw new HttpRequestException("No mapped DNS address was reachable.", lastException);
                },
            };
            return new HttpClient(handler, disposeHandler: true);
        }

        private sealed class AffinityActivatingLiveNodes : AlternatorLiveNodes
        {
            private bool activated;

            public AffinityActivatingLiveNodes(AlternatorConfig config, HttpClient client)
                : base(config, client)
            {
            }

            protected override IReadOnlyList<Uri> GetActiveNodesInternal()
            {
                if (!this.activated)
                {
                    this.activated = true;
                    var affinity = KeyRouteAffinityConfig.builder().withType(KeyRouteAffinity.ANY_WRITE).build();
                    _ = new AffinityQueryPlanInterceptor(affinity, this);
                    InvokeUpdateLiveNodes(this);
                }

                return base.GetActiveNodesInternal();
            }
        }

        private sealed class TrackingHttpMessageHandler : HttpMessageHandler
        {
            private readonly string responseBody;
            private int disposeCount;
            private int sendCount;

            internal TrackingHttpMessageHandler(string responseBody = "[\"127.0.0.2\"]")
            {
                this.responseBody = responseBody;
            }

            internal int DisposeCount => this.disposeCount;

            internal int SendCount => this.sendCount;

            internal Uri? LastRequestUri { get; private set; }

            internal string? LastHostHeader { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref this.sendCount);
                this.LastRequestUri = request.RequestUri;
                this.LastHostHeader = request.Headers.Host;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(this.responseBody, Encoding.UTF8, "application/json"),
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

        private sealed class DiscoveryHttpMessageHandler : HttpMessageHandler
        {
            private readonly IReadOnlyDictionary<string, string> responsesByHost;
            private readonly ISet<string> failingHosts;
            private readonly ISet<string> failingQueries;

            internal DiscoveryHttpMessageHandler(
                IReadOnlyDictionary<string, string> responsesByHost,
                ISet<string>? failingHosts = null,
                ISet<string>? failingQueries = null)
            {
                this.responsesByHost = responsesByHost;
                this.failingHosts = failingHosts ?? new HashSet<string>();
                this.failingQueries = failingQueries ?? new HashSet<string>();
            }

            internal List<Uri> RequestedUris { get; } = new List<Uri>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was not set.");
                this.RequestedUris.Add(uri);
                if (this.failingHosts.Contains(uri.Host) || this.failingQueries.Contains(uri.Query))
                {
                    throw new HttpRequestException("simulated discovery failure for " + uri.Host);
                }

                if (!this.responsesByHost.TryGetValue(uri.Host, out var responseBody))
                {
                    throw new InvalidOperationException("Unexpected discovery host: " + uri.Host);
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                });
            }
        }

        private sealed class PartialClusterDiscoveryHttpMessageHandler : HttpMessageHandler
        {
            private readonly string? secondRefreshFirstSeedResponse;
            private int requestCount;

            internal PartialClusterDiscoveryHttpMessageHandler(string? secondRefreshFirstSeedResponse)
            {
                this.secondRefreshFirstSeedResponse = secondRefreshFirstSeedResponse;
            }

            internal List<Uri> RequestedUris { get; } = new List<Uri>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was not set.");
                this.RequestedUris.Add(uri);
                var currentRequest = Interlocked.Increment(ref this.requestCount);
                if (currentRequest == 4 || (currentRequest == 3 && this.secondRefreshFirstSeedResponse == null))
                {
                    throw new HttpRequestException("simulated cluster discovery failure");
                }

                var responseBody = currentRequest switch
                {
                    1 => "[\"dc1-discovered1.example.com\",\"dc1-discovered2.example.com\"]",
                    2 => "[\"dc2-discovered1.example.com\",\"dc2-discovered2.example.com\"]",
                    3 => this.secondRefreshFirstSeedResponse!,
                    _ => throw new InvalidOperationException("Unexpected discovery request number: " + currentRequest),
                };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                });
            }
        }

        private sealed class TrackingRoutingScope : RoutingScope
        {
            private readonly RoutingScope? fallback;
            private readonly Func<string>? query;

            internal TrackingRoutingScope(RoutingScope? fallback = null, Func<string>? query = null)
            {
                this.fallback = fallback;
                this.query = query;
            }

            public string Name => "Custom";

            public string Description => "Custom scope";

            public RoutingScope? Fallback
            {
                get
                {
                    this.FallbackReads++;
                    return this.fallback ?? throw new InvalidOperationException("Unused fallback was read.");
                }
            }

            public string LocalNodesQuery => this.query?.Invoke() ?? "dc=custom";

            internal int FallbackReads { get; private set; }
        }

        private sealed class ScopeAwareDiscoveryHttpMessageHandler : HttpMessageHandler
        {
            private readonly string rackResponse;
            private readonly string datacenterResponse;
            private readonly System.Collections.Concurrent.ConcurrentQueue<Uri> requestedUris =
                new System.Collections.Concurrent.ConcurrentQueue<Uri>();

            internal ScopeAwareDiscoveryHttpMessageHandler(string rackNode, IEnumerable<string> datacenterNodes)
                : this(new[] { rackNode }, datacenterNodes)
            {
            }

            internal ScopeAwareDiscoveryHttpMessageHandler(IEnumerable<string> rackNodes, IEnumerable<string> datacenterNodes)
            {
                this.rackResponse = System.Text.Json.JsonSerializer.Serialize(rackNodes);
                this.datacenterResponse = System.Text.Json.JsonSerializer.Serialize(datacenterNodes);
            }

            internal bool HasRackAndDatacenterRequests =>
                this.requestedUris.Any(uri => uri.Query.Contains("&rack="))
                && this.requestedUris.Any(uri => uri.Query == "?dc=dc1");

            internal IReadOnlyList<Uri> RequestedUris => this.requestedUris.ToArray();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was not set.");
                this.requestedUris.Enqueue(uri);
                var responseBody = uri.Query == "?dc=dc1"
                    ? this.datacenterResponse
                    : this.rackResponse;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                });
            }
        }

        private sealed class LocalNodesServer : IDisposable
        {
            internal const string Http500 = "__HTTP_500__";

            private readonly TcpListener listener;
            private readonly int expectedRequests;
            private readonly Func<RequestRecord, string?> responseBody;
            private readonly Task serverTask;
            private bool disposed;

            internal LocalNodesServer(
                int expectedRequests,
                Func<RequestRecord, string?> responseBody,
                IPAddress? listenAddress = null)
            {
                this.expectedRequests = expectedRequests;
                this.responseBody = responseBody;
                this.listener = new TcpListener(listenAddress ?? IPAddress.Any, 0);
                this.listener.Start();
                this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
                this.serverTask = Task.Run(this.RunAsync);
            }

            internal int Port { get; }

            internal List<RequestRecord> Requests { get; } = new List<RequestRecord>();

            public void Dispose()
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
                this.listener.Stop();
                this.serverTask.Wait(TimeSpan.FromSeconds(5));
            }

            internal void WaitForRequests()
            {
                if (!this.serverTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    Assert.Fail("Timed out waiting for local HTTP test server requests.");
                }

                if (this.serverTask.IsFaulted)
                {
                    throw this.serverTask.Exception!;
                }
            }

            private async Task RunAsync()
            {
                for (var i = 0; i < this.expectedRequests; i++)
                {
                    using var client = await this.listener.AcceptTcpClientAsync();
                    await this.HandleClientAsync(client);
                }
            }

            private async Task HandleClientAsync(TcpClient client)
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                var host = string.Empty;
                var connection = string.Empty;
                string? header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
                {
                    if (header.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    {
                        host = header.Substring("Host:".Length).Trim();
                    }
                    else if (header.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                    {
                        connection = header.Substring("Connection:".Length).Trim();
                    }
                }

                var target = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) ?? string.Empty;
                var queryIndex = target.IndexOf('?');
                var query = queryIndex >= 0 ? target.Substring(queryIndex + 1) : string.Empty;
                var request = new RequestRecord(target, query, host, connection);
                this.Requests.Add(request);

                var body = this.responseBody(request);
                if (body == null)
                {
                    return;
                }

                if (body == Http500)
                {
                    var failureResponse = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 500 Internal Server Error\r\n"
                        + "Content-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(failureResponse);
                    return;
                }

                var bodyBytes = Encoding.UTF8.GetBytes(body);
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: application/json\r\n"
                    + "Content-Length: "
                    + bodyBytes.Length
                    + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response);
                await stream.WriteAsync(bodyBytes);
            }
        }

        private sealed class RequestRecord
        {
            internal RequestRecord(string target, string query, string host, string connection)
            {
                this.Target = target;
                this.Query = query;
                this.Host = host;
                this.Connection = connection;
            }

            internal string Target { get; }

            internal string Query { get; }

            internal string Host { get; }

            internal string Connection { get; }
        }
    }
}
