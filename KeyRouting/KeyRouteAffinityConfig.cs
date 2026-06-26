// <copyright file="KeyRouteAffinityConfig.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator.KeyRouting
{
    using System.Collections.ObjectModel;

    public sealed class KeyRouteAffinityConfig
    {
        private KeyRouteAffinityConfig(KeyRouteAffinity type, IDictionary<string, string> pkInfoPerTable)
        {
            this.Type = type;
            this.PkInfoPerTable = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(pkInfoPerTable, StringComparer.Ordinal));
        }

        public KeyRouteAffinity Type { get; }

        public IReadOnlyDictionary<string, string> PkInfoPerTable { get; }

        public bool IsEnabled => this.Type != KeyRouteAffinity.None;

        public static KeyRouteAffinityConfigBuilder Builder()
        {
            return new KeyRouteAffinityConfigBuilder();
        }

        public static KeyRouteAffinityConfig Of(KeyRouteAffinity? type)
        {
            return new KeyRouteAffinityConfig(type ?? KeyRouteAffinity.None, new Dictionary<string, string>());
        }

#pragma warning disable SA1300, IDE1006
        public static KeyRouteAffinityConfigBuilder builder()
        {
            return Builder();
        }

        public static KeyRouteAffinityConfig of(KeyRouteAffinity? type)
        {
            return Of(type);
        }

        public KeyRouteAffinity getType()
        {
            return this.Type;
        }

        public IReadOnlyDictionary<string, string> getPkInfoPerTable()
        {
            return this.PkInfoPerTable;
        }

        public bool isEnabled()
        {
            return this.IsEnabled;
        }
#pragma warning restore SA1300, IDE1006

        internal static KeyRouteAffinityConfig Create(KeyRouteAffinity type, IDictionary<string, string> pkInfoPerTable)
        {
            return new KeyRouteAffinityConfig(type, pkInfoPerTable);
        }
    }
}
