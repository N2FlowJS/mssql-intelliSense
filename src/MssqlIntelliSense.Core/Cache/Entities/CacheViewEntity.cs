#if NET
using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Cache;

/// <summary>View thuộc một database cụ thể trong một connection.</summary>
public class CacheViewEntity
{
    public int Id { get; set; }
    public int DatabaseId { get; set; }
    public string Schema { get; set; } = "dbo";
    public string Name { get; set; } = string.Empty;
    public bool IsIndexed { get; set; }

    public CacheDatabaseEntity Database { get; set; } = null!;
    public List<CacheViewColumnEntity> Columns { get; set; } = new();
}
#endif
