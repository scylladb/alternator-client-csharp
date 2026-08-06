// Copyright ScyllaDB, Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

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
