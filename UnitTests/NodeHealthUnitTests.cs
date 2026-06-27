// <copyright file="NodeHealthUnitTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.Net;
    using ScyllaDB.Alternator.Routing;

    [TestFixture]
    [Category("Unit")]
    public class NodeHealthUnitTests
    {
        [Test]
        public void ConnectionFailureMarksNodeDownAndSuccessPromotesThroughQuarantineTest()
        {
            using var pollingHttpClient = CreatePollingClient();
            var liveNodes = CreateLiveNodes(
                new[] { "node1.example.com" },
                builder => builder.withQuarantineSuccessThreshold(2),
                pollingHttpClient);
            var node = Node("node1.example.com");

            liveNodes.reportNodeResult(node, NodeHealthObservation.ConnectionFailure);

            Assert.That(liveNodes.getActiveNodes(), Is.Empty);
            Assert.That(liveNodes.getQuarantinedNodes(), Is.Empty);
            Assert.That(liveNodes.getDownNodes(), Is.EqualTo(new[] { node }));
            Assert.That(liveNodes.getNodeStatus(node) !.State, Is.EqualTo(NodeHealthState.Down));

            liveNodes.reportNodeResult(node, NodeHealthObservation.Success);

            Assert.That(liveNodes.getQuarantinedNodes(), Is.EqualTo(new[] { node }));
            Assert.That(liveNodes.getNodeStatus(node) !.ConsecutiveSuccesses, Is.EqualTo(1));

            liveNodes.reportNodeResult(node, NodeHealthObservation.Success);

            Assert.That(liveNodes.getActiveNodes(), Is.EqualTo(new[] { node }));
            Assert.That(liveNodes.getQuarantinedNodes(), Is.Empty);
            Assert.That(liveNodes.getNodeStatus(node) !.State, Is.EqualTo(NodeHealthState.Active));
        }

        [Test]
        public void ServerErrorsRespectThresholdAndRequestTimeoutIsNeutralTest()
        {
            using var pollingHttpClient = CreatePollingClient();
            var liveNodes = CreateLiveNodes(
                new[] { "node1.example.com" },
                builder => builder.withConsecutiveServerErrorThreshold(2),
                pollingHttpClient);
            var node = Node("node1.example.com");

            liveNodes.reportNodeResult(node, NodeHealthObservation.ServerError);
            liveNodes.reportNodeResult(node, NodeHealthObservation.RequestTimeout);

            var activeStatus = liveNodes.getNodeStatus(node) !;
            Assert.That(activeStatus.State, Is.EqualTo(NodeHealthState.Active));
            Assert.That(activeStatus.ConsecutiveServerErrors, Is.EqualTo(1));

            liveNodes.reportNodeResult(node, NodeHealthObservation.ServerError);

            var downStatus = liveNodes.getNodeStatus(node) !;
            Assert.That(downStatus.State, Is.EqualTo(NodeHealthState.Down));
            Assert.That(downStatus.ConsecutiveServerErrors, Is.EqualTo(2));
        }

        [Test]
        public void DisabledNodeHealthKeepsEveryKnownNodeActiveTest()
        {
            using var pollingHttpClient = CreatePollingClient();
            var liveNodes = CreateLiveNodes(
                new[] { "node1.example.com", "node2.example.com" },
                builder => builder.withDisabled(true),
                pollingHttpClient);
            var node = Node("node1.example.com");

            liveNodes.reportNodeResult(node, NodeHealthObservation.ConnectionFailure);
            liveNodes.reportNodeResult(node, NodeHealthObservation.ServerError);

            Assert.That(
                liveNodes.getActiveNodes(),
                Is.EqualTo(new[] { Node("node1.example.com"), Node("node2.example.com") }));
            Assert.That(liveNodes.getQuarantinedNodes(), Is.Empty);
            Assert.That(liveNodes.getDownNodes(), Is.Empty);
        }

        [Test]
        public void NextAsUriUsesActiveNodesAndSamplesQuarantinedNodesTest()
        {
            using var pollingHttpClient = CreatePollingClient();
            var liveNodes = CreateLiveNodes(
                new[] { "node1.example.com", "node2.example.com" },
                builder => builder
                    .withQuarantineTrafficInterval(2)
                    .withQuarantineSuccessThreshold(3),
                pollingHttpClient);
            var activeNode = Node("node1.example.com");
            var quarantinedNode = Node("node2.example.com");
            liveNodes.reportNodeResult(quarantinedNode, NodeHealthObservation.ConnectionFailure);
            liveNodes.reportNodeResult(quarantinedNode, NodeHealthObservation.Success);

            Assert.That(liveNodes.nextAsURI(), Is.EqualTo(activeNode));
            Assert.That(liveNodes.nextAsURI(), Is.EqualTo(quarantinedNode));
            Assert.That(liveNodes.nextAsURI(), Is.EqualTo(activeNode));
        }

        [Test]
        public void NextAsUriUsesQuarantinedNodeWhenNoActiveNodesExistTest()
        {
            using var pollingHttpClient = CreatePollingClient();
            var liveNodes = CreateLiveNodes(
                new[] { "node1.example.com" },
                builder => builder.withQuarantineSuccessThreshold(3),
                pollingHttpClient);
            var node = Node("node1.example.com");
            liveNodes.reportNodeResult(node, NodeHealthObservation.ConnectionFailure);
            liveNodes.reportNodeResult(node, NodeHealthObservation.Success);

            Assert.That(liveNodes.getActiveNodes(), Is.Empty);
            Assert.That(liveNodes.getQuarantinedNodes(), Is.EqualTo(new[] { node }));
            Assert.That(liveNodes.nextAsURI(), Is.EqualTo(node));
        }

        [Test]
        public async Task ProbeDownNodesMovesResponsiveNodeToQuarantineTest()
        {
            var probeHandler = new FuncHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[\"node1.example.com\"]"),
                }));
            using var pollingHttpClient = new HttpClient(probeHandler);
            var liveNodes = CreateLiveNodes(
                new[] { "node1.example.com" },
                builder => builder.withQuarantineSuccessThreshold(3),
                pollingHttpClient);
            var node = Node("node1.example.com");
            liveNodes.reportNodeResult(node, NodeHealthObservation.ConnectionFailure);

            var probed = await liveNodes.probeDownNodesAsync();

            Assert.That(probed, Is.EqualTo(new[] { node }));
            Assert.That(liveNodes.getQuarantinedNodes(), Is.EqualTo(new[] { node }));
            Assert.That(liveNodes.getNodeStatus(node) !.ConsecutiveSuccesses, Is.EqualTo(1));
            Assert.That(probeHandler.Requests.Single().AbsolutePath, Is.EqualTo("/localnodes"));
        }

        [Test]
        public void DiscoveryRemovesMissingDiscoveredNodesFromHealthStoreTest()
        {
            var discoveryHandler = new QueueHttpMessageHandler(new[]
            {
                "[\"node2.example.com\"]",
                "[\"node3.example.com\"]",
            });
            using var pollingHttpClient = new HttpClient(discoveryHandler);
            var liveNodes = CreateLiveNodes(new[] { "node1.example.com" }, null, pollingHttpClient);

            InvokeUpdateLiveNodes(liveNodes);
            Assert.That(liveNodes.getNodeStatus(Node("node2.example.com")), Is.Not.Null);

            InvokeUpdateLiveNodes(liveNodes);

            Assert.That(liveNodes.getNodeStatus(Node("node2.example.com")), Is.Null);
            Assert.That(liveNodes.getNodeStatus(Node("node3.example.com")), Is.Not.Null);
            Assert.That(
                liveNodes.getLiveNodes(),
                Is.EqualTo(new[] { Node("node3.example.com"), Node("node1.example.com") }));
        }

        [Test]
        public async Task HttpReportingHandlerClassifiesQuickServerErrorAndTimeoutShapedServerErrorTest()
        {
            using var pollingHttpClient = CreatePollingClient();
            var quickLiveNodes = CreateLiveNodes(
                new[] { "node1.example.com" },
                builder => builder
                    .withConsecutiveServerErrorThreshold(2)
                    .withServerRequestTimeoutThresholdMs(1000),
                pollingHttpClient);
            using var quickClient = CreateHealthReportingClient(
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
                quickLiveNodes);

            using var quickResponse = await quickClient.GetAsync(Node("node1.example.com"));

            Assert.That(quickResponse.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            Assert.That(quickLiveNodes.getNodeStatus(Node("node1.example.com")) !.ConsecutiveServerErrors, Is.EqualTo(1));

            using var timeoutPollingHttpClient = CreatePollingClient();
            var timeoutLiveNodes = CreateLiveNodes(
                new[] { "node1.example.com" },
                builder => builder
                    .withConsecutiveServerErrorThreshold(1)
                    .withServerRequestTimeoutThresholdMs(10),
                timeoutPollingHttpClient);
            using var timeoutClient = CreateHealthReportingClient(
                async _ =>
                {
                    await Task.Delay(30);
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                },
                timeoutLiveNodes);

            using var timeoutResponse = await timeoutClient.GetAsync(Node("node1.example.com"));

            Assert.That(timeoutResponse.StatusCode, Is.EqualTo(HttpStatusCode.InternalServerError));
            var timeoutStatus = timeoutLiveNodes.getNodeStatus(Node("node1.example.com")) !;
            Assert.That(timeoutStatus.State, Is.EqualTo(NodeHealthState.Active));
            Assert.That(timeoutStatus.ConsecutiveServerErrors, Is.EqualTo(0));
        }

        [Test]
        public void AlternatorConfigBuilderNormalizesNodeHealthThresholdsTest()
        {
            var nodeHealth = NodeHealthStoreConfig.builder()
                .withConsecutiveServerErrorThreshold(0)
                .withQuarantineSuccessThreshold(0)
                .withDownNodeProbePeriodMs(0)
                .withQuarantineTrafficInterval(0)
                .withServerRequestTimeoutThresholdMs(0)
                .disabled()
                .build();
            var config = AlternatorConfig.builder()
                .withNodeHealth(nodeHealth)
                .build();

            Assert.That(config.getNodeHealth().ConsecutiveServerErrorThreshold, Is.EqualTo(1));
            Assert.That(config.getNodeHealth().QuarantineSuccessThreshold, Is.EqualTo(1));
            Assert.That(config.getNodeHealth().DownNodeProbePeriodMs, Is.EqualTo(1));
            Assert.That(config.getNodeHealth().QuarantineTrafficInterval, Is.EqualTo(1));
            Assert.That(config.getNodeHealth().ServerRequestTimeoutThresholdMs, Is.EqualTo(0));
            Assert.That(config.getNodeHealth().Disabled, Is.True);
        }

        private static AlternatorLiveNodes CreateLiveNodes(
            IEnumerable<string> hosts,
            Action<NodeHealthStoreConfigBuilder>? configureNodeHealth,
            HttpClient pollingHttpClient)
        {
            var builder = AlternatorConfig.builder()
                .withSeedHosts(hosts)
                .withScheme("http")
                .withPort(8043)
                .withRoutingScope(ClusterScope.create());
            if (configureNodeHealth != null)
            {
                var nodeHealthBuilder = NodeHealthStoreConfig.builder();
                configureNodeHealth(nodeHealthBuilder);
                builder.withNodeHealth(nodeHealthBuilder.build());
            }

            return new AlternatorLiveNodes(builder.build(), pollingHttpClient);
        }

        private static HttpClient CreatePollingClient()
        {
            return new HttpClient(new FuncHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[]"),
                })));
        }

        private static HttpClient CreateHealthReportingClient(
            Func<HttpRequestMessage, Task<HttpResponseMessage>> responseFactory,
            AlternatorLiveNodes liveNodes)
        {
            var innerHandler = new FuncHttpMessageHandler((request, _) => responseFactory(request));
            var handler = CreateHealthReportingHandler(innerHandler, liveNodes);
            return new HttpClient(handler);
        }

        private static HttpMessageHandler CreateHealthReportingHandler(
            HttpMessageHandler innerHandler,
            AlternatorLiveNodes liveNodes)
        {
            var handlerType = typeof(AlternatorConfig).Assembly.GetType("ScyllaDB.Alternator.NodeHealthReportingHttpMessageHandler");
            Assert.That(handlerType, Is.Not.Null);
            var config = GetConfig(liveNodes);
            var value = Activator.CreateInstance(
                handlerType!,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,
                new object[] { innerHandler, liveNodes, config.NodeHealth },
                null);
            Assert.That(value, Is.Not.Null);
            return (HttpMessageHandler)value!;
        }

        private static AlternatorConfig GetConfig(AlternatorLiveNodes liveNodes)
        {
            var field = typeof(AlternatorLiveNodes).GetField(
                "config",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            var value = field!.GetValue(liveNodes);
            Assert.That(value, Is.Not.Null);
            return (AlternatorConfig)value!;
        }

        private static void InvokeUpdateLiveNodes(AlternatorLiveNodes liveNodes)
        {
            var method = typeof(AlternatorLiveNodes).GetMethod(
                "UpdateLiveNodes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method!.Invoke(liveNodes, Array.Empty<object>());
        }

        private static Uri Node(string host)
        {
            return new Uri($"http://{host}:8043");
        }

        private sealed class FuncHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory;

            internal FuncHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory)
            {
                this.responseFactory = responseFactory;
            }

            internal List<Uri> Requests { get; } = new List<Uri>();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri != null)
                {
                    this.Requests.Add(request.RequestUri);
                }

                return this.responseFactory(request, cancellationToken);
            }
        }

        private sealed class QueueHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<string> responses;

            internal QueueHttpMessageHandler(IEnumerable<string> responses)
            {
                this.responses = new Queue<string>(responses);
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (this.responses.Count == 0)
                {
                    throw new InvalidOperationException("No queued discovery response.");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(this.responses.Dequeue()),
                });
            }
        }
    }
}
