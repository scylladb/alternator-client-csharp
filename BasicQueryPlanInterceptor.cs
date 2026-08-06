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
    using ScyllaDB.Alternator.KeyRouting;

    public class BasicQueryPlanInterceptor : PipelineHandler
    {
        protected const string QueryPlanContextKey = "ScyllaDB.Alternator.QueryPlan";

        private readonly AlternatorLiveNodes liveNodes;

        public BasicQueryPlanInterceptor(AlternatorLiveNodes liveNodes)
        {
            this.liveNodes = liveNodes ?? throw new ArgumentNullException(nameof(liveNodes));
        }

        public AlternatorLiveNodes LiveNodes => this.liveNodes;

        public override void InvokeSync(IExecutionContext executionContext)
        {
            this.ApplyEndpoint(executionContext.RequestContext);
            base.InvokeSync(executionContext);
        }

        public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
        {
            this.ApplyEndpoint(executionContext.RequestContext);
            return base.InvokeAsync<T>(executionContext);
        }

        public LazyQueryPlan GetOrCreateQueryPlan(
            AmazonWebServiceRequest originalRequest,
            IDictionary<string, object> contextAttributes)
        {
            if (contextAttributes.TryGetValue(QueryPlanContextKey, out var storedQueryPlan)
                && storedQueryPlan is LazyQueryPlan queryPlan)
            {
                return queryPlan;
            }

            queryPlan = this.CreateQueryPlan(originalRequest);
            contextAttributes[QueryPlanContextKey] = queryPlan;
            return queryPlan;
        }

        public AlternatorLiveNodes GetLiveNodes()
        {
            return this.liveNodes;
        }

#pragma warning disable SA1300, IDE1006
        public AlternatorLiveNodes getLiveNodes()
        {
            return this.GetLiveNodes();
        }
#pragma warning restore SA1300, IDE1006

        protected virtual LazyQueryPlan CreateQueryPlan(AmazonWebServiceRequest originalRequest)
        {
            return this.liveNodes.CreateQueryPlan();
        }

        private void ApplyEndpoint(IRequestContext requestContext)
        {
            var queryPlan = this.GetOrCreateQueryPlan(requestContext.OriginalRequest, requestContext.ContextAttributes);
            if (!queryPlan.HasNext)
            {
                return;
            }

            var target = queryPlan.Next();
            requestContext.Request.Endpoint = target;
            requestContext.Request.Headers["Host"] = target.Authority;
            requestContext.Request.Headers["Connection"] = "keep-alive";
        }
    }
}
