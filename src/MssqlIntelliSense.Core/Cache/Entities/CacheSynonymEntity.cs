#if NET
namespace MssqlIntelliSense.Core.Cache;

/// <summary>Synonym thuộc một database cụ thể.</summary>
public class CacheSynonymEntity
{
    public int Id { get; set; }
    public int DatabaseId { get; set; }
    public string Schema { get; set; } = "dbo";
    public string Name { get; set; } = string.Empty;
    public string TargetObject { get; set; } = string.Empty;

    public CacheDatabaseEntity Database { get; set; } = null!;
}
#endif
