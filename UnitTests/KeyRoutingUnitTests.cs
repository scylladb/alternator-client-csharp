// <copyright file="KeyRoutingUnitTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using Amazon.DynamoDBv2;
    using Amazon.DynamoDBv2.Model;
    using Amazon.Runtime;
    using ScyllaDB.Alternator.KeyRouting;
    using ScyllaDB.Alternator.Routing;

    [TestFixture]
    [Category("Unit")]
    public class KeyRoutingUnitTests
    {
        [Test]
        public void AttributeValueHasherMatchesCrossLanguageVectorsTest()
        {
            Assert.That(AttributeValueHasher.Hash(null), Is.EqualTo(0L));
            Assert.That(AttributeValueHasher.hash(new AttributeValue { S = "hello" }), Is.EqualTo(8815023923555918238L));
            Assert.That(AttributeValueHasher.hash(new AttributeValue { S = string.Empty }), Is.EqualTo(8849112093580131862L));
            Assert.That(AttributeValueHasher.hash(new AttributeValue { S = "user_123" }), Is.EqualTo(-4025731529809423594L));
            Assert.That(AttributeValueHasher.hash(new AttributeValue { N = "42" }), Is.EqualTo(-5061732451827723051L));
            Assert.That(AttributeValueHasher.hash(new AttributeValue { N = "3.14159" }), Is.EqualTo(2139945193071104172L));
            Assert.That(
                AttributeValueHasher.hash(new AttributeValue { B = new MemoryStream(new byte[] { 0x01, 0x02, 0x03 }) }),
                Is.EqualTo(5026299041734804437L));

            var unsupported = Assert.Throws<ArgumentException>(() =>
                AttributeValueHasher.hash(new AttributeValue { BOOL = true }));
            Assert.That(
                unsupported!.Message,
                Does.Contain("Unsupported AttributeValue type. Only S (String), N (Number), and B (Binary) are supported as partition key types in Alternator."));
        }

        [Test]
        public void MurmurHash3MatchesJavaAndGoVectorsTest()
        {
            Assert.That(MurmurHash3.Hash(Array.Empty<byte>()), Is.EqualTo(0L));
            Assert.That(MurmurHash3.hash(System.Text.Encoding.UTF8.GetBytes("test")), Is.EqualTo(unchecked((long)0xac7d28cc74bde19dUL)));
            Assert.That(MurmurHash3.hash(System.Text.Encoding.UTF8.GetBytes("hello")), Is.EqualTo(unchecked((long)0xcbd8a7b341bd9b02UL)));
            Assert.That(MurmurHash3.hash(System.Text.Encoding.UTF8.GetBytes("user_123")), Is.EqualTo(0x104832bf621f0137L));
            Assert.That(MurmurHash3.hash(System.Text.Encoding.UTF8.GetBytes("0123456789abcdef")), Is.EqualTo(0x4be06d94cf4ad1a7L));
            Assert.That(MurmurHash3.hash(new byte[] { 0xff, 0x80, 0x7f, 0x00 }), Is.EqualTo(0x3408b0fbe4cb130cL));

            var data = System.Text.Encoding.UTF8.GetBytes("prefixHELLOsuffix");
            var hello = System.Text.Encoding.UTF8.GetBytes("HELLO");

            Assert.That(MurmurHash3.hash(data, 6, 5), Is.EqualTo(MurmurHash3.Hash(hello)));
        }

        [Test]
        public void BatchWriteItemRoutingTargetsIncludeOnlyValidPutAndDeleteCandidatesTest()
        {
            var request = BatchWrite(
                Table(
                    "orders",
                    Put(Attributes("pk", StringAttribute("put-key"), "data", StringAttribute("value"))),
                    Delete(Attributes("pk", StringAttribute("delete-key"))),
                    Put(Attributes("data", StringAttribute("missing-pk"))),
                    new WriteRequest(),
                    new WriteRequest { PutRequest = new PutRequest() },
                    new WriteRequest { DeleteRequest = new DeleteRequest() },
                    new WriteRequest
                    {
                        PutRequest = new PutRequest { Item = Attributes("pk", StringAttribute("invalid-put")) },
                        DeleteRequest = new DeleteRequest { Key = Attributes("pk", StringAttribute("invalid-delete")) },
                    }));

            var targets = KeyAffinityRequestClassifier.ExtractBatchWriteRoutingTargets(request);

            Assert.That(targets, Has.Count.EqualTo(3));
            Assert.That(KeyAffinityRequestClassifier.ShouldApply(KeyRouteAffinity.AnyWrite, request), Is.True);
            Assert.That(KeyAffinityRequestClassifier.ShouldApply(KeyRouteAffinity.Rmw, request), Is.False);
            AssertBatchWriteCandidate(targets, "orders", "PutRequest", "pk", "put-key");
            AssertBatchWriteCandidate(targets, "orders", "DeleteRequest", "pk", "delete-key");
            Assert.That(
                targets.Any(target =>
                    target.TableName == "orders"
                    && target.Operation == "PutRequest"
                    && target.PartitionKeyValue("pk") == null),
                Is.True);
            Assert.Throws<NotSupportedException>(() =>
                ((IList<KeyAffinityRequestClassifier.BatchWriteRoutingTarget>)targets).Clear());
        }

        [Test]
        public void KeyAffinityRequestClassifierReturnsNullForNullPartitionKeyNameLikeJavaTest()
        {
            var putRequest = new PutItemRequest
            {
                TableName = "orders",
                Item = Attributes("pk", StringAttribute("order-1")),
            };
            var batchRequest = BatchWrite(
                Table("orders", Put(Attributes("pk", StringAttribute("order-1")))));

            Assert.That(KeyAffinityRequestClassifier.extractPartitionKey(putRequest, null), Is.Null);
            Assert.That(KeyAffinityRequestClassifier.extractPartitionKey(batchRequest, null), Is.Null);
        }

        [Test]
        public void KeyAffinityRequestClassifierMatchesJavaRmwWriteRulesTest()
        {
            var key = Attributes("pk", StringAttribute("value"));
            var item = Attributes("pk", StringAttribute("value"));

            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.NONE,
                    new UpdateItemRequest
                    {
                        TableName = "test",
                        Key = key,
                        UpdateExpression = "SET x = :v",
                    }),
                Is.False);
            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.RMW,
                    new UpdateItemRequest
                    {
                        TableName = "test",
                        Key = key,
                        UpdateExpression = "SET x = :v",
                    }),
                Is.True);
            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.RMW,
                    new UpdateItemRequest
                    {
                        TableName = "test",
                        Key = key,
                        ReturnValues = ReturnValue.UPDATED_NEW,
                    }),
                Is.False);
            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.RMW,
                    new UpdateItemRequest
                    {
                        TableName = "test",
                        Key = key,
                        ReturnValues = ReturnValue.ALL_NEW,
                    }),
                Is.True);
            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.RMW,
                    new UpdateItemRequest
                    {
                        TableName = "test",
                        Key = key,
                        AttributeUpdates = new Dictionary<string, AttributeValueUpdate>
                        {
                            ["counter"] = new AttributeValueUpdate
                            {
                                Action = AttributeAction.ADD,
                                Value = NumberAttribute("1"),
                            },
                        },
                    }),
                Is.True);
            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.RMW,
                    new UpdateItemRequest
                    {
                        TableName = "test",
                        Key = key,
                        AttributeUpdates = new Dictionary<string, AttributeValueUpdate>
                        {
                            ["tags"] = new AttributeValueUpdate
                            {
                                Action = AttributeAction.DELETE,
                                Value = new AttributeValue
                                {
                                    SS = new List<string> { "tag1" },
                                },
                            },
                        },
                    }),
                Is.True);
            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.RMW,
                    new UpdateItemRequest
                    {
                        TableName = "test",
                        Key = key,
                        AttributeUpdates = new Dictionary<string, AttributeValueUpdate>
                        {
                            ["tags"] = new AttributeValueUpdate
                            {
                                Action = AttributeAction.DELETE,
                            },
                        },
                    }),
                Is.False);

            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.RMW,
                    new PutItemRequest
                    {
                        TableName = "test",
                        Item = item,
                    }),
                Is.False);
            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.RMW,
                    new PutItemRequest
                    {
                        TableName = "test",
                        Item = item,
                        ConditionExpression = "attribute_not_exists(pk)",
                    }),
                Is.True);
            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.RMW,
                    new DeleteItemRequest
                    {
                        TableName = "test",
                        Key = key,
                    }),
                Is.False);
            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.RMW,
                    new DeleteItemRequest
                    {
                        TableName = "test",
                        Key = key,
                        ReturnValues = ReturnValue.ALL_OLD,
                    }),
                Is.True);

            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.ANY_WRITE,
                    new QueryRequest { TableName = "test" }),
                Is.False);
            Assert.That(
                KeyAffinityRequestClassifier.shouldApply(
                    KeyRouteAffinity.ANY_WRITE,
                    new BatchWriteItemRequest()),
                Is.False);
        }

        [Test]
        public void BatchWriteItemRoutingTargetsPreserveValidWriteOrderTest()
        {
            var request = BatchWrite(
                Table(
                    "orders",
                    Delete(Attributes("pk", StringAttribute("delete-key"))),
                    Put(Attributes("pk", StringAttribute("put-key"), "data", StringAttribute("value")))));

            var targets = KeyAffinityRequestClassifier.extractBatchWriteRoutingTargets(request);
            var firstPartitionKey = targets[0].partitionKeyValue("pk");
            var secondPartitionKey = targets[1].partitionKeyValue("pk");

            Assert.That(targets, Has.Count.EqualTo(2));
            Assert.That(targets[0].tableName(), Is.EqualTo("orders"));
            Assert.That(targets[0].operation(), Is.EqualTo("DeleteRequest"));
            Assert.That(firstPartitionKey, Is.Not.Null);
            Assert.That(firstPartitionKey!.S, Is.EqualTo("delete-key"));
            Assert.That(targets[1].tableName(), Is.EqualTo("orders"));
            Assert.That(targets[1].operation(), Is.EqualTo("PutRequest"));
            Assert.That(secondPartitionKey, Is.Not.Null);
            Assert.That(secondPartitionKey!.S, Is.EqualTo("put-key"));
            Assert.Throws<NotSupportedException>(() =>
                ((IList<KeyAffinityRequestClassifier.BatchWriteRoutingTarget>)targets).Clear());
        }

        [Test]
        public void PartitionKeyResolverSupportsJavaStyleCloseAliasTest()
        {
            var resolver = new PartitionKeyResolver(new Dictionary<string, string>
            {
                ["users"] = "user_id",
            });

            Assert.That(resolver.getPartitionKeyName("users"), Is.EqualTo("user_id"));
            Assert.DoesNotThrow(() => resolver.close());
            Assert.DoesNotThrow(() => resolver.close());
        }

        [Test]
        public async Task PartitionKeyResolverDiscoversPartitionKeyTest()
        {
            var attempts = 0;
            using var resolver = CreatePartitionKeyResolver(
                null,
                (tableName, cancellationToken) =>
                {
                    attempts++;
                    return Task.FromResult(CreateDescribeTableResponse("session_id"));
                });

            resolver.TriggerDiscovery("sessions");

            await WaitUntilAsync(() => resolver.GetPartitionKeyName("sessions") != null);
            Assert.That(resolver.GetPartitionKeyName("sessions"), Is.EqualTo("session_id"));
            Assert.That(attempts, Is.EqualTo(1));
        }

        [Test]
        public async Task PartitionKeyResolverSuppressesConcurrentDiscoveryForSameTableTest()
        {
            var attempts = 0;
            var releaseDiscovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var resolver = CreatePartitionKeyResolver(
                null,
                async (tableName, cancellationToken) =>
                {
                    attempts++;
                    await releaseDiscovery.Task.WaitAsync(cancellationToken);
                    return CreateDescribeTableResponse("user_id");
                });

            resolver.TriggerDiscovery("users");
            resolver.TriggerDiscovery("users");
            resolver.TriggerDiscovery("users");

            await WaitUntilAsync(() => attempts == 1);
            releaseDiscovery.SetResult();
            await WaitUntilAsync(() => resolver.GetPartitionKeyName("users") != null);

            Assert.That(resolver.GetPartitionKeyName("users"), Is.EqualTo("user_id"));
            Assert.That(attempts, Is.EqualTo(1));
        }

        [Test]
        public async Task PartitionKeyResolverPermanentFailureCooldownBlocksRediscoveryTest()
        {
            var attempts = 0;
            using var resolver = CreatePartitionKeyResolver(
                null,
                (tableName, cancellationToken) =>
                {
                    attempts++;
                    throw new ResourceNotFoundException("Table not found");
                });

            resolver.TriggerDiscovery("missing");
            await WaitUntilAsync(() => resolver.IsInFailureCooldown("missing"));
            var attemptsAfterFailure = attempts;

            resolver.TriggerDiscovery("missing");
            await Task.Delay(100);

            Assert.That(resolver.GetPartitionKeyName("missing"), Is.Null);
            Assert.That(attempts, Is.EqualTo(attemptsAfterFailure));
            Assert.That(resolver.GetFailedTableCount(), Is.EqualTo(1));

            resolver.ClearFailure("missing");
            Assert.That(resolver.IsInFailureCooldown("missing"), Is.False);
        }

        [Test]
        public void QueryPlanPipelineHandlerReusesPlanAcrossRetriesTest()
        {
            var helper = CreateHelperForNodes("node1", "node2", "node3");
            var handler = CreateQueryPlanPipelineHandler(helper, null, null);
            var contextAttributes = new Dictionary<string, object>();
            var request = new ListTablesRequest();

            var queryPlan = InvokeGetOrCreateQueryPlan(handler, request, contextAttributes);
            var sameQueryPlan = InvokeGetOrCreateQueryPlan(handler, request, contextAttributes);
            var hosts = DrainQueryPlanHosts(queryPlan, 3);

            Assert.That(handler.getLiveNodes(), Is.SameAs(helper.getAlternatorLiveNodes()));
            Assert.That(handler.getConfig(), Is.Null);
            Assert.That(handler.getPartitionKeyResolver(), Is.Null);
            Assert.That(sameQueryPlan, Is.SameAs(queryPlan));
            Assert.That(hosts, Is.EquivalentTo(new[] { "node1", "node2", "node3" }));
        }

        [Test]
        public void BasicQueryPlanInterceptorSupportsJavaStylePublicApiTest()
        {
            var liveNodes = new AlternatorLiveNodes(
                new List<string> { "node1", "node2", "node3" },
                "http",
                8043,
                ClusterScope.create());
            var interceptor = new BasicQueryPlanInterceptor(liveNodes);
            var contextAttributes = new Dictionary<string, object>();
            var request = new ListTablesRequest();

            var queryPlan = interceptor.GetOrCreateQueryPlan(request, contextAttributes);
            var sameQueryPlan = interceptor.GetOrCreateQueryPlan(request, contextAttributes);
            var hosts = DrainQueryPlanHosts(queryPlan, 3);

            Assert.That(interceptor.getLiveNodes(), Is.SameAs(liveNodes));
            Assert.That(sameQueryPlan, Is.SameAs(queryPlan));
            Assert.That(hosts, Is.EquivalentTo(new[] { "node1", "node2", "node3" }));
        }

        [Test]
        public void AffinityQueryPlanInterceptorSupportsJavaStylePublicApiTest()
        {
            var liveNodes = new AlternatorLiveNodes(
                new List<string> { "node1", "node2", "node3", "node4", "node5" },
                "http",
                8043,
                ClusterScope.create());
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE)
                .withPkInfo("users", "user_id")
                .build();
            var interceptor = new AffinityQueryPlanInterceptor(affinity, liveNodes);
            var request = new PutItemRequest
            {
                TableName = "users",
                Item = new Dictionary<string, AttributeValue>
                {
                    ["user_id"] = new AttributeValue { S = "alice" },
                },
            };
            var expectedPlan = new LazyQueryPlan(liveNodes, AttributeValueHasher.Hash(request.Item["user_id"]));

            var actualPlan = interceptor.GetOrCreateQueryPlan(request, new Dictionary<string, object>());

            Assert.That(interceptor.getLiveNodes(), Is.SameAs(liveNodes));
            Assert.That(interceptor.getConfig(), Is.SameAs(affinity));
            Assert.That(interceptor.getPartitionKeyResolver(), Is.Not.Null);
            Assert.That(interceptor.getPartitionKeyResolver() !.getPartitionKeyName("users"), Is.EqualTo("user_id"));
            Assert.That(DrainQueryPlanUris(actualPlan, 5), Is.EqualTo(DrainQueryPlanUris(expectedPlan, 5)));
        }

        [Test]
        public void QueryPlanPipelineHandlerUsesStableAffinityPlanWhenPartitionKeyKnownTest()
        {
            var helper = CreateHelperForNodes("node1", "node2", "node3", "node4", "node5", "node6", "node7", "node8", "node9", "node10");
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE)
                .withPkInfo("users", "user_id")
                .build();
            using var resolver = new PartitionKeyResolver(affinity.PkInfoPerTable.ToDictionary(item => item.Key, item => item.Value));
            var handler = CreateQueryPlanPipelineHandler(helper, affinity, resolver);
            var request = new PutItemRequest
            {
                TableName = "users",
                Item = new Dictionary<string, AttributeValue>
                {
                    ["user_id"] = new AttributeValue { S = "alice" },
                },
            };
            var expectedPlan = InvokeCreateQueryPlan(helper, AttributeValueHasher.Hash(request.Item["user_id"]));

            var actualPlan = InvokeGetOrCreateQueryPlan(handler, request, new Dictionary<string, object>());

            Assert.That(handler.getConfig(), Is.SameAs(affinity));
            Assert.That(handler.getPartitionKeyResolver(), Is.SameAs(resolver));
            Assert.That(DrainQueryPlanUris(actualPlan, 10), Is.EqualTo(DrainQueryPlanUris(expectedPlan, 10)));
        }

        [Test]
        public void QueryPlanPipelineHandlerBatchWriteVotingOrdersAllVotedNodesTest()
        {
            var helper = CreateBatchWriteAffinityHelper();
            var sortedNodes = LazyQueryPlan.sortedAffinityNodes(helper.getAlternatorLiveNodes());
            var target = sortedNodes[0];
            var other = sortedNodes[1];
            var targetKeys = FindPartitionKeyValuesForNode(helper, target, 2);
            var otherKey = FindPartitionKeyValuesForNode(helper, other, 1)[0];
            var affinity = BatchWriteAffinityConfig(PkInfo("audit", "id", "orders", "id"));
            using var resolver = new PartitionKeyResolver(affinity.PkInfoPerTable.ToDictionary(item => item.Key, item => item.Value));
            var handler = CreateQueryPlanPipelineHandler(helper, affinity, resolver);
            var request = BatchWrite(
                Table(
                    "orders",
                    Put(ItemWithId(targetKeys[0], "orders-payload")),
                    Delete(KeyWithId(otherKey))),
                Table("audit", Put(ItemWithId(targetKeys[1], "audit-payload"))));

            var actualPlan = InvokeGetOrCreateQueryPlan(handler, request, new Dictionary<string, object>());
            var actualHosts = DrainQueryPlanUris(actualPlan, sortedNodes.Count).Select(uri => uri.Host).ToList();
            var expectedHosts = new[] { target.Host, other.Host }
                .Concat(sortedNodes.Where(node => !node.Equals(target) && !node.Equals(other)).Select(node => node.Host))
                .ToList();

            Assert.That(actualHosts, Is.EqualTo(expectedHosts));
        }

        [Test]
        public void QueryPlanPipelineHandlerBatchWriteVotingIsStableForEquivalentBatchesTest()
        {
            var helper = CreateBatchWriteAffinityHelper();
            var sortedNodes = LazyQueryPlan.sortedAffinityNodes(helper.getAlternatorLiveNodes());
            var target = sortedNodes[0];
            var other = sortedNodes[1];
            var targetKeys = FindPartitionKeyValuesForNode(helper, target, 2);
            var otherKey = FindPartitionKeyValuesForNode(helper, other, 1)[0];
            var affinity = BatchWriteAffinityConfig(PkInfo("audit", "id", "orders", "id"));
            var first = BatchWrite(
                Table(
                    "orders",
                    Put(ItemWithId(targetKeys[0], "payload-a")),
                    Delete(KeyWithId(otherKey))),
                Table("audit", Put(ItemWithId(targetKeys[1], "payload-b"))));
            var second = BatchWrite(
                Table("audit", Put(ItemWithId(targetKeys[1], "changed-audit-payload"))),
                Table(
                    "orders",
                    Delete(KeyWithId(otherKey)),
                    Put(ItemWithId(targetKeys[0], "changed-orders-payload"))));

            var firstNode = FirstBatchWriteRoute(helper, affinity, first);
            var secondNode = FirstBatchWriteRoute(helper, affinity, second);

            Assert.That(firstNode, Is.EqualTo(target));
            Assert.That(secondNode, Is.EqualTo(firstNode));
        }

        [Test]
        public void QueryPlanPipelineHandlerBatchWriteVotingUsesNodeAddressTieBreakTest()
        {
            var helper = CreateBatchWriteAffinityHelper();
            var sortedNodes = LazyQueryPlan.sortedAffinityNodes(helper.getAlternatorLiveNodes());
            var leftKey = FindPartitionKeyValuesForNode(helper, sortedNodes[0], 1)[0];
            var rightKey = FindPartitionKeyValuesForNode(helper, sortedNodes[1], 1)[0];
            var affinity = BatchWriteAffinityConfig(PkInfo("orders", "id"));
            using var resolver = new PartitionKeyResolver(affinity.PkInfoPerTable.ToDictionary(item => item.Key, item => item.Value));
            var handler = CreateQueryPlanPipelineHandler(helper, affinity, resolver);
            var request = BatchWrite(
                Table(
                    "orders",
                    Put(ItemWithId(leftKey, "left")),
                    Delete(KeyWithId(rightKey))));

            var queryPlan = InvokeTryCreateBatchWriteAffinityQueryPlan(handler, request);

            Assert.That(queryPlan, Is.Not.Null);
            Assert.That(DrainQueryPlanUris(queryPlan!, sortedNodes.Count), Is.EqualTo(sortedNodes));
        }

        [Test]
        public async Task QueryPlanPipelineHandlerBatchWriteVotingSkipsUnusableCandidatesTest()
        {
            var attempts = 0;
            var helper = CreateBatchWriteAffinityHelper();
            var target = LazyQueryPlan.sortedAffinityNodes(helper.getAlternatorLiveNodes())[0];
            var targetKey = FindPartitionKeyValuesForNode(helper, target, 1)[0];
            var affinity = BatchWriteAffinityConfig(PkInfo("orders", "id"));
            using var resolver = CreatePartitionKeyResolver(
                affinity.PkInfoPerTable.ToDictionary(item => item.Key, item => item.Value),
                (tableName, cancellationToken) =>
                {
                    attempts++;
                    return Task.FromResult(CreateDescribeTableResponse("id"));
                });
            var handler = CreateQueryPlanPipelineHandler(helper, affinity, resolver);
            var request = BatchWrite(
                Table("unknown", Put(ItemWithId("missing-metadata", "ignored"))),
                Table(
                    "orders",
                    new WriteRequest(),
                    new WriteRequest { PutRequest = new PutRequest() },
                    new WriteRequest
                    {
                        PutRequest = new PutRequest { Item = KeyWithId("invalid") },
                        DeleteRequest = new DeleteRequest { Key = KeyWithId("invalid") },
                    },
                    Put(Attributes("id", new AttributeValue { BOOL = true })),
                    Delete(KeyWithId(targetKey))));

            var actualPlan = InvokeGetOrCreateQueryPlan(handler, request, new Dictionary<string, object>());
            var firstNode = DrainQueryPlanUris(actualPlan, 1)[0];

            Assert.That(firstNode, Is.EqualTo(target));
            await WaitUntilAsync(() => resolver.GetPartitionKeyName("unknown") == "id");
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(resolver.GetPartitionKeyName("unknown"), Is.EqualTo("id"));
        }

        [Test]
        public void QueryPlanPipelineHandlerBatchWriteVotingSupportsPartitionKeyTypesTest()
        {
            var helper = CreateBatchWriteAffinityHelper();
            var affinity = BatchWriteAffinityConfig(PkInfo("orders", "id"));
            var cases = new Dictionary<string, AttributeValue>
            {
                ["string_partition_key"] = StringAttribute("string-key"),
                ["number_partition_key"] = NumberAttribute("42"),
                ["binary_partition_key"] = BinaryAttribute(0x00, 0xff),
            };

            foreach (var testCase in cases)
            {
                var request = BatchWrite(Table("orders", Put(Attributes("id", testCase.Value))));
                var expectedNode = NodeForPartitionKeyValue(helper, testCase.Value);

                var actualNode = FirstBatchWriteRoute(helper, affinity, request);

                Assert.That(actualNode, Is.EqualTo(expectedNode), testCase.Key);
            }
        }

        [Test]
        public async Task QueryPlanPipelineHandlerTriggersPartitionKeyDiscoveryAndFallsBackToBasicPlanTest()
        {
            var attempts = 0;
            var helper = CreateHelperForNodes("node1", "node2", "node3");
            var affinity = KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE)
                .build();
            using var resolver = CreatePartitionKeyResolver(
                null,
                (tableName, cancellationToken) =>
                {
                    attempts++;
                    return Task.FromResult(CreateDescribeTableResponse("user_id"));
                });
            var handler = CreateQueryPlanPipelineHandler(helper, affinity, resolver);
            var request = new PutItemRequest
            {
                TableName = "users",
                Item = new Dictionary<string, AttributeValue>
                {
                    ["user_id"] = new AttributeValue { S = "alice" },
                },
            };

            var queryPlan = InvokeGetOrCreateQueryPlan(handler, request, new Dictionary<string, object>());
            var hosts = DrainQueryPlanHosts(queryPlan, 3);

            Assert.That(hosts, Is.EquivalentTo(new[] { "node1", "node2", "node3" }));
            await WaitUntilAsync(() => resolver.GetPartitionKeyName("users") == "user_id");
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(resolver.GetPartitionKeyName("users"), Is.EqualTo("user_id"));
        }

        [Test]
        public void LazyQueryPlanMatchesCrossLanguageVectorsTest()
        {
            AssertQueryPlan(42L, 10, new[] { "node5", "node8", "node4", "node10", "node6", "node1" });
            AssertQueryPlan(123L, 10, new[] { "node5", "node1", "node3", "node2", "node9", "node4" });
            AssertQueryPlan(999L, 10, new[] { "node4", "node9", "node3", "node1", "node10", "node2" });
            AssertQueryPlan(0L, 10, new[] { "node4", "node1", "node10", "node9", "node5", "node7" });
            AssertQueryPlan(-1L, 10, new[] { "node10", "node4", "node1", "node2", "node5", "node9" });
            AssertQueryPlan(42L, 6, new[] { "node6", "node3", "node1", "node4", "node2", "node5" });
            AssertQueryPlan(12345L, 10, new[] { "node3", "node4", "node1", "node6", "node5", "node7" });
            AssertQueryPlan(long.MaxValue, 10, new[] { "node10", "node6", "node7", "node1", "node9", "node3" });
        }

        [Test]
        public void LazyQueryPlanSupportsJavaStylePublicApiTest()
        {
            var liveNodes = new AlternatorLiveNodes(
                new List<string> { "node3.example.com", "node1.example.com", "node2.example.com" },
                "http",
                8043,
                ClusterScope.create());
            var sorted = LazyQueryPlan.sortedAffinityNodes(liveNodes);
            var preferred = LazyQueryPlan.preferredNodeForHash(liveNodes, 42L);
            var seeded = new LazyQueryPlan(liveNodes, 42L);
            var preferredFirst = new LazyQueryPlan(liveNodes, new[] { sorted[1] });

            Assert.That(sorted.Select(uri => uri.Host), Is.EqualTo(new[] { "node1.example.com", "node2.example.com", "node3.example.com" }));
            Assert.That(preferred, Is.EqualTo(seeded.next()));
            Assert.That(preferredFirst.hasNext(), Is.True);
            Assert.That(preferredFirst.next(), Is.EqualTo(sorted[1]));
            Assert.That(preferredFirst.ToList(), Is.EqualTo(new[] { sorted[0], sorted[2] }));
            Assert.That(preferredFirst.hasNext(), Is.False);

            var iteratorPlan = new LazyQueryPlan(liveNodes, new[] { sorted[0] });
            using var iterator = iteratorPlan.iterator();
            Assert.That(iterator.MoveNext(), Is.True);
            Assert.That(iterator.Current, Is.EqualTo(sorted[0]));
        }

        [Test]
        public void LazyQueryPlanSeededAffinityUsesSortedNodeOrderLikeJavaTest()
        {
            var unsortedLiveNodes = new AlternatorLiveNodes(
                new List<string> { "node3.example.com", "node1.example.com", "node2.example.com" },
                "http",
                8043,
                ClusterScope.create());
            var sortedLiveNodes = new AlternatorLiveNodes(
                new List<string> { "node1.example.com", "node2.example.com", "node3.example.com" },
                "http",
                8043,
                ClusterScope.create());
            var unsortedPlan = new LazyQueryPlan(unsortedLiveNodes, 42L);
            var sortedPlan = new LazyQueryPlan(sortedLiveNodes, 42L);

            Assert.That(unsortedPlan.ToList(), Is.EqualTo(sortedPlan.ToList()));
            Assert.That(
                LazyQueryPlan.preferredNodeForHash(unsortedLiveNodes, 42L),
                Is.EqualTo(LazyQueryPlan.preferredNodeForHash(sortedLiveNodes, 42L)));
        }

        [Test]
        public void LazyQueryPlanUsesActiveNodesBeforeQuarantinedFallbackTest()
        {
            var activeNodes = Uris("node4", "node2");
            var quarantinedNodes = Uris("node3", "node1");
            var liveNodes = new HealthSplitLiveNodes(activeNodes, quarantinedNodes);
            var plan = new LazyQueryPlan(liveNodes, 42L);

            var activePlanNodes = DrainQueryPlanUris(plan, activeNodes.Count);
            var fallbackPlanNodes = DrainQueryPlanUris(plan, quarantinedNodes.Count);
            var preferredNode = LazyQueryPlan.preferredNodeForHash(liveNodes, 42L);

            Assert.That(liveNodes.getActiveNodes(), Is.EqualTo(activeNodes));
            Assert.That(liveNodes.getQuarantinedNodes(), Is.EqualTo(quarantinedNodes));
            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes), Is.EqualTo(SortUris(activeNodes)));
            Assert.That(activeNodes, Does.Contain(preferredNode));
            Assert.That(activePlanNodes, Is.EquivalentTo(activeNodes));
            Assert.That(fallbackPlanNodes, Is.EquivalentTo(quarantinedNodes));
            Assert.That(plan.hasNext(), Is.False);
        }

        [Test]
        public void LazyQueryPlanUsesQuarantinedNodesWhenNoActiveNodesTest()
        {
            var quarantinedNodes = Uris("node3", "node1", "node2");
            var liveNodes = new HealthSplitLiveNodes(Array.Empty<Uri>(), quarantinedNodes);
            var plan = new LazyQueryPlan(liveNodes, 42L);

            var planNodes = DrainQueryPlanUris(plan, quarantinedNodes.Count);

            Assert.That(LazyQueryPlan.sortedAffinityNodes(liveNodes), Is.EqualTo(SortUris(quarantinedNodes)));
            Assert.That(planNodes, Is.EquivalentTo(quarantinedNodes));
            Assert.That(plan.hasNext(), Is.False);
        }

        [Test]
        public void AffinityBatchWriteVotingUsesActiveNodesBeforeQuarantinedFallbackTest()
        {
            var activeNodes = Uris("node2");
            var quarantinedNodes = Uris("node1", "node3");
            var liveNodes = new HealthSplitLiveNodes(activeNodes, quarantinedNodes);
            var affinity = BatchWriteAffinityConfig(PkInfo("orders", "id"));
            var interceptor = new AffinityQueryPlanInterceptor(affinity, liveNodes);
            var request = BatchWrite(
                Table(
                    "orders",
                    Put(ItemWithId("order-1", "payload-a")),
                    Delete(KeyWithId("order-2")),
                    Put(ItemWithId("order-3", "payload-b"))));

            var queryPlan = InvokeTryCreateBatchWriteAffinityQueryPlan(interceptor, request);
            Assert.That(queryPlan, Is.Not.Null);
            var planNodes = DrainQueryPlanUris(queryPlan!, activeNodes.Count + quarantinedNodes.Count);

            Assert.That(planNodes.Take(activeNodes.Count), Is.EqualTo(activeNodes));
            Assert.That(planNodes.Skip(activeNodes.Count), Is.EquivalentTo(quarantinedNodes));
        }

        private static Helper CreateHelperForNodes(params string[] hosts)
        {
            var config = AlternatorConfig.builder()
                .WithSeedHosts(hosts)
                .WithScheme("http")
                .WithPort(8043)
                .Build();
            var constructor = typeof(Helper).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(AlternatorConfig), typeof(bool), typeof(bool), typeof(CancellationToken) },
                null);
            Assert.That(constructor, Is.Not.Null);
            return (Helper)constructor!.Invoke(new object[] { config, false, false, CancellationToken.None });
        }

        private static QueryPlanPipelineHandler CreateQueryPlanPipelineHandler(
            Helper helper,
            KeyRouteAffinityConfig? keyRouteAffinityConfig,
            PartitionKeyResolver? partitionKeyResolver)
        {
            return new QueryPlanPipelineHandler(helper, keyRouteAffinityConfig, partitionKeyResolver);
        }

        private static object InvokeCreateQueryPlan(Helper helper, long seed)
        {
            var method = typeof(Helper).GetMethod(
                "CreateQueryPlan",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(long) },
                null);
            Assert.That(method, Is.Not.Null);
            var queryPlan = method!.Invoke(helper, new object[] { seed });
            Assert.That(queryPlan, Is.Not.Null);
            return queryPlan!;
        }

        private static object InvokeGetOrCreateQueryPlan(
            QueryPlanPipelineHandler handler,
            AmazonWebServiceRequest request,
            IDictionary<string, object> contextAttributes)
        {
            return handler.GetOrCreateQueryPlan(request, contextAttributes);
        }

        private static List<string> DrainQueryPlanHosts(object queryPlan, int count)
        {
            return DrainQueryPlanUris(queryPlan, count).Select(uri => uri.Host).ToList();
        }

        private static List<Uri> DrainQueryPlanUris(object queryPlan, int count)
        {
            Assert.That(queryPlan, Is.InstanceOf<LazyQueryPlan>());
            var lazyQueryPlan = (LazyQueryPlan)queryPlan;
            var nodes = new List<Uri>();
            for (var index = 0; index < count; index++)
            {
                nodes.Add(lazyQueryPlan.next());
            }

            return nodes;
        }

        private static BatchWriteItemRequest BatchWrite(params KeyValuePair<string, List<WriteRequest>>[] tables)
        {
            var requestItems = new Dictionary<string, List<WriteRequest>>();
            foreach (var table in tables)
            {
                requestItems[table.Key] = table.Value;
            }

            return new BatchWriteItemRequest { RequestItems = requestItems };
        }

        private static KeyRouteAffinityConfig BatchWriteAffinityConfig(IDictionary<string, string> pkInfo)
        {
            return KeyRouteAffinityConfig.builder()
                .withType(KeyRouteAffinity.ANY_WRITE)
                .withPkInfoMap(pkInfo)
                .build();
        }

        private static Helper CreateBatchWriteAffinityHelper()
        {
            return CreateHelperForNodes("node2.example.com", "node10.example.com", "node1.example.com", "node3.example.com");
        }

        private static Uri FirstBatchWriteRoute(Helper helper, KeyRouteAffinityConfig affinity, BatchWriteItemRequest request)
        {
            using var resolver = new PartitionKeyResolver(affinity.PkInfoPerTable.ToDictionary(item => item.Key, item => item.Value));
            var handler = CreateQueryPlanPipelineHandler(helper, affinity, resolver);
            return DrainQueryPlanUris(InvokeGetOrCreateQueryPlan(handler, request, new Dictionary<string, object>()), 1)[0];
        }

        private static object? InvokeTryCreateBatchWriteAffinityQueryPlan(
            AffinityQueryPlanInterceptor handler,
            BatchWriteItemRequest request)
        {
            var method = typeof(AffinityQueryPlanInterceptor).GetMethod(
                "TryCreateBatchWriteAffinityQueryPlan",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(BatchWriteItemRequest) },
                null);
            Assert.That(method, Is.Not.Null);
            return method!.Invoke(handler, new object[] { request });
        }

        private static List<Uri> Uris(params string[] hosts)
        {
            return hosts.Select(host => new Uri($"http://{host}.example.com:8043")).ToList();
        }

        private static List<Uri> SortUris(IEnumerable<Uri> uris)
        {
            var sorted = uris.ToList();
            sorted.Sort((left, right) => string.Compare(left.ToString(), right.ToString(), StringComparison.Ordinal));
            return sorted;
        }

        private static Uri NodeForPartitionKeyValue(Helper helper, AttributeValue value)
        {
            var node = LazyQueryPlan.preferredNodeForHash(
                helper.getAlternatorLiveNodes(),
                AttributeValueHasher.hash(value));
            Assert.That(node, Is.Not.Null);
            return node!;
        }

        private static List<string> FindPartitionKeyValuesForNode(Helper helper, Uri target, int count)
        {
            var keys = new List<string>();
            for (var index = 0; index < 10000 && keys.Count < count; index++)
            {
                var candidate = $"{target.Host}-key-{index}";
                if (NodeForPartitionKeyValue(helper, StringAttribute(candidate)).Equals(target))
                {
                    keys.Add(candidate);
                }
            }

            Assert.That(keys, Has.Count.EqualTo(count));
            return keys;
        }

        private static Dictionary<string, AttributeValue> ItemWithId(string id, string payload)
        {
            return Attributes("id", StringAttribute(id), "data", StringAttribute("payload"), "payload", StringAttribute(payload));
        }

        private static Dictionary<string, AttributeValue> KeyWithId(string id)
        {
            return Attributes("id", StringAttribute(id));
        }

        private static void AssertBatchWriteCandidate(
            IReadOnlyList<KeyAffinityRequestClassifier.BatchWriteRoutingTarget> targets,
            string tableName,
            string operation,
            string pkName,
            string value)
        {
            Assert.That(
                targets.Any(target =>
                    target.TableName == tableName
                    && target.Operation == operation
                    && target.PartitionKeyValue(pkName)?.S == value),
                Is.True);
        }

        private static AttributeValue BinaryAttribute(params int[] values)
        {
            return new AttributeValue { B = new MemoryStream(values.Select(value => (byte)value).ToArray()) };
        }

        private static WriteRequest Delete(Dictionary<string, AttributeValue> key)
        {
            return new WriteRequest { DeleteRequest = new DeleteRequest { Key = key } };
        }

        private static Dictionary<string, AttributeValue> Attributes(params object[] keyValues)
        {
            var attributes = new Dictionary<string, AttributeValue>();
            for (var index = 0; index < keyValues.Length; index += 2)
            {
                attributes[(string)keyValues[index]] = (AttributeValue)keyValues[index + 1];
            }

            return attributes;
        }

        private static AttributeValue NumberAttribute(string value)
        {
            return new AttributeValue { N = value };
        }

        private static List<string> NodeSequence(long seed, int count)
        {
            var helper = CreateHelperForNodes(Enumerable.Range(1, 10).Select(index => $"node{index}.example.com").ToArray());
            return DrainQueryPlanUris(InvokeCreateQueryPlan(helper, seed), count)
                .Select(uri => uri.Host.Substring(0, uri.Host.IndexOf(".example.com", StringComparison.Ordinal)))
                .ToList();
        }

        private static Dictionary<string, string> PkInfo(string tableName, string pkName, params string[] rest)
        {
            var pkInfo = new Dictionary<string, string>
            {
                [tableName] = pkName,
            };
            for (var index = 0; index < rest.Length; index += 2)
            {
                pkInfo[rest[index]] = rest[index + 1];
            }

            return pkInfo;
        }

        private static WriteRequest Put(Dictionary<string, AttributeValue> item)
        {
            return new WriteRequest { PutRequest = new PutRequest { Item = item } };
        }

        private static AttributeValue StringAttribute(string value)
        {
            return new AttributeValue { S = value };
        }

        private static KeyValuePair<string, List<WriteRequest>> Table(string tableName, params WriteRequest[] writes)
        {
            return new KeyValuePair<string, List<WriteRequest>>(tableName, writes.ToList());
        }

        private static void AssertQueryPlan(long seed, int nodeCount, string[] expectedHosts)
        {
            var nodes = Enumerable.Range(1, nodeCount).Select(index => new Uri($"http://node{index}.example.com:8043")).ToList();
            var queryPlan = new LazyQueryPlan(nodes, seed);
            var actualHosts = new List<string>();
            for (var index = 0; index < expectedHosts.Length; index++)
            {
                var host = queryPlan.next().Host;
                actualHosts.Add(host.Substring(0, host.IndexOf(".example.com", StringComparison.Ordinal)));
            }

            Assert.That(actualHosts, Is.EqualTo(expectedHosts));
        }

        private static PartitionKeyResolver CreatePartitionKeyResolver(
            IDictionary<string, string>? preConfigured,
            Func<string, CancellationToken, Task<DescribeTableResponse>> describeTableAsync)
        {
            var constructor = typeof(PartitionKeyResolver).GetConstructor(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(IDictionary<string, string>), typeof(Func<string, CancellationToken, Task<DescribeTableResponse>>) },
                null);
            Assert.That(constructor, Is.Not.Null);
            return (PartitionKeyResolver)constructor!.Invoke(new object?[] { preConfigured, describeTableAsync });
        }

        private static DescribeTableResponse CreateDescribeTableResponse(string partitionKeyName)
        {
            return new DescribeTableResponse
            {
                Table = new TableDescription
                {
                    KeySchema = new List<KeySchemaElement>
                    {
                        new KeySchemaElement
                        {
                            AttributeName = partitionKeyName,
                            KeyType = KeyType.HASH,
                        },
                        new KeySchemaElement
                        {
                            AttributeName = "sort_key",
                            KeyType = KeyType.RANGE,
                        },
                    },
                },
            };
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
            {
                cancellation.Token.ThrowIfCancellationRequested();
                await Task.Delay(20, cancellation.Token);
            }
        }

        private sealed class HealthSplitLiveNodes : AlternatorLiveNodes
        {
            private readonly IReadOnlyList<Uri> activeNodes;
            private readonly IReadOnlyList<Uri> quarantinedNodes;

            internal HealthSplitLiveNodes(IEnumerable<Uri> activeNodes, IEnumerable<Uri> quarantinedNodes)
                : base(BuildSeedNodes(activeNodes, quarantinedNodes), "http", 8043, string.Empty, string.Empty)
            {
                this.activeNodes = activeNodes.ToList().AsReadOnly();
                this.quarantinedNodes = quarantinedNodes.ToList().AsReadOnly();
            }

            protected override IReadOnlyList<Uri> GetActiveNodesInternal()
            {
                return this.activeNodes;
            }

            protected override IReadOnlyList<Uri> GetQuarantinedNodesInternal()
            {
                return this.quarantinedNodes;
            }

            private static List<Uri> BuildSeedNodes(IEnumerable<Uri> activeNodes, IEnumerable<Uri> quarantinedNodes)
            {
                var seedNodes = activeNodes.Concat(quarantinedNodes).ToList();
                if (seedNodes.Count == 0)
                {
                    seedNodes.Add(new Uri("http://seed.example.com:8043"));
                }

                return seedNodes;
            }
        }
    }
}
