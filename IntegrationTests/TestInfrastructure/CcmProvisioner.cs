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

#pragma warning disable SA1118, SA1202, SA1204, SA1402

namespace ScyllaDB.Alternator.TestInfrastructure
{
    using System.Diagnostics;
    using System.Net;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using Amazon.Runtime;

    internal class CcmProvisioner
    {
        internal const int HttpPort = 8080;
        internal const int HttpsPort = 8043;
        internal const int StoragePort = 7000;

        private const string GossipingPropertyFileSnitch = "org.apache.cassandra.locator.GossipingPropertyFileSnitch";
        private const string TestUser = "alternator_tests";
        private const string TestSaltedPassword = "$6$IcPWfCigHWVhHTf.$h3.30m5R2CnYqIeniCumbXCBxBxvtYPP3MbZVsjKcu268ESOcrUtSJwf1iO1s83KUT3waITRtTiexBdSWEI0Q/";
        private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(5);
        private readonly string ccmExecutable;
        private readonly string runDirectory;
        private readonly string diagnosticsDirectory;

        internal CcmProvisioner(string runDirectory)
        {
            this.runDirectory = runDirectory;
            this.ccmExecutable = Environment.GetEnvironmentVariable("SCYLLA_CCM_PATH") ?? "ccm";
            this.diagnosticsDirectory = Path.Combine(runDirectory, "diagnostics");
            Directory.CreateDirectory(Path.Combine(runDirectory, "clusters"));
            Directory.CreateDirectory(this.diagnosticsDirectory);
        }

        internal virtual string RunDirectory => this.runDirectory;

        internal virtual async Task<PhysicalTestCluster> ProvisionAsync(
            ClusterSpec spec,
            string instanceId,
            int ccmId,
            CancellationToken cancellationToken)
        {
            spec.Validate();
            var ccmDirectory = Path.Combine(this.runDirectory, "clusters", instanceId);
            Directory.CreateDirectory(ccmDirectory);
            var nodes = BuildNodes(spec.Topology, ccmId);
            string? caCertificatePath = null;
            BasicAWSCredentials? credentials = null;

            try
            {
                if (spec.Transports.HasFlag(AlternatorTransport.Https))
                {
                    caCertificatePath = this.CreateCertificateAuthority(instanceId, ccmDirectory);
                }

                if (spec.Security.EnforceAlternatorAuthorization)
                {
                    credentials = new BasicAWSCredentials(TestUser, TestSaltedPassword);
                }

                var firstRackCounts = string.Join(
                    ':',
                    spec.Topology.Datacenters.Select(datacenter => datacenter.Racks[0].NodeCount));
                var createArguments = new List<string>
                {
                    "create",
                    "--config-dir",
                    ccmDirectory,
                    instanceId,
                    "--scylla",
                    "--version",
                    spec.ScyllaVersion,
                    "--nodes",
                    firstRackCounts,
                    "--id",
                    ccmId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                };
                if (spec.Topology.Datacenters.Count > 1)
                {
                    createArguments.Add("--snitch");
                    createArguments.Add(GossipingPropertyFileSnitch);
                }

                await this.RunAsync(ccmDirectory, createArguments, cancellationToken);
                await this.AddAdditionalRackNodesAsync(spec, nodes, ccmDirectory, cancellationToken);
                await this.ConfigureAsync(spec, ccmDirectory, cancellationToken);
                if (caCertificatePath != null)
                {
                    foreach (var node in nodes)
                    {
                        await this.ConfigureNodeCertificateAsync(
                            node,
                            ccmDirectory,
                            caCertificatePath,
                            cancellationToken);
                    }
                }

                var cluster = new PhysicalTestCluster(
                    this,
                    instanceId,
                    ccmId,
                    ccmDirectory,
                    spec,
                    nodes,
                    caCertificatePath,
                    credentials);
                await this.StartAsync(cluster, cancellationToken);
                return cluster;
            }
            catch (Exception provisioningException)
            {
                await this.CollectDiagnosticsBestEffortAsync(instanceId, ccmDirectory, CancellationToken.None);
                try
                {
                    await this.RemoveByNameAsync(instanceId, ccmDirectory);
                }
                catch (Exception rollbackException)
                {
                    var cluster = new PhysicalTestCluster(
                        this,
                        instanceId,
                        ccmId,
                        ccmDirectory,
                        spec,
                        nodes,
                        caCertificatePath,
                        credentials);
                    throw new CcmClusterProvisioningException(
                        cluster,
                        provisioningException,
                        rollbackException);
                }

                throw;
            }
        }

        internal virtual async Task StartAsync(PhysicalTestCluster cluster, CancellationToken cancellationToken)
        {
            await this.RunAsync(
                cluster.CcmDirectory,
                new[]
                {
                    "start",
                    "--config-dir",
                    cluster.CcmDirectory,
                    "--wait-for-binary-proto",
                    "--wait-other-notice",
                    "--jvm_arg=--smp",
                    $"--jvm_arg={cluster.Spec.Resources.Smp}",
                    "--jvm_arg=--memory",
                    $"--jvm_arg={cluster.Spec.Resources.MemoryMiB}M",
                },
                cancellationToken);
            await this.WaitForAlternatorAsync(cluster, cluster.Nodes, cancellationToken);
        }

        internal Task StopAsync(PhysicalTestCluster cluster, CancellationToken cancellationToken)
        {
            return this.RunAsync(
                cluster.CcmDirectory,
                new[] { "stop", "--config-dir", cluster.CcmDirectory },
                cancellationToken);
        }

        internal virtual async Task StartNodeAsync(
            PhysicalTestCluster cluster,
            TestClusterNode node,
            CancellationToken cancellationToken)
        {
            await this.RunAsync(
                cluster.CcmDirectory,
                new[]
                {
                    node.Name,
                    "start",
                    "--config-dir",
                    cluster.CcmDirectory,
                    "--wait-for-binary-proto",
                    "--wait-other-notice",
                    "--jvm_arg=--smp",
                    $"--jvm_arg={cluster.Spec.Resources.Smp}",
                    "--jvm_arg=--memory",
                    $"--jvm_arg={cluster.Spec.Resources.MemoryMiB}M",
                },
                cancellationToken);
            await this.WaitForAlternatorAsync(cluster, new[] { node }, cancellationToken);
        }

        internal Task StopNodeAsync(
            PhysicalTestCluster cluster,
            TestClusterNode node,
            CancellationToken cancellationToken)
        {
            return this.RunAsync(
                cluster.CcmDirectory,
                new[] { node.Name, "stop", "--config-dir", cluster.CcmDirectory },
                cancellationToken);
        }

        internal async Task<TestClusterNode> AddNodeAsync(
            PhysicalTestCluster cluster,
            string datacenter,
            string rack,
            CancellationToken cancellationToken)
        {
            if (cluster.Nodes.Count >= ClusterCapacity.MaximumNodeCount)
            {
                throw new InvalidOperationException($"A cluster cannot exceed {ClusterCapacity.MaximumNodeCount} nodes.");
            }

            var index = Enumerable.Range(1, ClusterCapacity.MaximumNodeCount)
                .First(candidate => cluster.Nodes.All(node => node.Name != $"node{candidate}"));
            var node = new TestClusterNode(
                $"node{index}",
                $"127.0.{cluster.CcmId}.{index}",
                datacenter,
                rack);
            var addAttempted = false;
            try
            {
                addAttempted = true;
                await this.RunAsync(
                    cluster.CcmDirectory,
                    new[]
                    {
                        "add",
                        "--config-dir",
                        cluster.CcmDirectory,
                        node.Name,
                        "--scylla",
                        "--auto-bootstrap",
                        "--itf",
                        node.Address,
                        "--data-center",
                        datacenter,
                        "--rack",
                        rack,
                    },
                    cancellationToken);
                if (cluster.CaCertificatePath != null)
                {
                    await this.ConfigureNodeCertificateAsync(
                        node,
                        cluster.CcmDirectory,
                        cluster.CaCertificatePath,
                        cancellationToken);
                }

                await this.StartNodeAsync(cluster, node, cancellationToken);
                return node;
            }
            catch (Exception provisioningException) when (addAttempted)
            {
                try
                {
                    await this.RunAsync(
                        cluster.CcmDirectory,
                        new[] { node.Name, "remove", "--config-dir", cluster.CcmDirectory },
                        CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    throw new CcmNodeProvisioningException(
                        node,
                        true,
                        provisioningException,
                        rollbackException);
                }

                throw new CcmNodeProvisioningException(node, false, provisioningException);
            }
        }

        internal Task DecommissionNodeAsync(
            PhysicalTestCluster cluster,
            TestClusterNode node,
            CancellationToken cancellationToken)
        {
            return this.RunAsync(
                cluster.CcmDirectory,
                new[] { node.Name, "decommission", "--config-dir", cluster.CcmDirectory },
                cancellationToken);
        }

        internal Task DeleteNodeStateAsync(
            PhysicalTestCluster cluster,
            TestClusterNode node,
            CancellationToken cancellationToken)
        {
            return this.RunAsync(
                cluster.CcmDirectory,
                new[] { node.Name, "remove", "--config-dir", cluster.CcmDirectory },
                cancellationToken);
        }

        internal virtual async Task<bool> IsHealthyAsync(
            PhysicalTestCluster cluster,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.WaitForAlternatorAsync(cluster, cluster.Nodes, cancellationToken, TimeSpan.FromSeconds(10));
                return true;
            }
            catch (Exception exception) when (exception is HttpRequestException || exception is TimeoutException || exception is TaskCanceledException)
            {
                return false;
            }
        }

        internal virtual async Task RemoveAsync(PhysicalTestCluster cluster, CancellationToken cancellationToken)
        {
            await this.CollectDiagnosticsBestEffortAsync(
                cluster.InstanceId,
                cluster.CcmDirectory,
                cancellationToken);
            await this.RunAsync(
                cluster.CcmDirectory,
                new[] { "remove", "--config-dir", cluster.CcmDirectory, cluster.InstanceId },
                cancellationToken);
            await this.CollectDiagnosticsBestEffortAsync(
                cluster.InstanceId,
                cluster.CcmDirectory,
                cancellationToken);
        }

        private static List<TestClusterNode> BuildNodes(ClusterTopology topology, int ccmId)
        {
            var nodes = new List<TestClusterNode>();
            var index = 0;
            for (var dcIndex = 0; dcIndex < topology.Datacenters.Count; dcIndex++)
            {
                for (var nodeIndex = 0; nodeIndex < topology.Datacenters[dcIndex].Racks[0].NodeCount; nodeIndex++)
                {
                    index++;
                    nodes.Add(CreateNode(index, ccmId, dcIndex, 0));
                }
            }

            for (var dcIndex = 0; dcIndex < topology.Datacenters.Count; dcIndex++)
            {
                for (var rackIndex = 1; rackIndex < topology.Datacenters[dcIndex].Racks.Count; rackIndex++)
                {
                    for (var nodeIndex = 0; nodeIndex < topology.Datacenters[dcIndex].Racks[rackIndex].NodeCount; nodeIndex++)
                    {
                        index++;
                        nodes.Add(CreateNode(index, ccmId, dcIndex, rackIndex));
                    }
                }
            }

            return nodes;
        }

        private static TestClusterNode CreateNode(int index, int ccmId, int datacenterIndex, int rackIndex)
        {
            return new TestClusterNode(
                $"node{index}",
                $"127.0.{ccmId}.{index}",
                $"dc{datacenterIndex + 1}",
                $"RAC{rackIndex + 1}");
        }

        private async Task AddAdditionalRackNodesAsync(
            ClusterSpec spec,
            IReadOnlyList<TestClusterNode> nodes,
            string ccmDirectory,
            CancellationToken cancellationToken)
        {
            var firstRackNodeCount = spec.Topology.Datacenters.Sum(dc => dc.Racks[0].NodeCount);
            foreach (var node in nodes.Skip(firstRackNodeCount))
            {
                await this.RunAsync(
                    ccmDirectory,
                    new[]
                    {
                        "add",
                        "--config-dir",
                        ccmDirectory,
                        node.Name,
                        "--scylla",
                        "--itf",
                        node.Address,
                        "--data-center",
                        node.Datacenter,
                        "--rack",
                        node.Rack,
                    },
                    cancellationToken);
            }
        }

        private async Task ConfigureAsync(
            ClusterSpec spec,
            string ccmDirectory,
            CancellationToken cancellationToken)
        {
            var options = new SortedDictionary<string, string>(StringComparer.Ordinal)
            {
                ["alternator_write_isolation"] = "only_rmw_uses_lwt",
                ["endpoint_snitch"] = GossipingPropertyFileSnitch,
                ["start_native_transport"] = "true",
            };
            if (spec.Transports.HasFlag(AlternatorTransport.Http))
            {
                options["alternator_port"] = HttpPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (spec.Transports.HasFlag(AlternatorTransport.Https))
            {
                options["alternator_https_port"] = HttpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (spec.Security.Authentication != AuthenticationMode.AllowAll)
            {
                options["authenticator"] = GetAuthenticator(spec.Security.Authentication);
                options["auth_superuser_name"] = TestUser;
                options["auth_superuser_salted_password"] = TestSaltedPassword;
            }

            if (spec.Security.Authorization != AuthorizationMode.AllowAll)
            {
                options["authorizer"] = GetAuthorizer(spec.Security.Authorization);
            }

            if (spec.Security.EnforceAlternatorAuthorization)
            {
                options["alternator_enforce_authorization"] = "true";
            }

            foreach (var option in spec.ScyllaYamlOverrides)
            {
                options.Add(option.Key, option.Value);
            }

            var arguments = new List<string> { "updateconf", "--config-dir", ccmDirectory };
            arguments.AddRange(options.Select(option => $"{option.Key}:{option.Value}"));
            await this.RunAsync(ccmDirectory, arguments, cancellationToken);
        }

        private async Task ConfigureNodeCertificateAsync(
            TestClusterNode node,
            string ccmDirectory,
            string caCertificatePath,
            CancellationToken cancellationToken)
        {
            var (certificatePath, keyPath) = this.CreateNodeCertificate(
                node,
                ccmDirectory,
                caCertificatePath);
            await this.RunAsync(
                ccmDirectory,
                new[]
                {
                    node.Name,
                    "updateconf",
                    "--config-dir",
                    ccmDirectory,
                    $"alternator_encryption_options.certificate:{certificatePath}",
                    $"alternator_encryption_options.keyfile:{keyPath}",
                },
                cancellationToken);
        }

        private static string GetAuthenticator(AuthenticationMode authentication)
        {
            return authentication switch
            {
                AuthenticationMode.Password => "org.apache.cassandra.auth.PasswordAuthenticator",
                AuthenticationMode.Transitional => "com.scylladb.auth.TransitionalAuthenticator",
                _ => throw new ArgumentOutOfRangeException(nameof(authentication)),
            };
        }

        private static string GetAuthorizer(AuthorizationMode authorization)
        {
            return authorization switch
            {
                AuthorizationMode.Cassandra => "org.apache.cassandra.auth.CassandraAuthorizer",
                AuthorizationMode.Transitional => "com.scylladb.auth.TransitionalAuthorizer",
                _ => throw new ArgumentOutOfRangeException(nameof(authorization)),
            };
        }

        private string CreateCertificateAuthority(string instanceId, string ccmDirectory)
        {
            var tlsDirectory = Path.Combine(ccmDirectory, "tls");
            Directory.CreateDirectory(tlsDirectory);
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest(
                $"CN={instanceId} test CA",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
                true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddYears(2));
            var certificatePath = Path.Combine(tlsDirectory, "ca.crt");
            File.WriteAllText(certificatePath, certificate.ExportCertificatePem(), Encoding.ASCII);
            File.WriteAllText(Path.Combine(tlsDirectory, "ca.key"), rsa.ExportPkcs8PrivateKeyPem(), Encoding.ASCII);
            return certificatePath;
        }

        private (string CertificatePath, string KeyPath) CreateNodeCertificate(
            TestClusterNode node,
            string ccmDirectory,
            string caCertificatePath)
        {
            var tlsDirectory = Path.Combine(ccmDirectory, "tls");
            var caKeyPath = Path.Combine(tlsDirectory, "ca.key");
            using var certificateAuthority = X509Certificate2.CreateFromPemFile(
                caCertificatePath,
                caKeyPath);
            using var rsa = RSA.Create(3072);
            var request = new CertificateRequest(
                $"CN={node.Name}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Parse(node.Address));
            request.CertificateExtensions.Add(san.Build());

            var serialNumber = RandomNumberGenerator.GetBytes(16);
            serialNumber[0] &= 0x7F;
            using var certificate = request.Create(
                certificateAuthority,
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddYears(1),
                serialNumber);
            var nodeTlsDirectory = Path.Combine(tlsDirectory, node.Name);
            Directory.CreateDirectory(nodeTlsDirectory);
            var certificatePath = Path.Combine(nodeTlsDirectory, "server.crt");
            var keyPath = Path.Combine(nodeTlsDirectory, "server.key");
            File.WriteAllText(certificatePath, certificate.ExportCertificatePem(), Encoding.ASCII);
            File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem(), Encoding.ASCII);
            return (certificatePath, keyPath);
        }

        private async Task WaitForAlternatorAsync(
            PhysicalTestCluster cluster,
            IEnumerable<TestClusterNode> nodes,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null)
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            var deadline = DateTimeOffset.UtcNow.Add(timeout ?? ReadinessTimeout);
            Exception? lastException = null;
            var endpoints = nodes.SelectMany(node => this.GetNodeEndpoints(cluster.Spec, node)).ToArray();
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var allReady = true;
                foreach (var endpoint in endpoints)
                {
                    try
                    {
                        using var response = await client.GetAsync(endpoint, cancellationToken);
                        if (!response.IsSuccessStatusCode)
                        {
                            allReady = false;
                            break;
                        }
                    }
                    catch (Exception exception) when (exception is HttpRequestException || exception is TaskCanceledException)
                    {
                        lastException = exception;
                        allReady = false;
                        break;
                    }
                }

                if (allReady)
                {
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }

            throw new TimeoutException(
                $"Alternator endpoints for cluster '{cluster.InstanceId}' did not become ready.",
                lastException);
        }

        private IEnumerable<Uri> GetNodeEndpoints(ClusterSpec spec, TestClusterNode node)
        {
            if (spec.Transports.HasFlag(AlternatorTransport.Http))
            {
                yield return new Uri($"http://{node.Address}:{HttpPort}/");
            }

            if (spec.Transports.HasFlag(AlternatorTransport.Https))
            {
                yield return new Uri($"https://{node.Address}:{HttpsPort}/");
            }
        }

        protected virtual async Task CollectDiagnosticsAsync(
            string instanceId,
            string ccmDirectory,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(ccmDirectory))
            {
                return;
            }

            var destination = Path.Combine(this.diagnosticsDirectory, instanceId);
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(ccmDirectory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativePath = Path.GetRelativePath(ccmDirectory, file);
                if (!ShouldCollect(relativePath))
                {
                    continue;
                }

                var destinationPath = Path.Combine(destination, relativePath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath)
                    ?? throw new InvalidOperationException("A diagnostic path has no parent directory.");
                Directory.CreateDirectory(destinationDirectory);
                File.Copy(file, destinationPath, true);
            }

            await Task.CompletedTask;
        }

        private async Task CollectDiagnosticsBestEffortAsync(
            string instanceId,
            string ccmDirectory,
            CancellationToken cancellationToken)
        {
            try
            {
                await this.CollectDiagnosticsAsync(instanceId, ccmDirectory, cancellationToken);
            }
            catch
            {
            }
        }

        private static bool ShouldCollect(string relativePath)
        {
            var path = relativePath.Replace(Path.DirectorySeparatorChar, '/');
            return path == "ccm-commands.log"
                || path.EndsWith("/cluster.conf", StringComparison.Ordinal)
                || path.EndsWith("/node.conf", StringComparison.Ordinal)
                || path.EndsWith("/conf/scylla.yaml", StringComparison.Ordinal)
                || path.Contains("/logs/", StringComparison.Ordinal);
        }

        private Task RemoveByNameAsync(string instanceId, string ccmDirectory)
        {
            return this.RunAsync(
                ccmDirectory,
                new[] { "remove", "--config-dir", ccmDirectory, instanceId },
                CancellationToken.None);
        }

        protected virtual async Task RunAsync(
            string ccmDirectory,
            IEnumerable<string> arguments,
            CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsLinux())
            {
                throw new PlatformNotSupportedException("The native scylla-ccm harness currently supports Linux only.");
            }

            var argumentList = arguments.ToArray();
            var startInfo = new ProcessStartInfo
            {
                FileName = this.ccmExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in argumentList)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            var command = this.ccmExecutable + " " + string.Join(' ', argumentList.Select(QuoteForLog));
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start CCM command: {command}");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var log = new StringBuilder()
                .AppendLine($"> {command}")
                .Append(stdout)
                .Append(stderr)
                .AppendLine($"[exit {process.ExitCode}]")
                .ToString();
            try
            {
                Directory.CreateDirectory(ccmDirectory);
                await File.AppendAllTextAsync(
                    Path.Combine(ccmDirectory, "ccm-commands.log"),
                    log,
                    CancellationToken.None);
                TestContext.Progress.Write(log);
            }
            catch (Exception exception)
            {
                TestContext.Error.WriteLine($"Failed to record CCM command output: {exception}");
            }

            if (process.ExitCode != 0)
            {
                throw new CcmCommandException(command, process.ExitCode, stdout, stderr);
            }
        }

        private static string QuoteForLog(string argument)
        {
            return argument.Any(char.IsWhiteSpace) ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument;
        }
    }

    internal sealed class CcmCommandException : Exception
    {
        internal CcmCommandException(string command, int exitCode, string stdout, string stderr)
            : base($"CCM command failed with exit code {exitCode}: {command}\n{stdout}\n{stderr}")
        {
        }
    }

    internal sealed class CcmClusterProvisioningException : Exception
    {
        internal CcmClusterProvisioningException(
            PhysicalTestCluster cluster,
            Exception provisioningException,
            Exception rollbackException)
            : base(
                $"Failed to provision '{cluster.InstanceId}' and CCM rollback failed.",
                new AggregateException(provisioningException, rollbackException))
        {
            this.Cluster = cluster;
        }

        internal PhysicalTestCluster Cluster { get; }
    }

    internal sealed class CcmNodeProvisioningException : Exception
    {
        internal CcmNodeProvisioningException(
            TestClusterNode node,
            bool nodeRemainsProvisioned,
            Exception provisioningException,
            Exception? rollbackException = null)
            : base(
                rollbackException == null
                    ? $"Failed to provision '{node.Name}'; CCM rollback succeeded."
                    : $"Failed to provision '{node.Name}' and CCM rollback failed.",
                rollbackException == null
                    ? provisioningException
                    : new AggregateException(provisioningException, rollbackException))
        {
            this.Node = node;
            this.NodeRemainsProvisioned = nodeRemainsProvisioned;
        }

        internal TestClusterNode Node { get; }

        internal bool NodeRemainsProvisioned { get; }
    }
}
