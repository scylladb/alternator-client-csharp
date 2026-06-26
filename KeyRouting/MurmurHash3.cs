// <copyright file="MurmurHash3.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator.KeyRouting
{
    public static class MurmurHash3
    {
        private const ulong C1 = 0x87c37b91114253d5UL;
        private const ulong C2 = 0x4cf5ad432745937fUL;
        private const ulong Fmix1 = 0xff51afd7ed558ccdUL;
        private const ulong Fmix2 = 0xc4ceb9fe1a85ec53UL;

        public static long Hash(byte[] data)
        {
            return Hash(data, 0, data.Length);
        }

        public static long Hash(byte[] data, int offset, int length)
        {
            unchecked
            {
                ulong h1 = 0;
                ulong h2 = 0;
                var blocks = length / 16;
                for (var i = 0; i < blocks; i++)
                {
                    var k1 = GetBlock(data, offset + (i * 16));
                    var k2 = GetBlock(data, offset + (i * 16) + 8);

                    k1 *= C1;
                    k1 = RotateLeft(k1, 31);
                    k1 *= C2;
                    h1 ^= k1;

                    h1 = RotateLeft(h1, 27);
                    h1 += h2;
                    h1 = (h1 * 5) + 0x52dce729UL;

                    k2 *= C2;
                    k2 = RotateLeft(k2, 33);
                    k2 *= C1;
                    h2 ^= k2;

                    h2 = RotateLeft(h2, 31);
                    h2 += h1;
                    h2 = (h2 * 5) + 0x38495ab5UL;
                }

                var tailOffset = offset + (blocks * 16);
                ulong tailK1 = 0;
                ulong tailK2 = 0;
                switch (length & 15)
                {
                    case 15:
                        tailK2 ^= (ulong)data[tailOffset + 14] << 48;
                        goto case 14;
                    case 14:
                        tailK2 ^= (ulong)data[tailOffset + 13] << 40;
                        goto case 13;
                    case 13:
                        tailK2 ^= (ulong)data[tailOffset + 12] << 32;
                        goto case 12;
                    case 12:
                        tailK2 ^= (ulong)data[tailOffset + 11] << 24;
                        goto case 11;
                    case 11:
                        tailK2 ^= (ulong)data[tailOffset + 10] << 16;
                        goto case 10;
                    case 10:
                        tailK2 ^= (ulong)data[tailOffset + 9] << 8;
                        goto case 9;
                    case 9:
                        tailK2 ^= data[tailOffset + 8];
                        tailK2 *= C2;
                        tailK2 = RotateLeft(tailK2, 33);
                        tailK2 *= C1;
                        h2 ^= tailK2;
                        goto case 8;
                    case 8:
                        tailK1 ^= (ulong)data[tailOffset + 7] << 56;
                        goto case 7;
                    case 7:
                        tailK1 ^= (ulong)data[tailOffset + 6] << 48;
                        goto case 6;
                    case 6:
                        tailK1 ^= (ulong)data[tailOffset + 5] << 40;
                        goto case 5;
                    case 5:
                        tailK1 ^= (ulong)data[tailOffset + 4] << 32;
                        goto case 4;
                    case 4:
                        tailK1 ^= (ulong)data[tailOffset + 3] << 24;
                        goto case 3;
                    case 3:
                        tailK1 ^= (ulong)data[tailOffset + 2] << 16;
                        goto case 2;
                    case 2:
                        tailK1 ^= (ulong)data[tailOffset + 1] << 8;
                        goto case 1;
                    case 1:
                        tailK1 ^= data[tailOffset];
                        tailK1 *= C1;
                        tailK1 = RotateLeft(tailK1, 31);
                        tailK1 *= C2;
                        h1 ^= tailK1;
                        break;
                }

                h1 ^= (ulong)length;
                h2 ^= (ulong)length;
                h1 += h2;
                h2 += h1;
                h1 = Fmix(h1);
                h2 = Fmix(h2);
                h1 += h2;
                return (long)h1;
            }
        }

#pragma warning disable SA1300, IDE1006
        public static long hash(byte[] data)
        {
            return Hash(data);
        }

        public static long hash(byte[] data, int offset, int length)
        {
            return Hash(data, offset, length);
        }
#pragma warning restore SA1300, IDE1006

        private static ulong Fmix(ulong value)
        {
            value ^= value >> 33;
            value *= Fmix1;
            value ^= value >> 33;
            value *= Fmix2;
            value ^= value >> 33;
            return value;
        }

        private static ulong GetBlock(byte[] data, int offset)
        {
            return data[offset]
                | ((ulong)data[offset + 1] << 8)
                | ((ulong)data[offset + 2] << 16)
                | ((ulong)data[offset + 3] << 24)
                | ((ulong)data[offset + 4] << 32)
                | ((ulong)data[offset + 5] << 40)
                | ((ulong)data[offset + 6] << 48)
                | ((ulong)data[offset + 7] << 56);
        }

        private static ulong RotateLeft(ulong value, int count)
        {
            return (value << count) | (value >> (64 - count));
        }
    }
}
