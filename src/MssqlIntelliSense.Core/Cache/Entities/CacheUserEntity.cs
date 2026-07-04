#if NET
namespace MssqlIntelliSense.Core.Cache;

/// <summary>Database user / principal (sys.database_principals).</summary>
public class CacheUserEntity
{
    public int Id { get; set; }
    public int DatabaseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string DefaultSchema { get; set; } = string.Empty;
    public string CreateDate { get; set; } = string.Empty;

    public CacheDatabaseEntity Database { get; set; } = null!;
}
#endif
