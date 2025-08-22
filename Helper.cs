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
        /// Initializes a new instance of the <see cref="Helper"/> class with the specified options.
        /// </summary>
        /// <param name="options">The helper configuration options.</param>
        public Helper(HelperOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            // Support both SeedUri (legacy) and InitialNodes (new) approaches
            if (options.SeedUri != null)
            {
                // Legacy approach with single seed URI
                this.liveNodes = new AlternatorLiveNodes(options.SeedUri, options.Datacenter, options.Rack);
            }
            else if (options.InitialNodes != null && options.InitialNodes.Count > 0)
            {
                // New approach with multiple initial nodes
                var nodeUris = new List<Uri>();
                foreach (var node in options.InitialNodes)
                {
                    var uri = new Uri($"{options.Schema}://{node}:{options.Port}");
                    nodeUris.Add(uri);
                }
                this.liveNodes = new AlternatorLiveNodes(nodeUris, options.Schema, options.Port, options.Datacenter, options.Rack);
            }
            else
            {
                throw new ArgumentException("Either SeedUri or InitialNodes must be provided.", nameof(options));
            }

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
                            var errorSource = options.SeedUri?.ToString() ?? string.Join(", ", options.InitialNodes);
                            Logger.Error($"server {errorSource} does not support rack or datacenter filtering");
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