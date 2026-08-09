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
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Net.Sockets;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Amazon.Runtime.Endpoints;
    using ScyllaDB.Alternator.KeyRouting;
    using ScyllaDB.Alternator.Routing;

    public class AlternatorLiveNodes
    {
        private const long DefaultShutdownTimeoutMs = 5000;
        private const int MaxCachedDnsEndpoints = 64;
        private const int MaxDiscoveredNodesPerCycle = 4096;
        private const int MaxAggregateLocalNodesResponseBytes = 1024 * 1024;
        private const int MaxLiveNodeSnapshotBytes = 1024 * 1024;
        private const int MaxRoutingScopeDepth = 32;
        private const int MaxLocalNodesResponseBytes = 1024 * 1024;
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        private readonly string alternatorScheme;
        private readonly int alternatorPort;
        private readonly ReaderWriterLockSlim liveNodesLock = new ReaderWriterLockSlim();
        private readonly List<Uri> initialNodes;
        private readonly AlternatorConfig config;
        private readonly HttpClient pollingHttpClient;
        private readonly bool ownsPollingHttpClient;
        private readonly bool enableDnsAddressFallback;
        private readonly Dictionary<(IPAddress Address, string Authority), Lazy<HttpClient>> addressPollingHttpClients =
            new Dictionary<(IPAddress Address, string Authority), Lazy<HttpClient>>();

        private readonly object addressPollingHttpClientsLock = new object();
        private readonly NodeHealthStore healthStore;
        private readonly object lifecycleLock = new object();
        private readonly object refreshGenerationLock = new object();
        private readonly object updateSignalLock = new object();
        private readonly CancellationTokenSource shutdownCancellation = new CancellationTokenSource();
        private List<Uri> liveNodes;
        private int nextLiveNodeIndex;
        private int nextQuarantinedNodeIndex;
        private int nextQuarantineTrafficSequence;
        private long nextRequestRefreshTicks;
        private long lastDownNodeProbeTicks;
        private int updateRequested;
        private bool started;
        private CancellationTokenSource? refreshCancellation;
        private Task? refreshTask;
        private int pollingHttpClientClosed;
        private int refreshThreadId;
        private volatile bool shutdownRequested;
        private long nextRefreshGeneration;
        private long publishedRefreshGeneration;
        private RoutingScope? publishedRoutingScope;

        public AlternatorLiveNodes(Uri liveNode, string datacenter, string rack)
            : this(CreateLegacyConfig(new List<Uri> { liveNode }, liveNode.Scheme, liveNode.Port, datacenter, rack))
        {
        }

        public AlternatorLiveNodes(Uri liveNode, RoutingScope? routingScope)
            : this(AlternatorConfig.Builder()
                .WithSeedNode(liveNode)
                .WithRoutingScope(routingScope)
                .Build())
        {
        }

        public AlternatorLiveNodes(Uri seedUri, AlternatorConfig config)
            : this(CreateConfigWithSeedUri(seedUri, config))
        {
        }

        public AlternatorLiveNodes(List<Uri> nodes, string scheme, int port, string datacenter, string rack)
            : this(CreateLegacyConfig(nodes, scheme, port, datacenter, rack))
        {
        }

        public AlternatorLiveNodes(List<string> seeds, string scheme, int port, RoutingScope? routingScope)
            : this(AlternatorConfig.Builder()
                .WithSeedHosts(seeds)
                .WithScheme(scheme)
                .WithPort(port)
                .WithRoutingScope(routingScope)
                .Build())
        {
        }

        public AlternatorLiveNodes(AlternatorConfig config)
            : this(config, CreatePollingHttpClient(config), true, true)
        {
        }

        public AlternatorLiveNodes(AlternatorConfig config, HttpClient pollingHttpClient)
            : this(config, pollingHttpClient, false, false)
        {
        }

        protected AlternatorLiveNodes(AlternatorConfig config, bool enableDnsAddressFallback)
            : this(config, CreatePollingHttpClient(config), true, enableDnsAddressFallback)
        {
        }

        private AlternatorLiveNodes(
            AlternatorConfig config,
            HttpClient pollingHttpClient,
            bool ownsPollingHttpClient,
            bool enableDnsAddressFallback)
        {
            ValidateConfigurationBeforeTransport(config);

            if (pollingHttpClient == null)
            {
                throw new SystemException("pollingHttpClient cannot be null");
            }

            this.config = config;
            this.pollingHttpClient = pollingHttpClient;
            this.ownsPollingHttpClient = ownsPollingHttpClient;
            this.enableDnsAddressFallback = enableDnsAddressFallback;
            this.alternatorScheme = config.Scheme;
            this.alternatorPort = config.Port;
            try
            {
                this.initialNodes = config.SeedHosts.Select(this.HostToUri).ToList();
            }
            catch (UriFormatException e)
            {
                throw new SystemException("Invalid host in seed configuration", e);
            }

            // Datacenter and rack seeds are discovery-only until /localnodes
            // proves that returned application nodes belong to the requested
            // scope. A cluster scope anywhere in the configured fallback chain
            // explicitly authorizes the unfiltered seeds for application routing.
            var seedRoutingScope = GetRoutingScopeChain(this.config.RoutingScope)
                .FirstOrDefault(IsClusterScope);
            this.liveNodes = seedRoutingScope == null
                ? new List<Uri>()
                : this.initialNodes.ToList();
            this.publishedRoutingScope = seedRoutingScope;
            this.healthStore = new NodeHealthStore(this.config.NodeHealth, this.liveNodes);

            try
            {
                this.Validate();
            }
            catch (ValidationError e)
            {
                throw new SystemException(e.Message, e);
            }
        }

        public Task Start(CancellationToken cancellationToken = default)
        {
            lock (this.lifecycleLock)
            {
                this.ThrowIfShutdown();
                if (this.started)
                {
                    return Task.CompletedTask;
                }

                this.Validate();
                this.refreshCancellation?.Dispose();
                var refreshSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                this.refreshCancellation = refreshSource;
                this.started = true;

                this.refreshTask = Task.Run(
                    () => this.UpdateCycle(refreshSource.Token),
                    CancellationToken.None);
                return Task.CompletedTask;
            }
        }

        public void Run(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource completion;
            CancellationTokenSource refreshSource;
            lock (this.lifecycleLock)
            {
                this.ThrowIfShutdown();
                if (this.started)
                {
                    return;
                }

                this.Validate();
                this.refreshCancellation?.Dispose();
                refreshSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                this.refreshCancellation = refreshSource;
                completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                this.refreshTask = completion.Task;
                this.started = true;
            }

            try
            {
                this.UpdateCycle(refreshSource.Token);
                completion.TrySetResult();
            }
            catch (Exception e)
            {
                completion.TrySetException(e);
                throw;
            }
        }

        public void Shutdown()
        {
            CancellationTokenSource? cancellation;
            Task? task;
            lock (this.lifecycleLock)
            {
                this.shutdownRequested = true;
                this.started = false;
                cancellation = this.refreshCancellation;
                task = this.refreshTask;
                this.shutdownCancellation.Cancel();
                cancellation?.Cancel();
            }

            this.SignalUpdateWaiters();
            if (task == null || task.IsCompleted)
            {
                this.ClosePollingHttpClient();
            }
        }

        public bool ShutdownAndWait()
        {
            return this.ShutdownAndWait(DefaultShutdownTimeoutMs);
        }

        public bool ShutdownAndWait(long timeoutMs)
        {
            this.Shutdown();
            Task? task;
            lock (this.lifecycleLock)
            {
                task = this.refreshTask;
            }

            if (task == null)
            {
                return true;
            }

            if (Volatile.Read(ref this.refreshThreadId) == Environment.CurrentManagedThreadId
                || (Task.CurrentId.HasValue && task.Id == Task.CurrentId.Value))
            {
                return false;
            }

            try
            {
                return task.Wait(TimeSpan.FromMilliseconds(Math.Max(0, timeoutMs))) || task.IsCompleted;
            }
            catch (AggregateException)
            {
                return true;
            }
        }

        public void Stop()
        {
            this.Shutdown();
        }

        public bool IsRunning()
        {
            lock (this.lifecycleLock)
            {
                return this.started && this.refreshTask?.IsCompleted != true;
            }
        }

        public void Validate()
        {
            try
            {
                // Make sure that `alternatorScheme` and `alternatorPort` are correct values
                this.HostToUri("1.1.1.1");
                _ = GetRoutingScopeChain(this.config.RoutingScope);
            }
            catch (Exception e) when (e is UriFormatException or IOException)
            {
                throw new ValidationError($"failed to validate configuration: {e.Message}", e);
            }
        }

        public void ValidateUri(Uri uri)
        {
            if (uri == null)
            {
                throw new ValidationError("URI cannot be null");
            }

            if (!uri.IsAbsoluteUri)
            {
                throw new ValidationError("Invalid URI: " + uri);
            }
        }

        public Uri NextAsUri()
        {
            this.MarkActivity();
            return this.SelectNextUri();
        }

        public IReadOnlyList<Uri> GetLiveNodes()
        {
            this.liveNodesLock.EnterReadLock();
            try
            {
                return this.liveNodes.ToList().AsReadOnly();
            }
            finally
            {
                this.liveNodesLock.ExitReadLock();
            }
        }

        public IReadOnlyList<Uri> GetActiveNodes()
        {
            return this.GetActiveNodesInternal().ToList().AsReadOnly();
        }

        public IReadOnlyList<Uri> GetQuarantinedNodes()
        {
            return this.GetQuarantinedNodesInternal().ToList().AsReadOnly();
        }

        public IReadOnlyList<Uri> GetDownNodes()
        {
            return this.GetDownNodesInternal().ToList().AsReadOnly();
        }

        public NodeHealthStatus? GetNodeStatus(Uri node)
        {
            return this.healthStore.GetNodeStatus(node);
        }

        public void ReportNodeResult(Uri node, NodeHealthObservation observation)
        {
            this.healthStore.ReportNodeResult(node, observation);
        }

        public async Task<IReadOnlyList<Uri>> ProbeDownNodesAsync(CancellationToken cancellationToken = default)
        {
            this.ThrowIfShutdown();
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                this.shutdownCancellation.Token);
            return await this.ProbeDownNodesInternalAsync(linkedCancellation.Token).ConfigureAwait(false);
        }

        public Uri NextAsUri(string? path, string? query)
        {
            Uri uri = this.NextAsUri();
            return BuildUri(uri, path, query);
        }

        public void CheckIfRackAndDatacenterSetCorrectly()
        {
            this.ThrowIfShutdown();
            var scope = this.config.RoutingScope;
            if (string.IsNullOrEmpty(scope.LocalNodesQuery))
            {
                return;
            }

            List<Uri> nodes;
            try
            {
                nodes = this.GetNodesForScope(scope);
            }
            catch (Exception e)
            {
                throw new FailedToCheck("failed to read list of nodes from the node", e);
            }

            if (nodes.Count == 0)
            {
                throw new ValidationError(
                    $"node returned empty list for {scope.Description}, routing scope may be set incorrectly");
            }
        }

        public bool CheckIfRackDatacenterFeatureIsSupported()
        {
            return this.CheckIfRoutingScopeFeatureIsSupported();
        }

        public bool CheckIfRoutingScopeFeatureIsSupported()
        {
            this.ThrowIfShutdown();
            using var discoveryContext = this.CreateDiscoveryContext(CancellationToken.None);
            var candidates = this.GetDiscoveryCandidates();
            Exception? lastException = null;
            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                var candidate = candidates[candidateIndex];
                var uri = BuildUri(candidate.Node, "/localnodes", null);
                var fakeRackUrl = BuildUri(candidate.Node, "/localnodes", "rack=fakeRack");
                var remainingCandidateRequests = (int)Math.Min(
                    int.MaxValue,
                    (long)(candidates.Count - candidateIndex - 1) * 2);
                try
                {
                    var hostsWithFakeRack = this.GetNodes(
                        fakeRackUrl,
                        discoveryContext,
                        AddReservedAttempts(remainingCandidateRequests, 1),
                        null);
                    var hostsWithoutRack = this.GetNodes(
                        uri,
                        discoveryContext,
                        remainingCandidateRequests,
                        null);
                    if (hostsWithoutRack.Nodes.Count == 0)
                    {
                        throw new IOException($"host {uri} returned empty list");
                    }

                    // When rack filtering is not supported server returns same nodes.
                    return hostsWithFakeRack.Nodes.Count != hostsWithoutRack.Nodes.Count;
                }
                catch (OperationCanceledException e) when (discoveryContext.CancellationToken.IsCancellationRequested)
                {
                    throw new FailedToCheck("routing-scope feature discovery was canceled", e);
                }
                catch (FatalDiscoveryException e)
                {
                    lastException = e;
                    break;
                }
                catch (Exception e)
                {
                    Logger.Warn(e, $"Failed to probe routing-scope support through {candidate.Description} {candidate.Node}");
                    lastException = e;
                }
            }

            throw new FailedToCheck(
                "failed to read list of nodes from any discovery candidate",
                lastException ?? new IOException("No discovery nodes available"));
        }

        public RoutingScope GetRoutingScope()
        {
            return this.config.RoutingScope;
        }

#pragma warning disable SA1300, IDE1006
        public Task start()
        {
            return this.Start();
        }

        public void run()
        {
            this.Run();
        }

        public void shutdown()
        {
            this.Shutdown();
        }

        public bool shutdownAndWait()
        {
            return this.ShutdownAndWait();
        }

        public bool shutdownAndWait(long timeoutMs)
        {
            return this.ShutdownAndWait(timeoutMs);
        }

        public void stop()
        {
            this.Stop();
        }

        public bool isRunning()
        {
            return this.IsRunning();
        }

        public void validate()
        {
            this.Validate();
        }

        public void validateURI(Uri uri)
        {
            this.ValidateUri(uri);
        }

        public Uri nextAsURI()
        {
            return this.NextAsUri();
        }

        public Uri nextAsURI(string? path, string? query)
        {
            return this.NextAsUri(path, query);
        }

        public void checkIfRackAndDatacenterSetCorrectly()
        {
            this.CheckIfRackAndDatacenterSetCorrectly();
        }

        public bool checkIfRackDatacenterFeatureIsSupported()
        {
            return this.CheckIfRackDatacenterFeatureIsSupported();
        }

        public RoutingScope getRoutingScope()
        {
            return this.GetRoutingScope();
        }

        public IReadOnlyList<Uri> getLiveNodes()
        {
            return this.GetLiveNodes();
        }

        public IReadOnlyList<Uri> getActiveNodes()
        {
            return this.GetActiveNodes();
        }

        public IReadOnlyList<Uri> getQuarantinedNodes()
        {
            return this.GetQuarantinedNodes();
        }

        public IReadOnlyList<Uri> getDownNodes()
        {
            return this.GetDownNodes();
        }

        public NodeHealthStatus? getNodeStatus(Uri node)
        {
            return this.GetNodeStatus(node);
        }

        public void reportNodeResult(Uri node, NodeHealthObservation observation)
        {
            this.ReportNodeResult(node, observation);
        }

        public Task<IReadOnlyList<Uri>> probeDownNodesAsync()
        {
            return this.ProbeDownNodesAsync();
        }
#pragma warning restore SA1300, IDE1006

        internal Uri NextAsUriWithoutRefresh()
        {
            return this.SelectNextUri();
        }

        internal Uri NextAsUriWithoutRefresh(string? path, string? query)
        {
            Uri uri = this.SelectNextUri();
            return BuildUri(uri, path, query);
        }

        internal void MarkRequestActivity()
        {
            this.MarkActivity();
        }

        internal Uri GetNodeForHash(long hash)
        {
            var nodes = this.GetActiveNodesInternal();
            if (nodes.Count == 0)
            {
                nodes = this.GetQuarantinedNodesInternal();
            }

            if (nodes.Count == 0)
            {
                throw new InvalidOperationException("No live nodes available");
            }

            var index = Mod(hash, nodes.Count);
            return nodes[index];
        }

        internal LazyQueryPlan CreateQueryPlan(long seed)
        {
            return new LazyQueryPlan(this, seed);
        }

        internal LazyQueryPlan CreateQueryPlan(IEnumerable<Uri> preferredNodes)
        {
            return new LazyQueryPlan(this, preferredNodes);
        }

        internal LazyQueryPlan CreateQueryPlan()
        {
            return new LazyQueryPlan(this);
        }

        protected internal virtual IReadOnlyList<Uri> GetLiveNodesInternal()
        {
            this.liveNodesLock.EnterReadLock();
            try
            {
                return this.liveNodes;
            }
            finally
            {
                this.liveNodesLock.ExitReadLock();
            }
        }

        protected internal virtual IReadOnlyList<Uri> GetActiveNodesInternal()
        {
            return this.healthStore.GetActiveNodes();
        }

        protected internal virtual IReadOnlyList<Uri> GetQuarantinedNodesInternal()
        {
            return this.healthStore.GetQuarantinedNodes();
        }

        protected internal virtual IReadOnlyList<Uri> GetDownNodesInternal()
        {
            return this.healthStore.GetDownNodes();
        }

#pragma warning disable SA1300, IDE1006
        protected internal virtual IReadOnlyList<Uri> getLiveNodesInternal()
        {
            return this.GetLiveNodesInternal();
        }

        protected internal virtual IReadOnlyList<Uri> getActiveNodesInternal()
        {
            return this.GetActiveNodesInternal();
        }

        protected internal virtual IReadOnlyList<Uri> getQuarantinedNodesInternal()
        {
            return this.GetQuarantinedNodesInternal();
        }

        protected internal virtual IReadOnlyList<Uri> getDownNodesInternal()
        {
            return this.GetDownNodesInternal();
        }
#pragma warning restore SA1300, IDE1006

        protected virtual IReadOnlyList<IPAddress> ResolveHostAddresses(
            string host,
            CancellationToken cancellationToken)
        {
            var timeoutMs = this.config.ConnectionTimeoutMs > 0
                ? this.config.ConnectionTimeoutMs
                : AlternatorConfig.DefaultConnectionTimeoutMs;
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            try
            {
                return Dns.GetHostAddressesAsync(host, cancellation.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException e)
            {
                throw new IOException($"DNS lookup for {host} timed out after {timeoutMs} ms", e);
            }
        }

        private static AlternatorConfig CreateConfigWithSeedUri(Uri seedUri, AlternatorConfig config)
        {
            if (config == null)
            {
                throw new SystemException("config cannot be null");
            }

            _ = AlternatorEndpointValidation.ValidateSeedUri(seedUri, nameof(seedUri));

            if (config.SeedHosts.Count != 0)
            {
                return config;
            }

            var builder = AlternatorConfig.Builder()
                .WithSeedNode(seedUri)
                .WithRoutingScope(config.RoutingScope)
                .WithCompressionAlgorithm(config.CompressionAlgorithm)
                .WithMinCompressionSizeBytes(config.MinCompressionSizeBytes)
                .WithResponseCompression(config.ResponseCompressionAlgorithms)
                .WithOptimizeHeaders(config.OptimizeHeaders)
                .WithUserAgentEnabled(config.UserAgentEnabled)
                .WithAuthenticationEnabled(config.AuthenticationEnabled)
                .WithTlsConfig(config.TlsConfig)
                .WithKeyRouteAffinity(config.KeyRouteAffinityConfig)
                .WithActiveRefreshIntervalMs(config.ActiveRefreshIntervalMs)
                .WithIdleRefreshIntervalMs(config.IdleRefreshIntervalMs)
                .WithMaxConnections(config.MaxConnections)
                .WithConnectionMaxIdleTimeMs(config.ConnectionMaxIdleTimeMs)
                .WithConnectionTimeToLiveMs(config.ConnectionTimeToLiveMs)
                .WithConnectionAcquisitionTimeoutMs(config.ConnectionAcquisitionTimeoutMs)
                .WithConnectionTimeoutMs(config.ConnectionTimeoutMs)
                .WithHttpClientTimeoutMs(config.HttpClientTimeoutMs)
                .WithNodeHealth(config.NodeHealth);

            config.CopyHeaderOptimizationTo(builder);
            return builder.Build();
        }

        private static string ReadResponseBody(
            HttpContent content,
            CancellationToken cancellationToken,
            DiscoveryContext discoveryContext)
        {
            if (content.Headers.ContentLength > MaxLocalNodesResponseBytes)
            {
                throw new IOException(
                    $"/localnodes response exceeded {MaxLocalNodesResponseBytes} bytes");
            }

            using var stream = content.ReadAsStreamAsync(cancellationToken).GetAwaiter().GetResult();
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = stream.ReadAsync(buffer.AsMemory(), cancellationToken).AsTask().GetAwaiter().GetResult();
                if (read == 0)
                {
                    break;
                }

                if (body.Length > MaxLocalNodesResponseBytes - read)
                {
                    throw new IOException(
                        $"/localnodes response exceeded {MaxLocalNodesResponseBytes} bytes");
                }

                body.Write(buffer, 0, read);
            }

            var bytes = body.ToArray();
            discoveryContext.RecordResponseBytes(bytes.Length);
            var offset = bytes.Length >= 3
                && bytes[0] == 0xef
                && bytes[1] == 0xbb
                && bytes[2] == 0xbf
                    ? 3
                    : 0;
            try
            {
                return StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException e)
            {
                throw new IOException("/localnodes response was not valid UTF-8", e);
            }
        }

        private static HttpClient CreatePollingHttpClient(AlternatorConfig config)
        {
            ValidateConfigurationBeforeTransport(config);

            var handler = AlternatorHttpClientFactory.CreatePrimaryHandler(config);
            DisableRedirects(handler);
            var client = new HttpClient(handler);
            ConfigurePollingHttpClientTimeout(client, config.HttpClientTimeoutMs);
            return client;
        }

        private static void ConfigurePollingHttpClientTimeout(HttpClient client, long timeoutMs)
        {
            client.Timeout = timeoutMs == 0
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(timeoutMs);
        }

        private static void DisableRedirects(HttpMessageHandler handler)
        {
            if (handler is SocketsHttpHandler socketsHandler)
            {
                socketsHandler.AllowAutoRedirect = false;
            }
            else if (handler is HttpClientHandler clientHandler)
            {
                clientHandler.AllowAutoRedirect = false;
            }
        }

        private static Uri BuildUri(Uri baseUri, string? path, string? query)
        {
            var builder = new UriBuilder(baseUri.Scheme, baseUri.Host, baseUri.Port, path ?? string.Empty);
            if (!string.IsNullOrEmpty(query))
            {
                builder.Query = query;
            }

            return builder.Uri;
        }

        private static int Mod(long value, int divisor)
        {
            return (int)(((value % divisor) + divisor) % divisor);
        }

        private static int AddReservedAttempts(int first, int second)
        {
            return (int)Math.Min(int.MaxValue, (long)Math.Max(0, first) + Math.Max(0, second));
        }

        private static List<Uri> MergePartialClusterSnapshot(
            IReadOnlyList<Uri> discoveredNodes,
            IReadOnlyList<Uri> lastKnownGoodNodes)
        {
            var merged = new List<Uri>();
            var seen = new HashSet<Uri>();
            var snapshotBytes = 2;

            void AppendWithinBounds(IEnumerable<Uri> candidates, bool retainOnOverflow)
            {
                foreach (var node in candidates)
                {
                    if (!seen.Add(node))
                    {
                        continue;
                    }

                    var nodeBytes = StrictUtf8.GetByteCount(node.Host) + 3;
                    if (merged.Count == MaxDiscoveredNodesPerCycle
                        || snapshotBytes > MaxLiveNodeSnapshotBytes - nodeBytes)
                    {
                        seen.Remove(node);
                        if (retainOnOverflow)
                        {
                            continue;
                        }

                        throw new DiscoveryLimitException(
                            "fresh Cluster discovery exceeded the live-node snapshot bound");
                    }

                    merged.Add(node);
                    snapshotBytes += nodeBytes;
                }
            }

            // Freshly confirmed nodes have priority. Retained entries are added
            // only while the same node/byte bounds still have room.
            AppendWithinBounds(discoveredNodes, retainOnOverflow: false);
            AppendWithinBounds(lastKnownGoodNodes, retainOnOverflow: true);
            return merged;
        }

        private static AlternatorConfig CreateLegacyConfig(
            List<Uri> nodes,
            string scheme,
            int port,
            string datacenter,
            string rack)
        {
            if (nodes == null || nodes.Count == 0)
            {
                throw new SystemException("liveNodes cannot be null or empty");
            }

            var builder = AlternatorConfig.Builder()
                .WithSeedHosts(nodes.Select(node =>
                    AlternatorEndpointValidation.ValidateSeedUri(node, nameof(nodes))))
                .WithScheme(scheme)
                .WithPort(port)
                .WithRoutingScope(DeriveRoutingScope(datacenter, rack));
            return builder.Build();
        }

        private static RoutingScope DeriveRoutingScope(string datacenter, string rack)
        {
            var dc = datacenter ?? string.Empty;
            var rackName = rack ?? string.Empty;
            if (string.IsNullOrEmpty(dc))
            {
                return ClusterScope.Create();
            }

            if (string.IsNullOrEmpty(rackName))
            {
                return DatacenterScope.Of(dc, ClusterScope.Create());
            }

            return RackScope.Of(dc, rackName, DatacenterScope.Of(dc, ClusterScope.Create()));
        }

        private static void ValidateConfigurationBeforeTransport(AlternatorConfig? config)
        {
            if (config == null)
            {
                throw new SystemException("config cannot be null");
            }

            if (config.SeedHosts.Count == 0)
            {
                throw new SystemException("seedHosts cannot be empty");
            }

            try
            {
                foreach (var host in config.SeedHosts)
                {
                    _ = AlternatorEndpointValidation.NormalizeHost(host);
                }

                _ = new UriBuilder(config.Scheme, "1.1.1.1", config.Port).Uri;
                _ = GetRoutingScopeChain(config.RoutingScope);
            }
            catch (Exception e) when (e is ArgumentException or UriFormatException or IOException)
            {
                throw new SystemException($"Invalid live-node configuration: {e.Message}", e);
            }
        }

        private static IReadOnlyList<RoutingScope> GetRoutingScopeChain(RoutingScope initialScope)
        {
            var scopes = new List<RoutingScope>();
            var seenScopes = new HashSet<RoutingScope>(ReferenceEqualityComparer.Instance);
            RoutingScope? scope = initialScope;
            while (scope != null)
            {
                if (!seenScopes.Add(scope))
                {
                    throw new IOException("routing scope fallback chain contains a reference cycle");
                }

                if (scopes.Count == MaxRoutingScopeDepth)
                {
                    throw new IOException(
                        $"routing scope fallback chain exceeds {MaxRoutingScopeDepth} entries");
                }

                scopes.Add(scope);
                scope = scope.Fallback;
            }

            return scopes;
        }

        private static bool IsClusterScope(RoutingScope scope)
        {
            return string.IsNullOrEmpty(scope.LocalNodesQuery);
        }

        private DiscoveryContext CreateDiscoveryContext(CancellationToken cancellationToken)
        {
            var configuredTimeoutMs = this.config.HttpClientTimeoutMs > 0
                ? this.config.HttpClientTimeoutMs
                : AlternatorConfig.DefaultHttpClientTimeoutMs;
            var timeoutMs = Math.Min(configuredTimeoutMs, int.MaxValue);
            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                this.shutdownCancellation.Token);
            return new DiscoveryContext(
                linkedCancellation,
                TimeSpan.FromMilliseconds(timeoutMs));
        }

        private async ValueTask<Stream> ConnectToAddress(
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private void UpdateCycle(CancellationToken cancellationToken)
        {
            Volatile.Write(ref this.refreshThreadId, Environment.CurrentManagedThreadId);
            Logger.Debug("AlternatorLiveNodes thread started");
            try
            {
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    if (!this.WaitForRefreshSignalOrIdleInterval(cancellationToken))
                    {
                        return;
                    }

                    this.DeferRequestRefresh();
                    try
                    {
                        this.UpdateLiveNodesCore(cancellationToken);
                        this.ProbeDownNodesIfDue(cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (IOException e)
                    {
                        Logger.Error(e, "AlternatorLiveNodes failed to sync nodes list: %");
                    }
                }
            }
            finally
            {
                CancellationTokenSource? refreshSource;
                lock (this.lifecycleLock)
                {
                    this.started = false;
                    this.shutdownRequested = true;
                    this.shutdownCancellation.Cancel();
                    refreshSource = this.refreshCancellation;
                    this.refreshCancellation = null;
                }

                refreshSource?.Dispose();
                Volatile.Write(ref this.refreshThreadId, 0);
                this.ClosePollingHttpClient();
                Logger.Info("AlternatorLiveNodes thread stopped");
            }
        }

        private void ClosePollingHttpClient()
        {
            if (Interlocked.Exchange(ref this.pollingHttpClientClosed, 1) != 0)
            {
                return;
            }

            List<Lazy<HttpClient>> addressClients;
            lock (this.addressPollingHttpClientsLock)
            {
                addressClients = this.addressPollingHttpClients.Values.ToList();
                this.addressPollingHttpClients.Clear();
            }

            foreach (var client in addressClients)
            {
                if (client.IsValueCreated)
                {
                    client.Value.Dispose();
                }
            }

            if (this.ownsPollingHttpClient)
            {
                this.pollingHttpClient.Dispose();
            }
        }

        private Uri HostToUri(string host)
        {
            try
            {
                var normalizedHost = AlternatorEndpointValidation.NormalizeHost(host);
                return new UriBuilder(this.alternatorScheme, normalizedHost, this.alternatorPort).Uri;
            }
            catch (ArgumentException e)
            {
                throw new UriFormatException("Invalid host URI", e);
            }
        }

        private Uri SelectNextUri()
        {
            var activeNodes = this.GetActiveNodesInternal();
            var quarantinedNode = this.SelectQuarantinedNode(activeNodes.Count == 0);
            if (quarantinedNode != null)
            {
                return quarantinedNode;
            }

            if (activeNodes.Count == 0)
            {
                throw new InvalidOperationException("No live nodes available");
            }

            var sequence = Interlocked.Increment(ref this.nextLiveNodeIndex) - 1;
            var index = Mod(sequence, activeNodes.Count);
            return activeNodes[index];
        }

        private int GetRefreshInterval()
        {
            return checked((int)this.config.IdleRefreshIntervalMs);
        }

        private void MarkActivity()
        {
            var start = false;
            lock (this.lifecycleLock)
            {
                this.ThrowIfShutdown();
                start = this.refreshTask == null;
            }

            if (start)
            {
                this.Start();
            }

            this.TriggerUpdate();
        }

        private void ThrowIfShutdown()
        {
            if (this.shutdownRequested || Volatile.Read(ref this.pollingHttpClientClosed) != 0)
            {
                throw new ObjectDisposedException(
                    nameof(AlternatorLiveNodes),
                    "Live-node discovery has been shut down and cannot be restarted.");
            }
        }

        private void ReportDiscoveryNodeResult(
            Uri node,
            NodeHealthObservation observation,
            long? refreshGeneration)
        {
            if (!refreshGeneration.HasValue)
            {
                this.ReportNodeResult(node, observation);
                return;
            }

            lock (this.refreshGenerationLock)
            {
                if (refreshGeneration.Value == this.nextRefreshGeneration)
                {
                    this.ReportNodeResult(node, observation);
                }
            }
        }

        private void TriggerUpdate()
        {
            if (Interlocked.Exchange(ref this.updateRequested, 1) == 0)
            {
                this.SignalUpdateWaiters();
            }
        }

        private void DeferRequestRefresh()
        {
            var requestedNextRefresh = checked(
                DateTimeOffset.UtcNow.Ticks + TimeSpan.FromMilliseconds(this.config.ActiveRefreshIntervalMs).Ticks);
            Interlocked.Exchange(ref this.nextRequestRefreshTicks, requestedNextRefresh);
        }

        private void SignalUpdateWaiters()
        {
            lock (this.updateSignalLock)
            {
                Monitor.PulseAll(this.updateSignalLock);
            }
        }

        private bool WaitForRefreshSignalOrIdleInterval(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(this.SignalUpdateWaiters);
            lock (this.updateSignalLock)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (Volatile.Read(ref this.updateRequested) == 1)
                    {
                        var now = DateTimeOffset.UtcNow.Ticks;
                        var nextRefresh = Interlocked.Read(ref this.nextRequestRefreshTicks);
                        if (nextRefresh <= now)
                        {
                            Interlocked.Exchange(ref this.updateRequested, 0);
                            return true;
                        }

                        var ticksUntilRefresh = nextRefresh - now;
                        var waitMs = (ticksUntilRefresh + TimeSpan.TicksPerMillisecond - 1)
                            / TimeSpan.TicksPerMillisecond;
                        waitMs = Math.Clamp(waitMs, 1, int.MaxValue);
                        Monitor.Wait(this.updateSignalLock, TimeSpan.FromMilliseconds(waitMs));
                        continue;
                    }

                    if (!Monitor.Wait(this.updateSignalLock, this.GetRefreshInterval()))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool TrySetLiveNodes(
            List<Uri> nodes,
            RoutingScope routingScope,
            long refreshGeneration)
        {
            this.liveNodesLock.EnterWriteLock();
            try
            {
                if (refreshGeneration <= this.publishedRefreshGeneration)
                {
                    return false;
                }

                this.liveNodes = nodes;
                this.healthStore.SetKnownNodes(nodes);
                this.publishedRoutingScope = routingScope;
                this.publishedRefreshGeneration = refreshGeneration;
                return true;
            }
            finally
            {
                this.liveNodesLock.ExitWriteLock();
            }
        }

        private bool TryPublishAuthoritativeEmpty(
            IReadOnlySet<RoutingScope> authoritativeEmptyScopes,
            long refreshGeneration)
        {
            this.liveNodesLock.EnterWriteLock();
            try
            {
                if (refreshGeneration <= this.publishedRefreshGeneration)
                {
                    return false;
                }

                if (this.liveNodes.Count != 0
                    && (this.publishedRoutingScope == null
                        || IsClusterScope(this.publishedRoutingScope)
                        || !authoritativeEmptyScopes.Contains(this.publishedRoutingScope)))
                {
                    return false;
                }

                this.liveNodes = new List<Uri>();
                this.healthStore.SetKnownNodes(this.liveNodes);
                this.publishedRoutingScope = null;
                this.publishedRefreshGeneration = refreshGeneration;
                return true;
            }
            finally
            {
                this.liveNodesLock.ExitWriteLock();
            }
        }

        private void UpdateLiveNodes()
        {
            this.UpdateLiveNodesCore(CancellationToken.None);
        }

        private void UpdateLiveNodesCore(CancellationToken cancellationToken)
        {
            long refreshGeneration;
            lock (this.refreshGenerationLock)
            {
                refreshGeneration = ++this.nextRefreshGeneration;
            }

            using var discoveryContext = this.CreateDiscoveryContext(cancellationToken);
            var scopes = GetRoutingScopeChain(this.config.RoutingScope);
            Exception? lastException = null;
            var authoritativeEmptyScopes = new HashSet<RoutingScope>(ReferenceEqualityComparer.Instance);
            for (var scopeIndex = 0; scopeIndex < scopes.Count; scopeIndex++)
            {
                var scope = scopes[scopeIndex];
                try
                {
                    discoveryContext.ThrowIfUnavailable();
                    var result = this.GetNodesForScope(
                        scope,
                        discoveryContext,
                        refreshGeneration,
                        scopes.Count - scopeIndex - 1);
                    if (result.Nodes.Count != 0)
                    {
                        if (this.TrySetLiveNodes(result.Nodes, scope, refreshGeneration))
                        {
                            Logger.Info($"Updated hosts to {result.Nodes} using {scope.Description}");
                        }
                        else
                        {
                            Logger.Debug(
                                $"Ignored stale refresh generation {refreshGeneration} for {scope.Description}");
                        }

                        return;
                    }

                    if (result.AuthoritativeEmpty)
                    {
                        authoritativeEmptyScopes.Add(scope);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (FatalDiscoveryException e)
                {
                    Logger.Warn(e, "Live-node discovery reached a cycle-wide safety bound; keeping existing node list");
                    return;
                }
                catch (Exception e)
                {
                    Logger.Warn(e, $"Failed to discover nodes for {scope.Description}");
                    lastException = e;
                }

                if (scopeIndex + 1 < scopes.Count)
                {
                    Logger.Warn(
                        $"No nodes found for {scope.Description}; falling back to {scopes[scopeIndex + 1].Description}");
                }
            }

            if (authoritativeEmptyScopes.Count != 0)
            {
                if (this.TryPublishAuthoritativeEmpty(authoritativeEmptyScopes, refreshGeneration))
                {
                    Logger.Info("Cleared hosts after authoritative empty scoped discovery result");
                }
                else
                {
                    Logger.Info(
                        "Authoritative empty scoped discovery did not match the published routing scope; keeping existing node list");
                }

                return;
            }

            if (lastException != null)
            {
                Logger.Warn("All nodes unreachable in every routing scope, keeping existing node list");
                return;
            }

            Logger.Warn("No nodes found in any routing scope, keeping existing node list");
        }

        private List<Uri> GetNodesForScope(RoutingScope scope)
        {
            using var discoveryContext = this.CreateDiscoveryContext(CancellationToken.None);
            return this.GetNodesForScope(scope, discoveryContext, null, 0).Nodes;
        }

        private ScopeDiscoveryResult GetNodesForScope(
            RoutingScope scope,
            DiscoveryContext discoveryContext,
            long? refreshGeneration,
            int remainingScopeCount)
        {
            var query = scope.LocalNodesQuery;
            var requestQuery = string.IsNullOrEmpty(query) ? null : query;
            var candidates = this.GetDiscoveryCandidates();
            var futureScopeAttempts = (int)Math.Min(
                int.MaxValue,
                (long)remainingScopeCount * Math.Max(1, candidates.Count));
            return this.DiscoverNodes(
                scope,
                candidates,
                requestQuery,
                discoveryContext,
                refreshGeneration,
                futureScopeAttempts);
        }

        private List<DiscoveryCandidate> GetDiscoveryCandidates()
        {
            var currentNodes = this.GetLiveNodes();
            var downNodes = new HashSet<Uri>(this.GetDownNodesInternal());
            var candidates = new List<DiscoveryCandidate>();
            var seenCandidates = new HashSet<Uri>();

            void AddCandidates(IEnumerable<Uri> nodes, string description)
            {
                foreach (var node in nodes)
                {
                    if (seenCandidates.Add(node))
                    {
                        candidates.Add(new DiscoveryCandidate(node, description));
                    }
                }
            }

            AddCandidates(currentNodes.Where(node => !downNodes.Contains(node)), "live node");
            AddCandidates(this.initialNodes, "seed node");
            AddCandidates(currentNodes.Where(downNodes.Contains), "down live node");
            return candidates;
        }

        private ScopeDiscoveryResult DiscoverNodes(
            RoutingScope scope,
            IReadOnlyList<DiscoveryCandidate> candidates,
            string? requestQuery,
            DiscoveryContext discoveryContext,
            long? refreshGeneration,
            int futureScopeAttempts)
        {
            Exception? lastException = null;
            var nodes = new List<Uri>();
            var seen = new HashSet<Uri>();
            var failedCandidates = new HashSet<Uri>();
            var sawAuthoritativeEmpty = false;
            var clusterScope = string.IsNullOrEmpty(requestQuery);
            var completeClusterPass = true;
            for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
            {
                discoveryContext.ThrowIfUnavailable();
                var candidate = candidates[candidateIndex];
                var uri = BuildUri(candidate.Node, "/localnodes", requestQuery);
                var reservedAttempts = AddReservedAttempts(
                    futureScopeAttempts,
                    candidates.Count - candidateIndex - 1);
                try
                {
                    var response = this.GetNodes(
                        uri,
                        discoveryContext,
                        reservedAttempts,
                        refreshGeneration);
                    if (response.Nodes.Count == 0)
                    {
                        sawAuthoritativeEmpty |= response.WasEmptyArray;
                        completeClusterPass = false;
                        continue;
                    }

                    if (!clusterScope)
                    {
                        return new ScopeDiscoveryResult(response.Nodes, authoritativeEmpty: false);
                    }

                    foreach (var node in response.Nodes)
                    {
                        if (!failedCandidates.Contains(node) && seen.Add(node))
                        {
                            nodes.Add(node);
                        }
                    }
                }
                catch (OperationCanceledException) when (discoveryContext.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (FatalDiscoveryException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    completeClusterPass = false;
                    failedCandidates.Add(candidate.Node);
                    if (seen.Remove(candidate.Node))
                    {
                        nodes.Remove(candidate.Node);
                    }

                    Logger.Warn(
                        e,
                        $"Failed to contact {candidate.Description} {candidate.Node} for {scope.Description}");
                    lastException = e;
                }
            }

            if (nodes.Count != 0)
            {
                if (clusterScope && !completeClusterPass)
                {
                    nodes = MergePartialClusterSnapshot(nodes, this.GetLiveNodes());
                }

                return new ScopeDiscoveryResult(nodes, authoritativeEmpty: false);
            }

            if (sawAuthoritativeEmpty && !clusterScope)
            {
                return new ScopeDiscoveryResult(new List<Uri>(), authoritativeEmpty: true);
            }

            if (lastException != null)
            {
                throw lastException;
            }

            return new ScopeDiscoveryResult(new List<Uri>(), authoritativeEmpty: false);
        }

        private DiscoveryResponse GetNodes(
            Uri uri,
            DiscoveryContext discoveryContext,
            int reservedAttempts,
            long? refreshGeneration)
        {
            discoveryContext.ThrowIfUnavailable();
            var attemptBudget = discoveryContext.CreateAttemptBudget(reservedAttempts);
            if (!this.enableDnsAddressFallback || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns)
            {
                return this.GetNodes(
                    uri,
                    this.pollingHttpClient,
                    reportNodeHealth: true,
                    discoveryContext,
                    attemptBudget,
                    remainingAttemptOperations: 0,
                    refreshGeneration);
            }

            IReadOnlyList<IPAddress> resolvedAddresses;
            try
            {
                var dnsTimeoutMs = this.config.ConnectionTimeoutMs > 0
                    ? this.config.ConnectionTimeoutMs
                    : AlternatorConfig.DefaultConnectionTimeoutMs;
                using var dnsCancellation = attemptBudget.CreateAttemptCancellation(
                    reservedOperations: 1,
                    TimeSpan.FromMilliseconds(dnsTimeoutMs));
                resolvedAddresses = this.ResolveHostAddresses(uri.Host, dnsCancellation.Token);
            }
            catch (OperationCanceledException) when (discoveryContext.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (FatalDiscoveryException)
            {
                throw;
            }
            catch (Exception e)
            {
                this.ReportDiscoveryNodeResult(
                    uri,
                    NodeHealthObservation.ConnectionFailure,
                    refreshGeneration);
                throw new IOException($"Failed to resolve DNS entrypoint {uri.Host}", e);
            }

            var addresses = resolvedAddresses.Distinct().ToList();
            if (addresses.Count == 0)
            {
                this.ReportDiscoveryNodeResult(
                    uri,
                    NodeHealthObservation.ConnectionFailure,
                    refreshGeneration);
                throw new IOException($"DNS entrypoint {uri.Host} resolved without addresses");
            }

            Exception? lastException = null;
            var sawEmptyResponse = false;
            for (var addressIndex = 0; addressIndex < addresses.Count; addressIndex++)
            {
                discoveryContext.ThrowIfUnavailable();
                var address = addresses[addressIndex];
                try
                {
                    using var clientLease = this.GetAddressPollingHttpClient(uri, address);
                    var response = this.GetNodes(
                        uri,
                        clientLease.Client,
                        reportNodeHealth: false,
                        discoveryContext,
                        attemptBudget,
                        addresses.Count - addressIndex - 1,
                        refreshGeneration);
                    if (response.Nodes.Count != 0)
                    {
                        this.ReportDiscoveryNodeResult(
                            uri,
                            NodeHealthObservation.Success,
                            refreshGeneration);
                        return response;
                    }

                    if (response.WasEmptyArray)
                    {
                        sawEmptyResponse = true;
                        lastException = new IOException($"DNS address {address} returned an empty /localnodes list");
                    }
                    else
                    {
                        lastException = new IOException($"DNS address {address} returned no usable /localnodes entries");
                    }
                }
                catch (OperationCanceledException) when (discoveryContext.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (FatalDiscoveryException)
                {
                    throw;
                }
                catch (DiscoveryAttemptDeadlineException e)
                {
                    lastException = e;
                    break;
                }
                catch (Exception e)
                {
                    Logger.Warn(e, $"Failed to discover nodes from DNS address {address} for {uri.Host}");
                    lastException = e;
                }
            }

            if (sawEmptyResponse && !string.IsNullOrEmpty(uri.Query))
            {
                this.ReportDiscoveryNodeResult(
                    uri,
                    NodeHealthObservation.Success,
                    refreshGeneration);
                return new DiscoveryResponse(new List<Uri>(), wasEmptyArray: true);
            }

            this.ReportDiscoveryNodeResult(
                uri,
                NodeHealthObservation.ConnectionFailure,
                refreshGeneration);
            throw new IOException(
                $"No usable /localnodes response from any DNS address for {uri.Host}",
                lastException);
        }

        private DiscoveryResponse GetNodes(
            Uri uri,
            HttpClient httpClient,
            bool reportNodeHealth,
            DiscoveryContext discoveryContext,
            DiscoveryAttemptBudget attemptBudget,
            int remainingAttemptOperations,
            long? refreshGeneration)
        {
            using var requestCancellation = attemptBudget.CreateAttemptCancellation(remainingAttemptOperations);
            var requestToken = requestCancellation.Token;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Host = uri.Authority;
            request.Headers.Connection.Add("keep-alive");
            var started = Stopwatch.GetTimestamp();
            HttpResponseMessage response;
            try
            {
                response = httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (discoveryContext.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                if (reportNodeHealth)
                {
                    this.ReportDiscoveryNodeResult(
                        uri,
                        NodeHealthObservation.ConnectionFailure,
                        refreshGeneration);
                }

                throw;
            }

            using (response)
            {
                if (response.RequestMessage?.RequestUri is Uri responseUri && responseUri != uri)
                {
                    if (reportNodeHealth)
                    {
                        this.ReportDiscoveryNodeResult(
                            uri,
                            NodeHealthObservation.ServerError,
                            refreshGeneration);
                    }

                    throw new IOException(
                        $"host {uri} redirected /localnodes to {responseUri}; redirects are not allowed");
                }

                if (!response.IsSuccessStatusCode)
                {
                    try
                    {
                        _ = ReadResponseBody(response.Content, requestToken, discoveryContext);
                    }
                    catch (OperationCanceledException) when (discoveryContext.CancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (FatalDiscoveryException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        Logger.Debug(e, $"Failed to drain non-success /localnodes response from {uri}");
                    }

                    if (reportNodeHealth)
                    {
                        this.ReportDiscoveryNodeResult(
                            uri,
                            NodeHealthReportingHttpMessageHandler.ObservationFromResponse(
                                response,
                                Stopwatch.GetElapsedTime(started),
                                this.config.NodeHealth),
                            refreshGeneration);
                    }

                    throw new IOException(
                        $"host {uri} returned HTTP {(int)response.StatusCode} ({response.StatusCode}) for /localnodes");
                }

                try
                {
                    var responseBody = ReadResponseBody(response.Content, requestToken, discoveryContext);
                    var list = JsonSerializer.Deserialize<List<string>>(responseBody);
                    if (list == null)
                    {
                        throw new IOException($"host {uri} returned null /localnodes data");
                    }

                    if (list.Count == 0 && string.IsNullOrEmpty(uri.Query))
                    {
                        throw new IOException($"host {uri} returned an empty /localnodes list");
                    }

                    var newHosts = new List<Uri>();
                    var seenHosts = new HashSet<Uri>();
                    foreach (var host in list)
                    {
                        if (string.IsNullOrEmpty(host))
                        {
                            continue;
                        }

                        var trimmedHost = host.Trim();
                        try
                        {
                            var newHost = this.HostToUri(trimmedHost);
                            if (seenHosts.Add(newHost))
                            {
                                newHosts.Add(newHost);
                            }
                        }
                        catch (UriFormatException e)
                        {
                            Logger.Error(e, $"Invalid host: {trimmedHost}");
                        }
                    }

                    if (list.Count != 0 && newHosts.Count == 0)
                    {
                        throw new IOException($"host {uri} returned no usable /localnodes entries");
                    }

                    discoveryContext.RecordNodes(newHosts);
                    discoveryContext.ThrowIfUnavailable();
                    attemptBudget.ThrowIfUnavailable();

                    if (reportNodeHealth)
                    {
                        this.ReportDiscoveryNodeResult(
                            uri,
                            NodeHealthObservation.Success,
                            refreshGeneration);
                    }

                    return new DiscoveryResponse(newHosts, list.Count == 0);
                }
                catch (OperationCanceledException) when (discoveryContext.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    if (reportNodeHealth)
                    {
                        this.ReportDiscoveryNodeResult(
                            uri,
                            NodeHealthObservation.ServerError,
                            refreshGeneration);
                    }

                    throw;
                }
            }
        }

        private DiscoveryHttpClientLease GetAddressPollingHttpClient(Uri logicalUri, IPAddress address)
        {
            var cacheKey = (
                Address: address,
                Authority: logicalUri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped));
            Lazy<HttpClient>? cachedClient;
            lock (this.addressPollingHttpClientsLock)
            {
                if (!this.addressPollingHttpClients.TryGetValue(cacheKey, out cachedClient)
                    && this.addressPollingHttpClients.Count < MaxCachedDnsEndpoints)
                {
                    cachedClient = new Lazy<HttpClient>(
                        () => this.CreateAddressPollingHttpClient(address),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    this.addressPollingHttpClients.Add(cacheKey, cachedClient);
                }
            }

            if (cachedClient == null)
            {
                return new DiscoveryHttpClientLease(this.CreateAddressPollingHttpClient(address), ownsClient: true);
            }

            try
            {
                return new DiscoveryHttpClientLease(cachedClient.Value, ownsClient: false);
            }
            catch
            {
                lock (this.addressPollingHttpClientsLock)
                {
                    if (this.addressPollingHttpClients.TryGetValue(cacheKey, out var currentClient)
                        && ReferenceEquals(currentClient, cachedClient))
                    {
                        this.addressPollingHttpClients.Remove(cacheKey);
                    }
                }

                throw;
            }
        }

        private HttpClient CreateAddressPollingHttpClient(IPAddress address)
        {
            var handler = AlternatorHttpClientFactory.CreateSocketsHandler(
                this.config,
                socketsHandler =>
                {
                    socketsHandler.AllowAutoRedirect = false;
                    socketsHandler.UseProxy = false;
                    socketsHandler.ConnectCallback = (context, cancellationToken) =>
                        this.ConnectToAddress(address, context.DnsEndPoint.Port, cancellationToken);
                });
            var client = new HttpClient(handler, disposeHandler: true);
            ConfigurePollingHttpClientTimeout(client, this.config.HttpClientTimeoutMs);

            return client;
        }

        private Uri NextAsLocalNodesUri()
        {
            var query = this.config.RoutingScope.LocalNodesQuery;
            return this.NextAsUriWithoutRefresh("/localnodes", string.IsNullOrEmpty(query) ? null : query);
        }

        private Uri? SelectQuarantinedNode(bool activeNodesEmpty)
        {
            var quarantinedNodes = this.GetQuarantinedNodesInternal();
            if (quarantinedNodes.Count == 0)
            {
                return null;
            }

            if (!activeNodesEmpty)
            {
                var trafficSequence = Interlocked.Increment(ref this.nextQuarantineTrafficSequence);
                if (Mod(trafficSequence, this.config.NodeHealth.QuarantinedNodeSamplingInterval) != 0)
                {
                    return null;
                }
            }

            var sequence = Interlocked.Increment(ref this.nextQuarantinedNodeIndex) - 1;
            return quarantinedNodes[Mod(sequence, quarantinedNodes.Count)];
        }

        private void ProbeDownNodesOnce(CancellationToken cancellationToken)
        {
            try
            {
                this.ProbeDownNodesInternalAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private Task<IReadOnlyList<Uri>> ProbeDownNodesInternalAsync(CancellationToken cancellationToken)
        {
            return this.healthStore.ProbeDownNodesAsync(this.ProbeNodeAsync, cancellationToken);
        }

        private void ProbeDownNodesIfDue(CancellationToken cancellationToken)
        {
            if (this.config.NodeHealth.Disabled || this.GetDownNodesInternal().Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow.Ticks;
            var lastProbe = Interlocked.Read(ref this.lastDownNodeProbeTicks);
            var probePeriod = TimeSpan.FromMilliseconds(this.config.NodeHealth.DownNodeProbePeriodMs);
            if (lastProbe != 0 && new DateTimeOffset(now, TimeSpan.Zero) - new DateTimeOffset(lastProbe, TimeSpan.Zero) < probePeriod)
            {
                return;
            }

            Interlocked.Exchange(ref this.lastDownNodeProbeTicks, now);
            this.ProbeDownNodesOnce(cancellationToken);
        }

        private async Task<NodeHealthObservation?> ProbeNodeAsync(
            Uri node,
            NodeHealthStatus status,
            CancellationToken cancellationToken)
        {
            var query = this.config.RoutingScope.LocalNodesQuery;
            var requestQuery = string.IsNullOrEmpty(query) ? null : query;
            var uri = BuildUri(node, "/localnodes", requestQuery);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Host = uri.Authority;
            request.Headers.Connection.Add("keep-alive");
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await this.pollingHttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                return NodeHealthReportingHttpMessageHandler.ObservationFromResponse(
                    response,
                    Stopwatch.GetElapsedTime(started),
                    this.config.NodeHealth);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception)
            {
                return NodeHealthObservation.ConnectionFailure;
            }
        }

        public class ValidationError : ScyllaDB.Alternator.ValidationError
        {
            public ValidationError(string message)
                : base(message)
            {
            }

            public ValidationError(string message, Exception cause)
                : base(message, cause)
            {
            }
        }

        public class FailedToCheck : ScyllaDB.Alternator.FailedToCheck
        {
            public FailedToCheck(string message, Exception cause)
                : base(message, cause)
            {
            }

            public FailedToCheck(string message)
                : base(message)
            {
            }
        }

        private sealed class DiscoveryResponse
        {
            internal DiscoveryResponse(List<Uri> nodes, bool wasEmptyArray)
            {
                this.Nodes = nodes;
                this.WasEmptyArray = wasEmptyArray;
            }

            internal List<Uri> Nodes { get; }

            internal bool WasEmptyArray { get; }
        }

        private sealed class ScopeDiscoveryResult
        {
            internal ScopeDiscoveryResult(List<Uri> nodes, bool authoritativeEmpty)
            {
                this.Nodes = nodes;
                this.AuthoritativeEmpty = authoritativeEmpty;
            }

            internal List<Uri> Nodes { get; }

            internal bool AuthoritativeEmpty { get; }
        }

        private sealed class DiscoveryCandidate
        {
            internal DiscoveryCandidate(Uri node, string description)
            {
                this.Node = node;
                this.Description = description;
            }

            internal Uri Node { get; }

            internal string Description { get; }
        }

        private sealed class DiscoveryContext : IDisposable
        {
            private readonly CancellationTokenSource cancellation;
            private readonly TimeSpan timeout;
            private readonly long started;
            private readonly HashSet<Uri> observedNodes = new HashSet<Uri>();
            private int responseBytes;

            internal DiscoveryContext(CancellationTokenSource cancellation, TimeSpan timeout)
            {
                this.cancellation = cancellation;
                this.timeout = timeout;
                this.started = Stopwatch.GetTimestamp();
            }

            internal CancellationToken CancellationToken => this.cancellation.Token;

            public void Dispose()
            {
                this.cancellation.Dispose();
            }

            internal DiscoveryAttemptBudget CreateAttemptBudget(int reservedAttempts)
            {
                this.ThrowIfUnavailable();
                var remaining = this.GetRemainingTime();
                var divisor = Math.Max(1L, (long)reservedAttempts + 1);
                var attemptTicks = Math.Max(1L, remaining.Ticks / divisor);
                return new DiscoveryAttemptBudget(this, TimeSpan.FromTicks(attemptTicks));
            }

            internal CancellationTokenSource CreateCancellation(TimeSpan timeout)
            {
                this.ThrowIfUnavailable();
                var remaining = this.GetRemainingTime();
                var effectiveTimeout = timeout < remaining
                    ? timeout
                    : remaining;
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(this.cancellation.Token);
                cancellation.CancelAfter(effectiveTimeout);
                return cancellation;
            }

            internal void RecordResponseBytes(int bytes)
            {
                if (bytes < 0
                    || this.responseBytes > MaxAggregateLocalNodesResponseBytes - bytes)
                {
                    throw new DiscoveryLimitException(
                        $"aggregate /localnodes responses exceeded {MaxAggregateLocalNodesResponseBytes} bytes");
                }

                this.responseBytes += bytes;
            }

            internal void RecordNodes(IEnumerable<Uri> nodes)
            {
                foreach (var node in nodes)
                {
                    if (this.observedNodes.Add(node)
                        && this.observedNodes.Count > MaxDiscoveredNodesPerCycle)
                    {
                        throw new DiscoveryLimitException(
                            $"discovery cycle exceeded {MaxDiscoveredNodesPerCycle} unique nodes");
                    }
                }
            }

            internal void ThrowIfUnavailable()
            {
                this.cancellation.Token.ThrowIfCancellationRequested();
                if (this.GetRemainingTime() <= TimeSpan.Zero)
                {
                    throw new DiscoveryDeadlineException(
                        $"live-node discovery exceeded its {this.timeout.TotalMilliseconds:F0} ms cycle deadline");
                }
            }

            private TimeSpan GetRemainingTime()
            {
                return this.timeout - Stopwatch.GetElapsedTime(this.started);
            }
        }

        private sealed class DiscoveryAttemptBudget
        {
            private readonly DiscoveryContext discoveryContext;
            private readonly TimeSpan timeout;
            private readonly long started;

            internal DiscoveryAttemptBudget(DiscoveryContext discoveryContext, TimeSpan timeout)
            {
                this.discoveryContext = discoveryContext;
                this.timeout = timeout;
                this.started = Stopwatch.GetTimestamp();
            }

            internal CancellationTokenSource CreateAttemptCancellation(
                int reservedOperations,
                TimeSpan? operationTimeout = null)
            {
                this.ThrowIfUnavailable();
                var remaining = this.GetRemainingTime();
                var divisor = Math.Max(1L, (long)reservedOperations + 1);
                var timeoutTicks = Math.Max(1L, remaining.Ticks / divisor);
                var attemptTimeout = TimeSpan.FromTicks(timeoutTicks);
                if (operationTimeout.HasValue && operationTimeout.Value < attemptTimeout)
                {
                    attemptTimeout = operationTimeout.Value;
                }

                return this.discoveryContext.CreateCancellation(attemptTimeout);
            }

            internal void ThrowIfUnavailable()
            {
                this.discoveryContext.ThrowIfUnavailable();
                if (this.GetRemainingTime() <= TimeSpan.Zero)
                {
                    throw new DiscoveryAttemptDeadlineException(
                        $"discovery candidate exceeded its {this.timeout.TotalMilliseconds:F0} ms fair-share deadline");
                }
            }

            private TimeSpan GetRemainingTime()
            {
                return this.timeout - Stopwatch.GetElapsedTime(this.started);
            }
        }

        private abstract class FatalDiscoveryException : IOException
        {
            protected FatalDiscoveryException(string message)
                : base(message)
            {
            }
        }

        private sealed class DiscoveryDeadlineException : FatalDiscoveryException
        {
            internal DiscoveryDeadlineException(string message)
                : base(message)
            {
            }
        }

        private sealed class DiscoveryLimitException : FatalDiscoveryException
        {
            internal DiscoveryLimitException(string message)
                : base(message)
            {
            }
        }

        private sealed class DiscoveryAttemptDeadlineException : IOException
        {
            internal DiscoveryAttemptDeadlineException(string message)
                : base(message)
            {
            }
        }

        private sealed class DiscoveryHttpClientLease : IDisposable
        {
            private readonly bool ownsClient;

            internal DiscoveryHttpClientLease(HttpClient client, bool ownsClient)
            {
                this.Client = client;
                this.ownsClient = ownsClient;
            }

            internal HttpClient Client { get; }

            public void Dispose()
            {
                if (this.ownsClient)
                {
                    this.Client.Dispose();
                }
            }
        }
    }
}
