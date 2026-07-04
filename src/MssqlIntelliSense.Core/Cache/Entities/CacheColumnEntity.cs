#if NET
namespace MssqlIntelliSense.Core.Cache;

public class CacheColumnEntity
{
    public int Id { get; set; }
    public int TableId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public int Ordinal { get; set; }

    public CacheTableEntity Table { get; set; } = null!;
}
#endif
