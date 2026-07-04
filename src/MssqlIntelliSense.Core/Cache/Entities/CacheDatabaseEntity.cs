#if NET
using System.Collections.Generic;

namespace MssqlIntelliSense.Core.Cache;

/// <summary>
/// Đại diện cho một DATABASE cụ thể trong một SQL Server connection.
/// Là cha của tất cả schema objects (tables, views, procedures, ...).
/// </summary>
public class CacheDatabaseEntity
{
    public int Id { get; set; }
    public int ConnectionId { get; set; }
    public string Name { get; set; } = string.Empty;

    // ── Navigation ────────────────────────────────────────────────────────
    public ConnectionEntity Connection { get; set; } = null!;

    // Schema objects thuộc database này
    public List<CacheTableEntity>       Tables       { get; set; } = new();
    public List<CacheViewEntity>        Views        { get; set; } = new();
    public List<CacheForeignKeyEntity>  ForeignKeys  { get; set; } = new();
    public List<CacheIndexEntity>       Indexes      { get; set; } = new();
    public List<CacheProcedureEntity>   Procedures   { get; set; } = new();
    public List<CacheFunctionEntity>    Functions    { get; set; } = new();
    public List<CacheTriggerEntity>     Triggers     { get; set; } = new();
    public List<CacheUserTypeEntity>    UserTypes    { get; set; } = new();
    public List<CacheSynonymEntity>     Synonyms     { get; set; } = new();
    public List<CacheUserEntity>        Users        { get; set; } = new();
}
#endif
