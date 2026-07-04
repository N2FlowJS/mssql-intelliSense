#if NET
namespace MssqlIntelliSense.Core.Cache;

public class CacheLinkedServerEntity
{
    public int Id { get; set; }
    public int ConnectionId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Địa chỉ server thực của linked server (data_source từ sys.servers).</summary>
    public string DataSource { get; set; } = string.Empty;

    public ConnectionEntity Connection { get; set; } = null!;
}
#endif
