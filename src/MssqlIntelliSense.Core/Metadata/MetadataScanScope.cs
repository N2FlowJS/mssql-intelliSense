using System;

namespace MssqlIntelliSense.Core.Metadata;

[Flags]
public enum MetadataScanScope
{
    None = 0,
    DatabaseList = 1,
    Tables = 2,
    Relations = 4,
    Indexes = 8,
    Programmability = 16,
    Security = 32,
    Endpoints = 64,
    LinkedServers = 128,
    DatabaseObjects = DatabaseList | Tables | Relations | Indexes | Programmability | Security,
    ServerObjects = Endpoints | LinkedServers,
    All = DatabaseObjects | ServerObjects
}
