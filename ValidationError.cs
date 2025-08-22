// <copyright file="ValidationError.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    using System;

    public class ValidationError : Exception
    {
        public ValidationError(string message)
            : base(message)
        {
        }

        public ValidationError(string message, Exception cause)
            : base(message, cause)
        {
        }
    }
}
