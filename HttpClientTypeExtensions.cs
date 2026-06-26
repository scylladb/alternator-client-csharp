// <copyright file="HttpClientTypeExtensions.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public static class HttpClientTypeExtensions
    {
        public static bool SupportsSync(this HttpClientType httpClientType)
        {
            return httpClientType switch
            {
                HttpClientType.Netty => false,
                _ => true,
            };
        }

        public static bool SupportsAsync(this HttpClientType httpClientType)
        {
            return httpClientType switch
            {
                HttpClientType.Apache => false,
                _ => true,
            };
        }

#pragma warning disable SA1300, IDE1006
        public static bool supportsSync(this HttpClientType httpClientType)
        {
            return httpClientType.SupportsSync();
        }

        public static bool supportsAsync(this HttpClientType httpClientType)
        {
            return httpClientType.SupportsAsync();
        }
#pragma warning restore SA1300, IDE1006
    }
}
