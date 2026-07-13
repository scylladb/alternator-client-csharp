// <copyright file="UnitTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using Amazon.DynamoDBv2;
    using Amazon.DynamoDBv2.Model;
    using Amazon.Runtime;

    [TestFixture]
    [Category("Unit")]
    public class HelperOptionsBuilderUnitTests
    {
        public HelperOptionsBuilderUnitTests()
        {
        }

        [Test]
        public void HelperOptionsBuilderBasicTest()
        {
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("https://127.0.0.1:8181"))
                .WithDatacenter("dc1")
                .WithRack("rack1")
                .Build();

            Assert.That(options, Is.Not.Null);
            Assert.That(options.InitialNodes, Is.EqualTo(new List<string> { "127.0.0.1" }));
            Assert.That(options.Schema, Is.EqualTo("https"));
            Assert.That(options.Port, Is.EqualTo(8181));
            Assert.That(options.Datacenter, Is.EqualTo("dc1"));
            Assert.That(options.Rack, Is.EqualTo("rack1"));
            Assert.That(options.ValidateOnInitialization, Is.True);
            Assert.That(options.StartImmediately, Is.True);
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderWithValidationDisabledTest()
        {
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("http://127.0.0.1:8080"))
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            Assert.That(options, Is.Not.Null);
            Assert.That(options.ValidateOnInitialization, Is.False);
            Assert.That(options.StartImmediately, Is.False);
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderThrowsWhenSeedUriNotSetTest()
        {
            var builder = HelperOptionsBuilder.Create()
                .WithDatacenter("dc1")
                .WithRack("rack1");

            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Test]
        [Category("Unit")]
        public void HelperConstructorWithOptionsTest()
        {
            var uri = new Uri("http://127.0.0.1:8080");
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(uri)
                .WithDatacenter("dc1")
                .WithRack("rack1")
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            var helper = new Helper(options);

            Assert.That(helper, Is.Not.Null);
        }

        [Test]
        [Category("Unit")]
        public void HelperValidationDoesNotProbeLocalNodesTest()
        {
            using var server = new OptionalRequestServer();
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri($"http://127.0.0.1:{server.Port}"))
                .WithDeferredStart()
                .Build();

            var helper = new Helper(options);
            Thread.Sleep(100);

            Assert.That(helper, Is.Not.Null);
            Assert.That(server.RequestCount, Is.EqualTo(0));
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderSupportsCustomHeaderOptimizerTest()
        {
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("http://127.0.0.1:8080"))
                .WithCustomOptimizeHeaders(config => config.RequiredHeaders.Concat(new[] { "X-Helper-Trace" }))
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            using var wrapper = AlternatorDynamoDBClient.builder()
                .WithOptions(options)
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.OptimizeHeaders, Is.True);
            Assert.That(wrapper.Config.HeadersWhitelist, Does.Contain("X-Helper-Trace"));
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderSupportsResponseCompressionConfigurationTest()
        {
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("http://127.0.0.1:8080"))
                .WithResponseCompression(ResponseCompressionAlgorithm.GZIP)
                .WithoutValidation()
                .WithDeferredStart()
                .Build();
            var disabledOptions = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("http://127.0.0.1:8081"))
                .WithoutResponseCompression()
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            using var wrapper = AlternatorDynamoDBClient.builder()
                .WithOptions(options)
                .buildWithAlternatorAPI();
            using var disabledWrapper = AlternatorDynamoDBClient.builder()
                .WithOptions(disabledOptions)
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.ResponseCompressionAlgorithms, Is.EqualTo(new[] { ResponseCompressionAlgorithm.Gzip }));
            Assert.That(disabledWrapper.Config.ResponseCompressionAlgorithms, Is.Empty);
        }

        [Test]
        [Category("Unit")]
        public void HelperOptionsBuilderSupportsConnectionTuningAliasesTest()
        {
            var options = HelperOptionsBuilder.Create()
                .WithInitialNodeUri(new Uri("http://127.0.0.1:8080"))
                .WithMaxIdleHttpConnections(71)
                .WithMaxIdleHttpConnectionsPerHost(72)
                .WithIdleHttpConnectionTimeoutMs(73000)
                .WithConnectionTimeToLiveMs(74000)
                .WithConnectionAcquisitionTimeoutMs(75000)
                .WithConnectionTimeoutMs(76000)
                .WithHttpClientTimeoutMs(77000)
                .WithoutValidation()
                .WithDeferredStart()
                .Build();

            using var wrapper = AlternatorDynamoDBClient.builder()
                .WithOptions(options)
                .buildWithAlternatorAPI();

            Assert.That(wrapper.Config.MaxConnections, Is.EqualTo(72));
            Assert.That(wrapper.Config.ConnectionMaxIdleTimeMs, Is.EqualTo(73000));
            Assert.That(wrapper.Config.ConnectionTimeToLiveMs, Is.EqualTo(74000));
            Assert.That(wrapper.Config.ConnectionAcquisitionTimeoutMs, Is.EqualTo(75000));
            Assert.That(wrapper.Config.ConnectionTimeoutMs, Is.EqualTo(76000));
            Assert.That(wrapper.Config.HttpClientTimeoutMs, Is.EqualTo(77000));
            Assert.That(wrapper.getClient().Config.Timeout, Is.EqualTo(TimeSpan.FromMilliseconds(77000)));
        }

        private sealed class OptionalRequestServer : IDisposable
        {
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            private readonly TcpListener listener;
            private readonly Task serverTask;
            private int requestCount;
            private bool disposed;

            internal OptionalRequestServer()
            {
                this.listener = new TcpListener(IPAddress.Loopback, 0);
                this.listener.Start();
                this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
                this.serverTask = Task.Run(this.RunAsync);
            }

            internal int Port { get; }

            internal int RequestCount => Volatile.Read(ref this.requestCount);

            public void Dispose()
            {
                if (this.disposed)
                {
                    return;
                }

                this.disposed = true;
                this.cancellation.Cancel();
                this.listener.Stop();
                try
                {
                    this.serverTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException exception) when (exception.InnerExceptions.All(IsExpectedShutdownException))
                {
                }

                this.cancellation.Dispose();
            }

            private static bool IsExpectedShutdownException(Exception exception)
            {
                return exception is OperationCanceledException || exception is ObjectDisposedException;
            }

            private static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
            {
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string? header;
                do
                {
                    header = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                }
                while (!string.IsNullOrEmpty(header));

                var body = Encoding.UTF8.GetBytes("[\"127.0.0.1\"]");
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n"
                    + "Content-Type: application/json\r\n"
                    + "Content-Length: "
                    + body.Length
                    + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            }

            private async Task RunAsync()
            {
                try
                {
                    while (!this.cancellation.IsCancellationRequested)
                    {
                        using var client = await this.listener.AcceptTcpClientAsync(this.cancellation.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref this.requestCount);
                        await HandleClientAsync(client, this.cancellation.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (this.cancellation.IsCancellationRequested && IsExpectedShutdownException(exception))
                {
                }
            }
        }
    }
}
