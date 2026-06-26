// <copyright file="PartitionKeyResolver.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator.KeyRouting
{
    using System.Collections.Concurrent;
    using System.Net;
    using Amazon.DynamoDBv2;
    using Amazon.DynamoDBv2.Model;

    public sealed class PartitionKeyResolver : IDisposable
    {
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<string, string> cache = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, byte> discoveryInProgress = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, FailureRecord> failedTables = new ConcurrentDictionary<string, FailureRecord>(StringComparer.Ordinal);
        private readonly ConcurrentBag<Task> discoveryTasks = new ConcurrentBag<Task>();
        private readonly CancellationTokenSource shutdownSource = new CancellationTokenSource();
        private readonly Func<string, CancellationToken, Task<DescribeTableResponse>>? describeTableAsync;
        private IAmazonDynamoDB? clientForDiscovery;
        private bool disposed;

        public PartitionKeyResolver(IDictionary<string, string>? preConfigured)
        {
            if (preConfigured != null)
            {
                foreach (var item in preConfigured)
                {
                    this.cache[item.Key] = item.Value;
                }
            }
        }

        internal PartitionKeyResolver(
            IDictionary<string, string>? preConfigured,
            Func<string, CancellationToken, Task<DescribeTableResponse>> describeTableAsync)
            : this(preConfigured)
        {
            this.describeTableAsync = describeTableAsync ?? throw new ArgumentNullException(nameof(describeTableAsync));
        }

        public string? GetPartitionKeyName(string tableName)
        {
            return this.cache.TryGetValue(tableName, out var partitionKeyName) ? partitionKeyName : null;
        }

        public void SetClientForDiscovery(IAmazonDynamoDB client)
        {
            this.clientForDiscovery = client ?? throw new ArgumentNullException(nameof(client));
        }

        public void TriggerDiscovery(string tableName)
        {
            var describe = this.describeTableAsync;
            var client = this.clientForDiscovery;
            if (describe == null)
            {
                if (client == null)
                {
                    return;
                }

                describe = (name, cancellationToken) => client.DescribeTableAsync(new DescribeTableRequest { TableName = name }, cancellationToken);
            }

            this.TriggerDiscovery(tableName, describe);
        }

        public void TriggerDiscovery(string tableName, IAmazonDynamoDB client)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            this.TriggerDiscovery(tableName, (name, cancellationToken) => client.DescribeTableAsync(new DescribeTableRequest { TableName = name }, cancellationToken));
        }

        public void Register(string tableName, string pkAttributeName)
        {
            this.cache[tableName] = pkAttributeName;
        }

        public bool HasPartitionKeyInfo(string tableName)
        {
            return this.cache.ContainsKey(tableName);
        }

        public bool IsInFailureCooldown(string tableName)
        {
            return this.failedTables.TryGetValue(tableName, out var record) && !record.CanRetry();
        }

        public void ClearFailure(string tableName)
        {
            this.failedTables.TryRemove(tableName, out _);
        }

        public int GetFailedTableCount()
        {
            return this.failedTables.Count;
        }

        public void Shutdown()
        {
            if (this.disposed)
            {
                return;
            }

            this.disposed = true;
            this.shutdownSource.Cancel();
            try
            {
                Task.WaitAll(this.discoveryTasks.ToArray(), ShutdownTimeout);
            }
            catch (AggregateException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                this.shutdownSource.Dispose();
            }
        }

        public void Dispose()
        {
            this.Shutdown();
        }

#pragma warning disable SA1300, IDE1006
        public string? getPartitionKeyName(string tableName)
        {
            return this.GetPartitionKeyName(tableName);
        }

        public void setClientForDiscovery(IAmazonDynamoDB client)
        {
            this.SetClientForDiscovery(client);
        }

        public void triggerDiscovery(string tableName)
        {
            this.TriggerDiscovery(tableName);
        }

        public void triggerDiscovery(string tableName, IAmazonDynamoDB client)
        {
            this.TriggerDiscovery(tableName, client);
        }

        public void register(string tableName, string pkAttributeName)
        {
            this.Register(tableName, pkAttributeName);
        }

        public bool hasPartitionKeyInfo(string tableName)
        {
            return this.HasPartitionKeyInfo(tableName);
        }

        public bool isInFailureCooldown(string tableName)
        {
            return this.IsInFailureCooldown(tableName);
        }

        public void clearFailure(string tableName)
        {
            this.ClearFailure(tableName);
        }

        public int getFailedTableCount()
        {
            return this.GetFailedTableCount();
        }

        public void shutdownResolver()
        {
            this.Shutdown();
        }

        public void shutdown()
        {
            this.Shutdown();
        }

        public void close()
        {
            this.Dispose();
        }
#pragma warning restore SA1300, IDE1006

        private static bool IsPermanentFailure(AmazonDynamoDBException exception)
        {
            if (exception.StatusCode == HttpStatusCode.Forbidden)
            {
                return true;
            }

            if (exception.ErrorCode == "AccessDeniedException" || exception.ErrorCode == "ValidationException")
            {
                return true;
            }

            return exception.StatusCode >= HttpStatusCode.BadRequest
                && exception.StatusCode < HttpStatusCode.InternalServerError
                && exception.StatusCode != (HttpStatusCode)429;
        }

        private static long CalculateJitteredDelay(long baseDelay)
        {
            var jitterRange = baseDelay * 0.2;
            var jitter = ((Random.Shared.NextDouble() * 2) - 1) * jitterRange;
            return Math.Max(1, (long)Math.Round(baseDelay + jitter));
        }

        private void TriggerDiscovery(
            string tableName,
            Func<string, CancellationToken, Task<DescribeTableResponse>> describe)
        {
            if (string.IsNullOrEmpty(tableName) || this.cache.ContainsKey(tableName) || this.disposed)
            {
                return;
            }

            if (this.failedTables.TryGetValue(tableName, out var failureRecord) && !failureRecord.CanRetry())
            {
                return;
            }

            if (!this.discoveryInProgress.TryAdd(tableName, 0))
            {
                return;
            }

            if (this.cache.ContainsKey(tableName))
            {
                this.discoveryInProgress.TryRemove(tableName, out _);
                return;
            }

            this.failedTables.TryRemove(tableName, out _);
            this.discoveryTasks.Add(Task.Run(() => this.DiscoverWithRetryAsync(tableName, describe, this.shutdownSource.Token)));
        }

        private async Task DiscoverWithRetryAsync(
            string tableName,
            Func<string, CancellationToken, Task<DescribeTableResponse>> describe,
            CancellationToken cancellationToken)
        {
            var attempt = 0;
            var delay = AlternatorConfig.RecommendedPartitionKeyDiscoveryInitialDelayMs;

            try
            {
                while (attempt <= AlternatorConfig.RecommendedPartitionKeyDiscoveryMaxRetries)
                {
                    try
                    {
                        var response = await describe(tableName, cancellationToken).ConfigureAwait(false);
                        var partitionKey = response.Table?.KeySchema?.FirstOrDefault(schema => schema.KeyType == KeyType.HASH);
                        if (partitionKey?.AttributeName != null)
                        {
                            this.cache[tableName] = partitionKey.AttributeName;
                            return;
                        }

                        this.failedTables[tableName] = new FailureRecord(permanent: true);
                        return;
                    }
                    catch (ResourceNotFoundException)
                    {
                        this.failedTables[tableName] = new FailureRecord(permanent: true);
                        return;
                    }
                    catch (AmazonDynamoDBException e) when (IsPermanentFailure(e))
                    {
                        this.failedTables[tableName] = new FailureRecord(permanent: true);
                        return;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception)
                    {
                        attempt++;
                        if (attempt > AlternatorConfig.RecommendedPartitionKeyDiscoveryMaxRetries)
                        {
                            this.failedTables[tableName] = new FailureRecord(permanent: false);
                            return;
                        }

                        var jitteredDelay = CalculateJitteredDelay(delay);
                        await Task.Delay(TimeSpan.FromMilliseconds(jitteredDelay), cancellationToken).ConfigureAwait(false);
                        delay = Math.Min(delay * 2, AlternatorConfig.RecommendedPartitionKeyDiscoveryMaxDelayMs);
                    }
                }
            }
            finally
            {
                this.discoveryInProgress.TryRemove(tableName, out _);
            }
        }

        private sealed class FailureRecord
        {
            private readonly DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            private readonly bool permanent;

            internal FailureRecord(bool permanent)
            {
                this.permanent = permanent;
            }

            internal bool CanRetry()
            {
                return !this.permanent
                    || DateTimeOffset.UtcNow - this.timestamp > TimeSpan.FromMilliseconds(AlternatorConfig.RecommendedPartitionKeyDiscoveryCooldownMs);
            }
        }
    }
}
