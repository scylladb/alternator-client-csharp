// <copyright file="AlternatorUserAgent.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using Amazon.Runtime;

    internal static class AlternatorUserAgent
    {
        internal const string ProductName = "scylladb-alternator-client-csharp";
        private const string UnknownVersion = "unknown";

        private static readonly PropertyInfo? UserAgentAdditionProperty = typeof(AmazonWebServiceRequest).GetProperty(
            "UserAgentAddition",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly string Token = ProductName + "/" + ResolveVersion();

        internal static string UserAgentToken => Token;

        internal static Func<string, string?> ReplaceWith(string userAgent)
        {
            RequireValidUserAgent(userAgent);
            return _ => userAgent;
        }

        internal static Func<string, string?> Disable()
        {
            return _ => null;
        }

        internal static Func<string, string?> RequireUserAgentTransformer(Func<string, string?> userAgentTransformer)
        {
            return userAgentTransformer ?? throw new ArgumentException("userAgentTransformer cannot be null", nameof(userAgentTransformer));
        }

        internal static void ApplyTo(AmazonWebServiceRequest request, IDictionary<string, string> headers)
        {
            ApplyTo(request, headers, null, appendDefaultToken: true);
        }

        internal static void ApplyTo(
            AmazonWebServiceRequest request,
            IDictionary<string, string> headers,
            Func<string, string?>? transformer,
            bool appendDefaultToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (headers == null)
            {
                throw new ArgumentNullException(nameof(headers));
            }

            if (UserAgentAdditionProperty != null)
            {
                var existingAddition = (string?)UserAgentAdditionProperty.GetValue(request);
                UserAgentAdditionProperty.SetValue(request, Transform(existingAddition, transformer, appendDefaultToken));
            }

            var userAgentHeader = headers.Keys.FirstOrDefault(key => string.Equals(key, "User-Agent", StringComparison.OrdinalIgnoreCase));
            var transformedHeader = Transform(userAgentHeader == null ? null : headers[userAgentHeader], transformer, appendDefaultToken);
            if (string.IsNullOrWhiteSpace(transformedHeader))
            {
                if (userAgentHeader != null)
                {
                    headers.Remove(userAgentHeader);
                }

                return;
            }

            if (userAgentHeader == null)
            {
                headers["User-Agent"] = transformedHeader;
                return;
            }

            headers[userAgentHeader] = transformedHeader;
        }

        internal static void RequireValidUserAgent(string userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent))
            {
                throw new ArgumentException("userAgent cannot be null or blank", nameof(userAgent));
            }
        }

        private static string? Transform(string? current, Func<string, string?>? transformer, bool appendDefaultToken)
        {
            var userAgent = appendDefaultToken ? UserAgentToken : current;
            if (transformer != null)
            {
                userAgent = transformer(userAgent ?? string.Empty);
            }

            return string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim();
        }

        private static string ResolveVersion()
        {
            var assembly = typeof(AlternatorUserAgent).Assembly;
            var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(version))
            {
                var assemblyVersion = assembly.GetName().Version?.ToString();
                if (!string.IsNullOrWhiteSpace(assemblyVersion) && assemblyVersion != "0.0.0.0")
                {
                    version = assemblyVersion;
                }
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                version = UnknownVersion;
            }

            return Regex.Replace(version.Trim(), "\\s+", "_");
        }
    }
}
