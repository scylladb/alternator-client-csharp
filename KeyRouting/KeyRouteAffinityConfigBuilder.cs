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
