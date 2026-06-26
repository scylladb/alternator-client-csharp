// <copyright file="HeadersFilteringHttpMessageHandler.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.Net.Http;

    public sealed class HeadersFilteringHttpMessageHandler : DelegatingHandler
    {
        private readonly ISet<string> allowedHeaders;

        public HeadersFilteringHttpMessageHandler(HttpMessageHandler innerHandler, IEnumerable<string>? allowedHeaders)
            : base(innerHandler ?? throw new ArgumentNullException(nameof(innerHandler)))
        {
            this.allowedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (allowedHeaders == null)
            {
                return;
            }

            foreach (var header in allowedHeaders)
            {
                if (!string.IsNullOrEmpty(header))
                {
                    this.allowedHeaders.Add(header);
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.FilterHeaders(request);
            return base.SendAsync(request, cancellationToken);
        }

        private void FilterHeaders(HttpRequestMessage request)
        {
            foreach (var header in request.Headers.ToList())
            {
                if (!this.allowedHeaders.Contains(header.Key))
                {
                    request.Headers.Remove(header.Key);
                }
            }

            if (request.Content == null)
            {
                return;
            }

            foreach (var header in request.Content.Headers.ToList())
            {
                if (!this.allowedHeaders.Contains(header.Key))
                {
                    request.Content.Headers.Remove(header.Key);
                }
            }
        }
    }
}
