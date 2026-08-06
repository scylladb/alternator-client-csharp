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
