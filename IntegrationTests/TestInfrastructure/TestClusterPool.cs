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

#pragma warning disable SA1202, SA1204, SA1402, SA1649

namespace ScyllaDB.Alternator.TestInfrastructure
{
    using System.Globalization;
    using System.Net.Sockets;

    internal static class TestClusters
    {
        private static readonly Lazy<TestClusterPool> SharedPool = new Lazy<TestClusterPool>(
            TestClusterPool.CreateDefault,
            LazyThreadSafetyMode.ExecutionAndPublication);

        internal static Task<ReusableClusterLease> AcquireReusableAsync(
            ClusterSpec spec,
            CancellationToken cancellationToken = default)
        {
            return SharedPool.Value.AcquireReusableAsync(spec, cancellationToken);
        }

        internal static Task<PrivateClusterLease> ProvisionPrivateAsync(
            ClusterSpec spec,
            CancellationToken cancellationToken = default)
        {
            return SharedPool.Value.ProvisionPrivateAsync(spec, cancellationToken);
        }

        internal static Task DisposeAsync()
        {
            return SharedPool.IsValueCreated ? SharedPool.Value.DisposeAsync() : Task.CompletedTask;
        }
    }

    internal sealed class PooledCluster
    {
        internal PooledCluster(string reuseKey, ClusterSpec spec, string instanceId, int ccmId)
        {
            this.ReuseKey = reuseKey;
            this.Spec = spec;
            this.InstanceId = instanceId;
            this.CcmId = ccmId;
            this.ReadySource = new TaskCompletionSource<PhysicalTestCluster>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal string ReuseKey { get; }

        internal ClusterSpec Spec { get; }

        internal string InstanceId { get; }

        internal int CcmId { get; }

        internal TaskCompletionSource<PhysicalTestCluster> ReadySource { get; }

        internal Task<PhysicalTestCluster> Ready => this.ReadySource.Task;

        internal int ReferenceCount { get; set; }

        internal bool Poisoned { get; set; }

        internal bool ReservationReleased { get; set; }

        internal int PendingReleases { get; set; }

        internal Task<bool>? ReuseValidation { get; set; }

        internal DateTimeOffset LastReleased { get; set; } = DateTimeOffset.UtcNow;
    }

    internal sealed class TestClusterPool
    {
        private readonly object sync = new object();
        private readonly CcmProvisioner provisioner;
        private readonly ClusterCapacity capacity;
        private readonly int maximumNodes;
        private readonly Func<TestResourceScope, CancellationToken, Task> cleanupResources;
        private readonly Dictionary<string, PooledCluster> reusableClusters = new Dictionary<string, PooledCluster>(StringComparer.Ordinal);
        private readonly HashSet<PooledCluster> failedPooledRemovals = new HashSet<PooledCluster>();
        private readonly HashSet<PhysicalTestCluster> failedProvisioningRemovals = new HashSet<PhysicalTestCluster>();
        private readonly HashSet<PhysicalTestCluster> privateClusters = new HashSet<PhysicalTestCluster>();
        private readonly bool[] allocatedCcmIds = new bool[100];
        private readonly Dictionary<int, FileStream> ccmIdLocks = new Dictionary<int, FileStream>();
        private readonly CancellationTokenSource lifetimeCancellation = new CancellationTokenSource();
        private TaskCompletionSource<bool> capacityChanged = NewSignal();
        private long leaseCounter;
        private int instanceCounter;
        private int usedNodes;
        private long usedMemoryMiB;
        private bool disposed;

        internal TestClusterPool(
            CcmProvisioner provisioner,
            ClusterCapacity capacity,
            int maximumNodes,
            Func<TestResourceScope, CancellationToken, Task>? cleanupResources = null)
        {
            this.provisioner = provisioner;
            this.capacity = capacity;
            this.maximumNodes = maximumNodes;
            this.cleanupResources = cleanupResources ?? ((resources, token) => resources.CleanupAsync(token));
            TestContext.Progress.WriteLine(
                $"CCM capacity: {capacity.AvailableMemoryMiB} MiB available, "
                + $"{capacity.ReservedMemoryMiB} MiB reserved, {capacity.UsableMemoryMiB} MiB usable, "
                + $"maximum {maximumNodes} nodes.");
        }

        internal static TestClusterPool CreateDefault()
        {
            var runDirectory = Environment.GetEnvironmentVariable("SCYLLA_CCM_RUN_DIR");
            if (string.IsNullOrWhiteSpace(runDirectory))
            {
                runDirectory = Path.Combine(
                    Path.GetTempPath(),
                    $"alternator-client-csharp-ccm-{Environment.ProcessId}-{Guid.NewGuid():N}");
            }

            var availableMemory = AvailableMemoryDetector.GetAvailableMemoryMiB();
            var capacity = ClusterCapacity.FromAvailableMemory(availableMemory);
            var configuredMaximum = ParsePositiveInteger("SCYLLA_CCM_MAX_NODES", ClusterCapacity.MaximumNodeCount);
            return new TestClusterPool(
                new CcmProvisioner(runDirectory),
                capacity,
                Math.Min(configuredMaximum, ClusterCapacity.MaximumNodeCount));
        }

        internal async Task<ReusableClusterLease> AcquireReusableAsync(
            ClusterSpec spec,
            CancellationToken cancellationToken)
        {
            spec = spec.Snapshot();
            spec.Validate();
            this.ValidateDemand(spec);
            while (true)
            {
                PooledCluster? selected = null;
                PooledCluster? eviction = null;
                Task<bool>? reuseValidation = null;
                Task waitTask = Task.CompletedTask;
                var mustProvision = false;
                lock (this.sync)
                {
                    this.ThrowIfDisposed();
                    if (this.reusableClusters.TryGetValue(spec.ReuseKey, out var existing))
                    {
                        if (!existing.Poisoned && existing.PendingReleases == 0)
                        {
                            if (existing.ReferenceCount == 0 && existing.Ready.IsCompletedSuccessfully)
                            {
                                existing.ReuseValidation = this.ValidateReusableAsync(existing);
                            }

                            existing.ReferenceCount++;
                            selected = existing;
                            reuseValidation = existing.ReuseValidation;
                        }
                        else if (existing.ReferenceCount == 0)
                        {
                            this.reusableClusters.Remove(existing.ReuseKey);
                            eviction = existing;
                        }
                        else
                        {
                            waitTask = this.capacityChanged.Task;
                        }
                    }
                    else if (this.Fits(spec.Topology.NodeCount, this.MemoryRequired(spec)))
                    {
                        var ccmId = this.AllocateCcmId();
                        var instanceId = this.CreateInstanceId();
                        selected = new PooledCluster(spec.ReuseKey, spec, instanceId, ccmId)
                        {
                            ReferenceCount = 1,
                        };
                        this.Reserve(spec.Topology.NodeCount, this.MemoryRequired(spec));
                        this.reusableClusters.Add(spec.ReuseKey, selected);
                        mustProvision = true;
                    }
                    else
                    {
                        eviction = this.FindIdleEviction();
                        if (eviction != null)
                        {
                            this.reusableClusters.Remove(eviction.ReuseKey);
                        }
                        else
                        {
                            waitTask = this.capacityChanged.Task;
                        }
                    }
                }

                if (eviction != null)
                {
                    await this.DestroyPooledAsync(eviction);
                    continue;
                }

                if (selected != null)
                {
                    if (mustProvision)
                    {
                        _ = this.ProvisionPooledAsync(selected);
                    }

                    try
                    {
                        var cluster = await selected.Ready.WaitAsync(cancellationToken);
                        if (reuseValidation != null
                            && !await reuseValidation.WaitAsync(cancellationToken))
                        {
                            await this.AbandonReusableAsync(selected, true);
                            continue;
                        }

                        return new ReusableClusterLease(
                            this,
                            selected,
                            cluster,
                            this.CreateResourceScope(cluster));
                    }
                    catch (OperationCanceledException)
                    {
                        await this.AbandonReusableAsync(selected, false);
                        throw;
                    }
                }

                await waitTask.WaitAsync(cancellationToken);
            }
        }

        internal async Task<PrivateClusterLease> ProvisionPrivateAsync(
            ClusterSpec spec,
            CancellationToken cancellationToken)
        {
            spec = spec.Snapshot();
            spec.Validate();
            this.ValidateDemand(spec);
            await this.ReservePrivateCapacityAsync(spec, cancellationToken);
            int ccmId;
            string instanceId;
            try
            {
                lock (this.sync)
                {
                    ccmId = this.AllocateCcmId();
                    instanceId = this.CreateInstanceId();
                }
            }
            catch
            {
                lock (this.sync)
                {
                    this.ReleaseCapacity(spec.Topology.NodeCount, this.MemoryRequired(spec));
                }

                throw;
            }

            try
            {
                var cluster = await this.provisioner.ProvisionAsync(spec, instanceId, ccmId, cancellationToken);
                lock (this.sync)
                {
                    this.privateClusters.Add(cluster);
                }

                return new PrivateClusterLease(this, cluster, this.CreateResourceScope(cluster));
            }
            catch (CcmClusterProvisioningException exception)
            {
                lock (this.sync)
                {
                    this.failedProvisioningRemovals.Add(exception.Cluster);
                }

                throw;
            }
            catch
            {
                lock (this.sync)
                {
                    this.ReleaseReservation(spec.Topology.NodeCount, this.MemoryRequired(spec), ccmId);
                }

                throw;
            }
        }

        internal async Task ReleaseReusableAsync(PooledCluster pooledCluster, TestResourceScope resources)
        {
            lock (this.sync)
            {
                pooledCluster.PendingReleases++;
            }

            var poisoned = false;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                await this.cleanupResources(resources, timeout.Token);
                var cluster = await pooledCluster.Ready;
                poisoned = !await this.provisioner.IsHealthyAsync(cluster, timeout.Token);
            }
            catch (Exception exception)
            {
                poisoned = true;
                TestContext.Error.WriteLine($"Reusable cluster cleanup failed: {exception}");
            }

            var destroy = false;
            lock (this.sync)
            {
                pooledCluster.Poisoned |= poisoned;
                pooledCluster.PendingReleases--;
                pooledCluster.ReferenceCount--;
                pooledCluster.LastReleased = DateTimeOffset.UtcNow;
                if (pooledCluster.ReferenceCount < 0)
                {
                    throw new InvalidOperationException("A reusable cluster lease was released more than once.");
                }

                if (pooledCluster.ReferenceCount == 0)
                {
                    pooledCluster.ReuseValidation = null;
                    if (pooledCluster.Poisoned)
                    {
                        this.reusableClusters.Remove(pooledCluster.ReuseKey);
                        destroy = true;
                    }
                }

                this.PulseCapacityChanged();
            }

            if (destroy)
            {
                await this.DestroyPooledAsync(pooledCluster);
            }
        }

        internal async Task ReleasePrivateAsync(PhysicalTestCluster cluster)
        {
            await this.provisioner.RemoveAsync(cluster, CancellationToken.None);
            lock (this.sync)
            {
                if (this.privateClusters.Remove(cluster))
                {
                    this.ReleaseReservation(
                        cluster.Nodes.Count,
                        cluster.Nodes.Count * (long)cluster.Spec.Resources.MemoryMiB,
                        cluster.CcmId);
                }
            }
        }

        internal async Task ReserveAdditionalPrivateNodeAsync(
            PhysicalTestCluster cluster,
            CancellationToken cancellationToken)
        {
            var resultingNodeCount = cluster.Nodes.Count + 1;
            var resultingMemoryMiB = resultingNodeCount * (long)cluster.Spec.Resources.MemoryMiB;
            if (resultingNodeCount > this.maximumNodes || resultingMemoryMiB > this.capacity.UsableMemoryMiB)
            {
                throw new InvalidOperationException(
                    $"Adding a node would make the private cluster require {resultingNodeCount} nodes and "
                    + $"{resultingMemoryMiB} MiB, beyond this run's capacity.");
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PooledCluster? eviction;
                lock (this.sync)
                {
                    this.ThrowIfDisposed();
                    if (this.Fits(1, cluster.Spec.Resources.MemoryMiB))
                    {
                        this.Reserve(1, cluster.Spec.Resources.MemoryMiB);
                        return;
                    }

                    eviction = this.FindIdleEviction();
                    if (eviction == null)
                    {
                        throw new InvalidOperationException(
                            "Adding a node requires capacity that is held by active cluster leases; "
                            + "the operation cannot wait without risking a capacity deadlock.");
                    }

                    this.reusableClusters.Remove(eviction.ReuseKey);
                }

                await this.DestroyPooledAsync(eviction);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        internal void ReleaseAdditionalPrivateNode(ClusterSpec spec)
        {
            lock (this.sync)
            {
                this.usedNodes--;
                this.usedMemoryMiB -= spec.Resources.MemoryMiB;
                this.PulseCapacityChanged();
            }
        }

        internal async Task DisposeAsync()
        {
            PooledCluster[] reusable;
            PhysicalTestCluster[] privateClusterSnapshot;
            lock (this.sync)
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
                reusable = this.reusableClusters.Values
                    .Concat(this.failedPooledRemovals)
                    .Distinct()
                    .ToArray();
                privateClusterSnapshot = this.privateClusters.ToArray();
                this.reusableClusters.Clear();
                this.PulseCapacityChanged();
            }

            this.lifetimeCancellation.Cancel();

            var errors = new List<Exception>();
            foreach (var pooledCluster in reusable)
            {
                try
                {
                    await this.DestroyPooledAsync(pooledCluster);
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            foreach (var cluster in privateClusterSnapshot)
            {
                try
                {
                    await this.provisioner.RemoveAsync(cluster, CancellationToken.None);
                    lock (this.sync)
                    {
                        if (this.privateClusters.Remove(cluster))
                        {
                            this.ReleaseReservation(
                                cluster.Nodes.Count,
                                cluster.Nodes.Count * (long)cluster.Spec.Resources.MemoryMiB,
                                cluster.CcmId);
                        }
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            PhysicalTestCluster[] failedProvisioningSnapshot;
            lock (this.sync)
            {
                failedProvisioningSnapshot = this.failedProvisioningRemovals.ToArray();
            }

            foreach (var cluster in failedProvisioningSnapshot)
            {
                try
                {
                    await this.provisioner.RemoveAsync(cluster, CancellationToken.None);
                    lock (this.sync)
                    {
                        if (this.failedProvisioningRemovals.Remove(cluster))
                        {
                            this.ReleaseReservation(
                                cluster.Nodes.Count,
                                cluster.Nodes.Count * (long)cluster.Spec.Resources.MemoryMiB,
                                cluster.CcmId);
                        }
                    }
                }
                catch (Exception exception)
                {
                    errors.Add(exception);
                }
            }

            this.lifetimeCancellation.Dispose();
            if (errors.Count > 0)
            {
                throw new AggregateException("One or more CCM clusters could not be removed.", errors);
            }
        }

        private static TaskCompletionSource<bool> NewSignal()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private static int ParsePositiveInteger(string variable, int defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (value == null)
            {
                return defaultValue;
            }

            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1)
            {
                throw new InvalidOperationException($"{variable} must be a positive integer.");
            }

            return parsed;
        }

        private async Task ProvisionPooledAsync(PooledCluster pooledCluster)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(this.lifetimeCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            try
            {
                var cluster = await this.provisioner.ProvisionAsync(
                    pooledCluster.Spec,
                    pooledCluster.InstanceId,
                    pooledCluster.CcmId,
                    timeout.Token);
                pooledCluster.ReadySource.TrySetResult(cluster);
                lock (this.sync)
                {
                    this.PulseCapacityChanged();
                }
            }
            catch (Exception exception)
            {
                lock (this.sync)
                {
                    this.reusableClusters.Remove(pooledCluster.ReuseKey);
                    if (exception is CcmClusterProvisioningException provisioningException)
                    {
                        this.failedProvisioningRemovals.Add(provisioningException.Cluster);
                    }
                    else
                    {
                        this.ReleasePooledReservation(pooledCluster);
                    }
                }

                pooledCluster.ReadySource.TrySetException(exception);
            }
        }

        private async Task<bool> ValidateReusableAsync(PooledCluster pooledCluster)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(this.lifetimeCancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            return await this.provisioner.IsHealthyAsync(await pooledCluster.Ready, timeout.Token);
        }

        private async Task AbandonReusableAsync(PooledCluster pooledCluster, bool poison)
        {
            var destroy = false;
            lock (this.sync)
            {
                pooledCluster.Poisoned |= poison;
                pooledCluster.ReferenceCount--;
                if (pooledCluster.ReferenceCount == 0)
                {
                    pooledCluster.ReuseValidation = null;
                    if (pooledCluster.Poisoned)
                    {
                        this.reusableClusters.Remove(pooledCluster.ReuseKey);
                        destroy = true;
                    }
                }

                this.PulseCapacityChanged();
            }

            if (destroy)
            {
                await this.DestroyPooledAsync(pooledCluster);
            }
        }

        private async Task ReservePrivateCapacityAsync(ClusterSpec spec, CancellationToken cancellationToken)
        {
            await this.ReserveCapacityAsync(
                spec.Topology.NodeCount,
                this.MemoryRequired(spec),
                cancellationToken);
        }

        private async Task ReserveCapacityAsync(int nodes, long memoryMiB, CancellationToken cancellationToken)
        {
            while (true)
            {
                PooledCluster? eviction = null;
                Task waitTask = Task.CompletedTask;
                lock (this.sync)
                {
                    this.ThrowIfDisposed();
                    if (this.Fits(nodes, memoryMiB))
                    {
                        this.Reserve(nodes, memoryMiB);
                        return;
                    }

                    eviction = this.FindIdleEviction();
                    if (eviction != null)
                    {
                        this.reusableClusters.Remove(eviction.ReuseKey);
                    }
                    else
                    {
                        waitTask = this.capacityChanged.Task;
                    }
                }

                if (eviction != null)
                {
                    await this.DestroyPooledAsync(eviction);
                }
                else
                {
                    await waitTask.WaitAsync(cancellationToken);
                }
            }
        }

        private async Task DestroyPooledAsync(PooledCluster pooledCluster)
        {
            PhysicalTestCluster cluster;
            try
            {
                cluster = await pooledCluster.Ready;
            }
            catch (CcmClusterProvisioningException)
            {
                return;
            }
            catch when (pooledCluster.Ready.IsFaulted || pooledCluster.Ready.IsCanceled)
            {
                lock (this.sync)
                {
                    this.ReleasePooledReservation(pooledCluster);
                }

                return;
            }

            try
            {
                await this.provisioner.RemoveAsync(cluster, CancellationToken.None);
            }
            catch
            {
                lock (this.sync)
                {
                    this.failedPooledRemovals.Add(pooledCluster);
                }

                throw;
            }

            lock (this.sync)
            {
                this.failedPooledRemovals.Remove(pooledCluster);
                this.ReleasePooledReservation(pooledCluster);
            }
        }

        private void ReleasePooledReservation(PooledCluster pooledCluster)
        {
            if (pooledCluster.ReservationReleased)
            {
                return;
            }

            pooledCluster.ReservationReleased = true;
            this.ReleaseReservation(
                pooledCluster.Spec.Topology.NodeCount,
                this.MemoryRequired(pooledCluster.Spec),
                pooledCluster.CcmId);
        }

        private TestResourceScope CreateResourceScope(PhysicalTestCluster cluster)
        {
            return new TestResourceScope(
                cluster,
                Path.GetFileName(this.provisioner.RunDirectory),
                Interlocked.Increment(ref this.leaseCounter));
        }

        private long MemoryRequired(ClusterSpec spec)
        {
            return spec.Topology.NodeCount * (long)spec.Resources.MemoryMiB;
        }

        private bool Fits(int nodes, long memoryMiB)
        {
            return this.usedNodes + nodes <= this.maximumNodes
                && this.usedMemoryMiB + memoryMiB <= this.capacity.UsableMemoryMiB;
        }

        private void ValidateDemand(ClusterSpec spec)
        {
            var nodes = spec.Topology.NodeCount;
            var memory = nodes * (long)spec.Resources.MemoryMiB;
            if (nodes > this.maximumNodes || memory > this.capacity.UsableMemoryMiB)
            {
                throw new InvalidOperationException(
                    $"Cluster needs {nodes} nodes and {memory} MiB, but this run permits "
                    + $"{this.maximumNodes} nodes and {this.capacity.UsableMemoryMiB} MiB after reserving "
                    + $"{this.capacity.ReservedMemoryMiB} MiB for the system.");
            }
        }

        private PooledCluster? FindIdleEviction()
        {
            return this.reusableClusters.Values
                .Where(cluster => cluster.ReferenceCount == 0 && cluster.Ready.IsCompleted)
                .OrderBy(cluster => cluster.LastReleased)
                .FirstOrDefault();
        }

        private void Reserve(int nodes, long memoryMiB)
        {
            this.usedNodes += nodes;
            this.usedMemoryMiB += memoryMiB;
        }

        private void ReleaseReservation(int nodes, long memoryMiB, int ccmId)
        {
            this.ReleaseCapacity(nodes, memoryMiB);
            this.allocatedCcmIds[ccmId] = false;
            if (this.ccmIdLocks.Remove(ccmId, out var idLock))
            {
                if (OperatingSystem.IsLinux())
                {
                    idLock.Unlock(0, 1);
                }

                idLock.Dispose();
            }
        }

        private void ReleaseCapacity(int nodes, long memoryMiB)
        {
            this.usedNodes -= nodes;
            this.usedMemoryMiB -= memoryMiB;
            this.PulseCapacityChanged();
        }

        private int AllocateCcmId()
        {
            var lockDirectory = Path.Combine(Path.GetTempPath(), "alternator-client-csharp-ccm-ids");
            Directory.CreateDirectory(lockDirectory);
            for (var id = 1; id < this.allocatedCcmIds.Length; id++)
            {
                if (this.allocatedCcmIds[id] || IsCcmAddressRangeInUse(id))
                {
                    continue;
                }

                if (OperatingSystem.IsLinux())
                {
                    var idLock = new FileStream(
                        Path.Combine(lockDirectory, $"{id}.lock"),
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.ReadWrite);
                    try
                    {
                        idLock.Lock(0, 1);
                    }
                    catch (IOException)
                    {
                        idLock.Dispose();
                        continue;
                    }

                    this.ccmIdLocks.Add(id, idLock);
                }

                this.allocatedCcmIds[id] = true;
                return id;
            }

            throw new InvalidOperationException("No CCM cluster IDs are available.");
        }

        private static bool IsCcmAddressRangeInUse(int id)
        {
            for (var node = 1; node <= ClusterCapacity.MaximumNodeCount; node++)
            {
                foreach (var port in new[]
                {
                    CcmProvisioner.StoragePort,
                    9042,
                    CcmProvisioner.HttpPort,
                    CcmProvisioner.HttpsPort,
                })
                {
                    using var client = new TcpClient();
                    try
                    {
                        var connection = client.ConnectAsync($"127.0.{id}.{node}", port);
                        if (connection.Wait(TimeSpan.FromMilliseconds(20)) && client.Connected)
                        {
                            return true;
                        }
                    }
                    catch (AggregateException)
                    {
                    }
                }
            }

            return false;
        }

        private string CreateInstanceId()
        {
            return $"alternator-csharp-{Environment.ProcessId}-{++this.instanceCounter}";
        }

        private void PulseCapacityChanged()
        {
            var oldSignal = this.capacityChanged;
            this.capacityChanged = NewSignal();
            oldSignal.TrySetResult(true);
        }

        private void ThrowIfDisposed()
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(nameof(TestClusterPool));
            }
        }
    }

    internal static class AvailableMemoryDetector
    {
        internal static long GetAvailableMemoryMiB()
        {
            var configured = Environment.GetEnvironmentVariable("SCYLLA_CCM_AVAILABLE_MEMORY_MB");
            if (configured != null)
            {
                return ParseConfiguredMemoryMiB(configured);
            }

            var candidates = new List<long>();
            var hostAvailable = ReadHostAvailableMemoryMiB();
            if (hostAvailable > 0)
            {
                candidates.Add(hostAvailable);
            }

            var cgroupAvailable = ReadCgroupAvailableMemoryMiB();
            if (cgroupAvailable.HasValue)
            {
                candidates.Add(cgroupAvailable.Value);
            }

            var runtimeAvailable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            if (runtimeAvailable > 0)
            {
                candidates.Add(runtimeAvailable);
            }

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    "Unable to detect available memory; set SCYLLA_CCM_AVAILABLE_MEMORY_MB explicitly.");
            }

            return candidates.Min();
        }

        internal static long ParseConfiguredMemoryMiB(string configured)
        {
            if (!long.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out var configuredMiB)
                || configuredMiB <= 0)
            {
                throw new InvalidOperationException(
                    "SCYLLA_CCM_AVAILABLE_MEMORY_MB must be a positive integer.");
            }

            return configuredMiB;
        }

        private static long ReadHostAvailableMemoryMiB()
        {
            const string memInfo = "/proc/meminfo";
            if (!File.Exists(memInfo))
            {
                return 0;
            }

            var line = File.ReadLines(memInfo)
                .FirstOrDefault(value => value.StartsWith("MemAvailable:", StringComparison.Ordinal));
            if (line == null)
            {
                return 0;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2
                && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var kibibytes)
                ? kibibytes / 1024
                : 0;
        }

        private static long? ReadCgroupAvailableMemoryMiB()
        {
            const string cgroup = "/proc/self/cgroup";
            const string mountInfo = "/proc/self/mountinfo";
            if (!File.Exists(cgroup) || !File.Exists(mountInfo))
            {
                return null;
            }

            var candidates = ResolveCgroupMemoryPaths(
                    File.ReadAllText(cgroup),
                    File.ReadAllText(mountInfo))
                .Select(paths => ReadCgroupAvailableMemoryMiB(paths.MaximumPath, paths.CurrentPath))
                .Where(availableMiB => availableMiB.HasValue)
                .Select(availableMiB => availableMiB!.Value)
                .ToArray();
            return candidates.Length == 0 ? null : candidates.Min();
        }

        internal static IReadOnlyList<(string MaximumPath, string CurrentPath)> ResolveCgroupMemoryPaths(
            string cgroupText,
            string mountInfoText)
        {
            var memberships = cgroupText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => line.Split(':', 3))
                .Where(parts => parts.Length == 3)
                .ToArray();
            var v2Membership = memberships.FirstOrDefault(parts => parts[0] == "0" && parts[1].Length == 0)?[2];
            var v1Membership = memberships.FirstOrDefault(parts => parts[1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Contains("memory", StringComparer.Ordinal))?[2];
            var paths = new List<(string MaximumPath, string CurrentPath)>();

            foreach (var line in mountInfoText.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var separator = Array.IndexOf(fields, "-");
                if (separator < 6 || separator + 3 >= fields.Length)
                {
                    continue;
                }

                var fileSystem = fields[separator + 1];
                var superOptions = fields[separator + 3]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries);
                string? membership = null;
                string maximumFile;
                string currentFile;
                if (fileSystem == "cgroup2" && v2Membership != null)
                {
                    membership = v2Membership;
                    maximumFile = "memory.max";
                    currentFile = "memory.current";
                }
                else if (fileSystem == "cgroup"
                    && v1Membership != null
                    && superOptions.Contains("memory", StringComparer.Ordinal))
                {
                    membership = v1Membership;
                    maximumFile = "memory.limit_in_bytes";
                    currentFile = "memory.usage_in_bytes";
                }
                else
                {
                    continue;
                }

                var root = DecodeMountInfoPath(fields[3]);
                var mountPoint = DecodeMountInfoPath(fields[4]);
                var relativeMembership = GetRelativeCgroupMembership(root, membership);
                while (true)
                {
                    var directory = relativeMembership.Length == 0
                        ? mountPoint
                        : Path.Combine(mountPoint, relativeMembership);
                    paths.Add((
                        Path.Combine(directory, maximumFile),
                        Path.Combine(directory, currentFile)));
                    if (relativeMembership.Length == 0)
                    {
                        break;
                    }

                    relativeMembership = Path.GetDirectoryName(relativeMembership) ?? string.Empty;
                }
            }

            return paths.Distinct().ToArray();
        }

        private static string DecodeMountInfoPath(string path)
        {
            return path
                .Replace("\\040", " ", StringComparison.Ordinal)
                .Replace("\\011", "\t", StringComparison.Ordinal)
                .Replace("\\012", "\n", StringComparison.Ordinal)
                .Replace("\\134", "\\", StringComparison.Ordinal);
        }

        private static string GetRelativeCgroupMembership(string root, string membership)
        {
            root = root.TrimEnd('/');
            membership = membership.TrimEnd('/');
            if (root.Length == 0 || root == "/")
            {
                return membership.TrimStart('/');
            }

            if (membership == root)
            {
                return string.Empty;
            }

            var rootPrefix = root + "/";
            return membership.StartsWith(rootPrefix, StringComparison.Ordinal)
                ? membership[rootPrefix.Length..]
                : membership.TrimStart('/');
        }

        private static long? ReadCgroupAvailableMemoryMiB(string maximumPath, string currentPath)
        {
            if (!File.Exists(maximumPath) || !File.Exists(currentPath))
            {
                return null;
            }

            return CalculateCgroupAvailableMemoryMiB(
                File.ReadAllText(maximumPath).Trim(),
                File.ReadAllText(currentPath).Trim());
        }

        internal static long? CalculateCgroupAvailableMemoryMiB(string maximumText, string currentText)
        {
            if (maximumText == "max"
                || !long.TryParse(maximumText, NumberStyles.None, CultureInfo.InvariantCulture, out var maximum)
                || maximum >= 1L << 60)
            {
                return null;
            }

            return long.TryParse(
                currentText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var current)
                ? Math.Max(0, maximum - current) / (1024 * 1024)
                : null;
        }
    }
}
