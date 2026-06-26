// <copyright file="RequestPipelineUnitTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.Net.Security;
    using System.Security.Cryptography;
    using System.Security.Cryptography.X509Certificates;
    using Amazon.DynamoDBv2.Model;
    using Amazon.Runtime;
    using Amazon.Runtime.Internal;

    [TestFixture]
    [Category("Unit")]
    public class RequestPipelineUnitTests
    {
        [Test]
        public void AlternatorHttpClientFactoryAppliesClientCertificateToHandlersTest()
        {
            var (directory, certificatePath, privateKeyPath) = CreateTemporaryClientCertificateFiles();
            try
            {
                var tlsConfig = TlsConfig.builder()
                    .withClientCertificate(certificatePath, privateKeyPath)
                    .build();

                using var httpClientHandler = CreateAlternatorHttpClientHandler(tlsConfig, 10, _ => { });
                Assert.That(httpClientHandler.ClientCertificateOptions, Is.EqualTo(ClientCertificateOption.Manual));
                Assert.That(httpClientHandler.ClientCertificates, Has.Count.EqualTo(1));
                var httpClientCertificate = (X509Certificate2)httpClientHandler.ClientCertificates[0];
                Assert.That(httpClientCertificate.HasPrivateKey, Is.True);

                var alternatorConfig = AlternatorConfig.builder()
                    .withTlsConfig(tlsConfig)
                    .build();
                using var socketsHttpHandler = CreateAlternatorSocketsHttpHandler(alternatorConfig, _ => { });
                Assert.That(socketsHttpHandler.SslOptions.ClientCertificates, Is.Not.Null);
                Assert.That(socketsHttpHandler.SslOptions.ClientCertificates, Has.Count.EqualTo(1));
                var socketsClientCertificate = (X509Certificate2)socketsHttpHandler.SslOptions.ClientCertificates![0];
                Assert.That(socketsClientCertificate.HasPrivateKey, Is.True);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void AlternatorHttpClientFactoryRejectsSystemTrustedCertificateWithHostnameMismatchTest()
        {
            using var certificate = LoadSystemTrustedCertificate();
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Assert.That(chain.Build(certificate), Is.True);
            var caPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(caPath, certificate.ExportCertificatePem());
                var tlsConfig = TlsConfig.builder()
                    .withCaCertPath(caPath)
                    .build();

                var accepted = InvokeValidateCertificate(
                    tlsConfig,
                    certificate,
                    chain,
                    SslPolicyErrors.RemoteCertificateNameMismatch);

                Assert.That(accepted, Is.False);
            }
            finally
            {
                File.Delete(caPath);
            }
        }

        [Test]
        public async Task HeadersFilteringHttpMessageHandlerFiltersHeadersTest()
        {
            var innerHandler = new CapturingHttpMessageHandler();
            using var handler = new HeadersFilteringHttpMessageHandler(
                innerHandler,
                new[] { "x-keep", "content-type" });
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1/");
            request.Headers.Add("X-Keep", "1");
            request.Headers.Add("X-Drop", "2");
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
            request.Content.Headers.Add("X-Drop-Content", "3");

            using var response = await client.SendAsync(request);

            Assert.That(response.IsSuccessStatusCode, Is.True);
            Assert.That(innerHandler.Request, Is.Not.Null);
            Assert.That(innerHandler.Request!.Headers.Contains("X-Keep"), Is.True);
            Assert.That(innerHandler.Request.Headers.Contains("X-Drop"), Is.False);
            Assert.That(innerHandler.Request.Content!.Headers.Contains("Content-Type"), Is.True);
            Assert.That(innerHandler.Request.Content.Headers.Contains("X-Drop-Content"), Is.False);

            using var emptyHandler = new HeadersFilteringHttpMessageHandler(new CapturingHttpMessageHandler(), null);
            using var emptyClient = new HttpClient(emptyHandler);
            using var emptyRequest = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1/");
            emptyRequest.Headers.Add("X-Drop", "1");

            using var emptyResponse = await emptyClient.SendAsync(emptyRequest);

            Assert.That(emptyResponse.IsSuccessStatusCode, Is.True);
            Assert.That(emptyRequest.Headers.Contains("X-Drop"), Is.False);
        }

        [Test]
        public async Task HeadersFilteringHttpMessageHandlerMatchesJavaWrapperSemanticsTest()
        {
            var innerHandler = new CapturingHttpMessageHandler();
            using var handler = new HeadersFilteringHttpMessageHandler(
                innerHandler,
                new[] { "Host", "Authorization", "Accept-Encoding" });
            using var client = new HttpClient(handler);
            using var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8080/path?query=value");
            request.Headers.Host = "127.0.0.1:8080";
            request.Headers.TryAddWithoutValidation("Authorization", "AWS4-HMAC-SHA256...");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", new[] { "gzip", "deflate" });
            request.Headers.TryAddWithoutValidation("User-Agent", "aws-sdk-dotnet/4.x");
            request.Headers.TryAddWithoutValidation("X-Amz-Sdk-Invocation-Id", "some-id");

            using var response = await client.SendAsync(request);

            Assert.That(response.IsSuccessStatusCode, Is.True);
            Assert.That(innerHandler.Request, Is.Not.Null);
            Assert.That(innerHandler.Request!.Method, Is.EqualTo(HttpMethod.Post));
            Assert.That(innerHandler.Request.RequestUri, Is.EqualTo(new Uri("http://127.0.0.1:8080/path?query=value")));
            Assert.That(innerHandler.Request.Headers.Host, Is.EqualTo("127.0.0.1:8080"));
            Assert.That(innerHandler.Request.Headers.Contains("Authorization"), Is.True);
            Assert.That(innerHandler.Request.Headers.GetValues("Accept-Encoding"), Is.EquivalentTo(new[] { "gzip", "deflate" }));
            Assert.That(innerHandler.Request.Headers.Contains("User-Agent"), Is.False);
            Assert.That(innerHandler.Request.Headers.Contains("X-Amz-Sdk-Invocation-Id"), Is.False);
        }

        [Test]
        public async Task HeadersFilteringHttpMessageHandlerPreservesRequiredHeadersAndDelegatesDisposeTest()
        {
            var config = AlternatorConfig.builder()
                .authenticationEnabled(true)
                .build();
            var innerHandler = new CapturingHttpMessageHandler();
            var handler = new HeadersFilteringHttpMessageHandler(innerHandler, config.RequiredHeaders);
            using (var client = new HttpClient(handler))
            using (var request = new HttpRequestMessage(HttpMethod.Post, "http://127.0.0.1:8080/"))
            {
                request.Headers.Host = "127.0.0.1:8080";
                request.Headers.TryAddWithoutValidation("X-Amz-Target", "DynamoDB_20120810.GetItem");
                request.Headers.TryAddWithoutValidation("Authorization", "AWS4-HMAC-SHA256...");
                request.Headers.TryAddWithoutValidation("X-Amz-Date", "20240101T000000Z");
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
                request.Headers.TryAddWithoutValidation("User-Agent", "scylladb-alternator-client-csharp/test");
                request.Headers.TryAddWithoutValidation("amz-sdk-request", "attempt=1");
                request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request);

                Assert.That(response.IsSuccessStatusCode, Is.True);
                Assert.That(innerHandler.Request, Is.Not.Null);
                Assert.That(innerHandler.Request!.Headers.Contains("Host"), Is.True);
                Assert.That(innerHandler.Request.Headers.Contains("X-Amz-Target"), Is.True);
                Assert.That(innerHandler.Request.Headers.Contains("Authorization"), Is.True);
                Assert.That(innerHandler.Request.Headers.Contains("X-Amz-Date"), Is.True);
                Assert.That(innerHandler.Request.Headers.Contains("Accept-Encoding"), Is.True);
                Assert.That(innerHandler.Request.Headers.Contains("User-Agent"), Is.True);
                Assert.That(innerHandler.Request.Content!.Headers.Contains("Content-Type"), Is.True);
                Assert.That(innerHandler.Request.Headers.Contains("amz-sdk-request"), Is.False);
            }

            Assert.That(innerHandler.Disposed, Is.True);
        }

        [Test]
        public void AlternatorUserAgentApplyToRequestReplacesAwsSdkUserAgentHeaderTest()
        {
            var token = GetAlternatorUserAgentToken();
            var applyTo = GetAlternatorUserAgentApplyTo();
            var request = new PutItemRequest();
            var headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "aws-sdk-dotnet/4.x",
            };

            applyTo(request, headers);

            Assert.That(headers["User-Agent"], Is.EqualTo(token));
            Assert.That(headers["User-Agent"], Does.Not.Contain("aws-sdk-dotnet"));
        }

        [Test]
        public void AlternatorUserAgentSupportsReplacementTransformAndRemovalTest()
        {
            var token = GetAlternatorUserAgentToken();
            var applyTo = GetAlternatorUserAgentApplyToWithOptions();
            var replaceRequest = new PutItemRequest();
            var replaceHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = "aws-sdk-dotnet/4.x",
            };

            applyTo(replaceRequest, replaceHeaders, _ => "custom/1", false);
            Assert.That(replaceHeaders["User-Agent"], Is.EqualTo("custom/1"));

            var transformRequest = new PutItemRequest();
            var transformHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = "aws-sdk-dotnet/4.x",
            };
            applyTo(transformRequest, transformHeaders, userAgent => "prefix " + userAgent + " suffix", true);
            Assert.That(transformHeaders["User-Agent"], Is.EqualTo("prefix " + token + " suffix"));

            var removeRequest = new PutItemRequest();
            var removeHeaders = new Dictionary<string, string>
            {
                ["User-Agent"] = "aws-sdk-dotnet/4.x",
            };
            applyTo(removeRequest, removeHeaders, _ => null, false);
            Assert.That(removeHeaders, Does.Not.ContainKey("User-Agent"));
        }

        [Test]
        public void AlternatorUserAgentTokenMatchesJavaStyleProductTest()
        {
            var token = GetAlternatorUserAgentToken();

            Assert.That(token, Does.StartWith("scylladb-alternator-client-csharp/"));
            Assert.That(token, Does.Not.Contain(" "));
            Assert.That(token, Does.Not.EndWith("/unknown"));
        }

        [Test]
        public void GzipRequestPipelineHandlerCompressesBytesTest()
        {
            var handler = new GzipRequestPipelineHandler(1024);
            var originalBytes = System.Text.Encoding.UTF8.GetBytes(new string('x', 2048));
            var compressedBytes = GzipRequestPipelineHandler.GzipCompress(originalBytes);

            using var compressedStream = new MemoryStream(compressedBytes);
            using var gzipStream = new System.IO.Compression.GZipStream(
                compressedStream,
                System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzipStream.CopyTo(output);

            Assert.That(handler, Is.Not.Null);
            Assert.That(output.ToArray(), Is.EqualTo(originalBytes));
            Assert.That(compressedBytes.Length, Is.LessThan(originalBytes.Length));
        }

        [Test]
        public void GzipRequestPipelineHandlerMarksRequestsAtOrAboveThresholdTest()
        {
            var handler = new GzipRequestPipelineHandler(10);
            var belowThreshold = CreateDefaultRequest(new byte[9]);
            var atThreshold = CreateDefaultRequest(new byte[10]);
            var aboveThreshold = CreateDefaultRequest(new byte[11]);

            InvokeCompressRequest(handler, belowThreshold);
            InvokeCompressRequest(handler, atThreshold);
            InvokeCompressRequest(handler, aboveThreshold);

            Assert.That(belowThreshold.CompressionAlgorithm, Is.Not.EqualTo(CompressionEncodingAlgorithm.gzip));
            Assert.That(atThreshold.CompressionAlgorithm, Is.EqualTo(CompressionEncodingAlgorithm.gzip));
            Assert.That(aboveThreshold.CompressionAlgorithm, Is.EqualTo(CompressionEncodingAlgorithm.gzip));
        }

        [Test]
        public void GzipRequestPipelineHandlerPreservesBodylessAndAlreadyCompressedRequestsTest()
        {
            var zeroThresholdHandler = new GzipRequestPipelineHandler(0);
            var emptyContent = CreateDefaultRequest(Array.Empty<byte>());
            var bodyless = CreateDefaultRequest();
            var alreadyCompressed = CreateDefaultRequest(new byte[10]);
            alreadyCompressed.Headers["Content-Encoding"] = "br, gzip";

            Assert.DoesNotThrow(() => InvokeCompressRequest(zeroThresholdHandler, null));
            InvokeCompressRequest(zeroThresholdHandler, emptyContent);
            InvokeCompressRequest(zeroThresholdHandler, bodyless);
            InvokeCompressRequest(zeroThresholdHandler, alreadyCompressed);

            Assert.That(emptyContent.CompressionAlgorithm, Is.EqualTo(CompressionEncodingAlgorithm.gzip));
            Assert.That(bodyless.CompressionAlgorithm, Is.Not.EqualTo(CompressionEncodingAlgorithm.gzip));
            Assert.That(alreadyCompressed.CompressionAlgorithm, Is.Not.EqualTo(CompressionEncodingAlgorithm.gzip));
        }

        [Test]
        public void GzipRequestPipelineHandlerUsesRemainingSeekableStreamLengthTest()
        {
            var handler = new GzipRequestPipelineHandler(10);
            using var belowThresholdStream = new MemoryStream(new byte[15]);
            using var atThresholdStream = new MemoryStream(new byte[15]);
            belowThresholdStream.Position = 6;
            atThresholdStream.Position = 5;
            var belowThreshold = CreateDefaultRequest(contentStream: belowThresholdStream);
            var atThreshold = CreateDefaultRequest(contentStream: atThresholdStream);

            InvokeCompressRequest(handler, belowThreshold);
            InvokeCompressRequest(handler, atThreshold);

            Assert.That(belowThreshold.CompressionAlgorithm, Is.Not.EqualTo(CompressionEncodingAlgorithm.gzip));
            Assert.That(atThreshold.CompressionAlgorithm, Is.EqualTo(CompressionEncodingAlgorithm.gzip));
        }

        private static (string DirectoryPath, string CertificatePath, string PrivateKeyPath) CreateTemporaryClientCertificateFiles()
        {
            var directory = Directory.CreateTempSubdirectory("alternator-client-cert-").FullName;
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=alternator-client-test",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            var certificatePath = Path.Combine(directory, "client.crt");
            var privateKeyPath = Path.Combine(directory, "client.key");
            File.WriteAllText(certificatePath, certificate.ExportCertificatePem());
            File.WriteAllText(privateKeyPath, rsa.ExportPkcs8PrivateKeyPem());
            return (directory, certificatePath, privateKeyPath);
        }

        private static DefaultRequest CreateDefaultRequest(byte[] content = null!, Stream? contentStream = null)
        {
            return new DefaultRequest(new GetItemRequest(), "dynamodb")
            {
                Content = content,
                ContentStream = contentStream,
            };
        }

        private static X509Certificate2 LoadSystemTrustedCertificate()
        {
            var certificate = TryLoadSystemTrustedCertificate(StoreLocation.CurrentUser)
                ?? TryLoadSystemTrustedCertificate(StoreLocation.LocalMachine);
            if (certificate == null)
            {
                Assert.Ignore("No system root certificate is available for TLS validation tests.");
            }

            return certificate;
        }

        private static X509Certificate2? TryLoadSystemTrustedCertificate(StoreLocation storeLocation)
        {
            using var store = new X509Store(StoreName.Root, storeLocation);
            store.Open(OpenFlags.ReadOnly);
            var now = DateTime.Now;
            var certificate = store.Certificates
                .FirstOrDefault(candidate => candidate.NotBefore <= now && candidate.NotAfter >= now);
            return certificate == null
                ? null
                : new X509Certificate2(certificate.Export(X509ContentType.Cert));
        }

        private static void InvokeCompressRequest(GzipRequestPipelineHandler handler, IRequest? request)
        {
            var method = typeof(GzipRequestPipelineHandler).GetMethod(
                "CompressRequest",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);

            try
            {
                method!.Invoke(handler, new object?[] { request });
            }
            catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw exception.InnerException;
            }
        }

        private static bool InvokeValidateCertificate(
            TlsConfig tlsConfig,
            X509Certificate2 certificate,
            X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            var factoryType = typeof(AlternatorConfig).Assembly.GetType("ScyllaDB.Alternator.AlternatorHttpClientFactory");
            Assert.That(factoryType, Is.Not.Null);

            var method = factoryType!.GetMethod(
                "ValidateCertificate",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(TlsConfig), typeof(X509Certificate2), typeof(X509Chain), typeof(SslPolicyErrors) },
                null);
            Assert.That(method, Is.Not.Null);
            var value = method!.Invoke(null, new object?[] { tlsConfig, certificate, chain, sslPolicyErrors });
            Assert.That(value, Is.Not.Null);
            return (bool)value!;
        }

        private static HttpClientHandler CreateAlternatorHttpClientHandler(
            TlsConfig tlsConfig,
            int maxConnections,
            Action<HttpClientHandler> customizer)
        {
            var factoryType = typeof(AlternatorConfig).Assembly.GetType("ScyllaDB.Alternator.AlternatorHttpClientFactory");
            Assert.That(factoryType, Is.Not.Null);

            var method = factoryType!.GetMethod(
                "CreateHandler",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var value = method!.Invoke(null, new object?[] { tlsConfig, maxConnections, customizer });
            Assert.That(value, Is.Not.Null);
            return (HttpClientHandler)value!;
        }

        private static SocketsHttpHandler CreateAlternatorSocketsHttpHandler(
            AlternatorConfig config,
            Action<SocketsHttpHandler> customizer)
        {
            var factoryType = typeof(AlternatorConfig).Assembly.GetType("ScyllaDB.Alternator.AlternatorHttpClientFactory");
            Assert.That(factoryType, Is.Not.Null);

            var method = factoryType!.GetMethod(
                "CreateSocketsHandler",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var value = method!.Invoke(null, new object?[] { config, customizer });
            Assert.That(value, Is.Not.Null);
            return (SocketsHttpHandler)value!;
        }

        private static string GetAlternatorUserAgentToken()
        {
            var userAgentType = typeof(AlternatorConfig).Assembly.GetType("ScyllaDB.Alternator.AlternatorUserAgent");
            Assert.That(userAgentType, Is.Not.Null);

            var property = userAgentType!.GetProperty(
                "UserAgentToken",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            var value = property!.GetValue(null);
            Assert.That(value, Is.Not.Null);
            return (string)value!;
        }

        private static Action<AmazonWebServiceRequest, IDictionary<string, string>> GetAlternatorUserAgentApplyTo()
        {
            var userAgentType = typeof(AlternatorConfig).Assembly.GetType("ScyllaDB.Alternator.AlternatorUserAgent");
            Assert.That(userAgentType, Is.Not.Null);

            var method = userAgentType!.GetMethod(
                "ApplyTo",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(AmazonWebServiceRequest), typeof(IDictionary<string, string>) },
                null);
            Assert.That(method, Is.Not.Null);
            return (request, headers) => method!.Invoke(null, new object[] { request, headers });
        }

        private static Action<AmazonWebServiceRequest, IDictionary<string, string>, Func<string, string?>?, bool> GetAlternatorUserAgentApplyToWithOptions()
        {
            var userAgentType = typeof(AlternatorConfig).Assembly.GetType("ScyllaDB.Alternator.AlternatorUserAgent");
            Assert.That(userAgentType, Is.Not.Null);

            var method = userAgentType!.GetMethod(
                "ApplyTo",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(AmazonWebServiceRequest), typeof(IDictionary<string, string>), typeof(Func<string, string>), typeof(bool) },
                null);
            Assert.That(method, Is.Not.Null);
            return (request, headers, transformer, appendDefaultToken) =>
                method!.Invoke(null, new object?[] { request, headers, transformer, appendDefaultToken });
        }

        private sealed class CapturingHttpMessageHandler : HttpMessageHandler
        {
            internal HttpRequestMessage? Request { get; private set; }

            internal bool Disposed { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                this.Request = request;
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    this.Disposed = true;
                }

                base.Dispose(disposing);
            }
        }
    }
}
