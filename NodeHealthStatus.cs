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
