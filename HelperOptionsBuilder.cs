// <copyright file="HelperOptionsBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    /// <summary>
    /// Builder for configuring Helper options using a fluent API.
    /// </summary>
    public class HelperOptionsBuilder
    {
        private readonly HelperOptions options;

        /// <summary>
        /// Initializes a new instance of the <see cref="HelperOptionsBuilder"/> class.
        /// </summary>
        public HelperOptionsBuilder()
        {
            this.options = new HelperOptions();
        }

        /// <summary>
        /// Creates a new HelperOptionsBuilder instance.
        /// </summary>
        /// <returns>A new HelperOptionsBuilder.</returns>
        public static HelperOptionsBuilder Create()
        {
            return new HelperOptionsBuilder();
        }

        /// <summary>
        /// Sets the datacenter for rack and datacenter filtering.
        /// </summary>
        /// <param name="datacenter">The datacenter name.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithDatacenter(string datacenter)
        {
            this.options.Datacenter = datacenter ?? string.Empty;
            return this;
        }

        /// <summary>
        /// Sets the rack for rack and datacenter filtering.
        /// </summary>
        /// <param name="rack">The rack name.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithRack(string rack)
        {
            this.options.Rack = rack ?? string.Empty;
            return this;
        }

        /// <summary>
        /// Sets both datacenter and rack for filtering.
        /// </summary>
        /// <param name="datacenter">The datacenter name.</param>
        /// <param name="rack">The rack name.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithDatacenterAndRack(string datacenter, string rack)
        {
            this.options.Datacenter = datacenter ?? string.Empty;
            this.options.Rack = rack ?? string.Empty;
            return this;
        }

        /// <summary>
        /// Sets whether to validate the connection during initialization.
        /// </summary>
        /// <param name="validate">True to validate, false to skip validation.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithValidation(bool validate)
        {
            this.options.ValidateOnInitialization = validate;
            return this;
        }

        /// <summary>
        /// Disables validation during initialization.
        /// </summary>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithoutValidation()
        {
            this.options.ValidateOnInitialization = false;
            return this;
        }

        /// <summary>
        /// Sets whether to start live nodes monitoring immediately.
        /// </summary>
        /// <param name="startImmediately">True to start immediately, false to defer.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithImmediateStart(bool startImmediately)
        {
            this.options.StartImmediately = startImmediately;
            return this;
        }

        /// <summary>
        /// Defers the start of live nodes monitoring.
        /// </summary>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithDeferredStart()
        {
            this.options.StartImmediately = false;
            return this;
        }

        /// <summary>
        /// Sets the cancellation token for initialization operations.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithCancellationToken(CancellationToken cancellationToken)
        {
            this.options.CancellationToken = cancellationToken;
            return this;
        }

        /// <summary>
        /// Sets the initial nodes for connecting to ScyllaDB Alternator.
        /// </summary>
        /// <param name="initialNodes">The list of initial node hostnames or IPs.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithInitialNodeUri(Uri uri)
        {
            this.options.InitialNodes = new List<string> { uri.hostname.ToString() };
            this.options.Port = uri.Port;
            this.options.Schema = uri.Scheme;
            return this;
        }


        /// <summary>
        /// Sets the initial nodes for connecting to ScyllaDB Alternator.
        /// </summary>
        /// <param name="initialNodes">The list of initial node hostnames or IPs.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithInitialNodes(List<string> initialNodes)
        {
            this.options.InitialNodes = initialNodes ?? throw new ArgumentNullException(nameof(initialNodes));
            return this;
        }

        /// <summary>
        /// Sets the initial nodes for connecting to ScyllaDB Alternator.
        /// </summary>
        /// <param name="initialNodes">The array of initial node hostnames or IPs.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithInitialNodes(params string[] initialNodes)
        {
            if (initialNodes == null)
            {
                throw new ArgumentNullException(nameof(initialNodes));
            }

            this.options.InitialNodes = new List<string>(initialNodes);
            return this;
        }

        /// <summary>
        /// Adds an initial node to the list of nodes for connecting to ScyllaDB Alternator.
        /// </summary>
        /// <param name="node">The node hostname or IP to add.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder AddInitialNode(string node)
        {
            if (string.IsNullOrWhiteSpace(node))
            {
                throw new ArgumentException("Node cannot be null or empty.", nameof(node));
            }

            this.options.InitialNodes.Add(node);
            return this;
        }

        /// <summary>
        /// Sets the schema (protocol) for connecting to ScyllaDB Alternator.
        /// </summary>
        /// <param name="schema">The schema (http or https).</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithSchema(string schema)
        {
            this.options.Schema = schema ?? throw new ArgumentNullException(nameof(schema));
            return this;
        }

        /// <summary>
        /// Sets the port for connecting to ScyllaDB Alternator.
        /// </summary>
        /// <param name="port">The port number.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithPort(int port)
        {
            this.options.Port = port;
            return this;
        }

        /// <summary>
        /// Builds the HelperOptions instance.
        /// </summary>
        /// <returns>The configured HelperOptions.</returns>
        /// <exception cref="InvalidOperationException">Thrown when required properties are not set.</exception>
        public HelperOptions Build()
        {
            if (this.options.InitialNodes == null || this.options.InitialNodes.Count == 0)
            {
                throw new InvalidOperationException("Either InitialNodes must be set before building HelperOptions.");
            }

            return this.options;
        }
    }
}
