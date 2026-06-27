// <copyright file="NodeHealthStatus.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public sealed class NodeHealthStatus
    {
        public NodeHealthState State { get; internal set; } = NodeHealthState.Active;

        public int ConsecutiveServerErrors { get; internal set; }

        public int ConsecutiveSuccesses { get; internal set; }

        public DateTimeOffset UpdatedAt { get; internal set; } = DateTimeOffset.UtcNow;

        internal NodeHealthStatus Copy()
        {
            return new NodeHealthStatus
            {
                State = this.State,
                ConsecutiveServerErrors = this.ConsecutiveServerErrors,
                ConsecutiveSuccesses = this.ConsecutiveSuccesses,
                UpdatedAt = this.UpdatedAt,
            };
        }
    }
}
