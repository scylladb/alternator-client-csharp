// <copyright file="KeyRouteAffinityConfigBuilder.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator.KeyRouting
{
    public sealed class KeyRouteAffinityConfigBuilder
    {
        private readonly Dictionary<string, string> pkInfoPerTable = new Dictionary<string, string>(StringComparer.Ordinal);
        private KeyRouteAffinity type = KeyRouteAffinity.None;

        public KeyRouteAffinityConfigBuilder WithType(KeyRouteAffinity? type)
        {
            this.type = type ?? KeyRouteAffinity.None;
            return this;
        }

        public KeyRouteAffinityConfigBuilder WithPkInfo(string tableName, string pkAttributeName)
        {
            if (tableName != null && pkAttributeName != null)
            {
                this.pkInfoPerTable[tableName] = pkAttributeName;
            }

            return this;
        }

        public KeyRouteAffinityConfigBuilder WithPkInfoMap(IDictionary<string, string> pkInfo)
        {
            if (pkInfo != null)
            {
                foreach (var item in pkInfo)
                {
                    this.pkInfoPerTable[item.Key] = item.Value;
                }
            }

            return this;
        }

        public KeyRouteAffinityConfig Build()
        {
            return KeyRouteAffinityConfig.Create(this.type, this.pkInfoPerTable);
        }

#pragma warning disable SA1300, IDE1006
        public KeyRouteAffinityConfigBuilder withType(KeyRouteAffinity? type)
        {
            return this.WithType(type);
        }

        public KeyRouteAffinityConfigBuilder withPkInfo(string tableName, string pkAttributeName)
        {
            return this.WithPkInfo(tableName, pkAttributeName);
        }

        public KeyRouteAffinityConfigBuilder withPkInfoMap(IDictionary<string, string> pkInfo)
        {
            return this.WithPkInfoMap(pkInfo);
        }

        public KeyRouteAffinityConfig build()
        {
            return this.Build();
        }
#pragma warning restore SA1300, IDE1006
    }
}
