// <copyright file="ResponseCompressionAlgorithm.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public enum ResponseCompressionAlgorithm
    {
        Gzip = 0,
        Deflate = 1,

#pragma warning disable SA1300, IDE1006
        GZIP = Gzip,
        DEFLATE = Deflate,
#pragma warning restore SA1300, IDE1006
    }
}
