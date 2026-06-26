// <copyright file="Helper.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon.Runtime.Endpoints;
    using ScyllaDB.Alternator.KeyRouting;

    public class Helper : IEndpointProvider
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly AlternatorLiveNodes liveNodes;

        /// <summary>
        /// Initializes a new instance of the <see cref="Helper"/> class with the specified options.
        /// </summary>
        /// <param name="options">The helper configuration options.</param>
        public Helper(HelperOptions options)
            : this(options, options?.ToAlternatorConfig())
        {
        }

        public Helper(AlternatorConfig config)
            : this(null, config)
        {
        }

        internal Helper(
            AlternatorConfig config,
            bool validateOnInitialization,
            bool startImmediately,
            CancellationToken cancellationToken)
            : this(null, config, validateOnInitialization, startImmediately, cancellationToken)
        {
        }

        private Helper(HelperOptions? options, AlternatorConfig? config)
            : this(
                options,
                config,
                options?.ValidateOnInitialization ?? true,
                options?.StartImmediately ?? true,
                options?.CancellationToken ?? CancellationToken.None)
        {
        }

        private Helper(
            HelperOptions? options,
            AlternatorConfig? config,
            bool validateOnInitialization,
            bool startImmediately,
            CancellationToken cancellationToken)
        {
            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            this.liveNodes = new AlternatorLiveNodes(config);

            if (validateOnInitialization)
            {
                try
                {
                    this.liveNodes.Validate();
                    if (!this.liveNodes.CheckIfRoutingScopeFeatureIsSupported())
                    {
                        Logger.Error("server does not support rack or datacenter filtering");
                    }
                }
                catch (Exception e)
                {
                    throw new SystemException("failed to start Helper", e);
                }
            }

            if (startImmediately)
            {
                this.liveNodes.Start(cancellationToken);
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

        public void Stop()
        {
            this.liveNodes.Shutdown();
        }

        public bool IsRunning()
        {
            return this.liveNodes.IsRunning();
        }

        public bool CheckIfRackDatacenterFeatureIsSupported()
        {
            return this.liveNodes.CheckIfRackDatacenterFeatureIsSupported();
        }

        public AlternatorLiveNodes GetAlternatorLiveNodes()
        {
            return this.liveNodes;
        }

        public IReadOnlyList<Uri> GetLiveNodes()
        {
            return this.liveNodes.GetLiveNodes();
        }

        public Uri NextAsUri()
        {
            return this.liveNodes.NextAsUri();
        }

#pragma warning disable SA1300, IDE1006
        public void start()
        {
            this.Start();
        }

        public void stop()
        {
            this.Stop();
        }

        public bool isRunning()
        {
            return this.IsRunning();
        }

        public IReadOnlyList<Uri> getLiveNodes()
        {
            return this.GetLiveNodes();
        }

        public Uri nextAsURI()
        {
            return this.NextAsUri();
        }

        public AlternatorLiveNodes getAlternatorLiveNodes()
        {
            return this.GetAlternatorLiveNodes();
        }
#pragma warning restore SA1300, IDE1006

        internal Uri GetNodeForHash(long hash)
        {
            return this.liveNodes.GetNodeForHash(hash);
        }

        internal LazyQueryPlan CreateQueryPlan(long seed)
        {
            return this.liveNodes.CreateQueryPlan(seed);
        }

        internal LazyQueryPlan CreateQueryPlan()
        {
            return this.liveNodes.CreateQueryPlan();
        }
    }
}
