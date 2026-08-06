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
    public enum HttpClientType
    {
        Auto = 0,
        SystemNetHttp = 1,
        Apache = 2,
        Crt = 3,
        Netty = 4,

#pragma warning disable SA1300, IDE1006
        AUTO = Auto,
        SYSTEM_NET_HTTP = SystemNetHttp,
        APACHE = Apache,
        CRT = Crt,
        NETTY = Netty,
#pragma warning restore SA1300, IDE1006
    }
}
