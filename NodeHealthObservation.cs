// <copyright file="NodeHealthObservation.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator
{
    public enum NodeHealthObservation
    {
        Success,
        ServerError,
        RequestTimeout,
        ConnectionFailure,
    }
}
