// <copyright file="Helper.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon.Runtime.Endpoints;

    public class Helper : IEndpointProvider
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly AlternatorLiveNodes liveNodes;

        public Helper(Uri seedUri, string datacenter, string rack)
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
                throw new SystemException("failed to start Helper", e);
            }

            this.liveNodes.Start(CancellationToken.None);
        }

        public Endpoint ResolveEndpoint(EndpointParameters parameters)
        {
            return new Endpoint(this.liveNodes.NextAsUri().ToString());
        }
    }
}