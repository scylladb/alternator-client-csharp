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
    using ScyllaDB.Alternator.TestInfrastructure;

    [TestFixture]
    [Category("Integration")]
    [Parallelizable(ParallelScope.All)]
    [FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
    public sealed class ClusterProvisioningTests
    {
        [Test]
        public async Task SameSpecReusesClusterWithIndependentResourceScopes()
        {
            var firstTask = TestClusters.AcquireReusableAsync(ClusterSpecs.Default);
            var secondTask = TestClusters.AcquireReusableAsync(ClusterSpecs.Default);
            await using var first = await firstTask;
            await using var second = await secondTask;

            Assert.That(second.Cluster.InstanceId, Is.EqualTo(first.Cluster.InstanceId));
            Assert.That(second.Resources.NewTableName("table"), Is.Not.EqualTo(first.Resources.NewTableName("table")));
        }

        [Test]
        public async Task AuthorizedClusterProvidesWorkingCredentials()
        {
            var spec = ClusterSpecs.Default
                .WithTopology(ClusterTopology.SingleDatacenter(1))
                .WithTransports(AlternatorTransport.Http)
                .WithSecurity(ClusterSecuritySpec.Enforced);
            await using var lease = await TestClusters.AcquireReusableAsync(spec);
            var connection = lease.Cluster.Connection(AlternatorTransport.Http);
            using var unauthorizedClient = AlternatorDynamoDBClient.builder()
                .endpointOverride(connection.SeedEndpoint)
                .credentialsProvider(new BasicAWSCredentials("wrong-user", "wrong-password"))
                .Build();
            using var client = lease.Cluster.ClientBuilder(AlternatorTransport.Http).Build();

            Assert.That(
                async () => await unauthorizedClient.ListTablesAsync(),
                Throws.InstanceOf<AmazonServiceException>());
            var response = await client.ListTablesAsync();

            Assert.That(response.TableNames, Is.Not.Null);
        }

        [Test]
        public async Task PrivateHttpsClusterCanChangeNodeLifecycleAndTopology()
        {
            var spec = ClusterSpecs.Default
                .WithTopology(ClusterTopology.SingleDatacenter(1))
                .WithTransports(AlternatorTransport.Https);
            await using var lease = await TestClusters.ProvisionPrivateAsync(spec);

            var added = await lease.Control.AddNodeAsync("dc1", "RAC1");
            Assert.That(lease.Cluster.Nodes, Has.Count.EqualTo(2));
            var connection = lease.Cluster.Connection(AlternatorTransport.Https);
            using (var client = AlternatorDynamoDBClient.builder()
                .endpointOverride(new Uri($"https://{added.Address}:{CcmProvisioner.HttpsPort}"))
                .withTlsConfig(TlsConfig.builder()
                    .withCaCertPath(connection.CaCertificatePath!)
                    .withTrustSystemCaCerts(false)
                    .build())
                .Build())
            {
                var tables = await client.ListTablesAsync();
                Assert.That(tables.TableNames, Is.Not.Null);
            }

            await lease.Control.StopNodeAsync(added);
            await lease.Control.StartNodeAsync(added);
            await lease.Control.RemoveNodeAsync(added);

            Assert.That(lease.Cluster.Nodes, Has.Count.EqualTo(1));

            var replacement = await lease.Control.AddNodeAsync("dc1", "RAC1");
            Assert.That(replacement.Address, Is.EqualTo(added.Address));
            using var replacementClient = AlternatorDynamoDBClient.builder()
                .endpointOverride(new Uri($"https://{replacement.Address}:{CcmProvisioner.HttpsPort}"))
                .withTlsConfig(TlsConfig.builder()
                    .withCaCertPath(connection.CaCertificatePath!)
                    .withTrustSystemCaCerts(false)
                    .build())
                .Build();
            var replacementTables = await replacementClient.ListTablesAsync();
            Assert.That(replacementTables.TableNames, Is.Not.Null);
        }
    }
}
