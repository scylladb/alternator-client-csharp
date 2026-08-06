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
    using System.Diagnostics;
    using System.Net.Http;

    internal sealed class NodeHealthReportingHttpMessageHandler : DelegatingHandler
    {
        private readonly AlternatorLiveNodes liveNodes;
        private readonly NodeHealthStoreConfig healthConfig;

        internal NodeHealthReportingHttpMessageHandler(
            HttpMessageHandler innerHandler,
            AlternatorLiveNodes liveNodes,
            NodeHealthStoreConfig healthConfig)
            : base(innerHandler)
        {
            this.liveNodes = liveNodes ?? throw new ArgumentNullException(nameof(liveNodes));
            this.healthConfig = NodeHealthStoreConfig.Normalize(healthConfig);
        }

        internal static NodeHealthObservation ObservationFromResponse(
            HttpResponseMessage response,
            TimeSpan elapsed,
            NodeHealthStoreConfig healthConfig)
        {
            if ((int)response.StatusCode < 500)
            {
                return NodeHealthObservation.Success;
            }

            var timeoutThresholdMs = NodeHealthStoreConfig.Normalize(healthConfig).ServerRequestTimeoutThresholdMs;
            if (timeoutThresholdMs > 0 && elapsed >= TimeSpan.FromMilliseconds(timeoutThresholdMs))
            {
                return NodeHealthObservation.RequestTimeout;
            }

            return NodeHealthObservation.ServerError;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var node = TryExtractAlternatorNode(request.RequestUri);
            var started = Stopwatch.GetTimestamp();
            try
            {
                var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (node != null)
                {
                    this.liveNodes.ReportNodeResult(
                        node,
                        ObservationFromResponse(response, Stopwatch.GetElapsedTime(started), this.healthConfig));
                }

                return response;
            }
            catch (Exception) when (node != null && !cancellationToken.IsCancellationRequested)
            {
                this.liveNodes.ReportNodeResult(node, NodeHealthObservation.ConnectionFailure);
                throw;
            }
        }

        private static Uri? TryExtractAlternatorNode(Uri? requestUri)
        {
            if (requestUri == null || !requestUri.IsAbsoluteUri)
            {
                return null;
            }

            return new UriBuilder(requestUri.Scheme, requestUri.Host, requestUri.Port).Uri;
        }
    }
}
