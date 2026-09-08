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
    using Amazon.DynamoDBv2;
    using Amazon.Runtime;
    using ScyllaDB.Alternator.KeyRouting;

    public class AffinityQueryPlanInterceptor : BasicQueryPlanInterceptor
    {
        private readonly KeyRouteAffinityConfig? config;
        private readonly PartitionKeyResolver? partitionKeyResolver;

        public AffinityQueryPlanInterceptor(
            KeyRouteAffinityConfig config,
            AlternatorLiveNodes liveNodes,
            IAmazonDynamoDB? clientForDiscovery)
            : this(
                config,
                liveNodes,
                new PartitionKeyResolver(config?.PkInfoPerTable.ToDictionary(item => item.Key, item => item.Value)))
        {
            if (clientForDiscovery != null)
            {
                this.SetClientForDiscovery(clientForDiscovery);
            }
        }

        public AffinityQueryPlanInterceptor(
            KeyRouteAffinityConfig config,
            AlternatorLiveNodes liveNodes)
            : this(config, liveNodes, (IAmazonDynamoDB?)null)
        {
        }

        internal AffinityQueryPlanInterceptor(
            KeyRouteAffinityConfig? config,
            AlternatorLiveNodes liveNodes,
            PartitionKeyResolver? partitionKeyResolver)
            : base(liveNodes)
        {
            this.config = config;
            this.partitionKeyResolver = partitionKeyResolver;
        }

        public KeyRouteAffinityConfig? Config => this.config;

        public PartitionKeyResolver? PartitionKeyResolver => this.partitionKeyResolver;

        public PartitionKeyResolver? GetPartitionKeyResolver()
        {
            return this.partitionKeyResolver;
        }

        public KeyRouteAffinityConfig? GetConfig()
        {
            return this.config;
        }

        public void SetClientForDiscovery(IAmazonDynamoDB client)
        {
            this.partitionKeyResolver?.SetClientForDiscovery(client);
        }

#pragma warning disable SA1300, IDE1006
        public PartitionKeyResolver? getPartitionKeyResolver()
        {
            return this.GetPartitionKeyResolver();
        }

        public KeyRouteAffinityConfig? getConfig()
        {
            return this.GetConfig();
        }

        public void setClientForDiscovery(IAmazonDynamoDB client)
        {
            this.SetClientForDiscovery(client);
        }
#pragma warning restore SA1300, IDE1006

        protected override LazyQueryPlan CreateQueryPlan(AmazonWebServiceRequest originalRequest)
        {
            return this.TryCreateAffinityQueryPlan(originalRequest) ?? base.CreateQueryPlan(originalRequest);
        }

        private LazyQueryPlan? TryCreateAffinityQueryPlan(AmazonWebServiceRequest originalRequest)
        {
            if (this.config?.IsEnabled != true
                || this.partitionKeyResolver == null
                || !KeyAffinityRequestClassifier.ShouldApply(this.config.Type, originalRequest))
            {
                return null;
            }

            if (originalRequest is Amazon.DynamoDBv2.Model.BatchWriteItemRequest batchWriteItem)
            {
                return this.TryCreateBatchWriteAffinityQueryPlan(batchWriteItem);
            }

            var tableName = KeyAffinityRequestClassifier.ExtractTableName(originalRequest);
            if (string.IsNullOrEmpty(tableName))
            {
                return null;
            }

            var partitionKeyName = this.partitionKeyResolver.GetPartitionKeyName(tableName);
            if (partitionKeyName == null)
            {
                this.partitionKeyResolver.TriggerDiscovery(tableName);
                return null;
            }

            var partitionKey = KeyAffinityRequestClassifier.ExtractPartitionKey(originalRequest, partitionKeyName);
            if (partitionKey == null)
            {
                return null;
            }

            return this.LiveNodes.CreateQueryPlan(AttributeValueHasher.Hash(partitionKey));
        }

        private LazyQueryPlan? TryCreateBatchWriteAffinityQueryPlan(Amazon.DynamoDBv2.Model.BatchWriteItemRequest request)
        {
            var resolver = this.partitionKeyResolver;
            if (resolver == null)
            {
                return null;
            }

            var targets = KeyAffinityRequestClassifier.ExtractBatchWriteRoutingTargets(request);
            if (targets.Count == 0)
            {
                return null;
            }

            var activeNodes = this.LiveNodes.GetActiveNodesInternal().ToList();
            if (activeNodes.Count == 0)
            {
                return null;
            }

            var votes = new Dictionary<Uri, int>();
            var discoveryTriggered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var target in targets)
            {
                var partitionKeyName = resolver.GetPartitionKeyName(target.TableName);
                if (partitionKeyName == null)
                {
                    if (discoveryTriggered.Add(target.TableName))
                    {
                        resolver.TriggerDiscovery(target.TableName);
                    }

                    continue;
                }

                var partitionKey = target.PartitionKeyValue(partitionKeyName);
                if (partitionKey == null)
                {
                    continue;
                }

                try
                {
                    var preferredNode = LazyQueryPlan.PreferredNodeForHash(
                        activeNodes,
                        AttributeValueHasher.Hash(partitionKey));
                    if (preferredNode != null)
                    {
                        votes[preferredNode] = votes.TryGetValue(preferredNode, out var count) ? count + 1 : 1;
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            var preferredNodes = this.SelectBatchWritePreferredNodes(votes);
            return preferredNodes.Count == 0 ? null : this.LiveNodes.CreateQueryPlan(preferredNodes);
        }

        private IReadOnlyList<Uri> SelectBatchWritePreferredNodes(IReadOnlyDictionary<Uri, int> votes)
        {
            return votes
                .Where(vote => vote.Value > 0)
                .OrderByDescending(vote => vote.Value)
                .ThenBy(vote => vote.Key.ToString(), StringComparer.Ordinal)
                .Select(vote => vote.Key)
                .ToArray();
        }
    }
}
