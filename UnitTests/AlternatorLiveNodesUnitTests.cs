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
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Net;
    using System.Net.Security;
    using System.Net.Sockets;
    using System.Security.Authentication;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Text.Json;
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
        public void ClusterScopeKeepsSuccessfulDiscoveryWhenAnotherSeedFailsTest()
        {
            var handler = new DiscoveryHttpMessageHandler(
                new Dictionary<string, string>
                {
                    ["dc1-node1.example.com"] = "[\"dc1-node1.example.com\"]",
                },
                new HashSet<string> { "dc2-node1.example.com" });
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
                Is.EqualTo(new[] { "dc1-node1.example.com", "dc2-node1.example.com" }));
            Assert.That(
                handler.RequestedUris.Select(uri => uri.Host),
                Is.EqualTo(new[] { "dc1-node1.example.com", "dc2-node1.example.com" }));
        }

        [Test]
        public void ClusterRefreshUnionsLiveNodesWithRecoveredOriginalSeedsTest()
        {
            var handler = new SequenceDiscoveryHttpMessageHandler(
                () => JsonResponse("[\"live-dc1.example.com\"]"),
                () => throw new HttpRequestException("dc2 seed initially unavailable"),
                () => JsonResponse("[\"live-dc1.example.com\"]"),
                () => JsonResponse("[\"live-dc1.example.com\"]"),
                () => JsonResponse("[\"live-dc2.example.com\"]"));
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "seed-dc1.example.com", "seed-dc2.example.com" })
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
                    "live-dc1.example.com",
                    "seed-dc1.example.com",
                    "seed-dc2.example.com",
                }));

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "live-dc1.example.com", "live-dc2.example.com" }));
            Assert.That(
                handler.RequestedUris.Select(uri => uri.Host),
                Is.EqualTo(new[]
                {
                    "seed-dc1.example.com",
                    "seed-dc2.example.com",
                    "live-dc1.example.com",
                    "seed-dc1.example.com",
                    "seed-dc2.example.com",
                }));
        }

        [Test]
        public void PartialClusterRefreshRetainsUnavailablePartitionWithFreshNodesFirstTest()
        {
            var responses = new Dictionary<string, string>
            {
                ["seed.example.com"] = "[\"dc-a-old.example.com\",\"dc-b-old.example.com\"]",
            };
            var failingHosts = new HashSet<string>();
            var handler = new DiscoveryHttpMessageHandler(responses, failingHosts);
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            responses.Clear();
            responses["dc-a-old.example.com"] = "[\"dc-a-new.example.com\"]";
            failingHosts.UnionWith(new[] { "dc-b-old.example.com", "seed.example.com" });

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[]
                {
                    "dc-a-new.example.com",
                    "dc-a-old.example.com",
                    "dc-b-old.example.com",
                }));
        }

        [Test]
        public void EmptyClusterPartitionCannotDeleteAnotherPartitionsLastKnownGoodTest()
        {
            var responses = new Dictionary<string, string>
            {
                ["seed.example.com"] = "[\"dc-a-old.example.com\",\"dc-b-old.example.com\"]",
            };
            var handler = new DiscoveryHttpMessageHandler(responses);
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            responses.Clear();
            responses["dc-a-old.example.com"] = "[\"dc-a-new.example.com\"]";
            responses["dc-b-old.example.com"] = "[]";
            responses["seed.example.com"] = "[]";

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[]
                {
                    "dc-a-new.example.com",
                    "dc-a-old.example.com",
                    "dc-b-old.example.com",
                }));
        }

        [Test]
        public void CompleteClusterRefreshReplacesPreviousSnapshotTest()
        {
            var responses = new Dictionary<string, string>
            {
                ["seed.example.com"] = "[\"old-a.example.com\",\"old-b.example.com\"]",
            };
            var handler = new DiscoveryHttpMessageHandler(responses);
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            responses.Clear();
            responses["old-a.example.com"] = "[\"fresh.example.com\"]";
            responses["old-b.example.com"] = "[\"fresh.example.com\"]";
            responses["seed.example.com"] = "[\"fresh.example.com\"]";

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "fresh.example.com" }));
        }

        [Test]
        public void PartialClusterMergePrioritizesFreshNodesWithinSnapshotBoundTest()
        {
            var retained = Enumerable.Range(0, 4096)
                .Select(index => new Uri($"http://old-{index}.example.com:8000"))
                .ToList();
            var fresh = new Uri("http://fresh.example.com:8000");

            var merged = InvokeMergePartialClusterSnapshot(new[] { fresh }, retained);

            Assert.That(merged, Has.Count.EqualTo(4096));
            Assert.That(merged[0], Is.EqualTo(fresh));
            Assert.That(merged.Skip(1), Is.EqualTo(retained.Take(4095)));
            Assert.That(merged, Does.Not.Contain(retained[4095]));
        }

        [Test]
        public void WholeDiscoveryCycleSharesDeadlineFairlyAcrossSeedsTest()
        {
            var handler = new TimeoutThenHealthySeedHttpMessageHandler(
                new HashSet<string>
                {
                    "slow-one.example.com",
                    "slow-two.example.com",
                    "slow-three.example.com",
                });
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[]
                {
                    "slow-one.example.com",
                    "slow-two.example.com",
                    "slow-three.example.com",
                    "healthy.example.com",
                })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .withHttpClientTimeoutMs(600)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            var stopwatch = Stopwatch.StartNew();
            InvokeUpdateLiveNodes(liveNodes);
            stopwatch.Stop();

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(1000)));
            Assert.That(handler.RequestedHosts, Does.Contain("healthy.example.com"));
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[]
                {
                    "healthy-node.example.com",
                    "slow-one.example.com",
                    "slow-two.example.com",
                    "slow-three.example.com",
                    "healthy.example.com",
                }));
        }

        [Test]
        public void WholeDiscoveryDeadlineReservesOpportunityForFallbackScopeTest()
        {
            var handler = new ScopedTimeoutHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[]
                {
                    "seed-one.example.com",
                    "seed-two.example.com",
                    "seed-three.example.com",
                })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(DatacenterScope.of("missing", ClusterScope.create()))
                .withHttpClientTimeoutMs(600)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            var stopwatch = Stopwatch.StartNew();
            InvokeUpdateLiveNodes(liveNodes);
            stopwatch.Stop();

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(1000)));
            Assert.That(handler.RequestedUris.Any(uri => string.IsNullOrEmpty(uri.Query)), Is.True);
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "fallback-node.example.com" }));
        }

        [Test]
        public void TimedOutDnsAddressesCannotStarveLaterSeedWithinCycleDeadlineTest()
        {
            var firstAddress = IPAddress.Parse("127.0.0.2");
            var secondAddress = IPAddress.Parse("127.0.0.3");
            using var healthyServer = new LocalNodesServer(
                1,
                _ => "[\"healthy-node.example.com\"]",
                IPAddress.Loopback);
            using var blackholes = new BlackholeServer(
                healthyServer.Port,
                firstAddress,
                secondAddress);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "slow.example.com", "healthy.example.com" })
                .withScheme("http")
                .withPort(healthyServer.Port)
                .withRoutingScope(ClusterScope.create())
                .withConnectionTimeoutMs(600)
                .withHttpClientTimeoutMs(600)
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => new[] { firstAddress, secondAddress },
                () => new[] { IPAddress.Loopback });

            try
            {
                var stopwatch = Stopwatch.StartNew();
                InvokeUpdateLiveNodes(liveNodes);
                stopwatch.Stop();
                healthyServer.WaitForRequests();

                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(900)));
                Assert.That(blackholes.AcceptedConnections, Is.EqualTo(2));
                Assert.That(liveNodes.ResolutionCount, Is.EqualTo(2));
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[]
                    {
                        "healthy-node.example.com",
                        "slow.example.com",
                        "healthy.example.com",
                    }));
            }
            finally
            {
                liveNodes.ShutdownAndWait();
            }
        }

        [Test]
        public void ManyDnsAddressesCannotConsumeLaterSeedsUsefulDeadlineShareTest()
        {
            var blackholeAddresses = Enumerable.Range(2, 6)
                .Select(index => IPAddress.Parse($"127.0.0.{index}"))
                .ToArray();
            using var healthyServer = new LocalNodesServer(
                1,
                _ => LocalNodesServer.HttpDelayedValid,
                IPAddress.Loopback);
            using var blackholes = new BlackholeServer(
                healthyServer.Port,
                blackholeAddresses);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "many-addresses.example.com", "healthy.example.com" })
                .withScheme("http")
                .withPort(healthyServer.Port)
                .withRoutingScope(ClusterScope.create())
                .withConnectionTimeoutMs(800)
                .withHttpClientTimeoutMs(800)
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => blackholeAddresses,
                () => new[] { IPAddress.Loopback });

            try
            {
                var stopwatch = Stopwatch.StartNew();
                InvokeUpdateLiveNodes(liveNodes);
                stopwatch.Stop();
                healthyServer.WaitForRequests();

                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(900)));
                Assert.That(blackholes.AcceptedConnections, Is.EqualTo(blackholeAddresses.Length));
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[]
                    {
                        "healthy-node.example.com",
                        "many-addresses.example.com",
                        "healthy.example.com",
                    }));
            }
            finally
            {
                liveNodes.ShutdownAndWait();
            }
        }

        [Test]
        public void AggregateDiscoveredNodeLimitRetainsPreviousAtomicSnapshotTest()
        {
            var oversizedHosts = Enumerable.Range(0, 5000)
                .Select(index => $"node-{index}.example.com")
                .ToArray();
            var handler = new SequenceDiscoveryHttpMessageHandler(
                () => JsonResponse("[\"stable.example.com\"]"),
                () => JsonResponse("[\"stable.example.com\"]"),
                () => throw new HttpRequestException("learned node became unavailable"),
                () => JsonResponse(JsonSerializer.Serialize(oversizedHosts)),
                () => JsonResponse("[\"other.example.com\"]"));
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "seed-one.example.com", "seed-two.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "stable.example.com" }));
        }

        [Test]
        public void AggregateDiscoveryResponseByteLimitRetainsPreviousAtomicSnapshotTest()
        {
            var repeatedHosts = Enumerable.Repeat("duplicate.example.com", 28000).ToArray();
            var largeResponse = JsonSerializer.Serialize(repeatedHosts);
            Assert.That(Encoding.UTF8.GetByteCount(largeResponse), Is.LessThan(1024 * 1024));
            Assert.That(Encoding.UTF8.GetByteCount(largeResponse) * 2, Is.GreaterThan(1024 * 1024));
            var handler = new SequenceDiscoveryHttpMessageHandler(
                () => JsonResponse("[\"stable.example.com\"]"),
                () => JsonResponse("[\"stable.example.com\"]"),
                () => throw new HttpRequestException("learned node became unavailable"),
                () => JsonResponse(largeResponse),
                () => JsonResponse(largeResponse));
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "seed-one.example.com", "seed-two.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "stable.example.com" }));
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
        public void ScopedSeedRemainsDiscoveryOnlyUntilScopeValidationTest()
        {
            var handler = new DiscoveryHttpMessageHandler(new Dictionary<string, string>
            {
                ["seed.example.com"] = "[]",
            });
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(DatacenterScope.of("missing", null))
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            Assert.That(liveNodes.getLiveNodes(), Is.Empty);
            Assert.That(liveNodes.getActiveNodes(), Is.Empty);
            Assert.Throws<InvalidOperationException>(() => InvokeNextAsUriWithoutRefresh(liveNodes));

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(handler.RequestedUris.Select(uri => uri.Host), Is.EqualTo(new[] { "seed.example.com" }));
            Assert.That(liveNodes.getLiveNodes(), Is.Empty);
            Assert.That(liveNodes.getActiveNodes(), Is.Empty);
            Assert.Throws<InvalidOperationException>(() => InvokeNextAsUriWithoutRefresh(liveNodes));
        }

        [Test]
        public void ClusterFallbackAuthorizesSeedRoutingDuringInitializationTest()
        {
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(DatacenterScope.of("dc1", ClusterScope.create()))
                .build();
            using var pollingHttpClient = new HttpClient(new TrackingHttpMessageHandler("[]"));
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            Assert.That(
                liveNodes.getLiveNodes(),
                Is.EqualTo(new[] { new Uri("http://seed.example.com:8000") }));
            Assert.That(
                InvokeNextAsUriWithoutRefresh(liveNodes),
                Is.EqualTo(new Uri("http://seed.example.com:8000")));
        }

        [Test]
        public void RoutingScopeCycleIsRejectedByReferenceIdentityTest()
        {
            var scope = new MutableRoutingScope("dc=dc1");
            scope.Fallback = scope;
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(scope)
                .build();
            using var pollingHttpClient = new HttpClient(new TrackingHttpMessageHandler("[]"));

            var exception = Assert.Throws<SystemException>(() =>
                _ = new AlternatorLiveNodes(config, pollingHttpClient));

            Assert.That(exception!.Message, Does.Contain("routing scope").IgnoreCase);
        }

        [Test]
        public void RoutingScopeDepthIsBoundedTest()
        {
            var scopes = Enumerable.Range(0, 40)
                .Select(index => new MutableRoutingScope($"dc=dc{index}"))
                .ToArray();
            for (var i = 0; i < scopes.Length - 1; i++)
            {
                scopes[i].Fallback = scopes[i + 1];
            }

            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(scopes[0])
                .build();
            using var pollingHttpClient = new HttpClient(new TrackingHttpMessageHandler("[]"));

            var exception = Assert.Throws<SystemException>(() =>
                _ = new AlternatorLiveNodes(config, pollingHttpClient));

            Assert.That(exception!.Message, Does.Contain("routing scope").IgnoreCase);
        }

        [Test]
        public void CustomUnscopedRoutingScopeUsesCompleteClusterUnionSemanticsTest()
        {
            var scope = new MutableRoutingScope(string.Empty);
            var handler = new DiscoveryHttpMessageHandler(new Dictionary<string, string>
            {
                ["seed-one.example.com"] = "[\"node-one.example.com\"]",
                ["seed-two.example.com"] = "[\"node-two.example.com\"]",
            });
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "seed-one.example.com", "seed-two.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(scope)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            Assert.That(liveNodes.getLiveNodes(), Has.Count.EqualTo(2));
            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "node-one.example.com", "node-two.example.com" }));
        }

        [Test]
        public void DynamicallyIntroducedRoutingScopeCycleDoesNotBusyLoopTest()
        {
            var scope = new MutableRoutingScope("dc=dc1");
            var handler = new TrackingHttpMessageHandler("[]");
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(scope)
                .withIdleRefreshIntervalMs(50)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            liveNodes.Start().GetAwaiter().GetResult();
            scope.Fallback = scope;
            Thread.Sleep(150);

            Assert.That(liveNodes.ShutdownAndWait(2000), Is.True);
            Assert.That(handler.SendCount, Is.LessThanOrEqualTo(1));
        }

        [Test]
        public void AuthoritativeScopedEmptyRefreshClearsPriorSnapshotAndHealthTest()
        {
            var handler = new SequenceDiscoveryHttpMessageHandler(
                () => JsonResponse("[\"seed.example.com\"]"),
                () => JsonResponse("[]"));
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(DatacenterScope.of("dc1", null))
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            Assert.That(liveNodes.getLiveNodes(), Has.Count.EqualTo(1));

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(liveNodes.getLiveNodes(), Is.Empty);
            Assert.That(liveNodes.getActiveNodes(), Is.Empty);
            Assert.That(liveNodes.getNodeStatus(new Uri("http://seed.example.com:8000")), Is.Null);
        }

        [Test]
        public void PrimaryEmptyWithFallbackFailureRetainsFallbackPublishedSnapshotTest()
        {
            var handler = new SequenceDiscoveryHttpMessageHandler(
                () => JsonResponse("[]"),
                () => JsonResponse("[\"fallback.example.com\"]"),
                () => JsonResponse("[]"),
                () => JsonResponse("[]"),
                () => throw new HttpRequestException("fallback live node unavailable"),
                () => throw new HttpRequestException("fallback seed unavailable"));
            using var pollingHttpClient = new HttpClient(handler);
            var fallback = DatacenterScope.of("dc2", null);
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(DatacenterScope.of("dc1", fallback))
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "fallback.example.com" }));

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "fallback.example.com" }));
        }

        [Test]
        public void PrimaryEmptyWithFallbackFailureClearsPrimaryPublishedSnapshotTest()
        {
            var handler = new SequenceDiscoveryHttpMessageHandler(
                () => JsonResponse("[\"primary.example.com\"]"),
                () => JsonResponse("[]"),
                () => JsonResponse("[]"),
                () => throw new HttpRequestException("fallback live node unavailable"),
                () => throw new HttpRequestException("fallback seed unavailable"));
            using var pollingHttpClient = new HttpClient(handler);
            var fallback = DatacenterScope.of("dc2", null);
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(DatacenterScope.of("dc1", fallback))
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "primary.example.com" }));

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(liveNodes.getLiveNodes(), Is.Empty);
            Assert.That(liveNodes.getActiveNodes(), Is.Empty);
        }

        [Test]
        public void ClusterEmptyRefreshRetainsLastKnownGoodSnapshotTest()
        {
            var handler = new SequenceDiscoveryHttpMessageHandler(
                () => JsonResponse("[\"learned.example.com\"]"),
                () => JsonResponse("[]"),
                () => JsonResponse("[]"));
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "learned.example.com" }));
            var learnedStatus = liveNodes.getNodeStatus(new Uri("http://learned.example.com:8000"));
            Assert.That(learnedStatus, Is.Not.Null);
            Assert.That(
                learnedStatus!.State,
                Is.EqualTo(NodeHealthState.Active));
        }

        [Test]
        public void FeatureSupportProbeRetriesLaterDiscoverySeedTest()
        {
            var handler = new FeatureProbeHttpMessageHandler("broken.example.com");
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "broken.example.com", "healthy.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            Assert.That(liveNodes.CheckIfRoutingScopeFeatureIsSupported(), Is.True);
            Assert.That(
                handler.RequestedUris.Select(uri => (uri.Host, uri.Query)),
                Is.EqualTo(new[]
                {
                    ("broken.example.com", "?rack=fakeRack"),
                    ("healthy.example.com", "?rack=fakeRack"),
                    ("healthy.example.com", string.Empty),
                }));
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

        [TestCase(LocalNodesServer.Http500)]
        [TestCase("not-json")]
        [TestCase("[]")]
        [TestCase("[\" \"]")]
        [TestCase(LocalNodesServer.HttpTruncated)]
        public void DnsAddressFallbackContinuesAfterUnusableLocalNodesResponseTest(string failingResponse)
        {
            var firstAddress = IPAddress.Parse("127.0.0.2");
            var secondAddress = IPAddress.Loopback;
            var expectedAddresses = new[] { firstAddress, secondAddress };
            using var server = new LocalNodesServer(
                expectedAddresses.Length,
                request => request.LocalAddress.Equals(firstAddress)
                    ? failingResponse
                    : "[\"127.0.0.9\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .withHttpClientTimeoutMs(2000)
                .build();
            var liveNodes = new AddressMappedLiveNodes(config, () => new[] { firstAddress, secondAddress });
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                server.WaitForRequests();

                Assert.That(
                    server.Requests.Select(request => request.LocalAddress),
                    Is.EqualTo(expectedAddresses));
                Assert.That(
                    server.Requests.Select(request => request.Host),
                    Is.All.EqualTo($"entrypoint.test:{server.Port}"));
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { "127.0.0.9" }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void DnsAddressFallbackRejectsRedirectAndTriesNextAddressTest()
        {
            var redirectingAddress = IPAddress.Parse("127.0.0.2");
            var reachableAddress = IPAddress.Loopback;
            using var server = new LocalNodesServer(
                2,
                request => request.LocalAddress.Equals(redirectingAddress)
                    ? request.Target == "/localnodes"
                        ? LocalNodesServer.HttpRedirect
                        : "[\"127.0.0.20\"]"
                    : "[\"127.0.0.21\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => new[] { redirectingAddress, reachableAddress });
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                server.WaitForRequests();

                Assert.That(
                    server.Requests.Select(request => request.LocalAddress),
                    Is.EqualTo(new[] { redirectingAddress, reachableAddress }));
                Assert.That(
                    server.Requests.Select(request => request.Target),
                    Is.All.EqualTo("/localnodes"));
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { "127.0.0.21" }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void OversizedLocalNodesResponseFallsBackWithoutPublishingPartialDataTest()
        {
            var oversizedResponse = "[\"127.0.0.22\"]" + new string(' ', (1024 * 1024) + 1);
            var handler = new DiscoveryHttpMessageHandler(new Dictionary<string, string>
            {
                ["oversized.example.com"] = oversizedResponse,
                ["healthy.example.com"] = "[\"127.0.0.23\"]",
            });
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "oversized.example.com", "healthy.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                handler.RequestedUris.Select(uri => uri.Host),
                Is.EqualTo(new[] { "oversized.example.com", "healthy.example.com" }));
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[]
                {
                    "127.0.0.23",
                    "oversized.example.com",
                    "healthy.example.com",
                }));
        }

        [Test]
        public void FaultedCachedAddressClientIsEvictedAndLaterRefreshRecoversTest()
        {
            using var server = new LocalNodesServer(1, _ => "[\"127.0.0.24\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AddressMappedLiveNodes(config, () => new[] { IPAddress.Loopback });
            InjectFaultedAddressClient(
                liveNodes,
                IPAddress.Loopback,
                new Uri($"http://entrypoint.test:{server.Port}"));
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { "entrypoint.test" }));

                InvokeUpdateLiveNodes(liveNodes);
                server.WaitForRequests();

                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { "127.0.0.24" }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void SuccessfulDnsAddressFallbackKeepsLogicalNodeActiveTest()
        {
            var firstAddress = IPAddress.Parse("127.0.0.2");
            using var server = new LocalNodesServer(
                2,
                request => request.LocalAddress.Equals(firstAddress)
                    ? LocalNodesServer.Http500
                    : "[\"entrypoint.test\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .withNodeHealth(NodeHealthStoreConfig.builder()
                    .withConsecutiveServerErrorThreshold(1)
                    .build())
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => new[] { firstAddress, IPAddress.Loopback });
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                server.WaitForRequests();

                var logicalNode = new Uri($"http://entrypoint.test:{server.Port}");
                Assert.That(liveNodes.getActiveNodes(), Is.EqualTo(new[] { logicalNode }));
                Assert.That(liveNodes.getQuarantinedNodes(), Is.Empty);
                Assert.That(liveNodes.getDownNodes(), Is.Empty);
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void DnsAddressFallbackDeduplicatesRecordsAndTriesSeveralLeadingFailuresTest()
        {
            var firstAddress = IPAddress.Parse("127.0.0.2");
            var secondAddress = IPAddress.Parse("127.0.0.3");
            var reachableAddress = IPAddress.Loopback;
            using var server = new LocalNodesServer(
                3,
                request => request.LocalAddress.Equals(firstAddress)
                    ? LocalNodesServer.Http500
                    : request.LocalAddress.Equals(secondAddress)
                        ? "not-json"
                        : "[\"127.0.0.10\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => new[] { firstAddress, firstAddress, secondAddress, reachableAddress });
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                server.WaitForRequests();

                Assert.That(
                    server.Requests.Select(request => request.LocalAddress),
                    Is.EqualTo(new[] { firstAddress, secondAddress, reachableAddress }));
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { "127.0.0.10" }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void DnsAddressFallbackContinuesAfterConnectionFailureTest()
        {
            var unreachableAddress = IPAddress.Parse("127.0.0.2");
            using var server = new LocalNodesServer(
                1,
                _ => "[\"127.0.0.11\"]",
                IPAddress.Loopback);
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .withConnectionTimeoutMs(500)
                .withHttpClientTimeoutMs(2000)
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => new[] { unreachableAddress, IPAddress.Loopback });
            try
            {
                var stopwatch = Stopwatch.StartNew();
                InvokeUpdateLiveNodes(liveNodes);
                stopwatch.Stop();
                server.WaitForRequests();

                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
                Assert.That(server.Requests.Single().LocalAddress, Is.EqualTo(IPAddress.Loopback));
                Assert.That(server.Requests.Single().Host, Is.EqualTo($"entrypoint.test:{server.Port}"));
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { "127.0.0.11" }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void DnsAddressFallbackPreservesTlsHostSniAndCertificateIdentityTest()
        {
            var firstAddress = IPAddress.Parse("127.0.0.2");
            var secondAddress = IPAddress.Loopback;
            using var certificate = CreateLogicalHostServerCertificate("entrypoint.test");
            using var firstListener = new TcpListener(firstAddress, 0);
            firstListener.Start();
            var port = ((IPEndPoint)firstListener.LocalEndpoint).Port;
            using var secondListener = new TcpListener(secondAddress, port);
            secondListener.Start();

            var firstRequest = ServeTlsLocalNodesOnceAsync(
                firstListener,
                certificate,
                HttpStatusCode.ServiceUnavailable,
                "{\"error\":\"temporary\"}");
            var secondRequest = ServeTlsLocalNodesOnceAsync(
                secondListener,
                certificate,
                HttpStatusCode.OK,
                "[\"127.0.0.20\"]");
            var tlsConfig = TlsConfig.builder()
                .WithTrustSystemCaCerts(false)
                .WithCaCertificate(certificate)
                .Build();
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("https")
                .withPort(port)
                .withTlsConfig(tlsConfig)
                .withRoutingScope(ClusterScope.create())
                .withConnectionTimeoutMs(1000)
                .withHttpClientTimeoutMs(3000)
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => new[] { firstAddress, secondAddress });
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                var requests = Task.WhenAll(firstRequest, secondRequest)
                    .WaitAsync(TimeSpan.FromSeconds(5))
                    .GetAwaiter()
                    .GetResult();

                Assert.That(
                    requests.Select(request => request.Host),
                    Is.EqualTo(new[] { $"entrypoint.test:{port}", $"entrypoint.test:{port}" }));
                Assert.That(
                    requests.Select(request => request.ServerName),
                    Is.EqualTo(new[] { "entrypoint.test", "entrypoint.test" }));
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { "127.0.0.20" }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
                firstListener.Stop();
                secondListener.Stop();
            }
        }

        [Test]
        public void MixedLocalNodesDataKeepsUsableEntriesWithoutTryingLaterAddressTest()
        {
            var firstAddress = IPAddress.Parse("127.0.0.2");
            using var server = new LocalNodesServer(1, _ => "[\"bad host\",\"127.0.0.12\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => new[] { firstAddress, IPAddress.Loopback });
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                server.WaitForRequests();

                Assert.That(server.Requests.Single().LocalAddress, Is.EqualTo(firstAddress));
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { "127.0.0.12" }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void AllDnsAddressesFailPromptlyAndPreserveOriginalSeedTest()
        {
            var firstAddress = IPAddress.Parse("127.0.0.2");
            var secondAddress = IPAddress.Parse("127.0.0.3");
            using var server = new LocalNodesServer(2, _ => LocalNodesServer.Http500);
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AddressMappedLiveNodes(config, () => new[] { firstAddress, secondAddress });
            try
            {
                var stopwatch = Stopwatch.StartNew();
                InvokeUpdateLiveNodes(liveNodes);
                stopwatch.Stop();
                server.WaitForRequests();

                Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
                Assert.That(
                    server.Requests.Select(request => request.LocalAddress),
                    Is.EqualTo(new[] { firstAddress, secondAddress }));
                Assert.That(
                    liveNodes.getLiveNodes(),
                    Is.EqualTo(new[] { new Uri($"http://entrypoint.test:{server.Port}") }));
                Assert.That(liveNodes.getActiveNodes(), Is.Empty);
                Assert.Throws<InvalidOperationException>(() => InvokeNextAsUriWithoutRefresh(liveNodes));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void ActiveSessionRecoversThroughOriginalSeedAfterLearnedNodeFailureTest()
        {
            var handler = new SequenceDiscoveryHttpMessageHandler(
                () => JsonResponse("[\"learned-old.example.com\"]"),
                () => throw new HttpRequestException("simulated seed outage"),
                () => throw new HttpRequestException("simulated learned-node outage"),
                () => JsonResponse("[\"learned-new.example.com\"]"));
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);
            var oldNode = new Uri("http://learned-old.example.com:8000");
            var newNode = new Uri("http://learned-new.example.com:8000");

            InvokeUpdateLiveNodes(liveNodes);
            liveNodes.reportNodeResult(oldNode, NodeHealthObservation.ConnectionFailure);
            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(new[] { oldNode }));
            Assert.That(liveNodes.getDownNodes(), Is.EqualTo(new[] { oldNode }));
            Assert.That(liveNodes.getActiveNodes(), Is.Empty);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(new[] { newNode, oldNode }));
            Assert.That(liveNodes.getActiveNodes(), Is.EqualTo(new[] { newNode }));
            Assert.That(InvokeNextAsUriWithoutRefresh(liveNodes), Is.EqualTo(newNode));
            Assert.That(
                handler.RequestedUris.Select(uri => uri.Host),
                Is.EqualTo(new[]
                {
                    "entrypoint.test",
                    "entrypoint.test",
                    "learned-old.example.com",
                    "entrypoint.test",
                    "learned-old.example.com",
                }));
        }

        [Test]
        public void AllDownRoutingFailsFastWhileSeedRecoveryRunsInBackgroundTest()
        {
            var handler = new CancellableBlockingHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .withActiveRefreshIntervalMs(10)
                .withIdleRefreshIntervalMs(10000)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);
            var seed = new Uri("http://127.0.0.1:8000");
            liveNodes.reportNodeResult(seed, NodeHealthObservation.ConnectionFailure);
            Exception? routingFailure = null;

            var routing = Task.Run(() =>
            {
                try
                {
                    _ = liveNodes.nextAsURI();
                }
                catch (Exception e)
                {
                    routingFailure = e;
                }
            });

            Assert.That(handler.WaitForRequest(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(routing.Wait(TimeSpan.FromMilliseconds(500)), Is.True);
            Assert.That(routingFailure, Is.TypeOf<InvalidOperationException>());
            Assert.That(liveNodes.ShutdownAndWait(2000), Is.True);
        }

        [Test]
        public void PartialLearnedNodeFailureRefreshesThroughRemainingLiveNodeTest()
        {
            var handler = new DiscoveryHttpMessageHandler(
                new Dictionary<string, string>
                {
                    ["entrypoint.test"] = "[\"live-one.example.com\",\"live-two.example.com\"]",
                    ["live-two.example.com"] = "[\"live-two.example.com\",\"live-three.example.com\"]",
                },
                new HashSet<string> { "live-one.example.com" });
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                handler.RequestedUris.Select(uri => uri.Host),
                Is.EqualTo(new[]
                {
                    "entrypoint.test",
                    "live-one.example.com",
                    "live-two.example.com",
                    "entrypoint.test",
                }));
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[]
                {
                    "live-two.example.com",
                    "live-three.example.com",
                    "live-one.example.com",
                }));
        }

        [Test]
        public void ActiveSessionReresolvesDnsAndUsesChangedAnswersTest()
        {
            var firstAddress = IPAddress.Parse("127.0.0.2");
            var secondAddress = IPAddress.Parse("127.0.0.3");
            var failedLearnedAddress = IPAddress.Parse("127.0.0.250");
            using var server = new LocalNodesServer(
                3,
                request => request.LocalAddress.Equals(firstAddress)
                    ? $"[\"{failedLearnedAddress}\"]"
                    : request.LocalAddress.Equals(failedLearnedAddress)
                        ? LocalNodesServer.Http500
                        : "[\"learned-new.example.com\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => new[] { firstAddress },
                () => new[] { secondAddress });
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { failedLearnedAddress.ToString() }));

                InvokeUpdateLiveNodes(liveNodes);
                server.WaitForRequests();

                Assert.That(liveNodes.ResolutionCount, Is.EqualTo(2));
                Assert.That(
                    server.Requests.Select(request => request.LocalAddress),
                    Is.EqualTo(new[] { firstAddress, failedLearnedAddress, secondAddress }));
                Assert.That(
                    server.Requests.Select(request => request.Host),
                    Is.EqualTo(new[]
                    {
                        $"entrypoint.test:{server.Port}",
                        $"{failedLearnedAddress}:{server.Port}",
                        $"entrypoint.test:{server.Port}",
                    }));
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { "learned-new.example.com", failedLearnedAddress.ToString() }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void FailedDnsResolutionRetainsSeedAndLaterResolutionRecoversTest()
        {
            using var server = new LocalNodesServer(1, _ => "[\"learned.example.com\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => throw new SocketException((int)SocketError.HostNotFound),
                () => new[] { IPAddress.Loopback });
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                Assert.That(
                    liveNodes.getLiveNodes(),
                    Is.EqualTo(new[] { new Uri($"http://entrypoint.test:{server.Port}") }));

                InvokeUpdateLiveNodes(liveNodes);
                server.WaitForRequests();

                Assert.That(liveNodes.ResolutionCount, Is.EqualTo(2));
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { "learned.example.com" }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void EmptyDnsAnswerRetainsSeedAndLaterResolutionRecoversTest()
        {
            using var server = new LocalNodesServer(1, _ => "[\"learned.example.com\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => Array.Empty<IPAddress>(),
                () => new[] { IPAddress.Loopback });
            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                Assert.That(
                    liveNodes.getLiveNodes(),
                    Is.EqualTo(new[] { new Uri($"http://entrypoint.test:{server.Port}") }));

                InvokeUpdateLiveNodes(liveNodes);
                server.WaitForRequests();

                Assert.That(liveNodes.ResolutionCount, Is.EqualTo(2));
                Assert.That(
                    liveNodes.getLiveNodes().Select(node => node.Host),
                    Is.EqualTo(new[] { "learned.example.com" }));
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public void OverlappingRefreshesPublishOnlyCompleteNodeSetsTest()
        {
            var handler = new ConcurrentDiscoveryHttpMessageHandler(
                "[\"node-a1.example.com\",\"node-a2.example.com\"]",
                "[\"node-b1.example.com\",\"node-b2.example.com\"]");
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);
            var firstRefresh = Task.Run(() => InvokeUpdateLiveNodes(liveNodes));
            var secondRefresh = Task.Run(() => InvokeUpdateLiveNodes(liveNodes));
            Assert.That(handler.WaitForRequests(TimeSpan.FromSeconds(5)), Is.True);

            using var stopObserver = new CancellationTokenSource();
            var snapshots = new ConcurrentQueue<string>();
            var observer = Task.Run(() =>
            {
                while (!stopObserver.IsCancellationRequested)
                {
                    snapshots.Enqueue(string.Join(",", liveNodes.getLiveNodes().Select(node => node.Host)));
                    Thread.Yield();
                }
            });
            Assert.That(
                SpinWait.SpinUntil(() => snapshots.Count >= 10, TimeSpan.FromSeconds(5)),
                Is.True);

            handler.ReleaseRequests();
            Assert.That(Task.WaitAll(new[] { firstRefresh, secondRefresh }, TimeSpan.FromSeconds(5)), Is.True);
            stopObserver.Cancel();
            Assert.That(observer.Wait(TimeSpan.FromSeconds(5)), Is.True);

            var allowedSnapshots = new HashSet<string>
            {
                "entrypoint.test",
                "node-a1.example.com,node-a2.example.com",
                "node-b1.example.com,node-b2.example.com",
            };
            Assert.That(snapshots, Is.Not.Empty);
            Assert.That(snapshots, Is.All.Matches<string>(allowedSnapshots.Contains));
            Assert.That(
                string.Join(",", liveNodes.getLiveNodes().Select(node => node.Host)),
                Is.AnyOf(
                    "node-a1.example.com,node-a2.example.com",
                    "node-b1.example.com,node-b2.example.com"));
        }

        [Test]
        public void OlderRefreshCannotOverwriteNewerCompletedGenerationTest()
        {
            var handler = new OutOfOrderDiscoveryHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            var olderRefresh = Task.Run(() => InvokeUpdateLiveNodes(liveNodes));
            Assert.That(handler.WaitForFirstRequest(TimeSpan.FromSeconds(5)), Is.True);
            var newerRefresh = Task.Run(() => InvokeUpdateLiveNodes(liveNodes));
            Assert.That(handler.WaitForSecondRequest(TimeSpan.FromSeconds(5)), Is.True);

            handler.ReleaseSecondRequest();
            Assert.That(newerRefresh.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "new.example.com" }));

            handler.ReleaseFirstRequest();
            Assert.That(olderRefresh.Wait(TimeSpan.FromSeconds(5)), Is.True);
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "new.example.com" }));
        }

        [Test]
        public void OlderRefreshFailureCannotPoisonNewerGenerationHealthTest()
        {
            var handler = new OutOfOrderDiscoveryHttpMessageHandler(firstRequestFails: true);
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("entrypoint.test")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);
            var seed = new Uri("http://entrypoint.test:8000");

            var olderRefresh = Task.Run(() => InvokeUpdateLiveNodes(liveNodes));
            Assert.That(handler.WaitForFirstRequest(TimeSpan.FromSeconds(5)), Is.True);
            var newerRefresh = Task.Run(() => InvokeUpdateLiveNodes(liveNodes));
            Assert.That(handler.WaitForSecondRequest(TimeSpan.FromSeconds(5)), Is.True);

            handler.ReleaseSecondRequest();
            Assert.That(newerRefresh.Wait(TimeSpan.FromSeconds(5)), Is.True);
            handler.ReleaseFirstRequest();
            Assert.That(olderRefresh.Wait(TimeSpan.FromSeconds(5)), Is.True);

            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(new[] { seed }));
            Assert.That(liveNodes.getActiveNodes(), Is.EqualTo(new[] { seed }));
            Assert.That(liveNodes.getDownNodes(), Is.Empty);
        }

        [Test]
        public void FailedRefreshKeepsLastLearnedNodesTest()
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
        public void MalformedSuccessfulResponseDoesNotReviveDownNodeTest()
        {
            var handler = new TrackingHttpMessageHandler("not-json");
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);
            var seed = new Uri("http://127.0.0.1:8000");
            liveNodes.reportNodeResult(seed, NodeHealthObservation.ConnectionFailure);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(liveNodes.getDownNodes(), Is.EqualTo(new[] { seed }));
            Assert.That(liveNodes.getQuarantinedNodes(), Is.Empty);
            Assert.That(liveNodes.getLiveNodes(), Is.EqualTo(new[] { seed }));
        }

        [TestCase("")]
        [TestCase(" ")]
        [TestCase("bad..name")]
        [TestCase(".bad.example")]
        [TestCase("-bad.example")]
        [TestCase("bad-.example")]
        [TestCase("user@example.com")]
        [TestCase("example.com/path")]
        [TestCase("example.com?query")]
        [TestCase("example.com#fragment")]
        [TestCase("example.com\\path")]
        [TestCase("example.com:8000")]
        [TestCase("[::1]")]
        [TestCase("999.999.999.999")]
        [TestCase("127.1")]
        [TestCase("2130706433")]
        [TestCase("0177.0.0.1")]
        [TestCase("127.00.0.1")]
        [TestCase("0x7f.0.0.1")]
        [TestCase("0x7f000001")]
        [TestCase("127.0.0.1.")]
        [TestCase("１２７.０.０.１")]
        [TestCase("１２７。０。０。１")]
        [TestCase("::ffff:127.1")]
        public void ConfigRejectsNonHostSeedSyntaxTest(string invalidHost)
        {
            Assert.Throws<ArgumentException>(() => AlternatorConfig.builder()
                .withSeedHost(invalidHost)
                .withScheme("http")
                .withPort(8000)
                .build());
        }

        [TestCase("127.0.0.1")]
        [TestCase("0.0.0.0")]
        [TestCase("255.255.255.255")]
        [TestCase("::1")]
        [TestCase("::ffff:192.0.2.1")]
        [TestCase("fe80::1%1")]
        [TestCase("fe80::1%lo")]
        [TestCase("example.com.")]
        [TestCase("münich.example")]
        [TestCase("localhost")]
        [TestCase("123.example")]
        [TestCase("0x7f.example")]
        [TestCase("node-127-0-0-1.example")]
        public void ConfigPreservesValidIpDnsIdnAndZoneSeedHostsTest(string host)
        {
            var config = AlternatorConfig.builder()
                .withSeedHost(host)
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            using var pollingHttpClient = new HttpClient(new TrackingHttpMessageHandler());

            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            Assert.That(liveNodes.getLiveNodes(), Has.Count.EqualTo(1));
        }

        [TestCase("http://user@example.com:8000/")]
        [TestCase("http://example.com:8000/path")]
        [TestCase("http://example.com:8000/?query")]
        [TestCase("http://example.com:8000/#fragment")]
        public void SeedUriRejectsNonAuthorityComponentsTest(string seed)
        {
            Assert.Throws<ArgumentException>(() => AlternatorConfig.builder()
                .withSeedNode(new Uri(seed))
                .build());
        }

        [TestCase("http://127.1:8000/")]
        [TestCase("http://2130706433:8000/")]
        [TestCase("http://0177.0.0.1:8000/")]
        [TestCase("http://0x7f000001:8000/")]
        [TestCase("http://127.0.0.1.:8000/")]
        public void SeedUriRejectsLegacyIpv4AliasesBeforeUriCanonicalizationTest(string seed)
        {
            Assert.Throws<ArgumentException>(() => AlternatorConfig.builder()
                .withSeedNode(seed)
                .build());
            Assert.Throws<ArgumentException>(() => AlternatorConfig.builder()
                .withSeedNode(new Uri(seed))
                .build());
        }

        [TestCase("127.1")]
        [TestCase("2130706433")]
        [TestCase("0177.0.0.1")]
        [TestCase("0x7f.0.0.1")]
        [TestCase("0x7f000001")]
        [TestCase("127.0.0.1.")]
        [TestCase("１２７.０.０.１")]
        public void LocalNodesRejectsLegacyIpv4AliasesTest(string alias)
        {
            var handler = new TrackingHttpMessageHandler(JsonSerializer.Serialize(new[] { alias }));
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("seed.example.com")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "seed.example.com" }));
        }

        [TestCase("http://[::1]:8000/")]
        [TestCase("http://[fe80::1%251]:8000/")]
        [TestCase("http://münich.example.:8000/")]
        public void SeedUriPreservesValidIpv6ZoneAndIdnAuthoritiesTest(string seed)
        {
            var config = AlternatorConfig.builder()
                .withSeedNode(new Uri(seed))
                .withRoutingScope(ClusterScope.create())
                .build();
            using var pollingHttpClient = new HttpClient(new TrackingHttpMessageHandler());

            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            Assert.That(liveNodes.getLiveNodes(), Has.Count.EqualTo(1));
        }

        [Test]
        public void LegacyUriListConstructorRejectsNonAuthorityNodesTest()
        {
            Assert.Throws<ArgumentException>(() => _ = new AlternatorLiveNodes(
                new List<Uri> { new Uri("http://example.com:8000/path") },
                "http",
                8000,
                string.Empty,
                string.Empty));
        }

        [Test]
        public void InvalidDnsLabelFromOneSeedFallsThroughToLaterSeedTest()
        {
            var handler = new DiscoveryHttpMessageHandler(new Dictionary<string, string>
            {
                ["bad-seed.example.com"] = "[\"bad..name\"]",
                ["good-seed.example.com"] = "[\"valid.example.com\"]",
            });
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHosts(new[] { "bad-seed.example.com", "good-seed.example.com" })
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(DatacenterScope.of("dc1", null))
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(
                handler.RequestedUris.Select(uri => uri.Host),
                Is.EqualTo(new[] { "bad-seed.example.com", "good-seed.example.com" }));
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "valid.example.com" }));
        }

        [Test]
        public void InvalidUtf8FromFirstDnsAddressFallsThroughToLaterAddressTest()
        {
            using var server = new LocalNodesServer(
                2,
                request => request.LocalAddress.Equals(IPAddress.Parse("127.0.0.2"))
                    ? LocalNodesServer.HttpInvalidUtf8
                    : "[\"valid.example.com\"]");
            var config = AlternatorConfig.builder()
                .withSeedHost("logical.test")
                .withScheme("http")
                .withPort(server.Port)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AddressMappedLiveNodes(
                config,
                () => new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Loopback });

            InvokeUpdateLiveNodes(liveNodes);
            server.WaitForRequests();

            Assert.That(
                server.Requests.Select(request => request.LocalAddress),
                Is.EqualTo(new[] { IPAddress.Parse("127.0.0.2"), IPAddress.Loopback }));
            Assert.That(
                liveNodes.getLiveNodes().Select(node => node.Host),
                Is.EqualTo(new[] { "valid.example.com" }));
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

            Assert.That(handler.SendCount, Is.EqualTo(3));
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
        public void ShutdownCancelsInFlightDiscoveryRequestTest()
        {
            var handler = new CancellableBlockingHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .withActiveRefreshIntervalMs(10)
                .withIdleRefreshIntervalMs(10000)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            liveNodes.start().GetAwaiter().GetResult();
            _ = liveNodes.nextAsURI();
            Assert.That(handler.WaitForRequest(TimeSpan.FromSeconds(5)), Is.True);

            var stopwatch = Stopwatch.StartNew();
            Assert.That(liveNodes.ShutdownAndWait(2000), Is.True);
            stopwatch.Stop();

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
            Assert.That(handler.WaitForCancellation(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(liveNodes.isRunning(), Is.False);
        }

        [Test]
        public void ShutdownCancelsSynchronousFeatureDiscoveryTest()
        {
            var handler = new CancellableBlockingHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);
            var featureCheck = Task.Run(() =>
                Assert.Throws<AlternatorLiveNodes.FailedToCheck>(() =>
                    liveNodes.CheckIfRoutingScopeFeatureIsSupported()));

            Assert.That(handler.WaitForRequest(TimeSpan.FromSeconds(5)), Is.True);

            liveNodes.Shutdown();

            Assert.That(featureCheck.Wait(TimeSpan.FromSeconds(2)), Is.True);
            Assert.That(handler.WaitForCancellation(TimeSpan.FromSeconds(1)), Is.True);
        }

        [Test]
        public void DiscoveryTimeoutCancelsStalledResponseBodyTest()
        {
            var handler = new BlockingBodyHttpMessageHandler();
            using var pollingHttpClient = new HttpClient(handler);
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .withHttpClientTimeoutMs(100)
                .build();
            var liveNodes = new AlternatorLiveNodes(config, pollingHttpClient);

            var stopwatch = Stopwatch.StartNew();
            InvokeUpdateLiveNodes(liveNodes);
            stopwatch.Stop();

            Assert.That(stopwatch.Elapsed, Is.LessThan(TimeSpan.FromSeconds(2)));
            Assert.That(handler.WaitForBodyRead(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(handler.WaitForCancellation(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(
                liveNodes.getLiveNodes(),
                Is.EqualTo(new[] { new Uri("http://127.0.0.1:8000") }));
        }

        [Test]
        public void ShutdownIsTerminalAndRejectsTransportReuseTest()
        {
            var config = AlternatorConfig.builder()
                .withSeedHost("127.0.0.1")
                .withScheme("http")
                .withPort(8000)
                .withRoutingScope(ClusterScope.create())
                .build();
            var liveNodes = new AlternatorLiveNodes(config);

            Assert.That(liveNodes.ShutdownAndWait(0), Is.True);

            Assert.Throws<ObjectDisposedException>(() => liveNodes.Start());
            Assert.Throws<ObjectDisposedException>(() => liveNodes.NextAsUri());
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
                        return handler.SendCount >= 3;
                    },
                    TimeSpan.FromSeconds(5)),
                Is.True);

            liveNodes.shutdownAndWait();
        }

        [Test]
        public async Task DynamoDbSdkRequestStartsDeferredDiscoveryBeforeIdleRefreshTest()
        {
            using var server = new LocalNodesServer(
                2,
                request =>
                {
                    if (request.Target.StartsWith("/localnodes", StringComparison.Ordinal))
                    {
                        Thread.Sleep(100);
                        return "[\"127.0.0.1\"]";
                    }

                    return "{\"TableNames\":[]}";
                },
                IPAddress.Loopback);
            using var wrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride($"http://127.0.0.1:{server.Port}")
                .withRoutingScope(DatacenterScope.of("dc1", null))
                .withActiveRefreshIntervalMs(5000)
                .withIdleRefreshIntervalMs(60000)
                .ConfigureAws(config =>
                {
                    config.MaxErrorRetry = 0;
                    config.Timeout = TimeSpan.FromSeconds(5);
                })
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();

            Exception? firstFailure = null;
            try
            {
                await wrapper.getClient().ListTablesAsync();
            }
            catch (Exception e)
            {
                firstFailure = e;
            }

            Assert.That(firstFailure, Is.TypeOf<InvalidOperationException>());
            Assert.That(
                SpinWait.SpinUntil(() => wrapper.getLiveNodes().Count == 1, TimeSpan.FromSeconds(5)),
                Is.True);

            var response = await wrapper.getClient().ListTablesAsync();
            server.WaitForRequests();

            Assert.That(response.TableNames, Is.Empty);
            Assert.That(
                server.Requests.Select(request => request.Target),
                Is.EqualTo(new[] { "/localnodes?dc=dc1", "/" }));
        }

        [Test]
        public async Task DynamoDbSdkRequestWakesSeedRecoveryWhenAllPublishedNodesAreDownTest()
        {
            using var server = new LocalNodesServer(
                2,
                request =>
                {
                    if (request.Target.StartsWith("/localnodes", StringComparison.Ordinal))
                    {
                        Thread.Sleep(100);
                        return "[\"127.0.0.1\"]";
                    }

                    return "{\"TableNames\":[]}";
                },
                IPAddress.Loopback);
            using var wrapper = AlternatorDynamoDBClient.builder()
                .endpointOverride($"http://127.0.0.1:{server.Port}")
                .withRoutingScope(ClusterScope.create())
                .withActiveRefreshIntervalMs(5000)
                .withIdleRefreshIntervalMs(60000)
                .ConfigureAws(config =>
                {
                    config.MaxErrorRetry = 0;
                    config.Timeout = TimeSpan.FromSeconds(5);
                })
                .WithoutValidation()
                .WithDeferredStart()
                .buildWithAlternatorAPI();
            var liveNodes = wrapper.getAlternatorLiveNodes();
            liveNodes.reportNodeResult(
                new Uri($"http://127.0.0.1:{server.Port}"),
                NodeHealthObservation.ConnectionFailure);

            Exception? firstFailure = null;
            try
            {
                await wrapper.getClient().ListTablesAsync();
            }
            catch (Exception e)
            {
                firstFailure = e;
            }

            Assert.That(firstFailure, Is.TypeOf<InvalidOperationException>());
            Assert.That(
                SpinWait.SpinUntil(() => liveNodes.getQuarantinedNodes().Count == 1, TimeSpan.FromSeconds(5)),
                Is.True);

            var response = await wrapper.getClient().ListTablesAsync();
            server.WaitForRequests();

            Assert.That(response.TableNames, Is.Empty);
            Assert.That(
                server.Requests.Select(request => request.Target),
                Is.EqualTo(new[] { "/localnodes", "/" }));
        }

        [Test]
        public void RequestDuringRefreshCooldownSchedulesDeferredUpdateTest()
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

            liveNodes.start().GetAwaiter().GetResult();
            _ = liveNodes.nextAsURI();
            Assert.That(
                SpinWait.SpinUntil(() => handler.SendCount == 1, TimeSpan.FromSeconds(5)),
                Is.True);

            _ = liveNodes.nextAsURI();

            Assert.That(
                SpinWait.SpinUntil(() => handler.SendCount >= 3, TimeSpan.FromSeconds(2)),
                Is.True);
            Assert.That(liveNodes.shutdownAndWait(), Is.True);
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

        private static IReadOnlyList<Uri> InvokeMergePartialClusterSnapshot(
            IReadOnlyList<Uri> discoveredNodes,
            IReadOnlyList<Uri> lastKnownGoodNodes)
        {
            var method = typeof(AlternatorLiveNodes).GetMethod(
                "MergePartialClusterSnapshot",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return method!.Invoke(null, new object[] { discoveredNodes, lastKnownGoodNodes }) as IReadOnlyList<Uri>
                ?? throw new InvalidOperationException("MergePartialClusterSnapshot did not return a node list.");
        }

        private static Uri InvokeNextAsUriWithoutRefresh(AlternatorLiveNodes liveNodes)
        {
            var method = typeof(AlternatorLiveNodes).GetMethod(
                "NextAsUriWithoutRefresh",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,
                Type.EmptyTypes,
                null);
            Assert.That(method, Is.Not.Null);
            try
            {
                return method!.Invoke(liveNodes, Array.Empty<object>()) as Uri
                    ?? throw new InvalidOperationException("NextAsUriWithoutRefresh did not return a URI.");
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        private static void InjectFaultedAddressClient(
            AlternatorLiveNodes liveNodes,
            IPAddress address,
            Uri logicalUri)
        {
            var field = typeof(AlternatorLiveNodes).GetField(
                "addressPollingHttpClients",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            var clients = field!.GetValue(liveNodes) as
                Dictionary<(IPAddress Address, string Authority), Lazy<HttpClient>>;
            Assert.That(clients, Is.Not.Null);
            var cacheKey = (
                Address: address,
                Authority: logicalUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
            clients![cacheKey] = new Lazy<HttpClient>(
                () => throw new IOException("simulated transient client-construction failure"));
        }

        private static HttpResponseMessage JsonResponse(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
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

        private static X509Certificate2 CreateLogicalHostServerCertificate(string hostname)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                $"CN={hostname}",
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            subjectAlternativeNames.AddDnsName(hostname);
            request.CertificateExtensions.Add(subjectAlternativeNames.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign,
                    true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var generated = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            return new X509Certificate2(generated.Export(X509ContentType.Pkcs12));
        }

        private static async Task<TlsRequestRecord> ServeTlsLocalNodesOnceAsync(
            TcpListener listener,
            X509Certificate2 certificate,
            HttpStatusCode status,
            string responseBody)
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            string? serverName = null;
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificateSelectionCallback = (_, requestedName) =>
                {
                    serverName = requestedName;
                    return certificate;
                },
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            });

            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            _ = await reader.ReadLineAsync();
            var host = string.Empty;
            string? header;
            while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
            {
                if (header.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    host = header.Substring("Host:".Length).Trim();
                }
            }

            var body = Encoding.UTF8.GetBytes(responseBody);
            var reason = status == HttpStatusCode.OK ? "OK" : "Service Unavailable";
            var responseHeaders = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(int)status} {reason}\r\n"
                + "Content-Type: application/json\r\n"
                + $"Content-Length: {body.Length}\r\n"
                + "Connection: close\r\n\r\n");
            await stream.WriteAsync(responseHeaders);
            await stream.WriteAsync(body);
            await stream.FlushAsync();
            return new TlsRequestRecord(host, serverName);
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

        private sealed class MutableRoutingScope : RoutingScope
        {
            internal MutableRoutingScope(string query)
            {
                this.LocalNodesQuery = query;
            }

            public string Name => "Mutable";

            public string Description => "Mutable test routing scope";

            public RoutingScope? Fallback { get; set; }

            public string LocalNodesQuery { get; }
        }

        private sealed class FeatureProbeHttpMessageHandler : HttpMessageHandler
        {
            private readonly string failingHost;

            internal FeatureProbeHttpMessageHandler(string failingHost)
            {
                this.failingHost = failingHost;
            }

            internal List<Uri> RequestedUris { get; } = new List<Uri>();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var uri = request.RequestUri
                    ?? throw new InvalidOperationException("Request URI was not set.");
                this.RequestedUris.Add(uri);
                if (uri.Host == this.failingHost)
                {
                    throw new HttpRequestException("simulated feature-probe failure");
                }

                return Task.FromResult(JsonResponse(
                    uri.Query == "?rack=fakeRack"
                        ? "[]"
                        : "[\"healthy.example.com\"]"));
            }
        }

        private sealed class TimeoutThenHealthySeedHttpMessageHandler : HttpMessageHandler
        {
            private readonly ISet<string> timedOutHosts;

            internal TimeoutThenHealthySeedHttpMessageHandler(ISet<string> timedOutHosts)
            {
                this.timedOutHosts = timedOutHosts;
            }

            internal List<string> RequestedHosts { get; } = new List<string>();

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var host = request.RequestUri?.Host
                    ?? throw new InvalidOperationException("Request URI was not set.");
                this.RequestedHosts.Add(host);
                if (this.timedOutHosts.Contains(host))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Infinite timeout completed without cancellation.");
                }

                return JsonResponse("[\"healthy-node.example.com\"]");
            }
        }

        private sealed class ScopedTimeoutHttpMessageHandler : HttpMessageHandler
        {
            internal List<Uri> RequestedUris { get; } = new List<Uri>();

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var uri = request.RequestUri
                    ?? throw new InvalidOperationException("Request URI was not set.");
                this.RequestedUris.Add(uri);
                if (!string.IsNullOrEmpty(uri.Query))
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Infinite timeout completed without cancellation.");
                }

                return JsonResponse("[\"fallback-node.example.com\"]");
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

        private sealed class SequenceDiscoveryHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<Func<HttpResponseMessage>> responses;

            internal SequenceDiscoveryHttpMessageHandler(params Func<HttpResponseMessage>[] responses)
            {
                this.responses = new Queue<Func<HttpResponseMessage>>(responses);
            }

            internal List<Uri> RequestedUris { get; } = new List<Uri>();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                this.RequestedUris.Add(
                    request.RequestUri ?? throw new InvalidOperationException("Request URI was not set."));
                if (this.responses.Count == 0)
                {
                    throw new InvalidOperationException("No simulated discovery response remains.");
                }

                return Task.FromResult(this.responses.Dequeue()());
            }
        }

        private sealed class ConcurrentDiscoveryHttpMessageHandler : HttpMessageHandler
        {
            private readonly string[] responseBodies;
            private readonly CountdownEvent requestsStarted = new CountdownEvent(2);
            private readonly ManualResetEventSlim releaseRequests = new ManualResetEventSlim();
            private int requestIndex;

            internal ConcurrentDiscoveryHttpMessageHandler(params string[] responseBodies)
            {
                this.responseBodies = responseBodies;
            }

            internal bool WaitForRequests(TimeSpan timeout)
            {
                return this.requestsStarted.Wait(timeout);
            }

            internal void ReleaseRequests()
            {
                this.releaseRequests.Set();
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var index = Interlocked.Increment(ref this.requestIndex) - 1;
                this.requestsStarted.Signal();
                this.releaseRequests.Wait(cancellationToken);
                if (index >= this.responseBodies.Length)
                {
                    throw new InvalidOperationException("Unexpected concurrent discovery request.");
                }

                return Task.FromResult(JsonResponse(this.responseBodies[index]));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.requestsStarted.Dispose();
                    this.releaseRequests.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private sealed class OutOfOrderDiscoveryHttpMessageHandler : HttpMessageHandler
        {
            private readonly ManualResetEventSlim firstRequestStarted = new ManualResetEventSlim();
            private readonly ManualResetEventSlim secondRequestStarted = new ManualResetEventSlim();
            private readonly ManualResetEventSlim releaseFirstRequest = new ManualResetEventSlim();
            private readonly ManualResetEventSlim releaseSecondRequest = new ManualResetEventSlim();
            private readonly bool firstRequestFails;
            private int requestIndex;

            internal OutOfOrderDiscoveryHttpMessageHandler(bool firstRequestFails = false)
            {
                this.firstRequestFails = firstRequestFails;
            }

            internal bool WaitForFirstRequest(TimeSpan timeout)
            {
                return this.firstRequestStarted.Wait(timeout);
            }

            internal bool WaitForSecondRequest(TimeSpan timeout)
            {
                return this.secondRequestStarted.Wait(timeout);
            }

            internal void ReleaseFirstRequest()
            {
                this.releaseFirstRequest.Set();
            }

            internal void ReleaseSecondRequest()
            {
                this.releaseSecondRequest.Set();
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var index = Interlocked.Increment(ref this.requestIndex) - 1;
                if (index == 0)
                {
                    this.firstRequestStarted.Set();
                    this.releaseFirstRequest.Wait(cancellationToken);
                    if (this.firstRequestFails)
                    {
                        throw new HttpRequestException("simulated stale refresh failure");
                    }

                    return Task.FromResult(JsonResponse("[\"old.example.com\"]"));
                }

                if (index == 1)
                {
                    this.secondRequestStarted.Set();
                    this.releaseSecondRequest.Wait(cancellationToken);
                    return Task.FromResult(JsonResponse(
                        this.firstRequestFails
                            ? "[\"entrypoint.test\"]"
                            : "[\"new.example.com\"]"));
                }

                throw new InvalidOperationException("Unexpected discovery request.");
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.firstRequestStarted.Dispose();
                    this.secondRequestStarted.Dispose();
                    this.releaseFirstRequest.Dispose();
                    this.releaseSecondRequest.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private sealed class CancellableBlockingHttpMessageHandler : HttpMessageHandler
        {
            private readonly ManualResetEventSlim requestStarted = new ManualResetEventSlim();
            private readonly ManualResetEventSlim requestCanceled = new ManualResetEventSlim();

            internal bool WaitForRequest(TimeSpan timeout)
            {
                return this.requestStarted.Wait(timeout);
            }

            internal bool WaitForCancellation(TimeSpan timeout)
            {
                return this.requestCanceled.Wait(timeout);
            }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                this.requestStarted.Set();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Infinite delay completed without cancellation.");
                }
                finally
                {
                    this.requestCanceled.Set();
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.requestStarted.Dispose();
                    this.requestCanceled.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private sealed class BlockingBodyHttpMessageHandler : HttpMessageHandler
        {
            private readonly ManualResetEventSlim bodyRead = new ManualResetEventSlim();
            private readonly ManualResetEventSlim bodyReadCanceled = new ManualResetEventSlim();

            internal bool WaitForBodyRead(TimeSpan timeout)
            {
                return this.bodyRead.Wait(timeout);
            }

            internal bool WaitForCancellation(TimeSpan timeout)
            {
                return this.bodyReadCanceled.Wait(timeout);
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(
                        new TimeoutObservingBodyStream(this.bodyRead, this.bodyReadCanceled)),
                });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.bodyRead.Dispose();
                    this.bodyReadCanceled.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private sealed class TimeoutObservingBodyStream : Stream
        {
            private readonly ManualResetEventSlim bodyRead;
            private readonly ManualResetEventSlim bodyReadCanceled;

            internal TimeoutObservingBodyStream(
                ManualResetEventSlim bodyRead,
                ManualResetEventSlim bodyReadCanceled)
            {
                this.bodyRead = bodyRead;
                this.bodyReadCanceled = bodyReadCanceled;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                this.bodyRead.Set();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    this.bodyReadCanceled.Set();
                    throw;
                }
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }
        }

        private sealed class AddressMappedLiveNodes : AlternatorLiveNodes
        {
            private readonly Queue<Func<IReadOnlyList<IPAddress>>> resolutions;
            private IReadOnlyList<IPAddress>? lastResolution;

            internal AddressMappedLiveNodes(
                AlternatorConfig config,
                params Func<IReadOnlyList<IPAddress>>[] resolutions)
                : base(config, enableDnsAddressFallback: true)
            {
                this.resolutions = new Queue<Func<IReadOnlyList<IPAddress>>>(resolutions);
            }

            internal int ResolutionCount { get; private set; }

            protected override IReadOnlyList<IPAddress> ResolveHostAddresses(
                string host,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                this.ResolutionCount++;
                if (this.resolutions.Count != 0)
                {
                    this.lastResolution = this.resolutions.Dequeue()();
                }

                return this.lastResolution
                    ?? throw new InvalidOperationException("No simulated DNS resolution remains for " + host);
            }
        }

        private sealed class TlsRequestRecord
        {
            internal TlsRequestRecord(string host, string? serverName)
            {
                this.Host = host;
                this.ServerName = serverName;
            }

            internal string Host { get; }

            internal string? ServerName { get; }
        }

        private sealed class BlackholeServer : IDisposable
        {
            private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
            private readonly List<TcpListener> listeners = new List<TcpListener>();
            private readonly List<Task> serverTasks = new List<Task>();
            private int acceptedConnections;

            internal BlackholeServer(int port, params IPAddress[] addresses)
            {
                foreach (var address in addresses)
                {
                    var listener = new TcpListener(address, port);
                    listener.Start();
                    this.listeners.Add(listener);
                    this.serverTasks.Add(Task.Run(() => this.RunAsync(listener)));
                }
            }

            internal int AcceptedConnections => Volatile.Read(ref this.acceptedConnections);

            public void Dispose()
            {
                this.shutdown.Cancel();
                foreach (var listener in this.listeners)
                {
                    listener.Stop();
                }

                Task.WaitAll(this.serverTasks.ToArray(), TimeSpan.FromSeconds(5));
                this.shutdown.Dispose();
            }

            private async Task RunAsync(TcpListener listener)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync(this.shutdown.Token);
                    Interlocked.Increment(ref this.acceptedConnections);
                    await Task.Delay(Timeout.InfiniteTimeSpan, this.shutdown.Token);
                }
                catch (OperationCanceledException) when (this.shutdown.IsCancellationRequested)
                {
                }
                catch (SocketException) when (this.shutdown.IsCancellationRequested)
                {
                }
            }
        }

        private sealed class LocalNodesServer : IDisposable
        {
            internal const string Http500 = "__HTTP_500__";
            internal const string HttpDelayedValid = "__HTTP_DELAYED_VALID__";
            internal const string HttpInvalidUtf8 = "__HTTP_INVALID_UTF8__";
            internal const string HttpRedirect = "__HTTP_REDIRECT__";
            internal const string HttpTruncated = "__HTTP_TRUNCATED__";

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
                var localAddress = ((IPEndPoint?)client.Client.LocalEndPoint)?.Address
                    ?? throw new InvalidOperationException("Accepted connection has no local endpoint.");
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
                var request = new RequestRecord(target, query, host, connection, localAddress);
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

                if (body == HttpRedirect)
                {
                    var redirectResponse = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 302 Found\r\n"
                        + "Location: /redirected\r\n"
                        + "Content-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(redirectResponse);
                    return;
                }

                if (body == HttpTruncated)
                {
                    var truncatedResponse = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n"
                        + "Content-Type: application/json\r\n"
                        + "Content-Length: 100\r\nConnection: close\r\n\r\n[");
                    await stream.WriteAsync(truncatedResponse);
                    return;
                }

                if (body == HttpInvalidUtf8)
                {
                    var invalidBody = Encoding.ASCII.GetBytes("[\"bad?.example\"]");
                    invalidBody[5] = 0xff;
                    var invalidResponse = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n"
                        + "Content-Type: application/json\r\n"
                        + "Content-Length: "
                        + invalidBody.Length
                        + "\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(invalidResponse);
                    await stream.WriteAsync(invalidBody);
                    return;
                }

                if (body == HttpDelayedValid)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(150));
                    body = "[\"healthy-node.example.com\"]";
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
            internal RequestRecord(
                string target,
                string query,
                string host,
                string connection,
                IPAddress localAddress)
            {
                this.Target = target;
                this.Query = query;
                this.Host = host;
                this.Connection = connection;
                this.LocalAddress = localAddress;
            }

            internal string Target { get; }

            internal string Query { get; }

            internal string Host { get; }

            internal string Connection { get; }

            internal IPAddress LocalAddress { get; }
        }
    }
}
