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
        /// Sets the seed URI for connecting to ScyllaDB Alternator.
        /// </summary>
        /// <param name="seedUri">The seed URI.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithSeedUri(Uri seedUri)
        {
            this.options.SeedUri = seedUri ?? throw new ArgumentNullException(nameof(seedUri));
            return this;
        }

        /// <summary>
        /// Sets the seed URI for connecting to ScyllaDB Alternator.
        /// </summary>
        /// <param name="seedUri">The seed URI as a string.</param>
        /// <returns>The builder instance for method chaining.</returns>
        public HelperOptionsBuilder WithSeedUri(string seedUri)
        {
            if (string.IsNullOrWhiteSpace(seedUri))
            {
                throw new ArgumentException("Seed URI cannot be null or empty.", nameof(seedUri));
            }

            this.options.SeedUri = new Uri(seedUri);
            return this;
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
        /// Builds the HelperOptions instance.
        /// </summary>
        /// <returns>The configured HelperOptions.</returns>
        /// <exception cref="InvalidOperationException">Thrown when required properties are not set.</exception>
        public HelperOptions Build()
        {
            if (this.options.SeedUri == null)
            {
                throw new InvalidOperationException("SeedUri must be set before building HelperOptions.");
            }

            return this.options;
        }
    }
}
