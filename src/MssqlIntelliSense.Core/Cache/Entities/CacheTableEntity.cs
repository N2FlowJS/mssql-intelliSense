#if NET
using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Cache;

/// <summary>Table thuộc một database cụ thể trong một connection.</summary>
public class CacheTableEntity
{
    public int Id { get; set; }
    public int DatabaseId { get; set; }
    public string Schema { get; set; } = "dbo";
    public string Name { get; set; } = string.Empty;
    /// <summary>CSV of primary key column names, e.g. "Id" or "TenantId,UserId".</summary>
    public string PkColumns { get; set; } = string.Empty;

    public CacheDatabaseEntity Database { get; set; } = null!;
    public List<CacheColumnEntity> Columns { get; set; } = new();
}
#endif
