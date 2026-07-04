#if NET
namespace MssqlIntelliSense.Core.Cache;

/// <summary>Trigger thuộc một database cụ thể.</summary>
public class CacheTriggerEntity
{
    public int Id { get; set; }
    public int DatabaseId { get; set; }
    public string Schema { get; set; } = "dbo";
    public string Name { get; set; } = string.Empty;
    public string TableSchema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    /// <summary>TR = DML, TA = CLR DML, E = DDL.</summary>
    public string TriggerType { get; set; } = "TR";
    public bool IsEnabled { get; set; } = true;
    public string Events { get; set; } = string.Empty;

    public CacheDatabaseEntity Database { get; set; } = null!;
}
#endif
