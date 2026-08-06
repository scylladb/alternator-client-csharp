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
    using System.Text;
    using Amazon.DynamoDBv2.Model;

    public static class AttributeValueHasher
    {
        private const byte TypeString = 0x01;
        private const byte TypeNumber = 0x02;
        private const byte TypeBinary = 0x03;

        public static long Hash(AttributeValue? value)
        {
            if (value == null)
            {
                return 0;
            }

            return MurmurHash3.Hash(ToBytes(value));
        }

#pragma warning disable SA1300, IDE1006
        public static long hash(AttributeValue? value)
        {
            return Hash(value);
        }
#pragma warning restore SA1300, IDE1006

        private static byte[] PrependTypePrefix(byte typePrefix, byte[] value)
        {
            var bytes = new byte[value.Length + 1];
            bytes[0] = typePrefix;
            Buffer.BlockCopy(value, 0, bytes, 1, value.Length);
            return bytes;
        }

        private static byte[] ToBytes(AttributeValue value)
        {
            if (value.S != null)
            {
                return PrependTypePrefix(TypeString, Encoding.UTF8.GetBytes(value.S));
            }

            if (value.N != null)
            {
                return PrependTypePrefix(TypeNumber, Encoding.UTF8.GetBytes(value.N));
            }

            if (value.B != null)
            {
                return PrependTypePrefix(TypeBinary, value.B.ToArray());
            }

            throw new ArgumentException(
                "Unsupported AttributeValue type. Only S (String), N (Number), and B (Binary) are supported as partition key types in Alternator.",
                nameof(value));
        }
    }
}
