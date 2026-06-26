// <copyright file="AlternatorUserAgentPipelineHandler.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon.Runtime;
    using Amazon.Runtime.Internal;

    internal sealed class AlternatorUserAgentPipelineHandler : PipelineHandler
    {
        private readonly bool appendDefaultToken;
        private readonly Func<string, string?>? transformer;

        internal AlternatorUserAgentPipelineHandler(Func<string, string?>? transformer, bool appendDefaultToken)
        {
            this.transformer = transformer;
            this.appendDefaultToken = appendDefaultToken;
        }

        public override void InvokeSync(IExecutionContext executionContext)
        {
            this.ApplyUserAgent(executionContext.RequestContext);
            base.InvokeSync(executionContext);
        }

        public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
        {
            this.ApplyUserAgent(executionContext.RequestContext);
            return base.InvokeAsync<T>(executionContext);
        }

        private void ApplyUserAgent(IRequestContext requestContext)
        {
            AlternatorUserAgent.ApplyTo(
                requestContext.OriginalRequest,
                requestContext.Request.Headers,
                this.transformer,
                this.appendDefaultToken);
        }
    }
}
