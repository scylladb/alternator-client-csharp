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
    using Amazon.Runtime.Endpoints;

    [Obsolete("Use AlternatorDynamoDBClient to build AmazonDynamoDBClient instances.")]
    public class EndpointProvider : IEndpointProvider
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly AlternatorLiveNodes liveNodes;

        public EndpointProvider(Uri seedUri, string datacenter, string rack)
        {
            this.liveNodes = new AlternatorLiveNodes(seedUri, datacenter, rack);
            try
            {
                this.liveNodes.Validate();
                this.liveNodes.CheckIfRackAndDatacenterSetCorrectly();
                if (datacenter.Length != 0 || rack.Length != 0)
                {
                    if (!this.liveNodes.CheckIfRackDatacenterFeatureIsSupported())
                    {
                        Logger.Error($"server {seedUri} does not support rack or datacenter filtering");
                    }
                }
            }
            catch (Exception e)
            {
                throw new SystemException("failed to start EndpointProvider", e);
            }

            this.liveNodes.Start(CancellationToken.None);
        }

        public Endpoint ResolveEndpoint(EndpointParameters parameters)
        {
            return new Endpoint(this.liveNodes.NextAsUri().ToString());
        }
    }
}
