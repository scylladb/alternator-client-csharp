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
        public void ClusterScopeKeepsCompleteTopologyWhenLaterDiscoveryIsPartialTest()
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
            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(completeTopology));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Does.Not.Contain("partial.example.com"));
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
        public void ClusterScopeKeepsCompleteTopologyWhenOneSeedIsEmptyAndAnotherFailsTest()
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

            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(completeTopology));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Does.Not.Contain("dc1-node1.example.com"));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Does.Not.Contain("dc2-node1.example.com"));
        }

        [Test]
        public void ClusterScopeKeepsCompleteTopologyWhenEverySeedFailsTest()
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

            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(completeTopology));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Does.Not.Contain("dc1-node1.example.com"));
            Assert.That(liveNodes.getLiveNodes().Select(node => node.Host), Does.Not.Contain("dc2-node1.example.com"));
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
        public void RackScopeAffinityUsesClusterNodesWhileBasicRoutingStaysLocalTest()
        {
            using var server = new LocalNodesServer(
                4,
                request => request.Query switch
                {
                    "dc=dc1&rack=rack1" => "[\"127.0.0.2\"]",
                    "dc=dc1&rack=rack2" => "[\"127.0.0.3\"]",
                    var query when string.IsNullOrEmpty(query) => "[\"127.0.0.2\",\"127.0.0.3\",\"127.0.0.4\"]",
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
                    string.Empty,
                    "dc=dc1&rack=rack2",
                    string.Empty,
                }));
        }

        [Test]
        public void AffinityInterceptorEnablesClusterDiscoveryOnSeparatelyCreatedRackManagerTest()
        {
            using var server = new LocalNodesServer(
                2,
                request => string.IsNullOrEmpty(request.Query)
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
                server.WaitForRequests();

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
                    Is.EqualTo(new[] { "dc=dc1&rack=rack1", string.Empty }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void ConfiguredRackAffinityStartsClusterDiscoveryWithoutRequestActivityTest()
        {
            var clusterNodes = new[]
            {
                "cluster-node1.example.com",
                "cluster-node2.example.com",
                "cluster-node3.example.com",
            };
            var rack1Handler = new ScopeAwareDiscoveryHttpMessageHandler("rack1-node.example.com", clusterNodes);
            var rack2Handler = new ScopeAwareDiscoveryHttpMessageHandler("rack2-node.example.com", clusterNodes);
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
                var rack1Interceptor = new AffinityQueryPlanInterceptor(affinity, rack1);
                var rack2Interceptor = new AffinityQueryPlanInterceptor(affinity, rack2);
                Assert.That(
                    SpinWait.SpinUntil(
                        () => rack1Handler.HasScopedAndClusterRequests && rack2Handler.HasScopedAndClusterRequests,
                        TimeSpan.FromSeconds(5)),
                    Is.True);

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
                Assert.That(rack1Plan.Select(node => node.Host), Is.EquivalentTo(clusterNodes));
            }
            finally
            {
                rack1.shutdownAndWait();
                rack2.shutdownAndWait();
            }
        }

        [Test]
        public void AffinityOnlyDownNodeIsProbedAndRecoversTest()
        {
            using var server = new LocalNodesServer(
                3,
                request => string.IsNullOrEmpty(request.Query)
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
        public void FailedRefreshKeepsLastCompleteIpv6TopologyTest()
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
                Is.EqualTo(new[] { "[::2]" }));
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

        [Test]
        public void StartDefersPollingUntilIdleRefreshIntervalTest()
        {
            var handler = new TrackingHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8080)
                .withRoutingScope(ClusterScope.create())
                .withActiveRefreshIntervalMs(10)
                .withIdleRefreshIntervalMs(500)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            liveNodes.start().Wait(TimeSpan.FromSeconds(5));
            Thread.Sleep(100);
            Assert.That(handler.SendCount, Is.EqualTo(0));
            Assert.That(
                SpinWait.SpinUntil(() => handler.SendCount == 1, TimeSpan.FromSeconds(5)),
                Is.True);

            liveNodes.shutdownAndWait();
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

            internal DiscoveryHttpMessageHandler(
                IReadOnlyDictionary<string, string> responsesByHost,
                ISet<string>? failingHosts = null)
            {
                this.responsesByHost = responsesByHost;
                this.failingHosts = failingHosts ?? new HashSet<string>();
            }

            internal List<Uri> RequestedUris { get; } = new List<Uri>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was not set.");
                this.RequestedUris.Add(uri);
                if (this.failingHosts.Contains(uri.Host))
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

        private sealed class ScopeAwareDiscoveryHttpMessageHandler : HttpMessageHandler
        {
            private readonly string rackNode;
            private readonly string clusterResponse;
            private readonly System.Collections.Concurrent.ConcurrentQueue<Uri> requestedUris =
                new System.Collections.Concurrent.ConcurrentQueue<Uri>();

            internal ScopeAwareDiscoveryHttpMessageHandler(string rackNode, IEnumerable<string> clusterNodes)
            {
                this.rackNode = rackNode;
                this.clusterResponse = System.Text.Json.JsonSerializer.Serialize(clusterNodes);
            }

            internal bool HasScopedAndClusterRequests =>
                this.requestedUris.Any(uri => !string.IsNullOrEmpty(uri.Query))
                && this.requestedUris.Any(uri => string.IsNullOrEmpty(uri.Query));

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI was not set.");
                this.requestedUris.Enqueue(uri);
                var responseBody = string.IsNullOrEmpty(uri.Query)
                    ? this.clusterResponse
                    : System.Text.Json.JsonSerializer.Serialize(new[] { this.rackNode });
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
