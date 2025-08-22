// <copyright file="Test.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon.DynamoDBv2;
    using Amazon.DynamoDBv2.Model;
    using Amazon.Runtime;

    [TestFixture]
    public class Test
    {
        public Test()
        {
        }

        [Test]
        public async Task BasicTableTest([Values("", "dc1")] string datacenter, [Values("", "rack1")] string rack)
        {
            var credentials = new BasicAWSCredentials(this.user, this.password);
            var ddb = GetAlternatorClient(new Uri(this.endpoint), credentials, datacenter, rack);
            var rand = new Random();
            string tabName = "table" + rand.Next(1000000);
            await ddb.CreateTableAsync(
                tabName,
                new List<KeySchemaElement>
                {
                    new KeySchemaElement("k", KeyType.HASH),
                    new KeySchemaElement("c", KeyType.RANGE),
                },
                new List<AttributeDefinition>
                {
                    new AttributeDefinition("k", ScalarAttributeType.N),
                    new AttributeDefinition("c", ScalarAttributeType.N),
                },
                new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 });
            for (int i = 0; i < 10; i++)
            {
                var tables = await ddb.ListTablesAsync();
                Console.WriteLine(tables);
            }

            await ddb.DeleteTableAsync(tabName);
            ddb.Dispose();
        }

        private readonly string user = TestContext.Parameters.Get("User", "none");
        private readonly string password = TestContext.Parameters.Get("Password", "none");
        private readonly string endpoint = TestContext.Parameters.Get("Endpoint", "http://127.0.0.1:8080");

        // Alternator-specific DynamoDB connection
        private static AmazonDynamoDBClient GetAlternatorClient(Uri uri, AWSCredentials credentials, string datacenter, string rack)
        {
            var handler = new EndpointProvider(uri, datacenter, rack);
            var config = new AmazonDynamoDBConfig
            {
                RegionEndpoint = Amazon.RegionEndpoint.USEast1,
                EndpointProvider = handler,
            };
            return new AmazonDynamoDBClient(credentials, config);
        }

        [Test]
        public async Task BasicTableTest([Values("", "dc1")] string datacenter, [Values("", "rack1")] string rack)
        {
            var credentials = new BasicAWSCredentials(this.user, this.password);

            var ddb = GetAlternatorClient(new Uri(this.endpoint), credentials, datacenter, rack);

            var rand = new Random();
            string tabName = "table" + rand.Next(1000000);

            await ddb.CreateTableAsync(
                tabName,
                new List<KeySchemaElement>
                {
                    new KeySchemaElement("k", KeyType.HASH),
                    new KeySchemaElement("c", KeyType.RANGE),
                },
                new List<AttributeDefinition>
                {
                    new AttributeDefinition("k", ScalarAttributeType.N),
                    new AttributeDefinition("c", ScalarAttributeType.N),
                },
                new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 });

            // run ListTables several times
            for (int i = 0; i < 10; i++)
            {
                var tables = await ddb.ListTablesAsync();
                Console.WriteLine(tables);
            }

            await ddb.DeleteTableAsync(tabName);
            ddb.Dispose();
        }

        // ...existing code...
    }
}
