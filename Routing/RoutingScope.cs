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

namespace ScyllaDB.Alternator.Routing
{
#pragma warning disable SA1302
    public interface RoutingScope
    {
        string Name { get; }

        string Description { get; }

        RoutingScope? Fallback { get; }

        string LocalNodesQuery { get; }

#pragma warning disable SA1300, IDE1006
        string getName()
        {
            return this.Name;
        }

        string getDescription()
        {
            return this.Description;
        }

        RoutingScope? getFallback()
        {
            return this.Fallback;
        }

        string getLocalNodesQuery()
        {
            return this.LocalNodesQuery;
        }
#pragma warning restore SA1300, IDE1006
    }
#pragma warning restore SA1302
}
