// <copyright file="ResponseCompressionHttpMessageHandler.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.IO.Compression;
    using System.Net.Http;
    using System.Net.Http.Headers;

    public sealed class ResponseCompressionHttpMessageHandler : DelegatingHandler
    {
        private const string AcceptEncodingHeader = "Accept-Encoding";
        private const string ContentEncodingHeader = "Content-Encoding";
        private const string ContentLengthHeader = "Content-Length";
        private readonly IReadOnlyList<ResponseCompressionAlgorithm> algorithms;
        private readonly string acceptEncodingValue;

        public ResponseCompressionHttpMessageHandler(
            HttpMessageHandler innerHandler,
            IEnumerable<ResponseCompressionAlgorithm> algorithms)
            : base(innerHandler ?? throw new ArgumentNullException(nameof(innerHandler)))
        {
            this.algorithms = AlternatorConfig.NormalizeResponseCompressionAlgorithms(algorithms);
            this.acceptEncodingValue = string.Join(", ", this.algorithms.Select(ToContentEncoding));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.ApplyAcceptEncoding(request);
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Content == null || this.algorithms.Count == 0)
            {
                return response;
            }

            var contentEncoding = GetSingleContentEncoding(response.Content.Headers);
            var decoder = this.CreateDecoder(contentEncoding);
            if (decoder == null)
            {
                return response;
            }

            var originalContent = response.Content;
            var originalStream = await originalContent.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var decodedContent = new StreamContent(decoder(originalStream));
            CopyContentHeaders(originalContent.Headers, decodedContent.Headers);
            response.Content = decodedContent;
            return response;
        }

        private static string ToContentEncoding(ResponseCompressionAlgorithm algorithm)
        {
            return algorithm switch
            {
                ResponseCompressionAlgorithm.Gzip => "gzip",
                ResponseCompressionAlgorithm.Deflate => "deflate",
                _ => throw new ArgumentException("Unsupported response compression algorithm: " + algorithm, nameof(algorithm)),
            };
        }

        private static string? GetSingleContentEncoding(HttpContentHeaders headers)
        {
            return headers.ContentEncoding.Count == 1
                ? headers.ContentEncoding.First()
                : null;
        }

        private static void CopyContentHeaders(HttpContentHeaders source, HttpContentHeaders destination)
        {
            foreach (var header in source)
            {
                if (string.Equals(header.Key, ContentEncodingHeader, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(header.Key, ContentLengthHeader, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                destination.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        private void ApplyAcceptEncoding(HttpRequestMessage request)
        {
            if (this.algorithms.Count == 0)
            {
                return;
            }

            if (request.Headers.TryGetValues(AcceptEncodingHeader, out var values))
            {
                var existing = string.Join(",", values).Trim();
                if (!string.Equals(existing, "identity", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                request.Headers.Remove(AcceptEncodingHeader);
            }

            request.Headers.TryAddWithoutValidation(AcceptEncodingHeader, this.acceptEncodingValue);
        }

        private Func<Stream, Stream>? CreateDecoder(string? contentEncoding)
        {
            if (contentEncoding == null)
            {
                return null;
            }

            if (string.Equals(contentEncoding, "gzip", StringComparison.OrdinalIgnoreCase)
                && this.algorithms.Contains(ResponseCompressionAlgorithm.Gzip))
            {
                return stream => new GZipStream(stream, CompressionMode.Decompress);
            }

            if (string.Equals(contentEncoding, "deflate", StringComparison.OrdinalIgnoreCase)
                && this.algorithms.Contains(ResponseCompressionAlgorithm.Deflate))
            {
                return stream => new DeflateStream(stream, CompressionMode.Decompress);
            }

            return null;
        }
    }
}
