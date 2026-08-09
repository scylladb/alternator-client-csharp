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
    using System.Globalization;
    using System.Net;
    using System.Net.Sockets;

    internal static class AlternatorEndpointValidation
    {
        internal static string ValidateHostArgument(string? host, string parameterName)
        {
            try
            {
                return NormalizeHost(host);
            }
            catch (UriFormatException e)
            {
                throw new ArgumentException(
                    "Seed host must be a valid DNS name or IP address without scheme, userinfo, port, path, query, or fragment.",
                    parameterName,
                    e);
            }
        }

        internal static string ValidateSeedUri(Uri? seedUri, string parameterName)
        {
            if (seedUri == null)
            {
                throw new ArgumentNullException(parameterName);
            }

            if (!seedUri.IsAbsoluteUri
                || string.IsNullOrEmpty(seedUri.Host)
                || !string.IsNullOrEmpty(seedUri.UserInfo)
                || (seedUri.AbsolutePath.Length != 0 && seedUri.AbsolutePath != "/")
                || !string.IsNullOrEmpty(seedUri.Query)
                || !string.IsNullOrEmpty(seedUri.Fragment))
            {
                throw new ArgumentException(
                    "Seed URI must contain only a scheme and host authority with an optional port.",
                    parameterName);
            }

            string host;
            try
            {
                // System.Uri canonicalizes legacy IPv4 spellings such as 127.1
                // before exposing Host. Validate the original authority as well
                // so passing a Uri cannot bypass the strict host grammar used by
                // WithSeedHost and /localnodes response validation.
                _ = ValidateHostArgument(GetOriginalHost(seedUri), parameterName);
                host = seedUri.GetComponents(UriComponents.Host, UriFormat.Unescaped);
                if (host.Length >= 2 && host[0] == '[' && host[host.Length - 1] == ']')
                {
                    host = host.Substring(1, host.Length - 2);
                }
            }
            catch (UriFormatException e)
            {
                throw new ArgumentException("Seed URI contains an invalid host.", parameterName, e);
            }

            return ValidateHostArgument(host, parameterName);
        }

        internal static string NormalizeHost(string? host)
        {
            if (string.IsNullOrWhiteSpace(host) || host != host.Trim())
            {
                throw new UriFormatException("Host cannot be null, empty, or surrounded by whitespace.");
            }

            if (host.IndexOfAny(new[] { '@', '/', '?', '#', '\\', '[', ']' }) >= 0
                || host.Any(char.IsControl))
            {
                throw new UriFormatException("Host contains URI authority or control characters.");
            }

            if (IsCanonicalIpv4Address(host))
            {
                return host;
            }

            if (host.Contains(':', StringComparison.Ordinal))
            {
                if (!IsCanonicalIpv6Address(host))
                {
                    throw new UriFormatException("Host is not a canonical IPv6 address.");
                }

                return host;
            }

            // IPAddress.TryParse deliberately accepts historical inet_aton
            // spellings (for example 127.1, 0177.0.0.1, 0x7f000001,
            // and 2130706433). They are ambiguous endpoint identities and must
            // never fall through as DNS names.
            if (IPAddress.TryParse(host, out _)
                || host.Contains('%', StringComparison.Ordinal))
            {
                throw new UriFormatException("Host is not a valid IP address.");
            }

            var dnsName = host.EndsWith(".", StringComparison.Ordinal)
                ? host.Substring(0, host.Length - 1)
                : host;
            if (dnsName.Length == 0)
            {
                throw new UriFormatException("DNS host cannot be empty.");
            }

            string asciiName;
            try
            {
                asciiName = new IdnMapping { UseStd3AsciiRules = true }.GetAscii(dnsName);
            }
            catch (ArgumentException e)
            {
                throw new UriFormatException("Host is not a valid IDN DNS name.", e);
            }

            if (asciiName.Length > 253)
            {
                throw new UriFormatException("DNS host exceeds 253 bytes.");
            }

            var labels = asciiName.Split('.');
            foreach (var label in labels)
            {
                if (label.Length == 0
                    || label.Length > 63
                    || label[0] == '-'
                    || label[label.Length - 1] == '-'
                    || label.Any(character =>
                        !char.IsAsciiLetterOrDigit(character) && character != '-'))
                {
                    throw new UriFormatException("Host contains an invalid DNS label.");
                }
            }

            if (LooksLikeLegacyIpv4Address(labels))
            {
                throw new UriFormatException("Host resembles a non-canonical IPv4 address.");
            }

            return host;
        }

        private static string GetOriginalHost(Uri seedUri)
        {
            var original = seedUri.OriginalString;
            var schemeEnd = original.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd < 1)
            {
                throw new UriFormatException("Seed URI must use an authority-form absolute URI.");
            }

            var authorityStart = schemeEnd + 3;
            var authorityEnd = original.IndexOfAny(new[] { '/', '?', '#' }, authorityStart);
            if (authorityEnd < 0)
            {
                authorityEnd = original.Length;
            }

            var authority = original.Substring(authorityStart, authorityEnd - authorityStart);
            if (authority.Length == 0 || authority.Contains('@', StringComparison.Ordinal))
            {
                throw new UriFormatException("Seed URI authority does not contain a usable host.");
            }

            string rawHost;
            if (authority[0] == '[')
            {
                var closingBracket = authority.IndexOf(']');
                if (closingBracket < 0)
                {
                    throw new UriFormatException("Seed URI contains an unterminated IPv6 literal.");
                }

                rawHost = authority.Substring(1, closingBracket - 1);
            }
            else
            {
                var portSeparator = authority.LastIndexOf(':');
                rawHost = portSeparator < 0
                    ? authority
                    : authority.Substring(0, portSeparator);
            }

            return Uri.UnescapeDataString(rawHost);
        }

        private static bool IsCanonicalIpv4Address(string host)
        {
            var parts = host.Split('.');
            if (parts.Length != 4)
            {
                return false;
            }

            foreach (var part in parts)
            {
                if (part.Length == 0 || (part.Length > 1 && part[0] == '0'))
                {
                    return false;
                }

                var value = 0;
                foreach (var character in part)
                {
                    if (!char.IsAsciiDigit(character))
                    {
                        return false;
                    }

                    value = (value * 10) + (character - '0');
                    if (value > byte.MaxValue)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsCanonicalIpv6Address(string host)
        {
            var addressEnd = host.IndexOf('%');
            var address = addressEnd < 0 ? host : host.Substring(0, addressEnd);
            if (address.Contains('.', StringComparison.Ordinal))
            {
                var ipv4Start = address.LastIndexOf(':') + 1;
                if (ipv4Start == 0 || !IsCanonicalIpv4Address(address.Substring(ipv4Start)))
                {
                    return false;
                }
            }

            return IPAddress.TryParse(host, out var parsed)
                && parsed.AddressFamily == AddressFamily.InterNetworkV6;
        }

        private static bool LooksLikeLegacyIpv4Address(IReadOnlyList<string> labels)
        {
            if (labels.Count == 0)
            {
                return false;
            }

            return labels.All(label =>
                label.Length != 0
                && (label.All(char.IsAsciiDigit) || IsPrefixedHexadecimal(label)));
        }

        private static bool IsPrefixedHexadecimal(string value)
        {
            return value.Length > 2
                && value[0] == '0'
                && (value[1] == 'x' || value[1] == 'X')
                && value.Skip(2).All(character =>
                    char.IsAsciiDigit(character)
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F'));
        }
    }
}
