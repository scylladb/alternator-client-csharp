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

#pragma warning disable SA1116, SA1201, SA1204, SA1402, SA1649

namespace ScyllaDB.Alternator.TestInfrastructure
{
    using Amazon.DynamoDBv2;
    using Amazon.DynamoDBv2.Model;
    using Amazon.Runtime;

    internal sealed class TestClusterNode
    {
        internal TestClusterNode(string name, string address, string datacenter, string rack)
        {
            this.Name = name;
            this.Address = address;
            this.Datacenter = datacenter;
            this.Rack = rack;
        }

        internal string Name { get; }

        internal string Address { get; }

        internal string Datacenter { get; }

        internal string Rack { get; }
    }

    internal sealed class AlternatorConnection
    {
        internal AlternatorConnection(
            Uri seedEndpoint,
            IReadOnlyList<Uri> nodeEndpoints,
            BasicAWSCredentials? credentials,
            string? caCertificatePath)
        {
            this.SeedEndpoint = seedEndpoint;
            this.NodeEndpoints = nodeEndpoints;
            this.Credentials = credentials;
            this.CaCertificatePath = caCertificatePath;
        }

        internal Uri SeedEndpoint { get; }

        internal IReadOnlyList<Uri> NodeEndpoints { get; }

        internal BasicAWSCredentials? Credentials { get; }

        internal string? CaCertificatePath { get; }
    }

    internal interface ITestClusterInfo
    {
        string InstanceId { get; }

        ClusterSpec Spec { get; }

        IReadOnlyList<TestClusterNode> Nodes { get; }

        AlternatorConnection Connection(AlternatorTransport transport);

        AlternatorDynamoDBClientBuilder ClientBuilder(AlternatorTransport transport);
    }

    internal interface IPrivateClusterControl
    {
        Task StartAsync(CancellationToken cancellationToken = default);

        Task StopAsync(CancellationToken cancellationToken = default);

        Task StartNodeAsync(TestClusterNode node, CancellationToken cancellationToken = default);

        Task StopNodeAsync(TestClusterNode node, CancellationToken cancellationToken = default);

        Task<TestClusterNode> AddNodeAsync(
            string datacenter,
            string rack,
            CancellationToken cancellationToken = default);

        Task RemoveNodeAsync(TestClusterNode node, CancellationToken cancellationToken = default);
    }

    internal sealed class TestResourceScope
    {
        private const int MaximumTableNameLength = 255;
        private const int UniqueSuffixLength = 33;
        private readonly PhysicalTestCluster cluster;
        private readonly string prefix;

        internal TestResourceScope(PhysicalTestCluster cluster, string runId, long leaseId)
        {
            this.cluster = cluster;
            var leaseComponent = $"_{leaseId.ToString(System.Globalization.CultureInfo.InvariantCulture)}_";
            var maximumRunIdLength = MaximumTableNameLength
                - UniqueSuffixLength
                - "csharp_it_".Length
                - leaseComponent.Length;
            var sanitizedRunId = Sanitize(runId);
            this.prefix = "csharp_it_"
                + Truncate(sanitizedRunId, maximumRunIdLength)
                + leaseComponent;
        }

        internal string NewTableName(string hint)
        {
            var suffix = "_" + Guid.NewGuid().ToString("N");
            var maximumHintLength = MaximumTableNameLength - this.prefix.Length - suffix.Length;
            return this.prefix + Truncate(Sanitize(hint), maximumHintLength) + suffix;
        }

        internal async Task CleanupAsync(CancellationToken cancellationToken)
        {
            var transport = this.cluster.Spec.Transports.HasFlag(AlternatorTransport.Http)
                ? AlternatorTransport.Http
                : AlternatorTransport.Https;
            using var client = this.cluster.ClientBuilder(transport).Build();
            string? startName = null;
            do
            {
                var response = await client.ListTablesAsync(new ListTablesRequest
                {
                    ExclusiveStartTableName = startName,
                }, cancellationToken);
                foreach (var tableName in response.TableNames.Where(name => name.StartsWith(this.prefix, StringComparison.Ordinal)))
                {
                    try
                    {
                        await client.DeleteTableAsync(tableName, cancellationToken);
                        await WaitForTableDeletionAsync(client, tableName, cancellationToken);
                    }
                    catch (ResourceNotFoundException)
                    {
                    }
                }

                startName = response.LastEvaluatedTableName;
            }
            while (!string.IsNullOrEmpty(startName));
        }

        private static async Task WaitForTableDeletionAsync(
            AmazonDynamoDBClient client,
            string tableName,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                try
                {
                    await client.DescribeTableAsync(tableName, cancellationToken);
                }
                catch (ResourceNotFoundException)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }

        private static string Sanitize(string value)
        {
            var characters = value.ToLowerInvariant()
                .Select(character => IsAllowedResourceNameCharacter(character) ? character : '_')
                .ToArray();
            return new string(characters);
        }

        private static bool IsAllowedResourceNameCharacter(char character)
        {
            return (character >= 'a' && character <= 'z')
                || (character >= '0' && character <= '9')
                || character == '_'
                || character == '-'
                || character == '.';
        }

        private static string Truncate(string value, int maximumLength)
        {
            return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
        }
    }

    internal sealed class ReusableClusterLease : IAsyncDisposable
    {
        private readonly TestClusterPool pool;
        private readonly PooledCluster pooledCluster;
        private int disposed;

        internal ReusableClusterLease(
            TestClusterPool pool,
            PooledCluster pooledCluster,
            PhysicalTestCluster cluster,
            TestResourceScope resources)
        {
            this.pool = pool;
            this.pooledCluster = pooledCluster;
            this.Cluster = new ReadOnlyTestCluster(cluster);
            this.Resources = resources;
        }

        internal ITestClusterInfo Cluster { get; }

        internal TestResourceScope Resources { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                await this.pool.ReleaseReusableAsync(this.pooledCluster, this.Resources);
            }
        }
    }

    internal sealed class PrivateClusterLease : IAsyncDisposable
    {
        private readonly TestClusterPool pool;
        private readonly PhysicalTestCluster physicalCluster;
        private int disposed;

        internal PrivateClusterLease(
            TestClusterPool pool,
            PhysicalTestCluster physicalCluster,
            TestResourceScope resources)
        {
            this.pool = pool;
            this.physicalCluster = physicalCluster;
            this.Cluster = physicalCluster;
            this.Control = new PrivateClusterControl(pool, physicalCluster);
            this.Resources = resources;
        }

        internal ITestClusterInfo Cluster { get; }

        internal IPrivateClusterControl Control { get; }

        internal TestResourceScope Resources { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.disposed, 1) == 0)
            {
                await this.pool.ReleasePrivateAsync(this.physicalCluster);
            }
        }
    }

    internal sealed class ReadOnlyTestCluster : ITestClusterInfo
    {
        private readonly PhysicalTestCluster cluster;
        private readonly IReadOnlyList<TestClusterNode> nodes;

        internal ReadOnlyTestCluster(PhysicalTestCluster cluster)
        {
            this.cluster = cluster;
            this.nodes = cluster.Nodes.ToArray();
        }

        public string InstanceId => this.cluster.InstanceId;

        public ClusterSpec Spec => this.cluster.Spec;

        public IReadOnlyList<TestClusterNode> Nodes => this.nodes;

        public AlternatorConnection Connection(AlternatorTransport transport)
        {
            return this.cluster.Connection(transport);
        }

        public AlternatorDynamoDBClientBuilder ClientBuilder(AlternatorTransport transport)
        {
            return this.cluster.ClientBuilder(transport);
        }
    }

    internal sealed class PrivateClusterControl : IPrivateClusterControl
    {
        private readonly TestClusterPool pool;
        private readonly PhysicalTestCluster cluster;

        internal PrivateClusterControl(TestClusterPool pool, PhysicalTestCluster cluster)
        {
            this.pool = pool;
            this.cluster = cluster;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return this.cluster.StartAsync(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return this.cluster.StopAsync(cancellationToken);
        }

        public Task StartNodeAsync(TestClusterNode node, CancellationToken cancellationToken = default)
        {
            return this.cluster.StartNodeAsync(node, cancellationToken);
        }

        public Task StopNodeAsync(TestClusterNode node, CancellationToken cancellationToken = default)
        {
            return this.cluster.StopNodeAsync(node, cancellationToken);
        }

        public async Task<TestClusterNode> AddNodeAsync(
            string datacenter,
            string rack,
            CancellationToken cancellationToken = default)
        {
            await this.pool.ReserveAdditionalPrivateNodeAsync(this.cluster, cancellationToken);
            try
            {
                return await this.cluster.AddNodeAsync(datacenter, rack, cancellationToken);
            }
            catch (CcmNodeProvisioningException exception) when (exception.NodeRemainsProvisioned)
            {
                throw;
            }
            catch
            {
                this.pool.ReleaseAdditionalPrivateNode(this.cluster.Spec);
                throw;
            }
        }

        public async Task RemoveNodeAsync(TestClusterNode node, CancellationToken cancellationToken = default)
        {
            await this.cluster.RemoveNodeAsync(node, cancellationToken);
            this.pool.ReleaseAdditionalPrivateNode(this.cluster.Spec);
        }
    }

    internal sealed class PhysicalTestCluster : ITestClusterInfo, IPrivateClusterControl
    {
        private readonly CcmProvisioner provisioner;
        private readonly SemaphoreSlim mutationLock = new SemaphoreSlim(1, 1);
        private readonly HashSet<string> decommissionedNodes = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> runningNodes;
        private readonly List<TestClusterNode> nodes;

        internal PhysicalTestCluster(
            CcmProvisioner provisioner,
            string instanceId,
            int ccmId,
            string ccmDirectory,
            ClusterSpec spec,
            IEnumerable<TestClusterNode> nodes,
            string? caCertificatePath,
            BasicAWSCredentials? credentials)
        {
            this.provisioner = provisioner;
            this.InstanceId = instanceId;
            this.CcmId = ccmId;
            this.CcmDirectory = ccmDirectory;
            this.Spec = spec;
            this.nodes = nodes.ToList();
            this.runningNodes = new HashSet<string>(this.nodes.Select(node => node.Name), StringComparer.Ordinal);
            this.CaCertificatePath = caCertificatePath;
            this.Credentials = credentials;
        }

        public string InstanceId { get; }

        public ClusterSpec Spec { get; }

        public IReadOnlyList<TestClusterNode> Nodes => this.nodes;

        internal int CcmId { get; }

        internal string CcmDirectory { get; }

        internal string? CaCertificatePath { get; }

        internal BasicAWSCredentials? Credentials { get; }

        public AlternatorConnection Connection(AlternatorTransport transport)
        {
            if (transport != AlternatorTransport.Http && transport != AlternatorTransport.Https)
            {
                throw new ArgumentException("Select exactly one Alternator transport.", nameof(transport));
            }

            if (!this.Spec.Transports.HasFlag(transport))
            {
                throw new InvalidOperationException($"Cluster '{this.InstanceId}' does not provide {transport}.");
            }

            var scheme = transport == AlternatorTransport.Http ? "http" : "https";
            var port = transport == AlternatorTransport.Http ? CcmProvisioner.HttpPort : CcmProvisioner.HttpsPort;
            var endpoints = this.nodes.Select(node => new Uri($"{scheme}://{node.Address}:{port}")).ToArray();
            return new AlternatorConnection(
                endpoints[0],
                endpoints,
                this.Credentials,
                transport == AlternatorTransport.Https ? this.CaCertificatePath : null);
        }

        public AlternatorDynamoDBClientBuilder ClientBuilder(AlternatorTransport transport)
        {
            var connection = this.Connection(transport);
            var builder = AlternatorDynamoDBClient.builder().endpointOverride(connection.SeedEndpoint);
            if (connection.Credentials != null)
            {
                builder.credentialsProvider(connection.Credentials);
            }

            if (transport == AlternatorTransport.Https && connection.CaCertificatePath != null)
            {
                builder.withTlsConfig(TlsConfig.builder()
                    .withCaCertPath(connection.CaCertificatePath)
                    .withTrustSystemCaCerts(false)
                    .build());
            }

            return builder;
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            return this.WithMutationLockAsync(
                async () =>
                {
                    await this.provisioner.StartAsync(this, cancellationToken);
                    this.runningNodes.UnionWith(this.nodes.Select(node => node.Name));
                },
                cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return this.WithMutationLockAsync(
                async () =>
                {
                    await this.provisioner.StopAsync(this, cancellationToken);
                    this.runningNodes.Clear();
                },
                cancellationToken);
        }

        public Task StartNodeAsync(TestClusterNode node, CancellationToken cancellationToken = default)
        {
            return this.WithMutationLockAsync(
                async () =>
                {
                    var existingNode = this.GetNode(node);
                    await this.provisioner.StartNodeAsync(this, existingNode, cancellationToken);
                    this.runningNodes.Add(existingNode.Name);
                },
                cancellationToken);
        }

        public Task StopNodeAsync(TestClusterNode node, CancellationToken cancellationToken = default)
        {
            return this.WithMutationLockAsync(
                async () =>
                {
                    var existingNode = this.GetNode(node);
                    await this.provisioner.StopNodeAsync(this, existingNode, cancellationToken);
                    this.runningNodes.Remove(existingNode.Name);
                },
                cancellationToken);
        }

        public async Task<TestClusterNode> AddNodeAsync(
            string datacenter,
            string rack,
            CancellationToken cancellationToken = default)
        {
            await this.mutationLock.WaitAsync(cancellationToken);
            try
            {
                var node = await this.provisioner.AddNodeAsync(this, datacenter, rack, cancellationToken);
                this.nodes.Add(node);
                this.decommissionedNodes.Remove(node.Name);
                this.runningNodes.Add(node.Name);
                return node;
            }
            catch (CcmNodeProvisioningException exception) when (exception.NodeRemainsProvisioned)
            {
                this.nodes.Add(exception.Node);
                throw;
            }
            finally
            {
                this.mutationLock.Release();
            }
        }

        public async Task RemoveNodeAsync(TestClusterNode node, CancellationToken cancellationToken = default)
        {
            await this.mutationLock.WaitAsync(cancellationToken);
            try
            {
                var existingNode = this.GetNode(node);
                if (this.nodes.Count > 1 && !this.decommissionedNodes.Contains(existingNode.Name))
                {
                    if (!this.runningNodes.Contains(existingNode.Name))
                    {
                        await this.provisioner.StartNodeAsync(this, existingNode, cancellationToken);
                        this.runningNodes.Add(existingNode.Name);
                    }

                    await this.provisioner.DecommissionNodeAsync(this, existingNode, cancellationToken);
                    this.decommissionedNodes.Add(existingNode.Name);
                }

                await this.provisioner.DeleteNodeStateAsync(this, existingNode, cancellationToken);
                this.nodes.Remove(existingNode);
                this.decommissionedNodes.Remove(existingNode.Name);
                this.runningNodes.Remove(existingNode.Name);
            }
            finally
            {
                this.mutationLock.Release();
            }
        }

        private TestClusterNode GetNode(TestClusterNode node)
        {
            return this.nodes.Single(candidate => candidate.Name == node.Name);
        }

        private async Task WithMutationLockAsync(Func<Task> action, CancellationToken cancellationToken)
        {
            await this.mutationLock.WaitAsync(cancellationToken);
            try
            {
                await action();
            }
            finally
            {
                this.mutationLock.Release();
            }
        }
    }
}
