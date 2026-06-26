// <copyright file="RoutingScope.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator.Routing
{
#pragma warning disable SA1302
    public interface RoutingScope
    {
        string Name { get; }

        string Description { get; }

        RoutingScope? Fallback { get; }

        string LocalNodesQuery { get; }

#pragma warning disable SA1300, IDE1006
        string getName()
        {
            return this.Name;
        }

        string getDescription()
        {
            return this.Description;
        }

        RoutingScope? getFallback()
        {
            return this.Fallback;
        }

        string getLocalNodesQuery()
        {
            return this.LocalNodesQuery;
        }
#pragma warning restore SA1300, IDE1006
    }
#pragma warning restore SA1302
}
