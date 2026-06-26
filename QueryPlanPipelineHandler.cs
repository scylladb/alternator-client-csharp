// <copyright file="QueryPlanPipelineHandler.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using ScyllaDB.Alternator.KeyRouting;

    public sealed class QueryPlanPipelineHandler : AffinityQueryPlanInterceptor
    {
        private readonly Helper endpointProvider;

        public QueryPlanPipelineHandler(
            Helper endpointProvider,
            KeyRouteAffinityConfig? keyRouteAffinityConfig,
            PartitionKeyResolver? partitionKeyResolver)
            : base(
                keyRouteAffinityConfig,
                (endpointProvider ?? throw new ArgumentNullException(nameof(endpointProvider))).GetAlternatorLiveNodes(),
                partitionKeyResolver)
        {
            this.endpointProvider = endpointProvider;
        }

        public Helper EndpointProvider => this.endpointProvider;
    }
}
