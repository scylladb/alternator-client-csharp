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
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using Amazon.DynamoDBv2;
    using Amazon.DynamoDBv2.Model;
    using ScyllaDB.Alternator.TestInfrastructure;

    [TestFixture]
    [Category("Integration")]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public class IntegrationTests
    {
        [Test]
        public async Task BasicTableTest(
            [Values(false, true)] bool useDatacenter,
            [Values(false, true)] bool useRack)
        {
            await using var lease = await TestClusters.AcquireReusableAsync(ClusterSpecs.Default);
            var firstNode = lease.Cluster.Nodes[0];
            var datacenter = useDatacenter ? firstNode.Datacenter : string.Empty;
            var rack = useRack ? firstNode.Rack : string.Empty;
            using var ddb = GetAlternatorClient(lease.Cluster, datacenter, rack);
            var tableName = lease.Resources.NewTableName("basic");

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
            await using var lease = await TestClusters.AcquireReusableAsync(ClusterSpecs.Default);
            using var ddb = lease.Cluster.ClientBuilder(AlternatorTransport.Http).Build();
            var tableName = lease.Resources.NewTableName("crud");

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
            await using var lease = await TestClusters.AcquireReusableAsync(ClusterSpecs.Default);
            using var wrapper = lease.Cluster.ClientBuilder(AlternatorTransport.Http)
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
            await using var lease = await TestClusters.AcquireReusableAsync(ClusterSpecs.Default);
            var endpoint = lease.Cluster.Connection(AlternatorTransport.Http).SeedEndpoint;
            using var server = new LocalNodesReplayServer(await FetchIntegrationLocalNodesAsync(endpoint));
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
            await using var lease = await TestClusters.AcquireReusableAsync(ClusterSpecs.Default);
            var requiredHeaders = AlternatorConfig.builder()
                .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP)
                .getRequiredHeaders();

            using var ddb = lease.Cluster.ClientBuilder(AlternatorTransport.Http)
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
            await using var lease = await TestClusters.AcquireReusableAsync(ClusterSpecs.Default);
            var endpoint = lease.Cluster.Connection(AlternatorTransport.Https).SeedEndpoint;
            using var ddb = AlternatorDynamoDBClient.builder()
                .endpointOverride(endpoint)
                .withTlsConfig(TlsConfig.trustAll())
                .Build();

            var tables = await ddb.ListTablesAsync();
            Assert.That(tables.TableNames, Is.Not.Null);
        }

        [Test]
        public async Task HttpsCustomCaClientCanListTablesTest()
        {
            await using var lease = await TestClusters.AcquireReusableAsync(ClusterSpecs.Default);
            var connection = lease.Cluster.Connection(AlternatorTransport.Https);
            var tlsConfig = TlsConfig.builder()
                .withCaCertPath(connection.CaCertificatePath!)
                .withTrustSystemCaCerts(false)
                .build();
            using var ddb = AlternatorDynamoDBClient.builder()
                .endpointOverride(connection.SeedEndpoint)
                .withTlsConfig(tlsConfig)
                .Build();

            var tables = await ddb.ListTablesAsync();
            Assert.That(tables.TableNames, Is.Not.Null);
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

        private static async Task<string> FetchIntegrationLocalNodesAsync(Uri endpoint)
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(5),
            };
            return await httpClient.GetStringAsync(new Uri(endpoint, "/localnodes"));
        }

        // Alternator-specific DynamoDB connection
        private static AmazonDynamoDBClient GetAlternatorClient(
            ITestClusterInfo cluster,
            string datacenter,
            string rack)
        {
            return cluster.ClientBuilder(AlternatorTransport.Http)
                .WithDatacenterAndRack(datacenter, rack)
                .Build();
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
