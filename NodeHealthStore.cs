// <copyright file="NodeHealthStore.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    internal sealed class NodeHealthStore
    {
        private readonly object sync = new object();
        private readonly NodeHealthStoreConfig config;
        private readonly Dictionary<Uri, NodeHealthStatus> statuses = new Dictionary<Uri, NodeHealthStatus>();

        internal NodeHealthStore(NodeHealthStoreConfig config, IEnumerable<Uri> initialNodes)
        {
            this.config = NodeHealthStoreConfig.Normalize(config);
            if (initialNodes == null)
            {
                throw new ArgumentNullException(nameof(initialNodes));
            }

            foreach (var node in initialNodes)
            {
                this.AddNode(node);
            }
        }

        internal IReadOnlyList<Uri> GetActiveNodes()
        {
            if (this.config.Disabled)
            {
                return this.GetNodes(_ => true);
            }

            return this.GetNodes(status => status.State == NodeHealthState.Active);
        }

        internal IReadOnlyList<Uri> GetQuarantinedNodes()
        {
            if (this.config.Disabled)
            {
                return Array.Empty<Uri>();
            }

            return this.GetNodes(status => status.State == NodeHealthState.Quarantined);
        }

        internal IReadOnlyList<Uri> GetDownNodes()
        {
            if (this.config.Disabled)
            {
                return Array.Empty<Uri>();
            }

            return this.GetNodes(status => status.State == NodeHealthState.Down);
        }

        internal NodeHealthStatus? GetNodeStatus(Uri node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            var normalizedNode = NormalizeNode(node);
            lock (this.sync)
            {
                return this.statuses.TryGetValue(normalizedNode, out var status)
                    ? status.Copy()
                    : null;
            }
        }

        internal void AddNode(Uri node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            var normalizedNode = NormalizeNode(node);
            lock (this.sync)
            {
                this.statuses.TryAdd(normalizedNode, new NodeHealthStatus());
            }
        }

        internal void RemoveNode(Uri node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            var normalizedNode = NormalizeNode(node);
            lock (this.sync)
            {
                this.statuses.Remove(normalizedNode);
            }
        }

        internal void SetKnownNodes(IEnumerable<Uri> nodes)
        {
            if (nodes == null)
            {
                throw new ArgumentNullException(nameof(nodes));
            }

            var normalizedNodes = new HashSet<Uri>(nodes.Select(NormalizeNode));
            lock (this.sync)
            {
                foreach (var node in normalizedNodes)
                {
                    this.statuses.TryAdd(node, new NodeHealthStatus());
                }

                var removedNodes = this.statuses.Keys
                    .Where(node => !normalizedNodes.Contains(node))
                    .ToList();
                foreach (var removedNode in removedNodes)
                {
                    this.statuses.Remove(removedNode);
                }
            }
        }

        internal void ReportNodeResult(Uri node, NodeHealthObservation observation)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            if (this.config.Disabled)
            {
                return;
            }

            var normalizedNode = NormalizeNode(node);
            lock (this.sync)
            {
                if (!this.statuses.TryGetValue(normalizedNode, out var status))
                {
                    return;
                }

                this.ApplyObservation(status, observation);
            }
        }

        internal async Task<IReadOnlyList<Uri>> ProbeDownNodesAsync(
            Func<Uri, NodeHealthStatus, CancellationToken, Task<NodeHealthObservation?>> probe,
            CancellationToken cancellationToken)
        {
            if (probe == null)
            {
                throw new ArgumentNullException(nameof(probe));
            }

            if (this.config.Disabled)
            {
                return Array.Empty<Uri>();
            }

            var downNodes = this.GetDownNodeSnapshot();
            var changedNodes = new List<Uri>();
            foreach (var item in downNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var observation = await probe(item.Node, item.Status, cancellationToken).ConfigureAwait(false);
                if (observation == null)
                {
                    continue;
                }

                this.ReportNodeResult(item.Node, observation.Value);
                var updatedStatus = this.GetNodeStatus(item.Node);
                if (updatedStatus?.State != NodeHealthState.Down)
                {
                    changedNodes.Add(item.Node);
                }
            }

            return SortNodes(changedNodes).AsReadOnly();
        }

        private static Uri NormalizeNode(Uri node)
        {
            if (!node.IsAbsoluteUri)
            {
                throw new ArgumentException("node URI must be absolute", nameof(node));
            }

            return new UriBuilder(node.Scheme, node.Host, node.Port).Uri;
        }

        private static List<Uri> SortNodes(IEnumerable<Uri> nodes)
        {
            var sorted = nodes.ToList();
            sorted.Sort((left, right) => string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal));
            return sorted;
        }

        private IReadOnlyList<Uri> GetNodes(Func<NodeHealthStatus, bool> predicate)
        {
            lock (this.sync)
            {
                return SortNodes(this.statuses
                    .Where(item => predicate(item.Value))
                    .Select(item => item.Key))
                    .AsReadOnly();
            }
        }

        private List<(Uri Node, NodeHealthStatus Status)> GetDownNodeSnapshot()
        {
            lock (this.sync)
            {
                return this.statuses
                    .Where(item => item.Value.State == NodeHealthState.Down)
                    .Select(item => (item.Key, item.Value.Copy()))
                    .ToList();
            }
        }

        private void ApplyObservation(NodeHealthStatus status, NodeHealthObservation observation)
        {
            switch (observation)
            {
                case NodeHealthObservation.ConnectionFailure:
                    status.State = NodeHealthState.Down;
                    status.ConsecutiveServerErrors = this.config.ConsecutiveServerErrorThreshold;
                    status.ConsecutiveSuccesses = 0;
                    break;
                case NodeHealthObservation.ServerError:
                    status.ConsecutiveServerErrors++;
                    status.ConsecutiveSuccesses = 0;
                    if (status.ConsecutiveServerErrors >= this.config.ConsecutiveServerErrorThreshold)
                    {
                        status.State = NodeHealthState.Down;
                    }

                    break;
                case NodeHealthObservation.RequestTimeout:
                    break;
                case NodeHealthObservation.Success:
                    status.ConsecutiveServerErrors = 0;
                    if (status.State == NodeHealthState.Down)
                    {
                        status.State = NodeHealthState.Quarantined;
                        status.ConsecutiveSuccesses = 1;
                    }
                    else if (status.State == NodeHealthState.Quarantined)
                    {
                        status.ConsecutiveSuccesses++;
                    }
                    else
                    {
                        status.ConsecutiveSuccesses = Math.Min(
                            this.config.QuarantineSuccessThreshold,
                            status.ConsecutiveSuccesses + 1);
                    }

                    if (status.State == NodeHealthState.Quarantined
                        && status.ConsecutiveSuccesses >= this.config.QuarantineSuccessThreshold)
                    {
                        status.State = NodeHealthState.Active;
                        status.ConsecutiveSuccesses = this.config.QuarantineSuccessThreshold;
                    }

                    break;
            }

            status.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }
}
