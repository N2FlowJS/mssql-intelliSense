#if NET
namespace MssqlIntelliSense.Core.Cache;

public class CacheProcedureParamEntity
{
    public int Id { get; set; }
    public int ProcedureId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsOutput { get; set; }
    public int Ordinal { get; set; }

    public CacheProcedureEntity Procedure { get; set; } = null!;
}
#endif
