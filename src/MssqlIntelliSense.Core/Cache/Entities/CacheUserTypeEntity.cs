#if NET
using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Cache;

/// <summary>User-Defined Type thuộc một database cụ thể.</summary>
public class CacheUserTypeEntity
{
    public int Id { get; set; }
    public int DatabaseId { get; set; }
    public string Schema { get; set; } = "dbo";
    public string Name { get; set; } = string.Empty;
    public string BaseType { get; set; } = string.Empty;
    public bool IsNullable { get; set; } = true;
    public bool IsTableType { get; set; }

    public CacheDatabaseEntity Database { get; set; } = null!;
    public List<CacheUdtColumnEntity> Columns { get; set; } = new();
}
#endif
