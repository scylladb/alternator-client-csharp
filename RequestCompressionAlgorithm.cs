// <copyright file="RequestCompressionAlgorithm.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public enum RequestCompressionAlgorithm
    {
        None = 0,
        Gzip = 1,

#pragma warning disable SA1300, IDE1006
        NONE = None,
        GZIP = Gzip,
#pragma warning restore SA1300, IDE1006
    }
}
