// <copyright file="RequestCompressionAlgorithmExtensions.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public static class RequestCompressionAlgorithmExtensions
    {
        public static bool IsEnabled(this RequestCompressionAlgorithm algorithm)
        {
            return algorithm != RequestCompressionAlgorithm.None;
        }

#pragma warning disable SA1300, IDE1006
        public static bool isEnabled(this RequestCompressionAlgorithm algorithm)
        {
            return algorithm.IsEnabled();
        }
#pragma warning restore SA1300, IDE1006
    }
}
