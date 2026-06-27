// <copyright file="NodeHealthStoreConfig.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public sealed class NodeHealthStoreConfig
    {
        public int ConsecutiveServerErrorThreshold { get; init; } =
            AlternatorConfig.DefaultConsecutiveServerErrorThreshold;

        public int QuarantineSuccessThreshold { get; init; } =
            AlternatorConfig.DefaultQuarantineSuccessThreshold;

        public long DownNodeProbePeriodMs { get; init; } =
            AlternatorConfig.DefaultDownNodeProbePeriodMs;

        public int QuarantinedNodeSamplingInterval { get; init; } =
            AlternatorConfig.DefaultQuarantinedNodeSamplingInterval;

        public long ServerRequestTimeoutThresholdMs { get; init; } =
            AlternatorConfig.DefaultServerRequestTimeoutThresholdMs;

        public bool Disabled { get; init; }

        public static NodeHealthStoreConfigBuilder Builder()
        {
            return new NodeHealthStoreConfigBuilder();
        }

#pragma warning disable SA1300, IDE1006
        public static NodeHealthStoreConfigBuilder builder()
        {
            return Builder();
        }
#pragma warning restore SA1300, IDE1006

        internal static NodeHealthStoreConfig Normalize(NodeHealthStoreConfig? config)
        {
            config ??= new NodeHealthStoreConfig();
            return new NodeHealthStoreConfig
            {
                ConsecutiveServerErrorThreshold = Math.Max(1, config.ConsecutiveServerErrorThreshold),
                QuarantineSuccessThreshold = Math.Max(1, config.QuarantineSuccessThreshold),
                DownNodeProbePeriodMs = Math.Max(1, config.DownNodeProbePeriodMs),
                QuarantinedNodeSamplingInterval = Math.Max(1, config.QuarantinedNodeSamplingInterval),
                ServerRequestTimeoutThresholdMs = config.ServerRequestTimeoutThresholdMs,
                Disabled = config.Disabled,
            };
        }
    }
}
