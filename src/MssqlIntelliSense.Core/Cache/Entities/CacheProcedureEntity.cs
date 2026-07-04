#if NET
namespace MssqlIntelliSense.Core.Cache;

/// <summary>Stored procedure thuộc một database cụ thể.</summary>
public class CacheProcedureEntity
{
    public int Id { get; set; }
    public int DatabaseId { get; set; }
    public string Schema { get; set; } = "dbo";
    public string Name { get; set; } = string.Empty;
    /// <summary>P = Stored Procedure.</summary>
    public string ObjectType { get; set; } = "P";

    public CacheDatabaseEntity Database { get; set; } = null!;
    public List<CacheProcedureParamEntity> Parameters { get; set; } = new();
}
#endif
