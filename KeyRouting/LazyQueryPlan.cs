// <copyright file="LazyQueryPlan.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator.KeyRouting
{
    public sealed class LazyQueryPlan : IEnumerable<Uri>
    {
        private readonly AlternatorLiveNodes? liveNodes;
        private readonly GoRand? random;
        private readonly List<Uri>? fixedNodes;
        private readonly List<Uri>? preferredNodes;
        private readonly HashSet<Uri> usedNodes = new HashSet<Uri>();
        private List<Uri>? remaining;
        private bool initialized;
        private Uri? nextNode;

        public LazyQueryPlan(IEnumerable<Uri> nodes)
            : this(nodes, Random.Shared.NextInt64())
        {
        }

        public LazyQueryPlan(IEnumerable<Uri> nodes, long seed)
        {
            if (nodes == null)
            {
                throw new ArgumentException("nodes cannot be null", nameof(nodes));
            }

            this.random = new GoRand(seed);
            this.fixedNodes = new List<Uri>(nodes);
        }

        public LazyQueryPlan(AlternatorLiveNodes liveNodes)
        {
            this.liveNodes = liveNodes ?? throw new ArgumentException("liveNodes cannot be null", nameof(liveNodes));
        }

        public LazyQueryPlan(AlternatorLiveNodes liveNodes, long seed)
        {
            this.liveNodes = liveNodes ?? throw new ArgumentException("liveNodes cannot be null", nameof(liveNodes));
            this.random = new GoRand(seed);
        }

        public LazyQueryPlan(AlternatorLiveNodes liveNodes, IEnumerable<Uri> preferredNodes)
        {
            if (liveNodes == null)
            {
                throw new ArgumentException("liveNodes cannot be null", nameof(liveNodes));
            }

            if (preferredNodes == null)
            {
                throw new ArgumentException("preferredNodes cannot be null", nameof(preferredNodes));
            }

            this.liveNodes = liveNodes;
            this.preferredNodes = new List<Uri>(preferredNodes);
        }

        public bool HasNext
        {
            get
            {
                if (this.random != null || this.preferredNodes != null || this.fixedNodes != null)
                {
                    this.EnsureInitialized();
                    return this.remaining!.Count != 0;
                }

                return this.ComputeNextNonSeeded() != null;
            }
        }

        public static List<Uri> SortedAffinityNodes(AlternatorLiveNodes liveNodes)
        {
            var nodes = GetLiveNodes(liveNodes);
            SortAffinityNodes(nodes);
            return nodes;
        }

        public static Uri? PreferredNodeForHash(AlternatorLiveNodes liveNodes, long seed)
        {
            var nodes = SortedAffinityNodes(liveNodes);
            if (nodes.Count == 0)
            {
                return null;
            }

            return nodes[new GoRand(seed).Intn(nodes.Count)];
        }

#pragma warning disable SA1300, IDE1006
        public static List<Uri> sortedAffinityNodes(AlternatorLiveNodes liveNodes)
        {
            return SortedAffinityNodes(liveNodes);
        }

        public static Uri? preferredNodeForHash(AlternatorLiveNodes liveNodes, long seed)
        {
            return PreferredNodeForHash(liveNodes, seed);
        }
#pragma warning restore SA1300, IDE1006

        public Uri Next()
        {
            if (this.random != null)
            {
                this.EnsureInitialized();
                if (this.remaining!.Count == 0)
                {
                    throw new InvalidOperationException("No more nodes available in query plan");
                }

                var index = this.random.Intn(this.remaining.Count);
                var node = this.remaining[index];
                var last = this.remaining.Count - 1;
                this.remaining[index] = this.remaining[last];
                this.remaining.RemoveAt(last);
                return node;
            }

            if (this.preferredNodes != null)
            {
                this.EnsureInitialized();
                if (this.remaining!.Count == 0)
                {
                    throw new InvalidOperationException("No more nodes available in query plan");
                }

                var node = this.remaining[0];
                this.remaining.RemoveAt(0);
                return node;
            }

            var next = this.ComputeNextNonSeeded();
            if (next == null)
            {
                throw new InvalidOperationException("No more nodes available in query plan");
            }

            this.usedNodes.Add(next);
            this.nextNode = null;
            return next;
        }

        public IEnumerator<Uri> GetEnumerator()
        {
            while (this.HasNext)
            {
                yield return this.Next();
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return this.GetEnumerator();
        }

#pragma warning disable SA1300, IDE1006
        public bool hasNext()
        {
            return this.HasNext;
        }

        public Uri next()
        {
            return this.Next();
        }

        public IEnumerator<Uri> iterator()
        {
            return this.GetEnumerator();
        }
#pragma warning restore SA1300, IDE1006

        private static List<Uri> GetLiveNodes(AlternatorLiveNodes liveNodes)
        {
            if (liveNodes == null)
            {
                throw new ArgumentException("liveNodes cannot be null", nameof(liveNodes));
            }

            return liveNodes.GetLiveNodesInternal().ToList();
        }

        private static void SortAffinityNodes(List<Uri> nodes)
        {
            nodes.Sort((left, right) => string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal));
        }

        private static List<Uri> OrderPreferredNodesFirst(List<Uri> sortedNodes, IEnumerable<Uri> preferredNodes)
        {
            if (preferredNodes == null)
            {
                throw new ArgumentException("preferredNodes cannot be null", nameof(preferredNodes));
            }

            var ordered = new List<Uri>(sortedNodes.Count);
            var remainingNodes = new List<Uri>(sortedNodes);
            foreach (var preferredNode in preferredNodes)
            {
                var index = remainingNodes.IndexOf(preferredNode);
                if (index >= 0)
                {
                    ordered.Add(remainingNodes[index]);
                    remainingNodes.RemoveAt(index);
                }
            }

            ordered.AddRange(remainingNodes);
            return ordered;
        }

        private void EnsureInitialized()
        {
            if (this.initialized)
            {
                return;
            }

            if (this.fixedNodes != null)
            {
                this.remaining = new List<Uri>(this.fixedNodes);
                SortAffinityNodes(this.remaining);
            }
            else
            {
                this.remaining = SortedAffinityNodes(this.liveNodes!);
                if (this.preferredNodes != null)
                {
                    this.remaining = OrderPreferredNodesFirst(this.remaining, this.preferredNodes);
                }
            }

            this.initialized = true;
        }

        private Uri? ComputeNextNonSeeded()
        {
            if (this.nextNode != null)
            {
                return this.nextNode;
            }

            if (this.liveNodes == null)
            {
                this.EnsureInitialized();
                if (this.remaining!.Count == 0)
                {
                    return null;
                }

                this.nextNode = this.remaining[Random.Shared.Next(this.remaining.Count)];
                return this.nextNode;
            }

            var availableNodes = this.liveNodes.GetLiveNodesInternal()
                .Where(node => !this.usedNodes.Contains(node))
                .ToList();
            if (availableNodes.Count == 0)
            {
                return null;
            }

            this.nextNode = availableNodes[Random.Shared.Next(availableNodes.Count)];
            return this.nextNode;
        }
    }
}
