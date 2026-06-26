// <copyright file="KeyRouteAffinity.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace ScyllaDB.Alternator.KeyRouting
{
    public enum KeyRouteAffinity
    {
        None = 0,
        Rmw = 1,
        AnyWrite = 2,

#pragma warning disable SA1300, IDE1006
        NONE = None,
        RMW = Rmw,
        ANY_WRITE = AnyWrite,
#pragma warning restore SA1300, IDE1006
    }
}
