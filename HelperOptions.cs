// <copyright file="HelperOptions.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System;
    using System.Collections.Generic;
    using System.Threading;
    using ScyllaDB.Alternator.KeyRouting;
    using ScyllaDB.Alternator.Routing;

    /// <summary>
    /// Configuration options for the Helper class.
    /// </summary>
    public class HelperOptions
    {
        /// <summary>
        /// Gets or sets the schema (protocol) for connecting to ScyllaDB Alternator.
        /// </summary>
        public string Schema { get; set; } = "http";

        /// <summary>
        /// Gets or sets the initial nodes for connecting to ScyllaDB Alternator.
        /// </summary>
        public List<string> InitialNodes { get; set; } = new List<string>();

        /// <summary>
        /// Gets or sets the port for connecting to ScyllaDB Alternator.
        /// </summary>
        public int Port { get; set; } = 8000;

        /// <summary>
        /// Gets or sets the datacenter name for rack and datacenter filtering.
        /// </summary>
        public string Datacenter { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the rack name for rack and datacenter filtering.
        /// </summary>
        public string Rack { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets a value indicating whether to validate the connection during initialization.
        /// Default is true.
        /// </summary>
        public bool ValidateOnInitialization { get; set; } = true;

        /// <summary>
        /// Gets or sets a value indicating whether to start the live nodes monitoring immediately.
        /// Default is true.
        /// </summary>
        public bool StartImmediately { get; set; } = true;

        /// <summary>
        /// Gets or sets the cancellation token for initialization operations.
        /// </summary>
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;

        public RoutingScope? RoutingScope { get; set; }

        public long ActiveRefreshIntervalMs { get; set; } = AlternatorConfig.DefaultActiveRefreshIntervalMs;

        public long IdleRefreshIntervalMs { get; set; } = AlternatorConfig.DefaultIdleRefreshIntervalMs;

        public TlsConfig TlsConfig { get; set; } = TlsConfig.TrustAll();

        public RequestCompressionAlgorithm CompressionAlgorithm { get; set; } = RequestCompressionAlgorithm.None;

        public int MinCompressionSizeBytes { get; set; } = AlternatorConfig.DefaultMinCompressionSizeBytes;

        public IReadOnlyList<ResponseCompressionAlgorithm> ResponseCompressionAlgorithms { get; set; } =
            AlternatorConfig.DefaultResponseCompressionAlgorithms;

        public bool OptimizeHeaders { get; set; }

        public ISet<string>? HeadersWhitelist { get; set; }

        public Func<AlternatorConfig, IEnumerable<string>>? CustomOptimizeHeaders { get; set; }

        public bool AuthenticationEnabled { get; set; } = true;

        public KeyRouteAffinityConfig? KeyRouteAffinityConfig { get; set; }

        internal AlternatorConfig ToAlternatorConfig()
        {
            var builder = AlternatorConfig.Builder()
                .WithSeedHosts(this.InitialNodes)
                .WithScheme(this.Schema)
                .WithPort(this.Port)
                .WithCompressionAlgorithm(this.CompressionAlgorithm)
                .WithMinCompressionSizeBytes(this.MinCompressionSizeBytes)
                .WithResponseCompression(this.ResponseCompressionAlgorithms)
                .WithOptimizeHeaders(this.OptimizeHeaders)
                .WithAuthenticationEnabled(this.AuthenticationEnabled)
                .WithKeyRouteAffinity(this.KeyRouteAffinityConfig)
                .WithActiveRefreshIntervalMs(this.ActiveRefreshIntervalMs)
                .WithIdleRefreshIntervalMs(this.IdleRefreshIntervalMs)
                .WithTlsConfig(this.TlsConfig);

            if (this.CustomOptimizeHeaders != null)
            {
                builder.WithCustomOptimizeHeaders(this.CustomOptimizeHeaders)
                    .WithOptimizeHeaders(this.OptimizeHeaders);
            }
            else if (this.HeadersWhitelist != null)
            {
                builder.WithHeadersWhitelist(this.HeadersWhitelist);
            }

            if (this.RoutingScope != null)
            {
                builder.WithRoutingScope(this.RoutingScope);
            }
            else if (!string.IsNullOrEmpty(this.Datacenter) && !string.IsNullOrEmpty(this.Rack))
            {
                builder.WithRoutingScope(RackScope.Of(
                    this.Datacenter,
                    this.Rack,
                    DatacenterScope.Of(this.Datacenter, ClusterScope.Create())));
            }
            else if (!string.IsNullOrEmpty(this.Datacenter))
            {
                builder.WithRoutingScope(DatacenterScope.Of(this.Datacenter, ClusterScope.Create()));
            }

            return builder.Build();
        }
    }
}
