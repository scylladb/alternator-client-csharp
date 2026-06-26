// <copyright file="GzipRequestPipelineHandler.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.IO.Compression;
    using Amazon.Runtime;
    using Amazon.Runtime.Internal;

    public sealed class GzipRequestPipelineHandler : PipelineHandler
    {
        private readonly int minCompressionSizeBytes;

        public GzipRequestPipelineHandler(int minCompressionSizeBytes)
        {
            this.minCompressionSizeBytes = minCompressionSizeBytes;
        }

        public static byte[] GzipCompress(byte[] data)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true))
            {
                gzip.Write(data, 0, data.Length);
            }

            return output.ToArray();
        }

        public override void InvokeSync(IExecutionContext executionContext)
        {
            this.CompressRequest(executionContext.RequestContext.Request);
            base.InvokeSync(executionContext);
        }

        public override Task<T> InvokeAsync<T>(IExecutionContext executionContext)
        {
            this.CompressRequest(executionContext.RequestContext.Request);
            return base.InvokeAsync<T>(executionContext);
        }

        internal void CompressRequest(IRequest request)
        {
            if (request == null || IsAlreadyCompressed(request) || !this.ShouldCompress(request))
            {
                return;
            }

            request.CompressionAlgorithm = CompressionEncodingAlgorithm.gzip;
        }

        private static bool IsAlreadyCompressed(IRequest request)
        {
            return request.Headers.TryGetValue("Content-Encoding", out var value)
                && value.Split(',').Any(token => string.Equals(token.Trim(), "gzip", StringComparison.OrdinalIgnoreCase));
        }

        private bool ShouldCompress(IRequest request)
        {
            if (request.Content != null)
            {
                return request.Content.Length >= this.minCompressionSizeBytes;
            }

            if (request.ContentStream == null)
            {
                return false;
            }

            return !request.ContentStream.CanSeek
                || request.ContentStream.Length - request.ContentStream.Position >= this.minCompressionSizeBytes;
        }
    }
}
