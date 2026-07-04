#if NET
namespace MssqlIntelliSense.Core.Cache;

/// <summary>
/// Factory tạo <see cref="MssqlIntelliSenseDbContext"/> mà không cần DI container.
/// Phù hợp với môi trường SSMS host (net472 / netstandard2.0).
/// </summary>
public static class MssqlIntelliSenseDbContextFactory
{
    private static volatile bool _initialized = false;
    private static readonly object _lock = new object();

    /// <summary>
    /// Tạo một instance <see cref="MssqlIntelliSenseDbContext"/> mới với connection string từ config.
    /// Caller có trách nhiệm dispose context sau khi dùng.
    /// </summary>
    public static MssqlIntelliSenseDbContext Create()
    {
        var cs = MssqlIntelliSenseConfig.GetDbConnectionString();
        var ctx = new MssqlIntelliSenseDbContext(cs);

        // Chỉ khởi tạo schema một lần duy nhất trong suốt vòng đời app
        if (!_initialized)
        {
            lock (_lock)
            {
                if (!_initialized)
                {
                    ctx.EnsureInitialized();
                    _initialized = true;
                }
            }
        }

        return ctx;
    }

    /// <summary>
    /// Reset trạng thái initialized (dùng cho unit test).
    /// </summary>
    internal static void Reset() => _initialized = false;
}
#endif
