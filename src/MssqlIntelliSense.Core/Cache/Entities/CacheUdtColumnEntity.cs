#if NET
namespace MssqlIntelliSense.Core.Cache;

public class CacheUdtColumnEntity
{
    public int Id { get; set; }
    public int UserTypeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public int Ordinal { get; set; }

    public CacheUserTypeEntity UserType { get; set; } = null!;
}
#endif
