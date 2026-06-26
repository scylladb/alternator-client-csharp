// <copyright file="HttpClientType.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

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
