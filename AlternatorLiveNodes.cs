namespace ScyllaDB.Alternator
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Amazon.Runtime.Endpoints;
    using ScyllaDB.Alternator.KeyRouting;
    using ScyllaDB.Alternator.Routing;

    public class AlternatorLiveNodes
    {
        private const long DefaultShutdownTimeoutMs = 5000;
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly string alternatorScheme;
        private readonly int alternatorPort;
        private readonly ReaderWriterLockSlim liveNodesLock = new ReaderWriterLockSlim();
        private readonly List<Uri> initialNodes;
        private readonly AlternatorConfig config;
        private readonly HttpClient pollingHttpClient;
        private readonly bool ownsPollingHttpClient;
        private List<Uri> liveNodes;
        private int nextLiveNodeIndex;
        private long lastActivityTicks;
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
            : this(config, CreatePollingHttpClient(config), true)
        {
        }

        public AlternatorLiveNodes(AlternatorConfig config, HttpClient pollingHttpClient)
            : this(config, pollingHttpClient, false)
        {
        }

        private AlternatorLiveNodes(AlternatorConfig config, HttpClient pollingHttpClient, bool ownsPollingHttpClient)
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
            var nodes = this.GetLiveNodes();
            if (nodes.Count == 0)
            {
                throw new InvalidOperationException("No live nodes available");
            }

            var sequence = Interlocked.Increment(ref this.nextLiveNodeIndex) - 1;
            var index = (int)(((sequence % nodes.Count) + nodes.Count) % nodes.Count);
            return nodes[index];
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
            var uri = this.NextAsUri("/localnodes", null);
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
#pragma warning restore SA1300, IDE1006

        internal Uri GetNodeForHash(long hash)
        {
            this.MarkActivity();
            var nodes = this.GetLiveNodes();
            if (nodes.Count == 0)
            {
                throw new InvalidOperationException("No live nodes available");
            }

            var index = (int)(((hash % nodes.Count) + nodes.Count) % nodes.Count);
            return nodes[index];
        }

        internal LazyQueryPlan CreateQueryPlan(long seed)
        {
            this.MarkActivity();
            return new LazyQueryPlan(this, seed);
        }

        internal LazyQueryPlan CreateQueryPlan(IEnumerable<Uri> preferredNodes)
        {
            this.MarkActivity();
            return new LazyQueryPlan(this, preferredNodes);
        }

        internal LazyQueryPlan CreateQueryPlan()
        {
            this.MarkActivity();
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
            return this.GetLiveNodesInternal();
        }

        protected internal virtual IReadOnlyList<Uri> GetQuarantinedNodesInternal()
        {
            return Array.Empty<Uri>();
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
#pragma warning restore SA1300, IDE1006

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
                .WithHttpClientTimeoutMs(config.HttpClientTimeoutMs);

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

                    try
                    {
                        this.UpdateLiveNodes();
                    }
                    catch (IOException e)
                    {
                        Logger.Error(e, "AlternatorLiveNodes failed to sync nodes list: %");
                    }

                    if (cancellationToken.WaitHandle.WaitOne(this.GetRefreshInterval()))
                    {
                        return;
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
            if (!this.ownsPollingHttpClient)
            {
                return;
            }

            if (Interlocked.Exchange(ref this.pollingHttpClientClosed, 1) == 0)
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

        private int GetRefreshInterval()
        {
            var lastActivity = Interlocked.Read(ref this.lastActivityTicks);
            var idleThreshold = TimeSpan.FromMilliseconds(this.config.IdleRefreshIntervalMs);
            var timeSinceActivity = DateTimeOffset.UtcNow - new DateTimeOffset(lastActivity, TimeSpan.Zero);
            if (timeSinceActivity < idleThreshold)
            {
                return checked((int)this.config.ActiveRefreshIntervalMs);
            }

            return checked((int)this.config.IdleRefreshIntervalMs);
        }

        private void MarkActivity()
        {
            Interlocked.Exchange(ref this.lastActivityTicks, DateTimeOffset.UtcNow.Ticks);
        }

        private void SetLiveNodes(List<Uri> nodes)
        {
            this.liveNodesLock.EnterWriteLock();
            this.liveNodes = nodes;
            this.liveNodesLock.ExitWriteLock();
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
                        var mergedNodes = this.MergeWithInitialNodes(nodes);
                        this.SetLiveNodes(mergedNodes);
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
                this.SetLiveNodes(this.MergeWithInitialNodes(this.GetLiveNodes().ToList()));
                Logger.Warn("All nodes unreachable in every routing scope, re-injected seed nodes into live list");
                return;
            }

            Logger.Warn("No nodes found in any routing scope, keeping existing node list");
        }

        private List<Uri> GetNodesForScope(RoutingScope scope)
        {
            var query = scope.LocalNodesQuery;
            var requestQuery = string.IsNullOrEmpty(query) ? null : query;
            Exception? lastException = null;
            var nodes = new List<Uri>();
            var seen = new HashSet<Uri>();
            foreach (var seedNode in this.initialNodes)
            {
                var uri = BuildUri(seedNode, "/localnodes", requestQuery);
                try
                {
                    var seedNodes = this.GetNodes(uri);
                    if (seedNodes.Count == 0)
                    {
                        continue;
                    }

                    if (scope is not ClusterScope)
                    {
                        return seedNodes;
                    }

                    foreach (var node in seedNodes)
                    {
                        if (seen.Add(node))
                        {
                            nodes.Add(node);
                        }
                    }
                }
                catch (Exception e)
                {
                    Logger.Warn(e, $"Failed to contact seed node {seedNode} for {scope.Description}");
                    lastException = e;
                }
            }

            if (nodes.Count != 0)
            {
                return nodes;
            }

            if (lastException != null)
            {
                throw lastException;
            }

            return new List<Uri>();
        }

        private List<Uri> GetNodes(Uri uri)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Host = uri.Authority;
            request.Headers.Connection.Add("keep-alive");
            using var response = this.pollingHttpClient.SendAsync(request).Result;
            if (!response.IsSuccessStatusCode)
            {
                return new List<Uri>();
            }

            var responseBody = StreamToString(response.Content.ReadAsStreamAsync().Result);
            var list = JsonSerializer.Deserialize<List<string>>(responseBody) ?? new List<string>();
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

            return newHosts;
        }

        private Uri NextAsLocalNodesUri()
        {
            var query = this.config.RoutingScope.LocalNodesQuery;
            return this.NextAsUri("/localnodes", string.IsNullOrEmpty(query) ? null : query);
        }

        private List<Uri> MergeWithInitialNodes(IEnumerable<Uri> nodes)
        {
            var merged = new List<Uri>();
            var seen = new HashSet<Uri>();
            foreach (var node in nodes.Concat(this.initialNodes))
            {
                if (seen.Add(node))
                {
                    merged.Add(node);
                }
            }

            return merged;
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
    }
}
