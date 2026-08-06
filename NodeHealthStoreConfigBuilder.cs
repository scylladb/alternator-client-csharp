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
    public sealed class NodeHealthStoreConfigBuilder
    {
        private int consecutiveServerErrorThreshold = AlternatorConfig.DefaultConsecutiveServerErrorThreshold;
        private int quarantineSuccessThreshold = AlternatorConfig.DefaultQuarantineSuccessThreshold;
        private long downNodeProbePeriodMs = AlternatorConfig.DefaultDownNodeProbePeriodMs;
        private int quarantinedNodeSamplingInterval = AlternatorConfig.DefaultQuarantinedNodeSamplingInterval;
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

        public NodeHealthStoreConfigBuilder WithQuarantinedNodeSamplingInterval(int interval)
        {
            this.quarantinedNodeSamplingInterval = interval;
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
                QuarantinedNodeSamplingInterval = this.quarantinedNodeSamplingInterval,
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

        public NodeHealthStoreConfigBuilder withQuarantinedNodeSamplingInterval(int interval)
        {
            return this.WithQuarantinedNodeSamplingInterval(interval);
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
