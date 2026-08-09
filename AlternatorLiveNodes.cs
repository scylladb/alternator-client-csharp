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
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Amazon.Runtime.Endpoints;
    using ScyllaDB.Alternator.KeyRouting;
    using ScyllaDB.Alternator.Routing;

    public class AlternatorLiveNodes
    {
        private const long DefaultShutdownTimeoutMs = 5000;
        private const int MaxCachedDnsAddresses = 64;
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly string alternatorScheme;
        private readonly int alternatorPort;
        private readonly ReaderWriterLockSlim liveNodesLock = new ReaderWriterLockSlim();
        private readonly List<Uri> initialNodes;
        private readonly AlternatorConfig config;
        private readonly HttpClient pollingHttpClient;
        private readonly bool ownsPollingHttpClient;
        private readonly bool enableDnsAddressFallback;
        private readonly Dictionary<IPAddress, Lazy<HttpClient>> addressPollingHttpClients =
            new Dictionary<IPAddress, Lazy<HttpClient>>();

        private readonly object addressPollingHttpClientsLock = new object();
        private readonly NodeHealthStore healthStore;
        private readonly object updateSignalLock = new object();
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
            if (config == null)
            {
                throw new SystemException("config cannot be null");
            }

            if (pollingHttpClient == null)
            {
                throw new SystemException("pollingHttpClient cannot be null");
            }

            if (config.SeedHosts.Count == 0)
            {
                throw new SystemException("seedHosts cannot be empty");
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

            this.liveNodes = new List<Uri>();
            foreach (var node in this.initialNodes)
            {
                this.liveNodes.Add(node);
            }

            this.healthStore = new NodeHealthStore(this.config.NodeHealth, this.initialNodes);

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
            if (this.started)
            {
                return Task.CompletedTask;
            }

            this.Validate();
            this.refreshCancellation?.Dispose();
            this.refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            this.started = true;

            this.refreshTask = Task.Run(
                () =>
            {
                this.UpdateCycle(this.refreshCancellation.Token);
            }, CancellationToken.None);
            return Task.CompletedTask;
        }

        public void Run(CancellationToken cancellationToken = default)
        {
            if (this.started)
            {
                return;
            }

            this.Validate();
            this.refreshCancellation?.Dispose();
            this.refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            this.started = true;
            this.UpdateCycle(this.refreshCancellation.Token);
        }

        public void Shutdown()
        {
            this.refreshCancellation?.Cancel();
            this.started = false;
            if (this.refreshTask == null || this.refreshTask.IsCompleted)
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
            var task = this.refreshTask;
            if (task == null)
            {
                return true;
            }

            if (Task.CurrentId.HasValue && task.Id == Task.CurrentId.Value)
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
            return this.started && this.refreshTask?.IsCompleted != true;
        }

        public void Validate()
        {
            try
            {
                // Make sure that `alternatorScheme` and `alternatorPort` are correct values
                this.HostToUri("1.1.1.1");
            }
            catch (UriFormatException e)
            {
                throw new ValidationError("failed to validate configuration", e);
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

        public Task<IReadOnlyList<Uri>> ProbeDownNodesAsync(CancellationToken cancellationToken = default)
        {
            return this.healthStore.ProbeDownNodesAsync(this.ProbeNodeAsync, cancellationToken);
        }

        public Uri NextAsUri(string? path, string? query)
        {
            Uri uri = this.NextAsUri();
            return BuildUri(uri, path, query);
        }

        public void CheckIfRackAndDatacenterSetCorrectly()
        {
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
            var uri = this.NextAsUriWithoutRefresh("/localnodes", null);
            Uri fakeRackUrl;
            try
            {
                fakeRackUrl = BuildUri(uri, "/localnodes", "rack=fakeRack");
            }
            catch (UriFormatException e)
            {
                // Should not ever happen
                throw new FailedToCheck("Invalid Uri: " + uri, e);
            }

            try
            {
                var hostsWithFakeRack = this.GetNodes(fakeRackUrl);
                var hostsWithoutRack = this.GetNodes(uri);
                if (hostsWithoutRack.Count == 0)
                {
                    // This should not normally happen.
                    // If list of nodes is empty, it is impossible to conclude if it supports rack/datacenter filtering or not.
                    throw new FailedToCheck($"host {uri} returned empty list");
                }

                // When rack filtering is not supported server returns same nodes.
                return hostsWithFakeRack.Count != hostsWithoutRack.Count;
            }
            catch (IOException e)
            {
                throw new FailedToCheck("failed to read list of nodes from the node", e);
            }
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

        protected virtual IReadOnlyList<IPAddress> ResolveHostAddresses(string host)
        {
            var timeoutMs = this.config.ConnectionTimeoutMs > 0
                ? this.config.ConnectionTimeoutMs
                : AlternatorConfig.DefaultConnectionTimeoutMs;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(timeoutMs));
            try
            {
                return Dns.GetHostAddressesAsync(host, cancellation.Token).GetAwaiter().GetResult();
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

        private static string StreamToString(Stream stream)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static HttpClient CreatePollingHttpClient(AlternatorConfig config)
        {
            if (config == null)
            {
                throw new SystemException("config cannot be null");
            }

            return new HttpClient(AlternatorHttpClientFactory.CreatePrimaryHandler(config));
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
                .WithSeedHosts(nodes.Select(node => node.Host))
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

        private static async ValueTask<Stream> ConnectToAddress(
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
                        this.UpdateLiveNodes();
                        this.ProbeDownNodesIfDue(cancellationToken);
                    }
                    catch (IOException e)
                    {
                        Logger.Error(e, "AlternatorLiveNodes failed to sync nodes list: %");
                    }
                }
            }
            finally
            {
                this.started = false;
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
                return new UriBuilder(this.alternatorScheme, host, this.alternatorPort).Uri;
            }
            catch (ArgumentException e)
            {
                throw new UriFormatException("Invalid host URI", e);
            }
        }

        private Uri SelectNextUri()
        {
            var activeNodes = this.GetActiveNodesInternal();
            if (activeNodes.Count == 0)
            {
                this.ProbeDownNodesOnce(CancellationToken.None);
                activeNodes = this.GetActiveNodesInternal();
            }

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
            if (this.refreshTask == null)
            {
                this.Start();
            }

            this.TriggerUpdate();
        }

        private void TriggerUpdate()
        {
            var now = DateTimeOffset.UtcNow.Ticks;
            var nextRefresh = Interlocked.Read(ref this.nextRequestRefreshTicks);
            if (nextRefresh >= now)
            {
                return;
            }

            var requestedNextRefresh = checked(now + TimeSpan.FromMilliseconds(this.config.ActiveRefreshIntervalMs).Ticks);
            if (Interlocked.CompareExchange(ref this.nextRequestRefreshTicks, requestedNextRefresh, nextRefresh) == nextRefresh)
            {
                Interlocked.Exchange(ref this.updateRequested, 1);
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
                    if (Interlocked.Exchange(ref this.updateRequested, 0) == 1)
                    {
                        return true;
                    }

                    if (!Monitor.Wait(this.updateSignalLock, this.GetRefreshInterval()))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void SetLiveNodes(List<Uri> nodes)
        {
            this.liveNodesLock.EnterWriteLock();
            try
            {
                this.liveNodes = nodes;
                this.healthStore.SetKnownNodes(nodes);
            }
            finally
            {
                this.liveNodesLock.ExitWriteLock();
            }
        }

        private void UpdateLiveNodes()
        {
            var scope = this.config.RoutingScope;
            Exception? lastException = null;
            while (scope != null)
            {
                try
                {
                    var nodes = this.GetNodesForScope(scope);
                    if (nodes.Count != 0)
                    {
                        this.SetLiveNodes(nodes);
                        Logger.Info($"Updated hosts to {this.liveNodes} using {scope.Description}");
                        return;
                    }
                }
                catch (Exception e)
                {
                    Logger.Warn(e, $"Failed to discover nodes for {scope.Description}");
                    lastException = e;
                }

                if (scope.Fallback != null)
                {
                    Logger.Warn($"No nodes found for {scope.Description}; falling back to {scope.Fallback.Description}");
                }

                scope = scope.Fallback;
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
            var query = scope.LocalNodesQuery;
            var requestQuery = string.IsNullOrEmpty(query) ? null : query;
            var liveAttempt = this.DiscoverNodes(
                scope,
                this.GetLiveNodes(),
                requestQuery,
                "live node");
            if (liveAttempt.Nodes.Count != 0)
            {
                return liveAttempt.Nodes;
            }

            var attemptedLiveNodes = new HashSet<Uri>(liveAttempt.Candidates);
            var seedAttempt = this.DiscoverNodes(
                scope,
                this.initialNodes.Where(node => !attemptedLiveNodes.Contains(node)),
                requestQuery,
                "seed node");
            if (seedAttempt.Nodes.Count != 0)
            {
                return seedAttempt.Nodes;
            }

            if (seedAttempt.LastException != null)
            {
                throw seedAttempt.LastException;
            }

            if (liveAttempt.LastException != null)
            {
                throw liveAttempt.LastException;
            }

            return new List<Uri>();
        }

        private DiscoveryAttempt DiscoverNodes(
            RoutingScope scope,
            IEnumerable<Uri> candidates,
            string? requestQuery,
            string candidateDescription)
        {
            Exception? lastException = null;
            var nodes = new List<Uri>();
            var seen = new HashSet<Uri>();
            var distinctCandidates = new List<Uri>();
            var seenCandidates = new HashSet<Uri>();
            foreach (var candidate in candidates)
            {
                if (!seenCandidates.Add(candidate))
                {
                    continue;
                }

                distinctCandidates.Add(candidate);
                var uri = BuildUri(candidate, "/localnodes", requestQuery);
                try
                {
                    var discoveredNodes = this.GetNodes(uri);
                    if (discoveredNodes.Count == 0)
                    {
                        continue;
                    }

                    if (scope is not ClusterScope)
                    {
                        return new DiscoveryAttempt(distinctCandidates, discoveredNodes, lastException);
                    }

                    foreach (var node in discoveredNodes)
                    {
                        if (seen.Add(node))
                        {
                            nodes.Add(node);
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Warn(
                        e,
                        $"Failed to contact {candidateDescription} {candidate} for {scope.Description}");
                    lastException = e;
                }
            }

            return new DiscoveryAttempt(distinctCandidates, nodes, lastException);
        }

        private List<Uri> GetNodes(Uri uri)
        {
            if (!this.enableDnsAddressFallback || Uri.CheckHostName(uri.Host) != UriHostNameType.Dns)
            {
                return this.GetNodes(uri, this.pollingHttpClient, reportNodeHealth: true).Nodes;
            }

            IReadOnlyList<IPAddress> resolvedAddresses;
            try
            {
                resolvedAddresses = this.ResolveHostAddresses(uri.Host);
            }
            catch (Exception e)
            {
                this.ReportNodeResult(uri, NodeHealthObservation.ConnectionFailure);
                throw new IOException($"Failed to resolve DNS entrypoint {uri.Host}", e);
            }

            var addresses = resolvedAddresses.Distinct().ToList();
            if (addresses.Count == 0)
            {
                this.ReportNodeResult(uri, NodeHealthObservation.ConnectionFailure);
                throw new IOException($"DNS entrypoint {uri.Host} resolved without addresses");
            }

            Exception? lastException = null;
            var sawEmptyResponse = false;
            foreach (var address in addresses)
            {
                try
                {
                    using var clientLease = this.GetAddressPollingHttpClient(address);
                    var response = this.GetNodes(uri, clientLease.Client, reportNodeHealth: false);
                    if (response.Nodes.Count != 0)
                    {
                        this.ReportNodeResult(uri, NodeHealthObservation.Success);
                        return response.Nodes;
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
                catch (Exception e)
                {
                    Logger.Warn(e, $"Failed to discover nodes from DNS address {address} for {uri.Host}");
                    lastException = e;
                }
            }

            if (sawEmptyResponse && !string.IsNullOrEmpty(uri.Query))
            {
                this.ReportNodeResult(uri, NodeHealthObservation.Success);
                return new List<Uri>();
            }

            this.ReportNodeResult(uri, NodeHealthObservation.ConnectionFailure);
            throw new IOException(
                $"No usable /localnodes response from any DNS address for {uri.Host}",
                lastException);
        }

        private DiscoveryResponse GetNodes(Uri uri, HttpClient httpClient, bool reportNodeHealth)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Host = uri.Authority;
            request.Headers.Connection.Add("keep-alive");
            var started = Stopwatch.GetTimestamp();
            HttpResponseMessage response;
            try
            {
                response = httpClient.SendAsync(request).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                if (reportNodeHealth)
                {
                    this.ReportNodeResult(uri, NodeHealthObservation.ConnectionFailure);
                }

                throw;
            }

            using (response)
            {
                if (reportNodeHealth)
                {
                    this.ReportNodeResult(
                        uri,
                        NodeHealthReportingHttpMessageHandler.ObservationFromResponse(
                            response,
                            Stopwatch.GetElapsedTime(started),
                            this.config.NodeHealth));
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new IOException(
                        $"host {uri} returned HTTP {(int)response.StatusCode} ({response.StatusCode}) for /localnodes");
                }

                var responseBody = StreamToString(response.Content.ReadAsStreamAsync().Result);
                var list = JsonSerializer.Deserialize<List<string>>(responseBody);
                if (list == null)
                {
                    throw new IOException($"host {uri} returned null /localnodes data");
                }

                var newHosts = new List<Uri>();
                foreach (var host in list)
                {
                    if (string.IsNullOrEmpty(host))
                    {
                        continue;
                    }

                    var trimmedHost = host.Trim();
                    try
                    {
                        newHosts.Add(this.HostToUri(trimmedHost));
                    }
                    catch (UriFormatException e)
                    {
                        Logger.Error(e, $"Invalid host: {trimmedHost}");
                    }
                }

                return new DiscoveryResponse(newHosts, list.Count == 0);
            }
        }

        private DiscoveryHttpClientLease GetAddressPollingHttpClient(IPAddress address)
        {
            Lazy<HttpClient>? cachedClient;
            lock (this.addressPollingHttpClientsLock)
            {
                if (!this.addressPollingHttpClients.TryGetValue(address, out cachedClient)
                    && this.addressPollingHttpClients.Count < MaxCachedDnsAddresses)
                {
                    cachedClient = new Lazy<HttpClient>(
                        () => this.CreateAddressPollingHttpClient(address),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    this.addressPollingHttpClients.Add(address, cachedClient);
                }
            }

            return cachedClient != null
                ? new DiscoveryHttpClientLease(cachedClient.Value, ownsClient: false)
                : new DiscoveryHttpClientLease(this.CreateAddressPollingHttpClient(address), ownsClient: true);
        }

        private HttpClient CreateAddressPollingHttpClient(IPAddress address)
        {
            var handler = AlternatorHttpClientFactory.CreateSocketsHandler(
                this.config,
                socketsHandler =>
                {
                    socketsHandler.UseProxy = false;
                    socketsHandler.ConnectCallback = (context, cancellationToken) =>
                        ConnectToAddress(address, context.DnsEndPoint.Port, cancellationToken);
                });
            var client = new HttpClient(handler, disposeHandler: true);
            if (this.config.HttpClientTimeoutMs > 0)
            {
                client.Timeout = TimeSpan.FromMilliseconds(this.config.HttpClientTimeoutMs);
            }

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
                this.ProbeDownNodesAsync(cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
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

        private sealed class DiscoveryAttempt
        {
            internal DiscoveryAttempt(
                List<Uri> candidates,
                List<Uri> nodes,
                Exception? lastException)
            {
                this.Candidates = candidates;
                this.Nodes = nodes;
                this.LastException = lastException;
            }

            internal List<Uri> Candidates { get; }

            internal List<Uri> Nodes { get; }

            internal Exception? LastException { get; }
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
