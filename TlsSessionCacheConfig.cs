// <copyright file="TlsSessionCacheConfig.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public sealed class TlsSessionCacheConfig
    {
        public const int DefaultSessionCacheSize = 1024;
        public const int DefaultSessionTimeoutSeconds = 86400;

#pragma warning disable SA1310, IDE1006
        public const int DEFAULT_SESSION_CACHE_SIZE = DefaultSessionCacheSize;
        public const int DEFAULT_SESSION_TIMEOUT_SECONDS = DefaultSessionTimeoutSeconds;
#pragma warning restore SA1310, IDE1006

        private static readonly TlsSessionCacheConfig DefaultInstance =
            new TlsSessionCacheConfig(true, DefaultSessionCacheSize, DefaultSessionTimeoutSeconds);

        private static readonly TlsSessionCacheConfig DisabledInstance =
            new TlsSessionCacheConfig(false, 0, 0);

        private TlsSessionCacheConfig(bool enabled, int sessionCacheSize, int sessionTimeoutSeconds)
        {
            this.Enabled = enabled;
            this.SessionCacheSize = sessionCacheSize;
            this.SessionTimeoutSeconds = sessionTimeoutSeconds;
        }

        public bool Enabled { get; }

        public int SessionCacheSize { get; }

        public int SessionTimeoutSeconds { get; }

        public static TlsSessionCacheConfig GetDefault()
        {
            return DefaultInstance;
        }

        public static TlsSessionCacheConfig Disabled()
        {
            return DisabledInstance;
        }

        public static TlsSessionCacheConfigBuilder Builder()
        {
            return new TlsSessionCacheConfigBuilder();
        }

#pragma warning disable SA1300, IDE1006
        public static TlsSessionCacheConfig getDefault()
        {
            return GetDefault();
        }

        public static TlsSessionCacheConfig disabled()
        {
            return Disabled();
        }

        public static TlsSessionCacheConfigBuilder builder()
        {
            return Builder();
        }

        public bool isEnabled()
        {
            return this.Enabled;
        }

        public int getSessionCacheSize()
        {
            return this.SessionCacheSize;
        }

        public int getSessionTimeoutSeconds()
        {
            return this.SessionTimeoutSeconds;
        }
#pragma warning restore SA1300, IDE1006

        public override string ToString()
        {
            return "TlsSessionCacheConfig{"
                + "enabled="
                + this.Enabled.ToString().ToLowerInvariant()
                + ", sessionCacheSize="
                + this.SessionCacheSize
                + ", sessionTimeoutSeconds="
                + this.SessionTimeoutSeconds
                + "}";
        }

        public override bool Equals(object? obj)
        {
            return obj is TlsSessionCacheConfig other
                && this.Enabled == other.Enabled
                && this.SessionCacheSize == other.SessionCacheSize
                && this.SessionTimeoutSeconds == other.SessionTimeoutSeconds;
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(this.Enabled, this.SessionCacheSize, this.SessionTimeoutSeconds);
        }

        internal static TlsSessionCacheConfig Create(bool enabled, int sessionCacheSize, int sessionTimeoutSeconds)
        {
            return new TlsSessionCacheConfig(enabled, sessionCacheSize, sessionTimeoutSeconds);
        }
    }
}
