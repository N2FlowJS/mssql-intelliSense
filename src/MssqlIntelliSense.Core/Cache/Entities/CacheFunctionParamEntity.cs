#if NET
namespace MssqlIntelliSense.Core.Cache;

public class CacheFunctionParamEntity
{
    public int Id { get; set; }
    public int FunctionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsOutput { get; set; }
    public int Ordinal { get; set; }

    public CacheFunctionEntity Function { get; set; } = null!;
}
#endif
