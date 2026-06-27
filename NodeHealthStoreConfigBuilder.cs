// <copyright file="NodeHealthStoreConfigBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public sealed class NodeHealthStoreConfigBuilder
    {
        private int consecutiveServerErrorThreshold = AlternatorConfig.DefaultConsecutiveServerErrorThreshold;
        private int quarantineSuccessThreshold = AlternatorConfig.DefaultQuarantineSuccessThreshold;
        private long downNodeProbePeriodMs = AlternatorConfig.DefaultDownNodeProbePeriodMs;
        private int quarantineTrafficInterval = AlternatorConfig.DefaultQuarantineTrafficInterval;
        private long serverRequestTimeoutThresholdMs = AlternatorConfig.DefaultServerRequestTimeoutThresholdMs;
        private bool isDisabled;

        public NodeHealthStoreConfigBuilder WithConsecutiveServerErrorThreshold(int threshold)
        {
            this.consecutiveServerErrorThreshold = threshold;
            return this;
        }

        public NodeHealthStoreConfigBuilder WithQuarantineSuccessThreshold(int threshold)
        {
            this.quarantineSuccessThreshold = threshold;
            return this;
        }

        public NodeHealthStoreConfigBuilder WithDownNodeProbePeriodMs(long periodMs)
        {
            this.downNodeProbePeriodMs = periodMs;
            return this;
        }

        public NodeHealthStoreConfigBuilder WithQuarantineTrafficInterval(int interval)
        {
            this.quarantineTrafficInterval = interval;
            return this;
        }

        public NodeHealthStoreConfigBuilder WithServerRequestTimeoutThresholdMs(long thresholdMs)
        {
            this.serverRequestTimeoutThresholdMs = thresholdMs;
            return this;
        }

        public NodeHealthStoreConfigBuilder WithDisabled(bool disabled)
        {
            this.isDisabled = disabled;
            return this;
        }

        public NodeHealthStoreConfigBuilder Disabled()
        {
            return this.WithDisabled(true);
        }

        public NodeHealthStoreConfig Build()
        {
            return NodeHealthStoreConfig.Normalize(new NodeHealthStoreConfig
            {
                ConsecutiveServerErrorThreshold = this.consecutiveServerErrorThreshold,
                QuarantineSuccessThreshold = this.quarantineSuccessThreshold,
                DownNodeProbePeriodMs = this.downNodeProbePeriodMs,
                QuarantineTrafficInterval = this.quarantineTrafficInterval,
                ServerRequestTimeoutThresholdMs = this.serverRequestTimeoutThresholdMs,
                Disabled = this.isDisabled,
            });
        }

#pragma warning disable SA1300, IDE1006
        public NodeHealthStoreConfigBuilder withConsecutiveServerErrorThreshold(int threshold)
        {
            return this.WithConsecutiveServerErrorThreshold(threshold);
        }

        public NodeHealthStoreConfigBuilder withQuarantineSuccessThreshold(int threshold)
        {
            return this.WithQuarantineSuccessThreshold(threshold);
        }

        public NodeHealthStoreConfigBuilder withDownNodeProbePeriodMs(long periodMs)
        {
            return this.WithDownNodeProbePeriodMs(periodMs);
        }

        public NodeHealthStoreConfigBuilder withQuarantineTrafficInterval(int interval)
        {
            return this.WithQuarantineTrafficInterval(interval);
        }

        public NodeHealthStoreConfigBuilder withServerRequestTimeoutThresholdMs(long thresholdMs)
        {
            return this.WithServerRequestTimeoutThresholdMs(thresholdMs);
        }

        public NodeHealthStoreConfigBuilder withDisabled(bool disabled)
        {
            return this.WithDisabled(disabled);
        }

        public NodeHealthStoreConfigBuilder disabled()
        {
            return this.Disabled();
        }

        public NodeHealthStoreConfig build()
        {
            return this.Build();
        }
#pragma warning restore SA1300, IDE1006
    }
}
