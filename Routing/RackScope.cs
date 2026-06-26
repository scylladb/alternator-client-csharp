// <copyright file="RackScope.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator.Routing
{
    public sealed class RackScope : RoutingScope
    {
        private RackScope(string datacenter, string rack, RoutingScope? fallback)
        {
            if (string.IsNullOrEmpty(datacenter))
            {
                throw new ArgumentException("datacenter cannot be null or empty", nameof(datacenter));
            }

            if (string.IsNullOrEmpty(rack))
            {
                throw new ArgumentException("rack cannot be null or empty", nameof(rack));
            }

            this.Datacenter = datacenter;
            this.Rack = rack;
            this.Fallback = fallback;
        }

        public string Datacenter { get; }

        public string Rack { get; }

        public string Name => "Rack";

        public string Description => $"Rack {this.Rack} in Datacenter {this.Datacenter}";

        public RoutingScope? Fallback { get; }

        public string LocalNodesQuery =>
            $"dc={this.Datacenter}&rack={this.Rack}";

        public static RackScope Of(string datacenter, string rack, RoutingScope? fallback)
        {
            return new RackScope(datacenter, rack, fallback);
        }

#pragma warning disable SA1300, IDE1006
        public static RackScope of(string datacenter, string rack, RoutingScope? fallback)
        {
            return Of(datacenter, rack, fallback);
        }

        public string getDatacenter()
        {
            return this.Datacenter;
        }

        public string getRack()
        {
            return this.Rack;
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
            return $"RackScope{{datacenter='{this.Datacenter}', rack='{this.Rack}', fallback={this.Fallback?.ToString() ?? "null"}}}";
        }

        public override bool Equals(object? obj)
        {
            return obj is RackScope other
                && string.Equals(this.Datacenter, other.Datacenter, StringComparison.Ordinal)
                && string.Equals(this.Rack, other.Rack, StringComparison.Ordinal)
                && EqualityComparer<RoutingScope?>.Default.Equals(this.Fallback, other.Fallback);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(this.Datacenter, this.Rack, this.Fallback);
        }
    }
}
