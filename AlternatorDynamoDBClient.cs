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
    using Amazon.DynamoDBv2;
    using Amazon.Runtime;

    public static class AlternatorDynamoDBClient
    {
        public static AlternatorDynamoDBClientBuilder Builder()
        {
            return new AlternatorDynamoDBClientBuilder();
        }

#pragma warning disable SA1300, IDE1006
        public static AlternatorDynamoDBClientBuilder builder()
        {
            return Builder();
        }
#pragma warning restore SA1300, IDE1006

        public static AmazonDynamoDBClient Create(
            AWSCredentials credentials,
            Uri initialNodeUri,
            string datacenter = "",
            string rack = "")
        {
            return Builder()
                .WithCredentials(credentials)
                .WithInitialNodeUri(initialNodeUri)
                .WithDatacenter(datacenter)
                .WithRack(rack)
                .Build();
        }

        public static AmazonDynamoDBClient Create(
            Uri initialNodeUri,
            string datacenter = "",
            string rack = "")
        {
            return Builder()
                .EndpointOverride(initialNodeUri)
                .WithDatacenter(datacenter)
                .WithRack(rack)
                .Build();
        }

        public static AmazonDynamoDBClient Create(AWSCredentials credentials, HelperOptions options)
        {
            return Builder()
                .WithCredentials(credentials)
                .WithOptions(options)
                .Build();
        }

        public static AmazonDynamoDBClient Create(AWSCredentials credentials, AlternatorConfig config)
        {
            return Builder(config)
                .WithCredentials(credentials)
                .Build();
        }

        public static AmazonDynamoDBClient Create(AlternatorConfig config)
        {
            return Builder(config).Build();
        }

        private static AlternatorDynamoDBClientBuilder Builder(AlternatorConfig? config)
        {
            var builder = Builder()
                .WithAlternatorConfig(config);

            if (config != null)
            {
                builder
                    .WithInitialNodes(config.SeedHosts.ToArray())
                    .WithSchema(config.Scheme)
                    .WithPort(config.Port);
            }

            return builder;
        }
    }
}
