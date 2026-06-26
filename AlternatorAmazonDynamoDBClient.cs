// <copyright file="AlternatorAmazonDynamoDBClient.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon.DynamoDBv2;
    using Amazon.Runtime;
    using Amazon.Runtime.Internal;
    using ScyllaDB.Alternator.KeyRouting;

    internal sealed class AlternatorAmazonDynamoDBClient : AmazonDynamoDBClient
    {
        private static readonly AsyncLocal<AlternatorConfig?> ConstructionConfig = new AsyncLocal<AlternatorConfig?>();
        private static readonly AsyncLocal<Helper?> ConstructionEndpointProvider = new AsyncLocal<Helper?>();
        private static readonly AsyncLocal<PartitionKeyResolver?> ConstructionPartitionKeyResolver = new AsyncLocal<PartitionKeyResolver?>();
        private static readonly AsyncLocal<Func<string, string?>?> ConstructionUserAgentTransformer = new AsyncLocal<Func<string, string?>?>();
        private static readonly AsyncLocal<bool> ConstructionAppendDefaultUserAgentToken = new AsyncLocal<bool>();
        private readonly AlternatorConfig? alternatorConfig;
        private readonly Helper? endpointProvider;
        private readonly PartitionKeyResolver? partitionKeyResolver;
        private readonly bool appendDefaultUserAgentToken;
        private readonly Func<string, string?>? userAgentTransformer;
        private int disposeSignaled;

        private AlternatorAmazonDynamoDBClient(
            AWSCredentials credentials,
            AmazonDynamoDBConfig clientConfig,
            AlternatorConfig alternatorConfig,
            Helper endpointProvider,
            PartitionKeyResolver? partitionKeyResolver,
            Func<string, string?>? userAgentTransformer,
            bool appendDefaultUserAgentToken)
            : base(credentials, clientConfig)
        {
            this.alternatorConfig = alternatorConfig;
            this.endpointProvider = endpointProvider;
            this.partitionKeyResolver = partitionKeyResolver;
            this.userAgentTransformer = userAgentTransformer;
            this.appendDefaultUserAgentToken = appendDefaultUserAgentToken;
        }

        internal static AlternatorAmazonDynamoDBClient Create(
            AWSCredentials credentials,
            AmazonDynamoDBConfig clientConfig,
            AlternatorConfig alternatorConfig,
            Helper endpointProvider,
            PartitionKeyResolver? partitionKeyResolver,
            Func<string, string?>? userAgentTransformer,
            bool appendDefaultUserAgentToken)
        {
            var previousConfig = ConstructionConfig.Value;
            var previousEndpointProvider = ConstructionEndpointProvider.Value;
            var previousPartitionKeyResolver = ConstructionPartitionKeyResolver.Value;
            var previousUserAgentTransformer = ConstructionUserAgentTransformer.Value;
            var previousAppendDefaultUserAgentToken = ConstructionAppendDefaultUserAgentToken.Value;
            ConstructionConfig.Value = alternatorConfig;
            ConstructionEndpointProvider.Value = endpointProvider;
            ConstructionPartitionKeyResolver.Value = partitionKeyResolver;
            ConstructionUserAgentTransformer.Value = userAgentTransformer;
            ConstructionAppendDefaultUserAgentToken.Value = appendDefaultUserAgentToken;
            try
            {
                return new AlternatorAmazonDynamoDBClient(
                    credentials,
                    clientConfig,
                    alternatorConfig,
                    endpointProvider,
                    partitionKeyResolver,
                    userAgentTransformer,
                    appendDefaultUserAgentToken);
            }
            finally
            {
                ConstructionConfig.Value = previousConfig;
                ConstructionEndpointProvider.Value = previousEndpointProvider;
                ConstructionPartitionKeyResolver.Value = previousPartitionKeyResolver;
                ConstructionUserAgentTransformer.Value = previousUserAgentTransformer;
                ConstructionAppendDefaultUserAgentToken.Value = previousAppendDefaultUserAgentToken;
            }
        }

        protected override void CustomizeRuntimePipeline(RuntimePipeline pipeline)
        {
            base.CustomizeRuntimePipeline(pipeline);
            var config = this.alternatorConfig ?? ConstructionConfig.Value;
            var userAgentTransformer = this.userAgentTransformer ?? ConstructionUserAgentTransformer.Value;
            var appendDefaultUserAgentToken = this.appendDefaultUserAgentToken || ConstructionAppendDefaultUserAgentToken.Value;
            if (userAgentTransformer != null || (config?.UserAgentEnabled == true && appendDefaultUserAgentToken))
            {
                pipeline.AddHandlerBefore<Signer>(new AlternatorUserAgentPipelineHandler(userAgentTransformer, appendDefaultUserAgentToken));
            }

            if (config != null && config.CompressionAlgorithm == RequestCompressionAlgorithm.Gzip)
            {
                pipeline.AddHandlerBefore<CompressionHandler>(new GzipRequestPipelineHandler(config.MinCompressionSizeBytes));
            }

            var helper = this.endpointProvider ?? ConstructionEndpointProvider.Value;
            if (helper != null)
            {
                var resolver = this.partitionKeyResolver ?? ConstructionPartitionKeyResolver.Value;
                var liveNodes = helper.GetAlternatorLiveNodes();
                if (config?.KeyRouteAffinityConfig?.IsEnabled == true && resolver != null)
                {
                    pipeline.AddHandlerBefore<Signer>(new AffinityQueryPlanInterceptor(config.KeyRouteAffinityConfig, liveNodes, resolver));
                }
                else
                {
                    pipeline.AddHandlerBefore<Signer>(new BasicQueryPlanInterceptor(liveNodes));
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref this.disposeSignaled, 1) == 0)
            {
                this.partitionKeyResolver?.Dispose();
                this.endpointProvider?.Stop();
            }

            base.Dispose(disposing);
        }
    }
}
