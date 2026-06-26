// <copyright file="TlsSessionCacheConfigBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public sealed class TlsSessionCacheConfigBuilder
    {
        private bool enabled = true;
        private int sessionCacheSize = TlsSessionCacheConfig.DefaultSessionCacheSize;
        private int sessionTimeoutSeconds = TlsSessionCacheConfig.DefaultSessionTimeoutSeconds;

        public TlsSessionCacheConfigBuilder WithEnabled(bool enabled)
        {
            this.enabled = enabled;
            return this;
        }

        public TlsSessionCacheConfigBuilder WithSessionCacheSize(int sessionCacheSize)
        {
            this.sessionCacheSize = sessionCacheSize;
            return this;
        }

        public TlsSessionCacheConfigBuilder WithSessionTimeoutSeconds(int sessionTimeoutSeconds)
        {
            this.sessionTimeoutSeconds = sessionTimeoutSeconds;
            return this;
        }

        public TlsSessionCacheConfig Build()
        {
            if (this.enabled)
            {
                if (this.sessionCacheSize <= 0)
                {
                    throw new ArgumentException(
                        "sessionCacheSize must be positive when TLS session cache is enabled, but was: " + this.sessionCacheSize,
                        nameof(this.sessionCacheSize));
                }

                if (this.sessionTimeoutSeconds <= 0)
                {
                    throw new ArgumentException(
                        "sessionTimeoutSeconds must be positive when TLS session cache is enabled, but was: " + this.sessionTimeoutSeconds,
                        nameof(this.sessionTimeoutSeconds));
                }
            }

            return TlsSessionCacheConfig.Create(this.enabled, this.sessionCacheSize, this.sessionTimeoutSeconds);
        }

#pragma warning disable SA1300, IDE1006
        public TlsSessionCacheConfigBuilder withEnabled(bool enabled)
        {
            return this.WithEnabled(enabled);
        }

        public TlsSessionCacheConfigBuilder withSessionCacheSize(int sessionCacheSize)
        {
            return this.WithSessionCacheSize(sessionCacheSize);
        }

        public TlsSessionCacheConfigBuilder withSessionTimeoutSeconds(int sessionTimeoutSeconds)
        {
            return this.WithSessionTimeoutSeconds(sessionTimeoutSeconds);
        }

        public TlsSessionCacheConfig build()
        {
            return this.Build();
        }
#pragma warning restore SA1300, IDE1006
    }
}
