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
    public sealed class DatacenterScope : RoutingScope
    {
        private DatacenterScope(string datacenter, RoutingScope? fallback)
        {
            if (string.IsNullOrEmpty(datacenter))
            {
                throw new ArgumentException("datacenter cannot be null or empty", nameof(datacenter));
            }

            this.Datacenter = datacenter;
            this.Fallback = fallback;
        }

        public string Datacenter { get; }

        public string Name => "Datacenter";

        public string Description => $"Datacenter {this.Datacenter}";

        public RoutingScope? Fallback { get; }

        public string LocalNodesQuery => $"dc={this.Datacenter}";

        public static DatacenterScope Of(string datacenter, RoutingScope? fallback)
        {
            return new DatacenterScope(datacenter, fallback);
        }

#pragma warning disable SA1300, IDE1006
        public static DatacenterScope of(string datacenter, RoutingScope? fallback)
        {
            return Of(datacenter, fallback);
        }

        public string getDatacenter()
        {
            return this.Datacenter;
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
            return $"DatacenterScope{{datacenter='{this.Datacenter}', fallback={this.Fallback?.ToString() ?? "null"}}}";
        }

        public override bool Equals(object? obj)
        {
            return obj is DatacenterScope other
                && string.Equals(this.Datacenter, other.Datacenter, StringComparison.Ordinal)
                && EqualityComparer<RoutingScope?>.Default.Equals(this.Fallback, other.Fallback);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(this.Datacenter, this.Fallback);
        }
    }
}
