// <copyright file="UnitTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
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
        public void UpdateLiveNodesAppendsSeedNodesToDiscoveredNodesTest()
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
                Is.EqualTo(new[] { "127.0.0.2", "127.0.0.1" }));
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
                Is.EqualTo(new[] { "dc1-rack1-node.example.com", "dc2-node1.example.com", "dc1-node1.example.com" }));
            Assert.That(
                handler.RequestedUris.Select(uri => uri.Host),
                Is.EqualTo(new[] { "dc2-node1.example.com", "dc1-node1.example.com" }));
            Assert.That(handler.RequestedUris.Select(uri => uri.AbsolutePath), Is.All.EqualTo("/localnodes"));
            Assert.That(handler.RequestedUris.Select(uri => uri.Query), Is.All.EqualTo("?dc=dc1&rack=rack1"));
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

            Assert.That(hosts, Is.EquivalentTo(new[] { "127.0.0.1", "127.0.0.2", "127.0.0.3" }));
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
                Is.EqualTo(new[] { "127.0.0.5", "127.0.0.1" }));
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
                Is.EqualTo(new[] { "127.0.0.2", "127.0.0.1" }));
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

        private sealed class TrackingHttpMessageHandler : HttpMessageHandler
        {
            private int disposeCount;
            private int sendCount;

            internal int DisposeCount => this.disposeCount;

            internal int SendCount => this.sendCount;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref this.sendCount);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[\"127.0.0.2\"]", Encoding.UTF8, "application/json"),
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

        private sealed class LocalNodesServer : IDisposable
        {
            internal const string Http500 = "__HTTP_500__";

            private readonly TcpListener listener;
            private readonly int expectedRequests;
            private readonly Func<RequestRecord, string?> responseBody;
            private readonly Task serverTask;
            private bool disposed;

            internal LocalNodesServer(int expectedRequests, Func<RequestRecord, string?> responseBody)
            {
                this.expectedRequests = expectedRequests;
                this.responseBody = responseBody;
                this.listener = new TcpListener(IPAddress.Any, 0);
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
