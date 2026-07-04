#if NET
using System;
using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Cache;

/// <summary>EF Core entity for the <c>connections</c> table — đại diện SQL Server instance.</summary>
public class ConnectionEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string ConnectionString { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastSeenAt { get; set; }
    /// <summary>Timestamp khi schema được scan thành công lần cuối.</summary>
    public DateTimeOffset? SchemaUpdatedAt { get; set; }

    // ── Instance-level navigation ─────────────────────────────────────────
    /// <summary>Danh sách databases trong SQL Server instance này.</summary>
    public List<CacheDatabaseEntity>     Databases     { get; set; } = new();
    /// <summary>Linked servers thuộc instance-level, không phải database-level.</summary>
    public List<CacheLinkedServerEntity> LinkedServers { get; set; } = new();
    /// <summary>Endpoints thuộc Server Objects của SQL Server instance.</summary>
    public List<CacheEndpointEntity> Endpoints { get; set; } = new();
}
#endif
