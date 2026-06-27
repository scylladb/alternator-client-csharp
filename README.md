# Alternator - Client-side load balancing - C#

## Introduction

`ScyllaDB.Alternator` adds client-side load balancing for ScyllaDB Alternator to the AWS SDK for .NET DynamoDB client.

DynamoDB applications normally point at one endpoint. Alternator is a distributed cluster, so a client should spread requests across live Alternator nodes and keep working when one node fails. This library discovers Alternator nodes through `/localnodes`, maintains the live-node list in the background, and installs AWS SDK pipeline handlers that choose a node for each request.

The library does not replace the AWS SDK. `AlternatorDynamoDBClient.builder().Build()` returns a regular `AmazonDynamoDBClient`, and DynamoDB operations use the normal C# AWS SDK API.

## Add the Package

```sh
dotnet add package ScyllaDB.Alternator
```

For local development from this repository:

```sh
make build
make pack
```

## Basic Usage

```csharp
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using ScyllaDB.Alternator;

var credentials = new BasicAWSCredentials("myuser", "mypassword");

AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .build();
```

If Alternator authentication is disabled, credentials can be omitted. The builder uses anonymous AWS credentials internally.

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .build();
```

The builder also exposes C#-style method names:

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.Builder()
    .EndpointOverride("http://127.0.0.1:8000")
    .WithCredentials(credentials)
    .Build();
```

## Alternator API Wrapper

Use `buildWithAlternatorAPI()` when the application also needs Alternator-specific state, such as the live-node view.

```csharp
using var alternator = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .buildWithAlternatorAPI();

AmazonDynamoDBClient client = alternator.getClient();
IReadOnlyList<Uri> liveNodes = alternator.getLiveNodes();
Uri nextNode = alternator.nextAsURI();
AlternatorLiveNodes manager = alternator.getAlternatorLiveNodes();
```

The wrapper owns the DynamoDB client and live-node polling. Disposing the wrapper shuts down polling and disposes the client.

## Routing Scope

Routing scopes mirror the Java client API and support fallback chains.

```csharp
using ScyllaDB.Alternator.Routing;

var scope = RackScope.of(
    "dc1",
    "rack1",
    DatacenterScope.of("dc1", ClusterScope.create()));

AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .withRoutingScope(scope)
    .build();
```

For cluster-wide routing in a multi-datacenter deployment, configure working seed
hosts from every datacenter. `ClusterScope.create()` uses the bare `/localnodes`
endpoint, and Alternator returns the nodes visible from the node that handled
that request. With only one seed, discovery usually covers only that seed node's
datacenter, so cluster scope will not route across datacenters unless the client
is given reachable seeds from all datacenters.

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .withScheme("https")
    .withPort(8043)
    .withInitialSeeds(
        "dc1-seed.example.com",
        "dc2-seed.example.com")
    .withRoutingScope(ClusterScope.create())
    .build();
```

Seeds passed to `withInitialSeeds(...)` are DNS names or IP addresses only. The
scheme and port are shared by all seeds and are configured separately.

Legacy datacenter/rack helpers remain available:

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.Create(
    credentials,
    new Uri("http://127.0.0.1:8000"),
    datacenter: "dc1",
    rack: "rack1");
```

## HTTP Client Configuration

The .NET SDK uses `AmazonDynamoDBConfig.HttpClientFactory` for transport customization. The Alternator builder configures an internal factory so TLS, header filtering, compression, connection pool tuning, and endpoint routing stay active.

Customize the default `SocketsHttpHandler` after Alternator defaults are applied:

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("https://127.0.0.1:8043")
    .credentialsProvider(credentials)
    .withMaxConnections(200)
    .withConnectionTimeoutMs(5000)
    .withSocketsHttpHandlerCustomizer(handler =>
    {
        handler.UseCookies = false;
    })
    .build();
```

`withHttpClientHandlerCustomizer(...)` remains available for callers that need the older `HttpClientHandler` API. That path cannot apply `SocketsHttpHandler`-only options such as pooled connection lifetime.

Use `ConfigureAws(...)` for C# AWS SDK configuration that belongs on `AmazonDynamoDBConfig`:

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .ConfigureAws(config =>
    {
        config.Timeout = TimeSpan.FromSeconds(10);
    })
    .build();
```

You can also provide the regular AWS SDK .NET HTTP factory directly. Java-style aliases `httpClient(...)` and `httpClientBuilder(...)` are available, but take `Amazon.Runtime.HttpClientFactory` because that is the C# AWS SDK transport extension point.

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .httpClientFactory(myHttpClientFactory)
    .build();
```

### Connection Pool Tuning

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .withMaxConnections(200)
    .withConnectionMaxIdleTimeMs(30000)
    .withConnectionTimeToLiveMs(60000)
    .withConnectionAcquisitionTimeoutMs(5000)
    .withConnectionTimeoutMs(3000)
    .build();
```

| Setting | Default | Description |
|---|---:|---|
| `maxConnections` | 400 | Maximum connections per server. |
| `connectionMaxIdleTimeMs` | 600000 | Maximum idle time for pooled connections. |
| `connectionTimeToLiveMs` | 0 | Maximum lifetime for pooled connections; `0` means unlimited. |
| `connectionAcquisitionTimeoutMs` | 10000 | Stored for Java API parity; .NET does not expose a separate per-pool acquisition timeout. |
| `connectionTimeoutMs` | 15000 | Applied to `AmazonDynamoDBConfig.Timeout` and `SocketsHttpHandler.ConnectTimeout`. |

## TLS

TLS options are configured on the Alternator builder and applied to the AWS SDK HTTP client factory.

```csharp
var tls = TlsConfig.builder()
    .withCaCertPath("/etc/scylla/alternator-ca.pem")
    .withTrustSystemCaCerts(false)
    .build();

AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("https://127.0.0.1:8043")
    .credentialsProvider(credentials)
    .withTlsConfig(tls)
    .build();
```

Convenience factories:

```csharp
TlsConfig trustAll = TlsConfig.trustAll();
TlsConfig systemDefault = TlsConfig.systemDefault();
```

TLS session resumption, including TLS session tickets when the server supports
them, is enabled by default on the default .NET `SocketsHttpHandler` transport.
It can be disabled explicitly:

```csharp
var tls = TlsConfig.builder()
    .withTlsSessionResumption(false)
    .build();
```

.NET exposes the TLS resumption enable/disable switch but does not expose
portable session ticket cache size or timeout controls through `HttpClient`.
Use the default `SocketsHttpHandler` transport for this setting; the legacy
`withHttpClientHandlerCustomizer(...)` path uses platform defaults and cannot
disable TLS resumption.

## Compression and Header Optimization

Request compression is installed in the AWS SDK runtime pipeline before request signing.

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP)
    .withMinCompressionSizeBytes(2048)
    .build();
```

Header optimization filters outgoing HTTP headers to an allow-list. If no
allow-list is supplied, the client computes one from the effective Alternator
configuration. The computed list keeps the required transport headers and adds
authentication, request compression, and User-Agent headers when those features
are enabled.

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP)
    .withOptimizeHeaders(true)
    .build();
```

Use a static whitelist when the exact header set should not change after it is
configured. Static whitelists are validated against the required headers for the
effective configuration.

```csharp
var configBuilder = AlternatorConfig.builder()
    .withSeedNode("http://127.0.0.1:8000")
    .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP);

var headers = configBuilder.getRequiredHeaders();

AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .withCompressionAlgorithm(RequestCompressionAlgorithm.GZIP)
    .withOptimizeHeaders(true)
    .withHeadersWhitelist(headers)
    .build();
```

Use a custom optimizer when the allow-list should be computed from the final
configuration.

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .withOptimizeHeaders(true)
    .withCustomOptimizeHeaders(config =>
        config.getRequiredHeaders().Concat(new[] { "X-Custom-Trace" }))
    .build();
```

## User-Agent

By default, the client appends a ScyllaDB Alternator token to the AWS SDK user-agent. The builder can replace, transform, or remove the final user-agent.

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .withUserAgent(userAgent => userAgent + " my-app/1.0")
    .build();
```

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .withoutUserAgent()
    .build();
```

## Key Route Affinity

Key route affinity can route qualifying write requests for the same partition key through a deterministic Alternator node order. Retries advance through the same Java/Go-compatible seeded query plan. Partition-key names can be preconfigured, or they are discovered asynchronously with `DescribeTable` after the first qualifying request for a table.

```csharp
using ScyllaDB.Alternator.KeyRouting;

var affinity = KeyRouteAffinityConfig.builder()
    .withType(KeyRouteAffinity.RMW)
    .withPkInfo("users", "user_id")
    .build();

AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .withKeyRouteAffinity(affinity)
    .build();
```

For automatic partition-key discovery, omit `withPkInfo`. Requests use normal load balancing until discovery completes.

```csharp
AmazonDynamoDBClient client = AlternatorDynamoDBClient.builder()
    .endpointOverride("http://127.0.0.1:8000")
    .credentialsProvider(credentials)
    .withKeyRouteAffinity(KeyRouteAffinity.ANY_WRITE)
    .build();
```

## AlternatorLiveNodes

`AlternatorLiveNodes` is exposed through `buildWithAlternatorAPI()` for advanced inspection and compatibility with Java-style examples.

```csharp
AlternatorLiveNodes liveNodes = alternator.getAlternatorLiveNodes();

List<Uri> nodes = liveNodes.getLiveNodes();
Uri localNodesEndpoint = liveNodes.nextAsURI("/localnodes", "dc=dc1");
RoutingScope scope = liveNodes.getRoutingScope();
```

## Build, Test, and CI

```sh
make build
make check
make test-unit
make test-integration
```

In CI, set `IS_CICD=1` for quieter `dotnet` output.

The Makefile includes Java-repo-equivalent cache helpers for integration tests:

```sh
make docker-cache-save
make docker-cache-load
make cert-cache-save
make cert-cache-load
```

`make test-integration` starts the Scylla/Alternator Docker Compose cluster, waits for Alternator, runs integration tests, and stops the cluster.
