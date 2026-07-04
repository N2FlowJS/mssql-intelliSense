using System;
using System.Linq;
using MssqlIntelliSense.Core.Cache;

namespace MssqlIntelliSense.Core.Metadata;

public static class MssqlIntelliSenseCacheWriter
{
    /// <summary>
    /// Đảm bảo database được khởi tạo (bảng tạo nếu chưa có, WAL bật).
    /// </summary>
    public static void InitializeDatabase()
    {
#if NET
        using var ctx = MssqlIntelliSenseDbContextFactory.Create();
        _ = ctx; // EnsureInitialized đã chạy trong factory lần đầu
#else
        MssqlIntelliSenseCacheAdoNet.InitializeDatabase();
#endif
    }

    /// <summary>
    /// Đăng ký hoặc cập nhật một connection. Trả về ID của connection.
    /// Normalizes connection string by removing Initial Catalog.
    /// </summary>
    public static int RegisterConnection(string connectionString, string name)
    {
        var normalizedConnectionString = NormalizeServerConnectionString(connectionString);

#if NET
        using var ctx = MssqlIntelliSenseDbContextFactory.Create();

        var existing = ctx.Connections
            .AsEnumerable()
            .FirstOrDefault(c => NormalizeServerConnectionString(c.ConnectionString)
                .Equals(normalizedConnectionString, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Name = name;
            existing.ConnectionString = normalizedConnectionString;
            existing.LastSeenAt = DateTimeOffset.UtcNow;
            existing.IsActive = true;
            ctx.SaveChanges();
            return existing.Id;
        }

        var newConnection = new ConnectionEntity
        {
            Name = name,
            ConnectionString = normalizedConnectionString,
            IsActive = true,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        ctx.Connections.Add(newConnection);
        ctx.SaveChanges();
        return newConnection.Id;
#else
        return MssqlIntelliSenseCacheAdoNet.RegisterConnection(normalizedConnectionString, name);
#endif
    }

    internal static string NormalizeServerConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            builder.Remove("Initial Catalog");
            builder.Remove("Database");
            return builder.ConnectionString;
        }
        catch
        {
            return connectionString;
        }
    }

    /// <summary>
    /// Lưu (upsert) schema cache cho một connection.
    /// </summary>
    public static void SaveSchemaCache(int connectionId, DatabaseMetadata metadata)
    {
#if NET
        using var ctx = MssqlIntelliSenseDbContextFactory.Create();
        try
        {
            var connection = ctx.Connections.Find(connectionId);
            if (connection == null) return;

            connection.SchemaUpdatedAt = DateTimeOffset.UtcNow;

            // Clear old cache databases (which cascade deletes schema objects)
            var oldDbs = ctx.CacheDatabases.Where(d => d.ConnectionId == connectionId).ToList();
            ctx.CacheDatabases.RemoveRange(oldDbs);

            // Clear old linked servers
            var oldLinked = ctx.CacheLinkedServers.Where(l => l.ConnectionId == connectionId).ToList();
            ctx.CacheLinkedServers.RemoveRange(oldLinked);

            // Clear old endpoints
            var oldEndpoints = ctx.CacheEndpoints.Where(ep => ep.ConnectionId == connectionId).ToList();
            ctx.CacheEndpoints.RemoveRange(oldEndpoints);

            ctx.SaveChanges();

            // Distinct list of database names across all schema objects
            var dbNames = metadata.Databases
                .Concat(metadata.Tables.Select(x => x.Database))
                .Concat(metadata.Views.Select(x => x.Database))
                .Concat(metadata.ForeignKeys.Select(x => x.Database))
                .Concat(metadata.Indexes.Select(x => x.Database))
                .Concat(metadata.Procedures.Select(x => x.Database))
                .Concat(metadata.Functions.Select(x => x.Database))
                .Concat(metadata.Triggers.Select(x => x.Database))
                .Concat(metadata.UserTypes.Select(x => x.Database))
                .Concat(metadata.Synonyms.Select(x => x.Database))
                .Where(db => !string.IsNullOrWhiteSpace(db))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var dbName in dbNames)
            {
                var dbEntity = new CacheDatabaseEntity
                {
                    ConnectionId = connectionId,
                    Name = dbName
                };

                // Tables
                dbEntity.Tables = metadata.Tables
                    .Where(t => string.Equals(t.Database, dbName, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new CacheTableEntity
                    {
                        Schema = t.Schema,
                        Name = t.Name,
                        PkColumns = t.PrimaryKeyColumnsString,
                        Columns = t.Columns.Select(c => new CacheColumnEntity
                        {
                            Name = c.Name,
                            DataType = c.DataType,
                            IsNullable = c.IsNullable,
                            Ordinal = c.Ordinal
                        }).ToList()
                    }).ToList();

                // Views
                dbEntity.Views = metadata.Views
                    .Where(v => string.Equals(v.Database, dbName, StringComparison.OrdinalIgnoreCase))
                    .Select(v => new CacheViewEntity
                    {
                        Schema = v.Schema,
                        Name = v.Name,
                        IsIndexed = v.IsIndexed,
                        Columns = v.Columns.Select(c => new CacheViewColumnEntity
                        {
                            Name = c.Name,
                            DataType = c.DataType,
                            IsNullable = c.IsNullable,
                            Ordinal = c.Ordinal
                        }).ToList()
                    }).ToList();

                // Foreign Keys
                dbEntity.ForeignKeys = metadata.ForeignKeys
                    .Where(fk => string.Equals(fk.Database, dbName, StringComparison.OrdinalIgnoreCase))
                    .Select(fk => new CacheForeignKeyEntity
                    {
                        Name = fk.Name,
                        FromSchema = fk.FromSchema,
                        FromTable = fk.FromTable,
                        FromColumn = fk.FromColumn,
                        ToSchema = fk.ToSchema,
                        ToTable = fk.ToTable,
                        ToColumn = fk.ToColumn,
                        Ordinal = fk.Ordinal
                    }).ToList();

                // Indexes
                dbEntity.Indexes = metadata.Indexes
                    .Where(i => string.Equals(i.Database, dbName, StringComparison.OrdinalIgnoreCase))
                    .Select(i => new CacheIndexEntity
                    {
                        Schema = i.Schema,
                        TableName = i.Table,
                        Name = i.Name,
                        IsUnique = i.IsUnique,
                        Columns = i.Columns.Select((c, idx) => new CacheIndexColumnEntity
                        {
                            ColumnName = c,
                            Ordinal = idx + 1
                        }).ToList()
                    }).ToList();

                // Procedures
                dbEntity.Procedures = metadata.Procedures
                    .Where(p => string.Equals(p.Database, dbName, StringComparison.OrdinalIgnoreCase))
                    .Select(p => new CacheProcedureEntity
                    {
                        Schema = p.Schema,
                        Name = p.Name,
                        ObjectType = p.ObjectType,
                        Parameters = p.Parameters.Select(pp => new CacheProcedureParamEntity
                        {
                            Name = pp.Name,
                            DataType = pp.DataType,
                            IsOutput = pp.IsOutput,
                            Ordinal = pp.Ordinal
                        }).ToList()
                    }).ToList();

                // Functions
                dbEntity.Functions = metadata.Functions
                    .Where(f => string.Equals(f.Database, dbName, StringComparison.OrdinalIgnoreCase))
                    .Select(f => new CacheFunctionEntity
                    {
                        Schema = f.Schema,
                        Name = f.Name,
                        FnType = f.FunctionType,
                        ReturnType = f.ReturnType,
                        Parameters = f.Parameters.Select(p => new CacheFunctionParamEntity
                        {
                            Name = p.Name,
                            DataType = p.DataType,
                            IsOutput = p.IsOutput,
                            Ordinal = p.Ordinal
                        }).ToList()
                    }).ToList();

                // Triggers
                dbEntity.Triggers = metadata.Triggers
                    .Where(t => string.Equals(t.Database, dbName, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new CacheTriggerEntity
                    {
                        Schema = t.Schema,
                        Name = t.Name,
                        TableSchema = t.TableSchema,
                        TableName = t.TableName,
                        TriggerType = t.TriggerType,
                        IsEnabled = t.IsEnabled,
                        Events = t.Events
                    }).ToList();

                // UserTypes
                dbEntity.UserTypes = metadata.UserTypes
                    .Where(u => string.Equals(u.Database, dbName, StringComparison.OrdinalIgnoreCase))
                    .Select(u => new CacheUserTypeEntity
                    {
                        Schema = u.Schema,
                        Name = u.Name,
                        BaseType = u.BaseType,
                        IsNullable = u.IsNullable,
                        IsTableType = u.IsTableType,
                        Columns = u.Columns.Select(c => new CacheUdtColumnEntity
                        {
                            Name = c.Name,
                            DataType = c.DataType,
                            IsNullable = c.IsNullable,
                            Ordinal = c.Ordinal
                        }).ToList()
                    }).ToList();

                // Synonyms
                dbEntity.Synonyms = metadata.Synonyms
                    .Where(s => string.Equals(s.Database, dbName, StringComparison.OrdinalIgnoreCase))
                    .Select(s => new CacheSynonymEntity
                    {
                        Schema = s.Schema,
                        Name = s.Name,
                        TargetObject = s.TargetObject
                    }).ToList();

                // Users
                dbEntity.Users = metadata.Users
                    .Where(u => string.Equals(u.Database, dbName, StringComparison.OrdinalIgnoreCase))
                    .Select(u => new CacheUserEntity
                    {
                        Name = u.Name,
                        Type = u.Type,
                        DefaultSchema = u.DefaultSchema,
                        CreateDate = u.CreateDate
                    }).ToList();

                ctx.CacheDatabases.Add(dbEntity);
                ctx.SaveChanges();
            }

            // Linked Servers (instance-level)
            var linkedServers = metadata.LinkedServers
                .Select(ls => new CacheLinkedServerEntity
                {
                    ConnectionId = connectionId,
                    Name = ls.Name,
                    DataSource = ls.DataSource
                }).ToList();
            ctx.CacheLinkedServers.AddRange(linkedServers);

            // Endpoints (instance-level Server Objects)
            var endpoints = metadata.Endpoints
                .Select(ep => new CacheEndpointEntity
                {
                    ConnectionId = connectionId,
                    Name = ep.Name,
                    Type = ep.Type,
                    Protocol = ep.Protocol,
                    State = ep.State,
                    Port = ep.Port
                }).ToList();
            ctx.CacheEndpoints.AddRange(endpoints);

            ctx.SaveChanges();
        }
        catch
        {
            throw;
        }
#else
        MssqlIntelliSenseCacheAdoNet.SaveSchemaCache(connectionId, metadata);
#endif
    }

    /// <summary>
    /// Lấy thời điểm cập nhật schema gần nhất của connection.
    /// </summary>
    public static DateTimeOffset? GetSchemaUpdatedAt(int connectionId)
    {
#if NET
        using var ctx = MssqlIntelliSenseDbContextFactory.Create();
        return ctx.Connections
            .Where(c => c.Id == connectionId)
            .Select(c => c.SchemaUpdatedAt)
            .FirstOrDefault();
#else
        return MssqlIntelliSenseCacheAdoNet.GetSchemaUpdatedAt(connectionId);
#endif
    }

    /// <summary>
    /// Xóa connection (cascade xóa luôn schema cache liên quan).
    /// </summary>
    public static void DeleteConnection(int connectionId, IProgress<string>? progress = null)
    {
#if NET
        using var ctx = MssqlIntelliSenseDbContextFactory.Create();
        progress?.Report("Đang xóa dữ liệu schema cache...");
        var connection = ctx.Connections.Find(connectionId);
        if (connection != null)
        {
            ctx.Connections.Remove(connection);
            ctx.SaveChanges();
        }
        progress?.Report("Đã xóa hoàn tất.");
#else
        MssqlIntelliSenseCacheAdoNet.DeleteConnection(connectionId, progress);
#endif
    }
}
