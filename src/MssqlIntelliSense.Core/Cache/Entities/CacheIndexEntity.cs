#if NET
using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Cache;

/// <summary>Index thuộc một database cụ thể.</summary>
public class CacheIndexEntity
{
    public int Id { get; set; }
    public int DatabaseId { get; set; }
    public string Schema { get; set; } = "dbo";
    public string TableName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsUnique { get; set; }

    public CacheDatabaseEntity Database { get; set; } = null!;
    public List<CacheIndexColumnEntity> Columns { get; set; } = new();
}
#endif
