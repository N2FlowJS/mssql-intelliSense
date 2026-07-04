#if NET
namespace MssqlIntelliSense.Core.Cache;

public class CacheIndexColumnEntity
{
    public int Id { get; set; }
    public int IndexId { get; set; }
    public string ColumnName { get; set; } = string.Empty;
    public int Ordinal { get; set; }

    public CacheIndexEntity Index { get; set; } = null!;
}
#endif
