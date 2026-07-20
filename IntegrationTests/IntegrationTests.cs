// <copyright file="IntegrationTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using Amazon.DynamoDBv2;
    using Amazon.DynamoDBv2.Model;
    using Amazon.Runtime;

    [TestFixture]
    [Category("Integration")]
    public class IntegrationTests
    {
        private readonly string user = Environment.GetEnvironmentVariable("ALTERNATOR_USER") ?? TestContext.Parameters.Get("User", "none");
        private readonly string password = Environment.GetEnvironmentVariable("ALTERNATOR_PASSWORD") ?? TestContext.Parameters.Get("Password", "none");
        private readonly string endpoint = Environment.GetEnvironmentVariable("ALTERNATOR_ENDPOINT") ?? TestContext.Parameters.Get("Endpoint", "http://127.0.0.1:8080");
        private readonly string httpsEndpoint = Environment.GetEnvironmentVariable("ALTERNATOR_HTTPS_ENDPOINT") ?? TestContext.Parameters.Get("HttpsEndpoint", "https://172.45.0.2:9999");
        private readonly string caCertPath = Environment.GetEnvironmentVariable("ALTERNATOR_CA_CERT_PATH") ?? TestContext.Parameters.Get("CaCertPath", "IntegrationTests/scylla/db.crt");

        public IntegrationTests()
        {
        }

        [Test]
        public async Task BasicTableTest([Values("", "dc1")] string datacenter, [Values("", "rack1")] string rack)
        {
            using var ddb = this.GetAlternatorClient(new Uri(this.endpoint), datacenter, rack);
            var tableName = CreateTableName("basic");

            try
            {
                await CreateNumberRangeTableAsync(ddb, tableName);
                for (int i = 0; i < 10; i++)
                {
                    var tables = await ddb.ListTablesAsync();
                    Assert.That(tables.TableNames, Does.Contain(tableName));
                }
            }
            finally
            {
                await DeleteTableIfExistsAsync(ddb, tableName);
            }
        }

        [Test]
        public async Task BuildReturnsRegularAwsClientThatPerformsCrudTest()
        {
            using var ddb = this.CreateBuilder(new Uri(this.endpoint)).Build();
            var tableName = CreateTableName("crud");

            try
            {
                await CreateStringTableAsync(ddb, tableName);
                var key = StringKey("item-1");
                await ddb.PutItemAsync(new PutItemRequest
                {
                    TableName = tableName,
                    Item = new Dictionary<string, AttributeValue>(key)
                    {
                        ["payload"] = new AttributeValue { S = "value-1" },
                    },
                });

                var loaded = await ddb.GetItemAsync(new GetItemRequest
                {
                    TableName = tableName,
                    Key = key,
                    ConsistentRead = true,
                });

                Assert.That(loaded.Item, Does.ContainKey("payload"));
                Assert.That(loaded.Item["payload"].S, Is.EqualTo("value-1"));

                await ddb.DeleteItemAsync(new DeleteItemRequest
                {
                    TableName = tableName,
                    Key = key,
                });

                var deleted = await ddb.GetItemAsync(new GetItemRequest
                {
                    TableName = tableName,
                    Key = key,
                    ConsistentRead = true,
                });
                Assert.That(deleted.Item == null || deleted.Item.Count == 0, Is.True);
            }
            finally
            {
                await DeleteTableIfExistsAsync(ddb, tableName);
            }
        }

        [Test]
        public async Task WrapperExposesAlternatorApiAndLiveNodesTest()
        {
            using var wrapper = this.CreateBuilder(new Uri(this.endpoint))
                .WithActiveRefreshIntervalMs(200)
                .WithIdleRefreshIntervalMs(1000)
                .BuildWithAlternatorAPI();

            await WaitUntilAsync(() => wrapper.getLiveNodes().Count > 0);
            var liveNodesManager = wrapper.getAlternatorLiveNodes();
            liveNodesManager.shutdownAndWait();
            var liveNodes = wrapper.getLiveNodes();
            var next = wrapper.nextAsURI();
            var config = wrapper.GetAlternatorConfig();

            Assert.That(wrapper.getClient(), Is.InstanceOf<AmazonDynamoDBClient>());
            Assert.That(config, Is.Not.Null);
            Assert.That(config!.SeedHosts, Is.Not.Empty);
            Assert.That(liveNodesManager.getLiveNodes(), Is.EqualTo(liveNodes));
            Assert.That(liveNodes, Does.Contain(next));
        }

        [Test]
        public async Task DnsEntrypointDiscoversLiveClusterNodesTest()
        {
            using var server = new LocalNodesReplayServer(await this.FetchIntegrationLocalNodesAsync());
            var config = AlternatorConfig.builder()
                .withSeedHost("localhost")
                .withScheme("http")
                .withPort(server.Port)
                .build();
            var liveNodes = new AlternatorLiveNodes(config);

            try
            {
                InvokeUpdateLiveNodes(liveNodes);
                server.WaitForRequest();

                Assert.That(server.LastHost, Is.EqualTo($"localhost:{server.Port}"));
                Assert.That(liveNodes.getLiveNodes(), Is.Not.Empty);
            }
            finally
            {
                liveNodes.shutdownAndWait();
            }
        }

        [Test]
        public async Task CompressionAndHeaderOptimizationClientCanSendCompressedRequestTest()
        {
            var requiredHeaders = AlternatorConfig.builder()
                .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP)
                .getRequiredHeaders();

            using var ddb = this.CreateBuilder(new Uri(this.endpoint))
                .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP)
                .withMinCompressionSizeBytes(1)
                .withOptimizeHeaders(true)
                .withHeadersWhitelist(requiredHeaders)
                .Build();

            try
            {
                await ddb.PutItemAsync(new PutItemRequest
                {
                    TableName = "nonexistent_table_for_combined_test",
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = new AttributeValue { S = "k" },
                        ["data"] = new AttributeValue { S = LargePayload() },
                    },
                });
                Assert.Fail("PutItem should fail after Alternator receives and parses the compressed request.");
            }
            catch (ResourceNotFoundException)
            {
            }
        }

        [Test]
        public async Task HttpsTrustAllClientCanListTablesTest()
        {
            using var ddb = this.CreateBuilder(new Uri(this.httpsEndpoint))
                .withTlsConfig(TlsConfig.trustAll())
                .Build();

            var tables = await ddb.ListTablesAsync();
            Assert.That(tables.TableNames, Is.Not.Null);
        }

        [Test]
        public async Task HttpsCustomCaClientCanListTablesTest()
        {
            var tlsConfig = TlsConfig.builder()
                .withCaCertPath(this.caCertPath)
                .withTrustSystemCaCerts(false)
                .build();
            using var ddb = this.CreateBuilder(new Uri(this.httpsEndpoint))
                .withTlsConfig(tlsConfig)
                .Build();

            var tables = await ddb.ListTablesAsync();
            Assert.That(tables.TableNames, Is.Not.Null);
        }

        private static string CreateTableName(string prefix)
        {
            return "csharp_it_" + prefix + "_" + Guid.NewGuid().ToString("N");
        }

        private static async Task CreateNumberRangeTableAsync(AmazonDynamoDBClient ddb, string tableName)
        {
            await ddb.CreateTableAsync(
                tableName,
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
        }

        private static async Task CreateStringTableAsync(AmazonDynamoDBClient ddb, string tableName)
        {
            await ddb.CreateTableAsync(
                tableName,
                new List<KeySchemaElement>
                {
                    new KeySchemaElement("pk", KeyType.HASH),
                },
                new List<AttributeDefinition>
                {
                    new AttributeDefinition("pk", ScalarAttributeType.S),
                },
                new ProvisionedThroughput { ReadCapacityUnits = 1, WriteCapacityUnits = 1 });
        }

        private static async Task DeleteTableIfExistsAsync(AmazonDynamoDBClient ddb, string tableName)
        {
            try
            {
                await ddb.DeleteTableAsync(tableName);
            }
            catch (ResourceNotFoundException)
            {
            }
        }

        private static Dictionary<string, AttributeValue> StringKey(string key)
        {
            return new Dictionary<string, AttributeValue>
            {
                ["pk"] = new AttributeValue { S = key },
            };
        }

        private static string LargePayload()
        {
            return string.Concat(Enumerable.Repeat("This is a test value that should be compressed. ", 100));
        }

        private static void InvokeUpdateLiveNodes(AlternatorLiveNodes liveNodes)
        {
            var method = typeof(AlternatorLiveNodes).GetMethod(
                "UpdateLiveNodes",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method!.Invoke(liveNodes, Array.Empty<object>());
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (condition())
                {
                    return;
                }

                await Task.Delay(100);
            }

            Assert.That(condition(), Is.True);
        }

        private async Task<string> FetchIntegrationLocalNodesAsync()
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            return await httpClient.GetStringAsync(new Uri(new Uri(this.endpoint), "/localnodes"));
        }

        // Alternator-specific DynamoDB connection
        private AmazonDynamoDBClient GetAlternatorClient(Uri uri, string datacenter, string rack)
        {
            return this.CreateBuilder(uri)
                .WithDatacenterAndRack(datacenter, rack)
                .Build();
        }

        private AlternatorDynamoDBClientBuilder CreateBuilder(Uri uri)
        {
            return AlternatorDynamoDBClient.builder()
                .endpointOverride(uri)
                .credentialsProvider(new BasicAWSCredentials(this.user, this.password));
        }

        private sealed class LocalNodesReplayServer : IDisposable
        {
            private readonly TcpListener listener;
            private readonly string body;
            private readonly Task serverTask;
            private bool disposed;

            internal LocalNodesReplayServer(string body)
            {
                this.body = body;
                this.listener = new TcpListener(IPAddress.Any, 0);
                this.listener.Start();
                this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
                this.serverTask = Task.Run(this.RunAsync);
            }

            internal int Port { get; }

            internal string LastHost { get; private set; } = string.Empty;

            public void Dispose()
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
                this.listener.Stop();
                try
                {
                    this.serverTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException exception) when (exception.InnerExceptions.All(IsExpectedShutdownException))
                {
                }
            }

            internal void WaitForRequest()
            {
                if (!this.serverTask.Wait(TimeSpan.FromSeconds(5)))
                {
                    Assert.Fail("Timed out waiting for DNS entrypoint request.");
                }

                if (this.serverTask.IsFaulted)
                {
                    throw this.serverTask.Exception!;
                }
            }

            private static bool IsExpectedShutdownException(Exception exception)
            {
                return exception is SocketException || exception is ObjectDisposedException;
            }

            private async Task RunAsync()
            {
                using var client = await this.listener.AcceptTcpClientAsync();
                await this.HandleClientAsync(client);
            }

            private async Task HandleClientAsync(TcpClient client)
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                _ = await reader.ReadLineAsync();
                string? header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync()))
                {
                    if (header.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                    {
                        this.LastHost = header.Substring("Host:".Length).Trim();
                    }
                }

                var responseBody = Encoding.UTF8.GetBytes(this.body);
                var responseHeader = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: application/json\r\n"
                    + $"Content-Length: {responseBody.Length}\r\n"
                    + "Connection: close\r\n\r\n");
                await stream.WriteAsync(responseHeader);
                await stream.WriteAsync(responseBody);
            }
        }
    }
}
