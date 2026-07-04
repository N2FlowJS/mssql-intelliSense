#if NET
using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Cache;

/// <summary>Function thuộc một database cụ thể.</summary>
public class CacheFunctionEntity
{
    public int Id { get; set; }
    public int DatabaseId { get; set; }
    public string Schema { get; set; } = "dbo";
    public string Name { get; set; } = string.Empty;
    /// <summary>FN = Scalar, TF = Multi-stmt TVF, IF = Inline TVF, AF = Aggregate, FS/FT = CLR.</summary>
    public string FnType { get; set; } = "FN";
    public string ReturnType { get; set; } = string.Empty;

    public CacheDatabaseEntity Database { get; set; } = null!;
    public List<CacheFunctionParamEntity> Parameters { get; set; } = new();
}
#endif
