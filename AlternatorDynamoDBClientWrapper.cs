// <copyright file="AlternatorDynamoDBClientWrapper.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon.DynamoDBv2;
    using ScyllaDB.Alternator.KeyRouting;

    public sealed class AlternatorDynamoDBClientWrapper : IDisposable
    {
        private readonly AlternatorConfig? config;
        private readonly AlternatorLiveNodes liveNodes;
        private readonly PartitionKeyResolver? partitionKeyResolver;

        public AlternatorDynamoDBClientWrapper(AmazonDynamoDBClient client, AlternatorLiveNodes liveNodes)
            : this(client, liveNodes, null, null)
        {
        }

        public AlternatorDynamoDBClientWrapper(AmazonDynamoDBClient client, AlternatorLiveNodes liveNodes, AlternatorConfig? config)
            : this(client, liveNodes, config, null)
        {
        }

        public AlternatorDynamoDBClientWrapper(
            AmazonDynamoDBClient client,
            AlternatorLiveNodes liveNodes,
            AlternatorConfig? config,
            PartitionKeyResolver? partitionKeyResolver)
        {
            this.Client = client ?? throw new ArgumentNullException(nameof(client));
            this.liveNodes = liveNodes ?? throw new ArgumentNullException(nameof(liveNodes));
            this.config = config;
            this.partitionKeyResolver = partitionKeyResolver;
            this.partitionKeyResolver?.SetClientForDiscovery(client);
        }

        internal AlternatorDynamoDBClientWrapper(
            AmazonDynamoDBClient client,
            Helper endpointProvider,
            AlternatorConfig config,
            PartitionKeyResolver? partitionKeyResolver)
            : this(client, RequireLiveNodes(endpointProvider), config, partitionKeyResolver)
        {
        }

        public AmazonDynamoDBClient Client { get; }

        public AlternatorConfig Config => this.config
            ?? throw new InvalidOperationException("AlternatorConfig is not available for this wrapper.");

        public PartitionKeyResolver? PartitionKeyResolver => this.partitionKeyResolver;

        public AmazonDynamoDBClient GetClient()
        {
            return this.Client;
        }

        public IReadOnlyList<Uri> GetLiveNodes()
        {
            return this.liveNodes.GetLiveNodes();
        }

        public Uri NextAsURI()
        {
            return this.liveNodes.NextAsUri();
        }

        public bool CheckIfRackDatacenterFeatureIsSupported()
        {
            return this.liveNodes.CheckIfRackDatacenterFeatureIsSupported();
        }

        public AlternatorConfig? GetAlternatorConfig()
        {
            return this.config;
        }

        public AlternatorLiveNodes GetAlternatorLiveNodes()
        {
            return this.liveNodes;
        }

        public void Close()
        {
            this.Dispose();
        }

#pragma warning disable SA1300, IDE1006
        public AmazonDynamoDBClient getClient()
        {
            return this.GetClient();
        }

        public IReadOnlyList<Uri> getLiveNodes()
        {
            return this.GetLiveNodes();
        }

        public Uri nextAsURI()
        {
            return this.NextAsURI();
        }

        public bool checkIfRackDatacenterFeatureIsSupported()
        {
            return this.CheckIfRackDatacenterFeatureIsSupported();
        }

        public AlternatorConfig? getAlternatorConfig()
        {
            return this.GetAlternatorConfig();
        }

        public AlternatorLiveNodes getAlternatorLiveNodes()
        {
            return this.GetAlternatorLiveNodes();
        }

        public void close()
        {
            this.Close();
        }
#pragma warning restore SA1300, IDE1006

        public void Dispose()
        {
            this.partitionKeyResolver?.Dispose();
            this.liveNodes.ShutdownAndWait();
            this.Client.Dispose();
        }

        private static AlternatorLiveNodes RequireLiveNodes(Helper endpointProvider)
        {
            if (endpointProvider == null)
            {
                throw new ArgumentNullException(nameof(endpointProvider));
            }

            return endpointProvider.GetAlternatorLiveNodes();
        }
    }
}
