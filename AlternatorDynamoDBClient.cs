// <copyright file="AlternatorDynamoDBClient.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

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
