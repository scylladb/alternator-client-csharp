// <copyright file="HelperOptions.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System;
    using System.Collections.Generic;
    using System.Threading;

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
    }
}
