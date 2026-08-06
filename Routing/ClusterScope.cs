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

namespace ScyllaDB.Alternator.Routing
{
    /// <summary>
    /// Routes requests across the unfiltered node list returned by the bare /localnodes endpoint.
    /// </summary>
    /// <remarks>
    /// Alternator usually returns only the nodes visible from the node that handled /localnodes.
    /// In multi-datacenter deployments, callers must provide reachable seed hosts from every
    /// datacenter; otherwise cluster scope cannot discover or route through the missing
    /// datacenters.
    /// </remarks>
    public sealed class ClusterScope : RoutingScope
    {
        private static readonly ClusterScope Instance = new ClusterScope();

        private ClusterScope()
        {
        }

        public string Name => "Cluster";

        public string Description => "Cluster (all nodes)";

        public RoutingScope? Fallback => null;

        public string LocalNodesQuery => string.Empty;

        public static ClusterScope Create()
        {
            return Instance;
        }

#pragma warning disable SA1300, IDE1006
        public static ClusterScope create()
        {
            return Create();
        }

        public string getName()
        {
            return this.Name;
        }

        public string getDescription()
        {
            return this.Description;
        }

        public RoutingScope? getFallback()
        {
            return this.Fallback;
        }

        public string getLocalNodesQuery()
        {
            return this.LocalNodesQuery;
        }
#pragma warning restore SA1300, IDE1006

        public override string ToString()
        {
            return "ClusterScope{}";
        }

        public override bool Equals(object? obj)
        {
            return obj is ClusterScope;
        }

        public override int GetHashCode()
        {
            return typeof(ClusterScope).GetHashCode();
        }
    }
}
