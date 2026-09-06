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

namespace ScyllaDB.Alternator.TestInfrastructure
{
    using System.Collections.Concurrent;
    using System.Formats.Asn1;
    using System.Net;
    using System.Reflection;
    using System.Security.Cryptography.X509Certificates;

    [TestFixture]
    [Category("InfrastructureUnit")]
    [Parallelizable(ParallelScope.All)]
    public sealed class ClusterInfrastructureUnitTests
    {
        [TestCase(1024, 512)]
        [TestCase(8192, 2048)]
        [TestCase(65536, 4096)]
        public void CapacityReserveIsOneQuarterWithinBounds(long availableMiB, long expectedReservedMiB)
        {
            var capacity = ClusterCapacity.FromAvailableMemory(availableMiB);

            Assert.That(capacity.ReservedMemoryMiB, Is.EqualTo(expectedReservedMiB));
            Assert.That(capacity.UsableMemoryMiB, Is.EqualTo(availableMiB - expectedReservedMiB));
        }

        [Test]
        public void ReuseKeyCanonicalizesYamlOverrideOrder()
        {
            var first = ClusterSpecs.Default
                .WithYamlOverride("permissions_validity_in_ms", "0")
                .WithYamlOverride("roles_validity_in_ms", "0");
            var second = ClusterSpecs.Default
                .WithYamlOverride("roles_validity_in_ms", "0")
                .WithYamlOverride("permissions_validity_in_ms", "0");

            Assert.That(first.ReuseKey, Is.EqualTo(second.ReuseKey));
        }

        [Test]
        public void TypedYamlKeysCannotBeOverridden()
        {
            var spec = ClusterSpecs.Default.WithYamlOverride("alternator_write_isolation", "always");

            Assert.That(
                spec.Validate,
                Throws.ArgumentException.With.Message.Contains("owned by a typed cluster option"));
        }

        [TestCase("alternator_write_isolation")]
        [TestCase("start_native_transport")]
        public void InternallyOwnedYamlKeysAreRejectedBeforeProvisioning(string key)
        {
            var spec = ClusterSpecs.Default.WithYamlOverride(key, "true");

            Assert.That(
                spec.Validate,
                Throws.ArgumentException.With.Message.Contains("owned by a typed cluster option"));
        }

        [Test]
        public void SecurityModesHaveIndependentReuseIdentity()
        {
            var passwordOnly = ClusterSpecs.Default.WithSecurity(new ClusterSecuritySpec(
                AuthenticationMode.Password,
                AuthorizationMode.AllowAll,
                false));
            var fullyDisabled = ClusterSpecs.Default.WithSecurity(ClusterSecuritySpec.Disabled);

            Assert.That(passwordOnly.ReuseKey, Is.Not.EqualTo(fullyDisabled.ReuseKey));
        }

        [Test]
        public void EnforcedAlternatorAuthorizationRequiresBothBackends()
        {
            var spec = ClusterSpecs.Default.WithSecurity(new ClusterSecuritySpec(
                AuthenticationMode.Password,
                AuthorizationMode.AllowAll,
                true));

            Assert.That(spec.Validate, Throws.ArgumentException.With.Message.Contains("requires authentication"));
        }

        [Test]
        public void TopologyCannotExceedNineNodes()
        {
            var spec = ClusterSpecs.Default.WithTopology(ClusterTopology.SingleDatacenter(10));

            Assert.That(spec.Validate, Throws.ArgumentException.With.Message.Contains("cannot exceed 9 nodes"));
        }

        [Test]
        public void ReusableLeaseDoesNotExposePrivateClusterControl()
        {
            Assert.That(typeof(IPrivateClusterControl).IsAssignableFrom(typeof(ReusableClusterLease)), Is.False);
            Assert.That(typeof(IPrivateClusterControl).IsAssignableFrom(typeof(ReadOnlyTestCluster)), Is.False);
            const BindingFlags properties = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Assert.That(typeof(ReusableClusterLease).GetProperty("Control", properties), Is.Null);
            Assert.That(typeof(PrivateClusterLease).GetProperty("Control", properties), Is.Not.Null);
        }

        [Test]
        public async Task CallerCancellationDoesNotCancelSharedProvisioning()
        {
            var provisioner = new FakeCcmProvisioner(blockProvisioning: true);
            var pool = CreatePool(provisioner);
            using var cancellation = new CancellationTokenSource();
            try
            {
                var cancelledAcquire = pool.AcquireReusableAsync(OneNodeSpec(), cancellation.Token);
                await provisioner.ProvisioningStarted.Task;
                var successfulAcquire = pool.AcquireReusableAsync(OneNodeSpec(), CancellationToken.None);

                cancellation.Cancel();
                Assert.That(
                    async () => await cancelledAcquire,
                    Throws.InstanceOf<OperationCanceledException>());
                provisioner.CompleteProvisioning();

                await using var lease = await successfulAcquire;
                Assert.That(provisioner.ProvisionCount, Is.EqualTo(1));
            }
            finally
            {
                provisioner.CompleteProvisioning();
                await pool.DisposeAsync();
            }
        }

        [Test]
        public async Task UnhealthyIdleClusterIsRemovedAndRecreated()
        {
            var provisioner = new FakeCcmProvisioner();
            provisioner.HealthResults.Enqueue(true); // First lease release.
            provisioner.HealthResults.Enqueue(false); // Next zero-to-one validation.
            provisioner.HealthResults.Enqueue(true); // Replacement lease release.
            var pool = CreatePool(provisioner);
            try
            {
                var first = await pool.AcquireReusableAsync(OneNodeSpec(), CancellationToken.None);
                var firstInstance = first.Cluster.InstanceId;
                await first.DisposeAsync();

                var replacement = await pool.AcquireReusableAsync(OneNodeSpec(), CancellationToken.None);
                Assert.That(replacement.Cluster.InstanceId, Is.Not.EqualTo(firstInstance));
                Assert.That(provisioner.ProvisionCount, Is.EqualTo(2));
                Assert.That(provisioner.RemoveCount, Is.EqualTo(1));
                await replacement.DisposeAsync();
            }
            finally
            {
                await pool.DisposeAsync();
            }
        }

        [Test]
        public async Task PrivateClusterDisposalSkipsEndpointResourceCleanup()
        {
            var cleanupCalls = 0;
            var provisioner = new FakeCcmProvisioner();
            var pool = CreatePool(
                provisioner,
                (_, _) =>
                {
                    cleanupCalls++;
                    throw new InvalidOperationException("Private cleanup should not run.");
                });
            try
            {
                var lease = await pool.ProvisionPrivateAsync(OneNodeSpec(), CancellationToken.None);
                await lease.DisposeAsync();

                Assert.That(cleanupCalls, Is.Zero);
                Assert.That(provisioner.RemoveCount, Is.EqualTo(1));
            }
            finally
            {
                await pool.DisposeAsync();
            }
        }

        [Test]
        public async Task ReusableCleanupFailurePoisonsAndRemovesCluster()
        {
            var provisioner = new FakeCcmProvisioner();
            var pool = CreatePool(
                provisioner,
                (_, _) => throw new InvalidOperationException("Injected cleanup failure."));
            try
            {
                var lease = await pool.AcquireReusableAsync(OneNodeSpec(), CancellationToken.None);
                await lease.DisposeAsync();

                Assert.That(provisioner.RemoveCount, Is.EqualTo(1));
            }
            finally
            {
                await pool.DisposeAsync();
            }
        }

        [Test]
        public async Task ReusableClusterIsNotHandedOutWhileReleaseValidationIsPending()
        {
            var cleanupStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishCleanup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cleanupCalls = 0;
            var provisioner = new FakeCcmProvisioner();
            var pool = CreatePool(
                provisioner,
                async (_, _) =>
                {
                    if (Interlocked.Increment(ref cleanupCalls) == 1)
                    {
                        cleanupStarted.TrySetResult(true);
                        await finishCleanup.Task;
                        throw new InvalidOperationException("Injected cleanup failure.");
                    }
                });
            try
            {
                var first = await pool.AcquireReusableAsync(OneNodeSpec(), CancellationToken.None);
                var firstInstance = first.Cluster.InstanceId;
                var release = first.DisposeAsync().AsTask();
                await cleanupStarted.Task;

                var replacementTask = pool.AcquireReusableAsync(OneNodeSpec(), CancellationToken.None);
                Assert.That(replacementTask.IsCompleted, Is.False);

                finishCleanup.TrySetResult(true);
                await release;
                await using var replacement = await replacementTask;
                Assert.That(replacement.Cluster.InstanceId, Is.Not.EqualTo(firstInstance));
            }
            finally
            {
                finishCleanup.TrySetResult(true);
                await pool.DisposeAsync();
            }
        }

        [Test]
        public async Task PrivateRemoveFailureRetainsCapacityUntilSuiteCleanupRetries()
        {
            var provisioner = new FakeCcmProvisioner { RemoveFailures = 1 };
            var pool = new TestClusterPool(
                provisioner,
                ClusterCapacity.FromAvailableMemory(2048),
                1,
                (_, _) => Task.CompletedTask);
            try
            {
                var failedLease = await pool.ProvisionPrivateAsync(OneNodeSpec(), CancellationToken.None);
                Assert.That(
                    async () => await failedLease.DisposeAsync(),
                    Throws.TypeOf<InvalidOperationException>());

                using var cancellation = new CancellationTokenSource();
                var replacement = pool.ProvisionPrivateAsync(OneNodeSpec(), cancellation.Token);
                Assert.That(replacement.IsCompleted, Is.False);
                cancellation.Cancel();
                Assert.That(
                    async () => await replacement,
                    Throws.InstanceOf<OperationCanceledException>());

                await pool.DisposeAsync();
                Assert.That(provisioner.ProvisionCount, Is.EqualTo(1));
                Assert.That(provisioner.RemoveCount, Is.EqualTo(2));
            }
            finally
            {
                await pool.DisposeAsync();
            }
        }

        [Test]
        public async Task PooledRemoveFailureRetainsCapacityUntilSuiteCleanupRetries()
        {
            var provisioner = new FakeCcmProvisioner { RemoveFailures = 1 };
            var pool = new TestClusterPool(
                provisioner,
                ClusterCapacity.FromAvailableMemory(2048),
                1,
                (_, _) => throw new InvalidOperationException("Poison the reusable cluster."));
            try
            {
                var failedLease = await pool.AcquireReusableAsync(OneNodeSpec(), CancellationToken.None);
                Assert.That(
                    async () => await failedLease.DisposeAsync(),
                    Throws.TypeOf<InvalidOperationException>());

                using var cancellation = new CancellationTokenSource();
                var replacement = pool.AcquireReusableAsync(OneNodeSpec(), cancellation.Token);
                Assert.That(replacement.IsCompleted, Is.False);
                cancellation.Cancel();
                Assert.That(
                    async () => await replacement,
                    Throws.InstanceOf<OperationCanceledException>());

                await pool.DisposeAsync();
                Assert.That(provisioner.ProvisionCount, Is.EqualTo(1));
                Assert.That(provisioner.RemoveCount, Is.EqualTo(2));
            }
            finally
            {
                await pool.DisposeAsync();
            }
        }

        [Test]
        public async Task PrivateNodeExpansionFailsInsteadOfWaitingForLeaseHeldCapacity()
        {
            var provisioner = new FakeCcmProvisioner();
            var pool = new TestClusterPool(
                provisioner,
                ClusterCapacity.FromAvailableMemory(4096),
                2,
                (_, _) => Task.CompletedTask);
            try
            {
                await using var first = await pool.ProvisionPrivateAsync(OneNodeSpec(), CancellationToken.None);
                await using var second = await pool.ProvisionPrivateAsync(OneNodeSpec(), CancellationToken.None);

                Assert.That(
                    async () => await first.Control.AddNodeAsync("dc1", "RAC1"),
                    Throws.InvalidOperationException.With.Message.Contains("capacity deadlock"));
            }
            finally
            {
                await pool.DisposeAsync();
            }
        }

        [Test]
        public async Task PrivateNodeExpansionEvictsAnIdleReusableCluster()
        {
            var provisioner = new FakeCcmProvisioner();
            var pool = new TestClusterPool(
                provisioner,
                ClusterCapacity.FromAvailableMemory(4096),
                2,
                (_, _) => Task.CompletedTask);
            try
            {
                var reusable = await pool.AcquireReusableAsync(OneNodeSpec(), CancellationToken.None);
                await reusable.DisposeAsync();
                await using var privateCluster = await pool.ProvisionPrivateAsync(
                    OneNodeSpec().WithYamlOverride("permissions_validity_in_ms", "0"),
                    CancellationToken.None);

                await pool.ReserveAdditionalPrivateNodeAsync(
                    (PhysicalTestCluster)privateCluster.Cluster,
                    CancellationToken.None);
                pool.ReleaseAdditionalPrivateNode(privateCluster.Cluster.Spec);

                Assert.That(provisioner.RemoveCount, Is.EqualTo(1));
            }
            finally
            {
                await pool.DisposeAsync();
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task FailedProvisioningRollbackRetainsCapacityUntilSuiteCleanupRetries(bool reusable)
        {
            var provisioner = new FakeCcmProvisioner { ProvisioningRollbackFailures = 1 };
            var pool = new TestClusterPool(
                provisioner,
                ClusterCapacity.FromAvailableMemory(2048),
                1,
                (_, _) => Task.CompletedTask);
            try
            {
                Assert.That(
                    async () =>
                    {
                        if (reusable)
                        {
                            await pool.AcquireReusableAsync(OneNodeSpec(), CancellationToken.None);
                        }
                        else
                        {
                            await pool.ProvisionPrivateAsync(OneNodeSpec(), CancellationToken.None);
                        }
                    },
                    Throws.TypeOf<CcmClusterProvisioningException>());

                using var cancellation = new CancellationTokenSource();
                var replacement = reusable
                    ? (Task)pool.AcquireReusableAsync(OneNodeSpec(), cancellation.Token)
                    : pool.ProvisionPrivateAsync(OneNodeSpec(), cancellation.Token);
                Assert.That(replacement.IsCompleted, Is.False);
                cancellation.Cancel();
                Assert.That(
                    async () => await replacement,
                    Throws.InstanceOf<OperationCanceledException>());

                await pool.DisposeAsync();
                Assert.That(provisioner.ProvisionCount, Is.EqualTo(1));
                Assert.That(provisioner.RemoveCount, Is.EqualTo(1));
            }
            finally
            {
                await pool.DisposeAsync();
            }
        }

        [Test]
        public void FailedInitialClusterRollbackExposesClusterForRetry()
        {
            var provisioner = new FailingInitialStartProvisioner();

            var exception = Assert.ThrowsAsync<CcmClusterProvisioningException>(
                async () => await provisioner.ProvisionAsync(
                    OneNodeSpec(),
                    "failed-cluster",
                    1,
                    CancellationToken.None));

            Assert.That(exception!.Cluster.InstanceId, Is.EqualTo("failed-cluster"));
            Assert.That(
                provisioner.Commands,
                Has.Exactly(1).Matches<string[]>(command => command[0] == "remove"));
        }

        [Test]
        public void ResourceNamesContainOnlyDynamoDbAsciiCharacters()
        {
            var provisioner = new FakeCcmProvisioner();
            var resources = new TestResourceScope(CreatePhysicalCluster(provisioner), "Rün.Id-1", 7);

            var tableName = resources.NewTableName("Tést-._١");

            Assert.That(tableName, Does.Match("^[a-z0-9_.-]+$"));
            Assert.That(tableName, Does.Contain("t_st-.__"));
        }

        [Test]
        public void ResourceNamesDoNotExceedDynamoDbLengthLimit()
        {
            var provisioner = new FakeCcmProvisioner();
            var resources = new TestResourceScope(
                CreatePhysicalCluster(provisioner),
                new string('r', 400),
                long.MaxValue);

            var tableName = resources.NewTableName(new string('h', 400));

            Assert.That(tableName, Has.Length.EqualTo(255));
            Assert.That(tableName, Does.Match("^[a-z0-9_.-]+$"));
        }

        [TestCase("4096", 4096)]
        [TestCase("1", 1)]
        public void ExplicitAvailableMemoryAcceptsPositiveIntegers(string configured, long expectedMiB)
        {
            Assert.That(AvailableMemoryDetector.ParseConfiguredMemoryMiB(configured), Is.EqualTo(expectedMiB));
        }

        [TestCase("4096MB")]
        [TestCase("0")]
        [TestCase("-1")]
        [TestCase("")]
        public void ExplicitAvailableMemoryRejectsInvalidValues(string configured)
        {
            Assert.That(
                () => AvailableMemoryDetector.ParseConfiguredMemoryMiB(configured),
                Throws.InvalidOperationException.With.Message.Contains("must be a positive integer"));
        }

        [Test]
        public void FailedAddedNodeStartIsRolledBack()
        {
            var provisioner = new FailingNodeStartProvisioner(rollbackFails: false);
            var cluster = CreatePhysicalCluster(provisioner);

            var exception = Assert.ThrowsAsync<CcmNodeProvisioningException>(
                async () => await provisioner.AddNodeAsync(cluster, "dc1", "RAC1", CancellationToken.None));

            Assert.That(exception!.NodeRemainsProvisioned, Is.False);
            Assert.That(provisioner.Commands.Any(IsNodeTwoRemove), Is.True);
        }

        [Test]
        public void FailedCcmAddCommandIsRolledBack()
        {
            var provisioner = new FailingNodeStartProvisioner(rollbackFails: false, addFails: true);
            var cluster = CreatePhysicalCluster(provisioner);

            var exception = Assert.ThrowsAsync<CcmNodeProvisioningException>(
                async () => await provisioner.AddNodeAsync(cluster, "dc1", "RAC1", CancellationToken.None));

            Assert.That(exception!.NodeRemainsProvisioned, Is.False);
            Assert.That(provisioner.Commands.Any(IsNodeTwoRemove), Is.True);
        }

        [Test]
        public void FailedAddedNodeRollbackRemainsTracked()
        {
            var provisioner = new FailingNodeStartProvisioner(rollbackFails: true);
            var cluster = CreatePhysicalCluster(provisioner);

            var exception = Assert.ThrowsAsync<CcmNodeProvisioningException>(
                async () => await cluster.AddNodeAsync("dc1", "RAC1", CancellationToken.None));

            Assert.That(exception!.NodeRemainsProvisioned, Is.True);
            Assert.That(cluster.Nodes.Select(node => node.Name), Does.Contain("node2"));
        }

        [Test]
        public async Task JoinedNodeIsDecommissionedBeforeItsCcmStateIsDeleted()
        {
            var provisioner = new RecordingCcmProvisioner();
            var spec = OneNodeSpec().WithTopology(ClusterTopology.SingleDatacenter(2));
            var nodes = new[]
            {
                new TestClusterNode("node1", "127.0.1.1", "dc1", "RAC1"),
                new TestClusterNode("node2", "127.0.1.2", "dc1", "RAC1"),
            };
            var cluster = new PhysicalTestCluster(
                provisioner,
                "node-removal-test",
                1,
                provisioner.RunDirectory,
                spec,
                nodes,
                null,
                null);

            await cluster.RemoveNodeAsync(nodes[1], CancellationToken.None);

            var decommission = provisioner.Commands.FindIndex(IsNodeTwoDecommission);
            var deleteState = provisioner.Commands.FindIndex(IsNodeTwoRemove);
            Assert.That(decommission, Is.GreaterThanOrEqualTo(0));
            Assert.That(deleteState, Is.GreaterThan(decommission));
            Assert.That(cluster.Nodes.Select(node => node.Name), Does.Not.Contain("node2"));
        }

        [Test]
        public async Task StoppedJoinedNodeIsRestartedBeforeDecommission()
        {
            var provisioner = new RecordingCcmProvisioner();
            var spec = OneNodeSpec().WithTopology(ClusterTopology.SingleDatacenter(2));
            var nodes = new[]
            {
                new TestClusterNode("node1", "127.0.1.1", "dc1", "RAC1"),
                new TestClusterNode("node2", "127.0.1.2", "dc1", "RAC1"),
            };
            var cluster = new PhysicalTestCluster(
                provisioner,
                "stopped-node-removal-test",
                1,
                provisioner.RunDirectory,
                spec,
                nodes,
                null,
                null);

            await cluster.StopNodeAsync(nodes[1], CancellationToken.None);
            await cluster.RemoveNodeAsync(nodes[1], CancellationToken.None);

            Assert.That(provisioner.StartNodeCount, Is.EqualTo(1));
            Assert.That(provisioner.Commands.Any(IsNodeTwoDecommission), Is.True);
        }

        [Test]
        public async Task HttpsNodesUseIndividualCertificatesFromClusterCertificateAuthority()
        {
            var provisioner = new RecordingCcmProvisioner();
            var spec = ClusterSpecs.Default
                .WithTopology(ClusterTopology.SingleDatacenter(1, 1))
                .WithTransports(AlternatorTransport.Https);
            var cluster = await provisioner.ProvisionAsync(spec, "certificate-test", 1, CancellationToken.None);

            var create = provisioner.Commands.Single(command => command[0] == "create");
            Assert.That(create, Does.Not.Contain("--snitch"));
            var updateConfiguration = provisioner.Commands.Single(command => command[0] == "updateconf");
            Assert.That(
                updateConfiguration,
                Does.Contain("endpoint_snitch:org.apache.cassandra.locator.GossipingPropertyFileSnitch"));
            var initialTopologyAdd = provisioner.Commands.Single(command =>
                command[0] == "add" && command.Contains("node2"));
            Assert.That(initialTopologyAdd, Does.Not.Contain("--auto-bootstrap"));
            AssertNodeCertificate(provisioner, cluster, "node1");
            AssertNodeCertificate(provisioner, cluster, "node2");

            using var node1Certificate = LoadNodeCertificate(provisioner.RunDirectory, cluster, "node1");
            using var node2Certificate = LoadNodeCertificate(provisioner.RunDirectory, cluster, "node2");
            Assert.That(node2Certificate.Thumbprint, Is.Not.EqualTo(node1Certificate.Thumbprint));

            var added = await cluster.AddNodeAsync("dc2", "RAC9", CancellationToken.None);
            Assert.That(added.Name, Is.EqualTo("node3"));
            var add = provisioner.Commands.Single(command => command[0] == "add" && command.Contains("node3"));
            Assert.That(add, Does.Contain("--auto-bootstrap"));
            AssertNodeCertificate(provisioner, cluster, added.Name);
            using var addedCertificate = LoadNodeCertificate(provisioner.RunDirectory, cluster, added.Name);
            Assert.That(addedCertificate.Thumbprint, Is.Not.EqualTo(node1Certificate.Thumbprint));
            Assert.That(addedCertificate.Thumbprint, Is.Not.EqualTo(node2Certificate.Thumbprint));
        }

        [Test]
        public async Task MultiDatacenterCreateUsesCcmSupportedSnitchFlag()
        {
            var provisioner = new RecordingCcmProvisioner();
            var spec = ClusterSpecs.Default
                .WithTopology(new ClusterTopology(new[]
                {
                    DatacenterSpec.Create(1),
                    DatacenterSpec.Create(1),
                }))
                .WithTransports(AlternatorTransport.Http);

            await provisioner.ProvisionAsync(spec, "multi-dc-test", 1, CancellationToken.None);

            var create = provisioner.Commands.Single(command => command[0] == "create");
            Assert.That(create, Does.Contain("1:1"));
            Assert.That(create, Does.Contain("--snitch"));
            Assert.That(create, Does.Contain("org.apache.cassandra.locator.GossipingPropertyFileSnitch"));
        }

        [Test]
        public async Task DiagnosticsFailuresDoNotSkipClusterRemoval()
        {
            var provisioner = new DiagnosticsFailingCcmProvisioner();
            var cluster = CreatePhysicalCluster(provisioner);

            await provisioner.RemoveAsync(cluster, CancellationToken.None);

            Assert.That(provisioner.DiagnosticsAttempts, Is.EqualTo(2));
            Assert.That(
                provisioner.Commands,
                Has.Exactly(1).Matches<string[]>(command => command[0] == "remove"));
        }

        [TestCase("1073741824", "536870912", 512)]
        [TestCase("max", "536870912", null)]
        [TestCase("9223372036854771712", "0", null)]
        [TestCase("1048576", "524289", 0)]
        public void CgroupMemorySupportsV1V2AndUnlimitedMarkers(
            string maximum,
            string current,
            long? expectedMiB)
        {
            Assert.That(
                AvailableMemoryDetector.CalculateCgroupAvailableMemoryMiB(maximum, current),
                Is.EqualTo(expectedMiB));
        }

        [Test]
        public void CgroupV2PathsFollowProcessMembershipAndAncestors()
        {
            const string cgroup = "0::/user.slice/user-1003.slice/session-c1.scope\n";
            const string mountInfo = "29 23 0:26 / /sys/fs/cgroup rw,nosuid,nodev,noexec - cgroup2 cgroup rw\n";

            var paths = AvailableMemoryDetector.ResolveCgroupMemoryPaths(cgroup, mountInfo);

            Assert.That(paths, Does.Contain((
                "/sys/fs/cgroup/user.slice/user-1003.slice/session-c1.scope/memory.max",
                "/sys/fs/cgroup/user.slice/user-1003.slice/session-c1.scope/memory.current")));
            Assert.That(paths, Does.Contain((
                "/sys/fs/cgroup/user.slice/memory.max",
                "/sys/fs/cgroup/user.slice/memory.current")));
            Assert.That(paths, Does.Contain((
                "/sys/fs/cgroup/memory.max",
                "/sys/fs/cgroup/memory.current")));
        }

        [Test]
        public void CgroupV1PathsHonorControllerMountRoot()
        {
            const string cgroup = "5:cpu,memory:/docker/root/ci/job\n";
            const string mountInfo = "31 23 0:28 /docker/root /sys/fs/cgroup/memory rw,nosuid,nodev,noexec - cgroup cgroup rw,memory\n";

            var paths = AvailableMemoryDetector.ResolveCgroupMemoryPaths(cgroup, mountInfo);

            Assert.That(paths[0], Is.EqualTo((
                "/sys/fs/cgroup/memory/ci/job/memory.limit_in_bytes",
                "/sys/fs/cgroup/memory/ci/job/memory.usage_in_bytes")));
        }

        private static ClusterSpec OneNodeSpec()
        {
            return ClusterSpecs.Default
                .WithTopology(ClusterTopology.SingleDatacenter(1))
                .WithTransports(AlternatorTransport.Http);
        }

        private static TestClusterPool CreatePool(
            FakeCcmProvisioner provisioner,
            Func<TestResourceScope, CancellationToken, Task>? cleanup = null)
        {
            return new TestClusterPool(
                provisioner,
                ClusterCapacity.FromAvailableMemory(16 * 1024),
                ClusterCapacity.MaximumNodeCount,
                cleanup ?? ((_, _) => Task.CompletedTask));
        }

        private static PhysicalTestCluster CreatePhysicalCluster(CcmProvisioner provisioner)
        {
            return new PhysicalTestCluster(
                provisioner,
                "test-cluster",
                1,
                provisioner.RunDirectory,
                OneNodeSpec(),
                new[] { new TestClusterNode("node1", "127.0.1.1", "dc1", "RAC1") },
                null,
                null);
        }

        private static bool IsNodeTwoRemove(string[] command)
        {
            return command.Length >= 2 && command[0] == "node2" && command[1] == "remove";
        }

        private static bool IsNodeTwoDecommission(string[] command)
        {
            return command.Length >= 2 && command[0] == "node2" && command[1] == "decommission";
        }

        private static X509Certificate2 LoadNodeCertificate(
            string runDirectory,
            PhysicalTestCluster cluster,
            string nodeName)
        {
            var certificatePath = Path.Combine(
                runDirectory,
                "clusters",
                cluster.InstanceId,
                "tls",
                nodeName,
                "server.crt");
            var keyPath = Path.Combine(
                runDirectory,
                "clusters",
                cluster.InstanceId,
                "tls",
                nodeName,
                "server.key");
            return X509Certificate2.CreateFromPemFile(certificatePath, keyPath);
        }

        private static void AssertNodeCertificate(
            RecordingCcmProvisioner provisioner,
            PhysicalTestCluster cluster,
            string nodeName)
        {
            using var certificateAuthority = X509Certificate2.CreateFromPem(
                File.ReadAllText(cluster.CaCertificatePath!));
            using var certificate = LoadNodeCertificate(provisioner.RunDirectory, cluster, nodeName);
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificateAuthority);

            Assert.That(certificate.HasPrivateKey, Is.True);
            Assert.That(chain.Build(certificate), Is.True);
            var expectedAddress = IPAddress.Parse(cluster.Nodes.Single(node => node.Name == nodeName).Address);
            var subjectAlternativeName = certificate.Extensions["2.5.29.17"]
                ?? throw new InvalidOperationException("A node certificate has no subject alternative name.");
            var reader = new AsnReader(subjectAlternativeName.RawData, AsnEncodingRules.DER).ReadSequence();
            var addresses = new List<IPAddress>();
            var ipAddressTag = new Asn1Tag(TagClass.ContextSpecific, 7);
            while (reader.HasData)
            {
                if (reader.PeekTag().HasSameClassAndValue(ipAddressTag))
                {
                    addresses.Add(new IPAddress(reader.ReadOctetString(ipAddressTag)));
                }
                else
                {
                    reader.ReadEncodedValue();
                }
            }

            Assert.That(addresses, Is.EqualTo(new[] { expectedAddress }));
            var configuration = provisioner.Commands.Single(command =>
                command.Length >= 2 && command[0] == nodeName && command[1] == "updateconf");
            var certificatePath = Path.Combine(
                cluster.CcmDirectory,
                "tls",
                nodeName,
                "server.crt");
            Assert.That(
                configuration,
                Does.Contain($"alternator_encryption_options.certificate:{certificatePath}"));
        }

        private sealed class FakeCcmProvisioner : CcmProvisioner
        {
            private readonly TaskCompletionSource<bool> provisionGate = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            internal FakeCcmProvisioner(bool blockProvisioning = false)
                : base(Path.Combine(Path.GetTempPath(), $"alternator-ccm-unit-{Guid.NewGuid():N}"))
            {
                if (!blockProvisioning)
                {
                    this.CompleteProvisioning();
                }
            }

            internal TaskCompletionSource<bool> ProvisioningStarted { get; } = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            internal ConcurrentQueue<bool> HealthResults { get; } = new ConcurrentQueue<bool>();

            internal int ProvisionCount { get; private set; }

            internal int RemoveCount { get; private set; }

            internal int RemoveFailures { get; set; }

            internal int ProvisioningRollbackFailures { get; set; }

            internal void CompleteProvisioning()
            {
                this.provisionGate.TrySetResult(true);
            }

            internal override async Task<PhysicalTestCluster> ProvisionAsync(
                ClusterSpec spec,
                string instanceId,
                int ccmId,
                CancellationToken cancellationToken)
            {
                this.ProvisionCount++;
                this.ProvisioningStarted.TrySetResult(true);
                await this.provisionGate.Task.WaitAsync(cancellationToken);
                var cluster = new PhysicalTestCluster(
                    this,
                    instanceId,
                    ccmId,
                    this.RunDirectory,
                    spec,
                    new[] { new TestClusterNode("node1", $"127.0.{ccmId}.1", "dc1", "RAC1") },
                    null,
                    null);
                if (this.ProvisioningRollbackFailures > 0)
                {
                    this.ProvisioningRollbackFailures--;
                    throw new CcmClusterProvisioningException(
                        cluster,
                        new InvalidOperationException("Injected provisioning failure."),
                        new InvalidOperationException("Injected rollback failure."));
                }

                return cluster;
            }

            internal override Task<bool> IsHealthyAsync(
                PhysicalTestCluster cluster,
                CancellationToken cancellationToken)
            {
                return Task.FromResult(!this.HealthResults.TryDequeue(out var result) || result);
            }

            internal override Task RemoveAsync(
                PhysicalTestCluster cluster,
                CancellationToken cancellationToken)
            {
                this.RemoveCount++;
                if (this.RemoveFailures > 0)
                {
                    this.RemoveFailures--;
                    throw new InvalidOperationException("Injected cluster removal failure.");
                }

                return Task.CompletedTask;
            }
        }

        private sealed class FailingNodeStartProvisioner : CcmProvisioner
        {
            private readonly bool rollbackFails;
            private readonly bool addFails;

            internal FailingNodeStartProvisioner(bool rollbackFails, bool addFails = false)
                : base(Path.Combine(Path.GetTempPath(), $"alternator-ccm-unit-{Guid.NewGuid():N}"))
            {
                this.rollbackFails = rollbackFails;
                this.addFails = addFails;
            }

            internal List<string[]> Commands { get; } = new List<string[]>();

            protected override Task RunAsync(
                string ccmDirectory,
                IEnumerable<string> arguments,
                CancellationToken cancellationToken)
            {
                var command = arguments.ToArray();
                this.Commands.Add(command);
                if (this.addFails && command[0] == "add")
                {
                    throw new InvalidOperationException("Injected CCM add failure.");
                }

                if (command.Length >= 2 && command[0] == "node2" && command[1] == "start")
                {
                    throw new InvalidOperationException("Injected node start failure.");
                }

                if (this.rollbackFails && IsNodeTwoRemove(command))
                {
                    throw new InvalidOperationException("Injected node rollback failure.");
                }

                return Task.CompletedTask;
            }
        }

        private sealed class FailingInitialStartProvisioner : CcmProvisioner
        {
            internal FailingInitialStartProvisioner()
                : base(Path.Combine(Path.GetTempPath(), $"alternator-ccm-unit-{Guid.NewGuid():N}"))
            {
            }

            internal List<string[]> Commands { get; } = new List<string[]>();

            internal override Task StartAsync(
                PhysicalTestCluster cluster,
                CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("Injected initial start failure.");
            }

            protected override Task RunAsync(
                string ccmDirectory,
                IEnumerable<string> arguments,
                CancellationToken cancellationToken)
            {
                var command = arguments.ToArray();
                this.Commands.Add(command);
                return command[0] == "remove"
                    ? Task.FromException(new InvalidOperationException("Injected rollback failure."))
                    : Task.CompletedTask;
            }
        }

        private sealed class RecordingCcmProvisioner : CcmProvisioner
        {
            internal RecordingCcmProvisioner()
                : base(Path.Combine(Path.GetTempPath(), $"alternator-ccm-unit-{Guid.NewGuid():N}"))
            {
            }

            internal List<string[]> Commands { get; } = new List<string[]>();

            internal int StartNodeCount { get; private set; }

            internal override Task StartAsync(
                PhysicalTestCluster cluster,
                CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            internal override Task StartNodeAsync(
                PhysicalTestCluster cluster,
                TestClusterNode node,
                CancellationToken cancellationToken)
            {
                this.StartNodeCount++;
                return Task.CompletedTask;
            }

            protected override Task RunAsync(
                string ccmDirectory,
                IEnumerable<string> arguments,
                CancellationToken cancellationToken)
            {
                this.Commands.Add(arguments.ToArray());
                return Task.CompletedTask;
            }
        }

        private sealed class DiagnosticsFailingCcmProvisioner : CcmProvisioner
        {
            internal DiagnosticsFailingCcmProvisioner()
                : base(Path.Combine(Path.GetTempPath(), $"alternator-ccm-unit-{Guid.NewGuid():N}"))
            {
            }

            internal List<string[]> Commands { get; } = new List<string[]>();

            internal int DiagnosticsAttempts { get; private set; }

            protected override Task CollectDiagnosticsAsync(
                string instanceId,
                string ccmDirectory,
                CancellationToken cancellationToken)
            {
                this.DiagnosticsAttempts++;
                throw new IOException("Injected diagnostics failure.");
            }

            protected override Task RunAsync(
                string ccmDirectory,
                IEnumerable<string> arguments,
                CancellationToken cancellationToken)
            {
                this.Commands.Add(arguments.ToArray());
                return Task.CompletedTask;
            }
        }
    }
}
