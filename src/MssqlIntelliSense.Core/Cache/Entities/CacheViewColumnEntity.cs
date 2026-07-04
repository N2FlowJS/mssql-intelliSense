#if NET
namespace MssqlIntelliSense.Core.Cache;

public class CacheViewColumnEntity
{
    public int Id { get; set; }
    public int ViewId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public int Ordinal { get; set; }

    public CacheViewEntity View { get; set; } = null!;
}
#endif
