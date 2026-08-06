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
