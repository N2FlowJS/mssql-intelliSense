#if NET
namespace MssqlIntelliSense.Core.Cache;

public class CacheEndpointEntity
{
    public int Id { get; set; }
    public int ConnectionId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int Port { get; set; }

    public ConnectionEntity Connection { get; set; } = null!;
}
#endif
