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

namespace ScyllaDB.Alternator.KeyRouting
{
    public sealed class LazyQueryPlan : IEnumerable<Uri>
    {
        private readonly AlternatorLiveNodes? liveNodes;
        private readonly GoRand? random;
        private readonly List<Uri>? fixedNodes;
        private readonly List<Uri>? fixedFallbackNodes;
        private readonly List<Uri>? preferredNodes;
        private readonly HashSet<Uri> usedNodes = new HashSet<Uri>();
        private List<Uri>? remaining;
        private List<Uri>? fallbackRemaining;
        private bool initialized;
        private Uri? nextNode;

        public LazyQueryPlan(IEnumerable<Uri> nodes)
            : this(nodes, Random.Shared.NextInt64())
        {
        }

        public LazyQueryPlan(IEnumerable<Uri> nodes, long seed)
            : this(nodes, Array.Empty<Uri>(), seed)
        {
        }

        public LazyQueryPlan(AlternatorLiveNodes liveNodes)
        {
            this.liveNodes = liveNodes ?? throw new ArgumentException("liveNodes cannot be null", nameof(liveNodes));
        }

        public LazyQueryPlan(AlternatorLiveNodes liveNodes, long seed)
        {
            if (liveNodes == null)
            {
                throw new ArgumentException("liveNodes cannot be null", nameof(liveNodes));
            }

            var nodes = liveNodes.CaptureAffinityQueryPlanNodes();
            this.random = new GoRand(seed);
            this.fixedNodes = new List<Uri>(nodes.PrimaryNodes);
            this.fixedFallbackNodes = new List<Uri>(nodes.FallbackNodes);
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

            var nodes = liveNodes.CaptureAffinityQueryPlanNodes();
            this.fixedNodes = new List<Uri>(nodes.PrimaryNodes);
            this.fixedFallbackNodes = new List<Uri>(nodes.FallbackNodes);
            this.preferredNodes = new List<Uri>(preferredNodes);
        }

#pragma warning disable SA1202
        internal LazyQueryPlan(
            IEnumerable<Uri> primaryNodes,
            IEnumerable<Uri> fallbackNodes,
            long seed)
        {
            if (primaryNodes == null)
            {
                throw new ArgumentException("primaryNodes cannot be null", nameof(primaryNodes));
            }

            if (fallbackNodes == null)
            {
                throw new ArgumentException("fallbackNodes cannot be null", nameof(fallbackNodes));
            }

            this.random = new GoRand(seed);
            this.fixedNodes = new List<Uri>(primaryNodes);
            this.fixedFallbackNodes = new List<Uri>(fallbackNodes);
        }

        internal LazyQueryPlan(
            IEnumerable<Uri> primaryNodes,
            IEnumerable<Uri> fallbackNodes,
            IEnumerable<Uri> preferredNodes)
        {
            if (primaryNodes == null)
            {
                throw new ArgumentException("primaryNodes cannot be null", nameof(primaryNodes));
            }

            if (fallbackNodes == null)
            {
                throw new ArgumentException("fallbackNodes cannot be null", nameof(fallbackNodes));
            }

            if (preferredNodes == null)
            {
                throw new ArgumentException("preferredNodes cannot be null", nameof(preferredNodes));
            }

            this.fixedNodes = new List<Uri>(primaryNodes);
            this.fixedFallbackNodes = new List<Uri>(fallbackNodes);
            this.preferredNodes = new List<Uri>(preferredNodes);
        }

        public bool HasNext
        {
            get
            {
                if (this.random != null || this.preferredNodes != null || this.fixedNodes != null)
                {
                    this.EnsureInitialized();
                    return this.HasRemainingInitializedNodes();
                }

                return this.ComputeNextNonSeeded() != null;
            }
        }

        public static List<Uri> SortedAffinityNodes(AlternatorLiveNodes liveNodes)
        {
            if (liveNodes == null)
            {
                throw new ArgumentException("liveNodes cannot be null", nameof(liveNodes));
            }

            var nodes = liveNodes.CaptureAffinityQueryPlanNodes().PrimaryNodes.ToList();
            SortAffinityNodes(nodes);
            return nodes;
        }

        public static Uri? PreferredNodeForHash(AlternatorLiveNodes liveNodes, long seed)
        {
            var nodes = SortedAffinityNodes(liveNodes);
            return PreferredNodeForHash(nodes, seed);
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
                    if (this.fallbackRemaining?.Count > 0)
                    {
                        this.remaining = this.fallbackRemaining;
                        this.fallbackRemaining = null;
                    }
                    else
                    {
                        throw new InvalidOperationException("No more nodes available in query plan");
                    }
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
                    if (this.fallbackRemaining?.Count > 0)
                    {
                        this.remaining = this.fallbackRemaining;
                        this.fallbackRemaining = null;
                    }
                    else
                    {
                        throw new InvalidOperationException("No more nodes available in query plan");
                    }
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
#pragma warning restore SA1202

        internal static Uri? PreferredNodeForHash(IEnumerable<Uri> nodes, long seed)
        {
            if (nodes == null)
            {
                throw new ArgumentException("nodes cannot be null", nameof(nodes));
            }

            var sortedNodes = nodes.ToList();
            SortAffinityNodes(sortedNodes);
            if (sortedNodes.Count == 0)
            {
                return null;
            }

            return sortedNodes[new GoRand(seed).Intn(sortedNodes.Count)];
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
                this.fallbackRemaining = new List<Uri>(this.fixedFallbackNodes ?? Enumerable.Empty<Uri>());
                SortAffinityNodes(this.remaining);
                SortAffinityNodes(this.fallbackRemaining);
                if (this.preferredNodes != null)
                {
                    this.remaining = OrderPreferredNodesFirst(this.remaining, this.preferredNodes);
                }
            }
            else
            {
                throw new InvalidOperationException("Seeded query plans require a captured node snapshot");
            }

            this.initialized = true;
        }

        private bool HasRemainingInitializedNodes()
        {
            return this.remaining!.Count != 0 || this.fallbackRemaining?.Count > 0;
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

            var availableNodes = this.liveNodes.GetActiveNodesInternal()
                .Where(node => !this.usedNodes.Contains(node))
                .ToList();
            if (availableNodes.Count == 0)
            {
                availableNodes = this.liveNodes.GetQuarantinedNodesInternal()
                    .Where(node => !this.usedNodes.Contains(node))
                    .ToList();
            }

            if (availableNodes.Count == 0)
            {
                return null;
            }

            this.nextNode = availableNodes[Random.Shared.Next(availableNodes.Count)];
            return this.nextNode;
        }
    }
}
