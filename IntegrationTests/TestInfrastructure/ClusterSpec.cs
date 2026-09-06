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

#pragma warning disable SA1204, SA1402, SA1649

namespace ScyllaDB.Alternator.TestInfrastructure
{
    using System.Collections.ObjectModel;
    using System.Text;

    [Flags]
    internal enum AlternatorTransport
    {
        Http = 1,
        Https = 2,
        Both = Http | Https,
    }

    internal enum AuthenticationMode
    {
        AllowAll,
        Password,
        Transitional,
    }

    internal enum AuthorizationMode
    {
        AllowAll,
        Cassandra,
        Transitional,
    }

    internal sealed class ClusterSecuritySpec
    {
        internal ClusterSecuritySpec(
            AuthenticationMode authentication,
            AuthorizationMode authorization,
            bool enforceAlternatorAuthorization)
        {
            this.Authentication = authentication;
            this.Authorization = authorization;
            this.EnforceAlternatorAuthorization = enforceAlternatorAuthorization;
        }

        internal static ClusterSecuritySpec Disabled { get; } = new ClusterSecuritySpec(
            AuthenticationMode.AllowAll,
            AuthorizationMode.AllowAll,
            false);

        internal static ClusterSecuritySpec Enforced { get; } = new ClusterSecuritySpec(
            AuthenticationMode.Password,
            AuthorizationMode.Cassandra,
            true);

        internal AuthenticationMode Authentication { get; }

        internal AuthorizationMode Authorization { get; }

        internal bool EnforceAlternatorAuthorization { get; }

        internal void Validate()
        {
            if (this.EnforceAlternatorAuthorization
                && (this.Authentication == AuthenticationMode.AllowAll
                    || this.Authorization == AuthorizationMode.AllowAll))
            {
                throw new ArgumentException(
                    "Alternator authorization enforcement requires authentication and authorization backends.");
            }
        }
    }

    internal sealed class NodeResources
    {
        internal NodeResources(int smp, int memoryMiB)
        {
            this.Smp = smp;
            this.MemoryMiB = memoryMiB;
        }

        internal static NodeResources Default { get; } = new NodeResources(2, 1024);

        internal int Smp { get; }

        internal int MemoryMiB { get; }
    }

    internal sealed class RackSpec
    {
        internal RackSpec(int nodeCount)
        {
            this.NodeCount = nodeCount;
        }

        internal int NodeCount { get; }
    }

    internal sealed class DatacenterSpec
    {
        internal DatacenterSpec(IReadOnlyList<RackSpec> racks)
        {
            this.Racks = racks;
        }

        internal IReadOnlyList<RackSpec> Racks { get; }

        internal static DatacenterSpec Create(params int[] nodesPerRack)
        {
            return new DatacenterSpec(nodesPerRack.Select(count => new RackSpec(count)).ToArray());
        }
    }

    internal sealed class ClusterTopology
    {
        internal ClusterTopology(IReadOnlyList<DatacenterSpec> datacenters)
        {
            this.Datacenters = datacenters;
        }

        internal IReadOnlyList<DatacenterSpec> Datacenters { get; }

        internal int NodeCount => this.Datacenters.Sum(dc => dc.Racks.Sum(rack => rack.NodeCount));

        internal static ClusterTopology SingleDatacenter(params int[] nodesPerRack)
        {
            return new ClusterTopology(new[] { DatacenterSpec.Create(nodesPerRack) });
        }
    }

    internal sealed class ClusterSpec
    {
        private static readonly HashSet<string> ReservedYamlKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "alternator_address",
            "alternator_encryption_options",
            "alternator_enforce_authorization",
            "alternator_https_port",
            "alternator_port",
            "alternator_write_isolation",
            "auth_superuser_name",
            "auth_superuser_salted_password",
            "authenticator",
            "authorizer",
            "broadcast_address",
            "broadcast_rpc_address",
            "cluster_name",
            "endpoint_snitch",
            "listen_address",
            "rpc_address",
            "seed_provider",
            "start_native_transport",
        };

        internal string ScyllaVersion { get; init; } = "release:2025.2";

        internal ClusterTopology Topology { get; init; } = ClusterTopology.SingleDatacenter(3);

        internal AlternatorTransport Transports { get; init; } = AlternatorTransport.Both;

        internal ClusterSecuritySpec Security { get; init; } = ClusterSecuritySpec.Disabled;

        internal NodeResources Resources { get; init; } = NodeResources.Default;

        internal IReadOnlyDictionary<string, string> ScyllaYamlOverrides { get; init; }
            = new Dictionary<string, string>(StringComparer.Ordinal);

        internal string ReuseKey
        {
            get
            {
                this.Validate();
                var key = new StringBuilder();
                key.Append(this.ScyllaVersion).Append('|')
                    .Append((int)this.Transports).Append('|')
                    .Append((int)this.Security.Authentication).Append('|')
                    .Append((int)this.Security.Authorization).Append('|')
                    .Append(this.Security.EnforceAlternatorAuthorization).Append('|')
                    .Append(this.Resources.Smp).Append('|')
                    .Append(this.Resources.MemoryMiB);
                foreach (var dc in this.Topology.Datacenters)
                {
                    key.Append("|dc");
                    foreach (var rack in dc.Racks)
                    {
                        key.Append(':').Append(rack.NodeCount);
                    }
                }

                foreach (var option in this.ScyllaYamlOverrides.OrderBy(option => option.Key, StringComparer.Ordinal))
                {
                    key.Append("|yaml:").Append(option.Key.Length).Append(':').Append(option.Key)
                        .Append('=').Append(option.Value.Length).Append(':').Append(option.Value);
                }

                return key.ToString();
            }
        }

        internal ClusterSpec WithScyllaVersion(string version)
        {
            return this.Copy(scyllaVersion: version);
        }

        internal ClusterSpec WithTopology(ClusterTopology topology)
        {
            return this.Copy(topology: topology);
        }

        internal ClusterSpec WithTransports(AlternatorTransport transports)
        {
            return this.Copy(transports: transports);
        }

        internal ClusterSpec WithSecurity(ClusterSecuritySpec security)
        {
            return this.Copy(security: security);
        }

        internal ClusterSpec WithResources(NodeResources resources)
        {
            return this.Copy(resources: resources);
        }

        internal ClusterSpec WithYamlOverride(string key, string yamlValue)
        {
            var overrides = new Dictionary<string, string>(this.ScyllaYamlOverrides, StringComparer.Ordinal)
            {
                [key] = yamlValue,
            };
            return this.Copy(overrides: overrides);
        }

        internal ClusterSpec Snapshot()
        {
            return this.Copy();
        }

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(this.ScyllaVersion))
            {
                throw new ArgumentException("A Scylla version is required.", nameof(this.ScyllaVersion));
            }

            if (this.Transports == 0 || (this.Transports & ~AlternatorTransport.Both) != 0)
            {
                throw new ArgumentException("At least one supported Alternator transport is required.", nameof(this.Transports));
            }

            if (this.Resources.Smp < 1 || this.Resources.MemoryMiB < 1)
            {
                throw new ArgumentException("Node SMP and memory must be positive.", nameof(this.Resources));
            }

            this.Security.Validate();

            if (this.Topology.Datacenters.Count == 0)
            {
                throw new ArgumentException("A cluster must contain at least one datacenter.", nameof(this.Topology));
            }

            foreach (var datacenter in this.Topology.Datacenters)
            {
                if (datacenter.Racks.Count == 0 || datacenter.Racks.Any(rack => rack.NodeCount < 1))
                {
                    throw new ArgumentException("Every datacenter and rack must contain at least one node.", nameof(this.Topology));
                }
            }

            if (this.Topology.NodeCount > ClusterCapacity.MaximumNodeCount)
            {
                throw new ArgumentException(
                    $"A cluster cannot exceed {ClusterCapacity.MaximumNodeCount} nodes.",
                    nameof(this.Topology));
            }

            foreach (var option in this.ScyllaYamlOverrides)
            {
                var rootKey = option.Key.Split('.', 2)[0];
                if (string.IsNullOrWhiteSpace(option.Key) || string.IsNullOrWhiteSpace(option.Value))
                {
                    throw new ArgumentException("Scylla YAML override keys and values cannot be empty.");
                }

                if (ReservedYamlKeys.Contains(rootKey))
                {
                    throw new ArgumentException($"Scylla YAML key '{rootKey}' is owned by a typed cluster option.");
                }
            }
        }

        private ClusterSpec Copy(
            string? scyllaVersion = null,
            ClusterTopology? topology = null,
            AlternatorTransport? transports = null,
            ClusterSecuritySpec? security = null,
            NodeResources? resources = null,
            IReadOnlyDictionary<string, string>? overrides = null)
        {
            var sourceTopology = topology ?? this.Topology;
            var topologyCopy = new ClusterTopology(sourceTopology.Datacenters
                .Select(dc => new DatacenterSpec(dc.Racks.Select(rack => new RackSpec(rack.NodeCount)).ToArray()))
                .ToArray());
            var overrideCopy = new ReadOnlyDictionary<string, string>(
                new SortedDictionary<string, string>(
                    (overrides ?? this.ScyllaYamlOverrides).ToDictionary(
                        option => option.Key,
                        option => option.Value,
                        StringComparer.Ordinal),
                    StringComparer.Ordinal));
            return new ClusterSpec
            {
                ScyllaVersion = scyllaVersion ?? this.ScyllaVersion,
                Topology = topologyCopy,
                Transports = transports ?? this.Transports,
                Security = security ?? new ClusterSecuritySpec(
                    this.Security.Authentication,
                    this.Security.Authorization,
                    this.Security.EnforceAlternatorAuthorization),
                Resources = resources ?? new NodeResources(this.Resources.Smp, this.Resources.MemoryMiB),
                ScyllaYamlOverrides = overrideCopy,
            };
        }
    }

    internal static class ClusterSpecs
    {
        internal static ClusterSpec Default => new ClusterSpec
        {
            ScyllaVersion = Environment.GetEnvironmentVariable("SCYLLA_VERSION") ?? "release:2025.2",
        };
    }

    internal sealed class ClusterCapacity
    {
        internal const int MaximumNodeCount = 9;

        internal ClusterCapacity(long availableMemoryMiB, long reservedMemoryMiB, long usableMemoryMiB)
        {
            this.AvailableMemoryMiB = availableMemoryMiB;
            this.ReservedMemoryMiB = reservedMemoryMiB;
            this.UsableMemoryMiB = usableMemoryMiB;
        }

        internal long AvailableMemoryMiB { get; }

        internal long ReservedMemoryMiB { get; }

        internal long UsableMemoryMiB { get; }

        internal static ClusterCapacity FromAvailableMemory(long availableMemoryMiB)
        {
            var reserved = Math.Clamp(availableMemoryMiB / 4, 512, 4096);
            return new ClusterCapacity(availableMemoryMiB, reserved, Math.Max(0, availableMemoryMiB - reserved));
        }
    }
}
