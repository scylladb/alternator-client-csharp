// <copyright file="EndpointProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon.Runtime.Endpoints;

    [Obsolete("Use AlternatorDynamoDBClient to build AmazonDynamoDBClient instances.")]
    public class EndpointProvider : IEndpointProvider
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly AlternatorLiveNodes liveNodes;

        public EndpointProvider(Uri seedUri, string datacenter, string rack)
        {
            this.liveNodes = new AlternatorLiveNodes(seedUri, datacenter, rack);
            try
            {
                this.liveNodes.Validate();
                this.liveNodes.CheckIfRackAndDatacenterSetCorrectly();
                if (datacenter.Length != 0 || rack.Length != 0)
                {
                    if (!this.liveNodes.CheckIfRackDatacenterFeatureIsSupported())
                    {
                        Logger.Error($"server {seedUri} does not support rack or datacenter filtering");
                    }
                }
            }
            catch (Exception e)
            {
                throw new SystemException("failed to start EndpointProvider", e);
            }

            this.liveNodes.Start(CancellationToken.None);
        }

        public Endpoint ResolveEndpoint(EndpointParameters parameters)
        {
            return new Endpoint(this.liveNodes.NextAsUri().ToString());
        }
    }
}
