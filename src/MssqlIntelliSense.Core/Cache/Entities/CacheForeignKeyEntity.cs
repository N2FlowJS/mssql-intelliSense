#if NET
namespace MssqlIntelliSense.Core.Cache;

/// <summary>Foreign key thuộc một database cụ thể.</summary>
public class CacheForeignKeyEntity
{
    public int Id { get; set; }
    public int DatabaseId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FromSchema { get; set; } = "dbo";
    public string FromTable { get; set; } = string.Empty;
    public string FromColumn { get; set; } = string.Empty;
    public string ToSchema { get; set; } = "dbo";
    public string ToTable { get; set; } = string.Empty;
    public string ToColumn { get; set; } = string.Empty;
    public int Ordinal { get; set; }

    public CacheDatabaseEntity Database { get; set; } = null!;
}
#endif
