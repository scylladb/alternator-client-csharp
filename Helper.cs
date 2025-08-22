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

        /// <summary>
        /// Initializes a new instance of the <see cref="Helper"/> class with the specified parameters.
        /// </summary>
        /// <param name="seedUri">The seed URI for connecting to ScyllaDB Alternator.</param>
        /// <param name="datacenter">The datacenter name for filtering.</param>
        /// <param name="rack">The rack name for filtering.</param>
        public Helper(Uri seedUri, string datacenter, string rack)
            : this(HelperOptionsBuilder.Create()
                .WithSeedUri(seedUri)
                .WithDatacenter(datacenter)
                .WithRack(rack)
                .Build())
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Helper"/> class with the specified options.
        /// </summary>
        /// <param name="options">The helper configuration options.</param>
        public Helper(HelperOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (options.SeedUri == null)
            {
                throw new ArgumentException("SeedUri cannot be null.", nameof(options));
            }

            this.liveNodes = new AlternatorLiveNodes(options.SeedUri, options.Datacenter, options.Rack);

            if (options.ValidateOnInitialization)
            {
                try
                {
                    this.liveNodes.Validate();
                    this.liveNodes.CheckIfRackAndDatacenterSetCorrectly();
                    if (options.Datacenter.Length != 0 || options.Rack.Length != 0)
                    {
                        if (!this.liveNodes.CheckIfRackDatacenterFeatureIsSupported())
                        {
                            Logger.Error($"server {options.SeedUri} does not support rack or datacenter filtering");
                        }
                    }
                }
                catch (Exception e)
                {
                    throw new SystemException("failed to start Helper", e);
                }
            }

            if (options.StartImmediately)
            {
                this.liveNodes.Start(options.CancellationToken);
            }
        }

        public Endpoint ResolveEndpoint(EndpointParameters parameters)
        {
            return new Endpoint(this.liveNodes.NextAsUri().ToString());
        }

        /// <summary>
        /// Starts the live nodes monitoring if it was deferred during initialization.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token for the operation.</param>
        public void Start(CancellationToken cancellationToken = default)
        {
            this.liveNodes.Start(cancellationToken);
        }
    }
}