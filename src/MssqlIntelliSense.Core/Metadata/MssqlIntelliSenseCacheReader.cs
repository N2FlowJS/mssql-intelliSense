using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
#if NET
using Microsoft.EntityFrameworkCore;
#endif
using MssqlIntelliSense.Core.Cache;

namespace MssqlIntelliSense.Core.Metadata;

public sealed record ConnectionInfo(int Id, string Name, string ConnectionString, bool IsActive, DateTimeOffset? LastSeenAt, DateTimeOffset? SchemaUpdatedAt);

public static class MssqlIntelliSenseCacheReader
{
    /// <summary>
    /// Lấy danh sách tất cả connection đã đăng ký, kèm thời điểm cập nhật schema.
    /// </summary>
    public static IReadOnlyList<ConnectionInfo> GetConnections()
    {
        try
        {
#if NET
            using var ctx = MssqlIntelliSenseDbContextFactory.Create();

            return ctx.Connections
                .OrderByDescending(c => c.LastSeenAt)
                .ThenBy(c => c.Name)
                .Select(c => new ConnectionInfo(
                    c.Id,
                    c.Name,
                    c.ConnectionString,
                    c.IsActive,
                    c.LastSeenAt,
                    c.SchemaUpdatedAt))
                .ToList();
#else
            return MssqlIntelliSenseCacheAdoNet.GetConnections();
#endif
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cache Read Error] {ex.Message}");
            return Array.Empty<ConnectionInfo>();
        }
    }

    /// <summary>
    /// Lấy chi tiết schema (deserialized + raw JSON + thời điểm cập nhật) theo connectionId.
    /// </summary>
    public static (DatabaseMetadata Metadata, string RawJson, DateTimeOffset? SchemaUpdatedAt) GetSchemaDetails(int connectionId)
    {
        try
        {
#if NET
            using var ctx = MssqlIntelliSenseDbContextFactory.Create();

            var conn = ctx.Connections
                .Include(c => c.Databases).ThenInclude(d => d.Tables).ThenInclude(t => t.Columns)
                .Include(c => c.Databases).ThenInclude(d => d.Views).ThenInclude(v => v.Columns)
                .Include(c => c.Databases).ThenInclude(d => d.ForeignKeys)
                .Include(c => c.Databases).ThenInclude(d => d.Indexes).ThenInclude(i => i.Columns)
                .Include(c => c.Databases).ThenInclude(d => d.Procedures).ThenInclude(p => p.Parameters)
                .Include(c => c.Databases).ThenInclude(d => d.Functions).ThenInclude(f => f.Parameters)
                .Include(c => c.Databases).ThenInclude(d => d.Triggers)
                .Include(c => c.Databases).ThenInclude(d => d.UserTypes).ThenInclude(u => u.Columns)
                .Include(c => c.Databases).ThenInclude(d => d.Synonyms)
                .Include(c => c.Databases).ThenInclude(d => d.Users)
                .Include(c => c.LinkedServers)
                .Include(c => c.Endpoints)
                .FirstOrDefault(c => c.Id == connectionId);

            if (conn == null)
                return (DatabaseMetadata.Empty, string.Empty, null);

            var metadata = BuildMetadataFromConnection(conn);
            var rawJson = JsonSerializer.Serialize(metadata);
            return (metadata, rawJson, conn.SchemaUpdatedAt);
#else
            return MssqlIntelliSenseCacheAdoNet.GetSchemaDetails(connectionId);
#endif
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cache Read Error] {ex.Message}");
            return (DatabaseMetadata.Empty, string.Empty, null);
        }
    }

    /// <summary>
    /// Lấy metadata schema theo connection string (case-insensitive).
    /// </summary>
    public static DatabaseMetadata GetMetadataByConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return DatabaseMetadata.Empty;

        var normalizedConnectionString = MssqlIntelliSenseCacheWriter.NormalizeServerConnectionString(connectionString);

        try
        {
#if NET
            using var ctx = MssqlIntelliSenseDbContextFactory.Create();

            var conn = ctx.Connections
                .Include(c => c.Databases).ThenInclude(d => d.Tables).ThenInclude(t => t.Columns)
                .Include(c => c.Databases).ThenInclude(d => d.Views).ThenInclude(v => v.Columns)
                .Include(c => c.Databases).ThenInclude(d => d.ForeignKeys)
                .Include(c => c.Databases).ThenInclude(d => d.Indexes).ThenInclude(i => i.Columns)
                .Include(c => c.Databases).ThenInclude(d => d.Procedures).ThenInclude(p => p.Parameters)
                .Include(c => c.Databases).ThenInclude(d => d.Functions).ThenInclude(f => f.Parameters)
                .Include(c => c.Databases).ThenInclude(d => d.Triggers)
                .Include(c => c.Databases).ThenInclude(d => d.UserTypes).ThenInclude(u => u.Columns)
                .Include(c => c.Databases).ThenInclude(d => d.Synonyms)
                .Include(c => c.Databases).ThenInclude(d => d.Users)
                .Include(c => c.LinkedServers)
                .Include(c => c.Endpoints)
                .AsEnumerable()
                .FirstOrDefault(c => MssqlIntelliSenseCacheWriter.NormalizeServerConnectionString(c.ConnectionString)
                    .Equals(normalizedConnectionString, StringComparison.OrdinalIgnoreCase));

            if (conn == null)
                return DatabaseMetadata.Empty;

            return BuildMetadataFromConnection(conn);
#else
            return MssqlIntelliSenseCacheAdoNet.GetMetadataByConnectionString(normalizedConnectionString);
#endif
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Cache Read Error] {ex.Message}");
            return DatabaseMetadata.Empty;
        }
    }

    /// <summary>
    /// Lấy metadata schema theo connection string và ưu tiên filter theo database đang active.
    /// Nếu activeDatabase được cung cấp, chỉ trả về metadata của database đó;
    /// nếu không tìm thấy, fallback về toàn bộ metadata.
    /// </summary>
    public static DatabaseMetadata GetMetadataByConnectionStringAndDatabase(
        string connectionString,
        string? activeDatabase)
    {
        var full = GetMetadataByConnectionString(connectionString);
        if (full == DatabaseMetadata.Empty) return full;
        if (string.IsNullOrWhiteSpace(activeDatabase)) return full;
        return FilterByDatabase(full, activeDatabase!);
    }

    /// <summary>
    /// Lọc metadata chỉ còn các object thuộc database chỉ định (so sánh không phân biệt hoa thường).
    /// Giữ lại toàn bộ nếu không có object nào thuộc database đó (tức là data chưa được gán Database).
    /// </summary>
    public static DatabaseMetadata FilterByDatabase(DatabaseMetadata full, string databaseName)
    {
        bool hasDbData = full.Tables.Any(t => !string.IsNullOrEmpty(t.Database));
        if (!hasDbData) return full; // schema chưa có thông tin database, fallback toàn bộ

        var tables     = full.Tables    .Where(t => t.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var fks        = full.ForeignKeys.Where(f => f.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var indexes    = full.Indexes   .Where(i => i.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var procedures = full.Procedures.Where(p => p.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var views      = full.Views     .Where(v => v.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var functions  = full.Functions .Where(f => f.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var triggers   = full.Triggers  .Where(t => t.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var userTypes  = full.UserTypes .Where(u => u.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var synonyms   = full.Synonyms  .Where(s => s.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
        var users      = full.Users     .Where(u => u.Database.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();

        // Nếu database đó hoàn toàn trống, có thể data chưa đánh tag → fallback
        if (tables.Length == 0 && views.Length == 0 && procedures.Length == 0)
            return full;

        return new DatabaseMetadata(tables, fks, indexes, new[] { databaseName }, full.LinkedServers)
        {
            Procedures = procedures,
            Views      = views,
            Functions  = functions,
            Triggers   = triggers,
            UserTypes  = userTypes,
            Synonyms   = synonyms,
            Users      = users,
            Endpoints  = full.Endpoints
        };
    }

#if NET
    private static DatabaseMetadata BuildMetadataFromConnection(ConnectionEntity conn)
    {
        var databases = conn.Databases.Select(d => d.Name).ToList();
        var linkedServers = conn.LinkedServers
            .Select(l => new LinkedServerInfo(l.Name, l.DataSource))
            .ToList();
        var endpoints = conn.Endpoints
            .Select(ep => new EndpointInfo(ep.Name, ep.Type, ep.Protocol, ep.State, ep.Port))
            .ToList();

        var tables = conn.Databases.SelectMany(d => d.Tables.Select(t => new TableMetadata(
            t.Schema,
            t.Name,
            t.Columns.OrderBy(c => c.Ordinal).Select(c => new ColumnMetadata(c.Name, c.DataType, c.IsNullable, c.Ordinal)).ToList(),
            t.PkColumns.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        ) { Database = d.Name })).ToList();

        var foreignKeys = conn.Databases.SelectMany(d => d.ForeignKeys.Select(fk => new ForeignKeyMetadata(
            fk.Name,
            fk.FromSchema,
            fk.FromTable,
            fk.FromColumn,
            fk.ToSchema,
            fk.ToTable,
            fk.ToColumn,
            fk.Ordinal
        ) { Database = d.Name })).ToList();

        var indexes = conn.Databases.SelectMany(d => d.Indexes.Select(i => new IndexMetadata(
            i.Schema,
            i.TableName,
            i.Name,
            i.IsUnique,
            i.Columns.OrderBy(c => c.Ordinal).Select(c => c.ColumnName).ToList()
        ) { Database = d.Name })).ToList();

        var procedures = conn.Databases.SelectMany(d => d.Procedures.Select(p => new ProcedureMetadata(p.Schema, p.Name)
        {
            Database = d.Name,
            ObjectType = p.ObjectType,
            Parameters = p.Parameters.OrderBy(pp => pp.Ordinal).Select(pp => new FunctionParameterMetadata(pp.Name, pp.DataType, pp.IsOutput, pp.Ordinal)).ToList()
        })).ToList();

        var views = conn.Databases.SelectMany(d => d.Views.Select(v => new ViewMetadata(
            v.Schema,
            v.Name,
            v.Columns.OrderBy(c => c.Ordinal).Select(c => new ColumnMetadata(c.Name, c.DataType, c.IsNullable, c.Ordinal)).ToList()
        ) { Database = d.Name, IsIndexed = v.IsIndexed })).ToList();

        var functions = conn.Databases.SelectMany(d => d.Functions.Select(f => new FunctionMetadata(f.Schema, f.Name)
        {
            Database = d.Name,
            FunctionType = f.FnType,
            ReturnType = f.ReturnType,
            Parameters = f.Parameters.OrderBy(p => p.Ordinal).Select(p => new FunctionParameterMetadata(p.Name, p.DataType, p.IsOutput, p.Ordinal)).ToList()
        })).ToList();

        var triggers = conn.Databases.SelectMany(d => d.Triggers.Select(t => new TriggerMetadata(t.Schema, t.Name, t.TableSchema, t.TableName)
        {
            Database = d.Name,
            TriggerType = t.TriggerType,
            IsEnabled = t.IsEnabled,
            Events = t.Events
        })).ToList();

        var userTypes = conn.Databases.SelectMany(d => d.UserTypes.Select(u => new UserTypeMetadata(u.Schema, u.Name)
        {
            Database = d.Name,
            BaseType = u.BaseType,
            IsNullable = u.IsNullable,
            IsTableType = u.IsTableType,
            Columns = u.Columns.OrderBy(c => c.Ordinal).Select(c => new ColumnMetadata(c.Name, c.DataType, c.IsNullable, c.Ordinal)).ToList()
        })).ToList();

        var synonyms = conn.Databases.SelectMany(d => d.Synonyms.Select(s => new SynonymMetadata(s.Schema, s.Name, s.TargetObject)
        {
            Database = d.Name
        })).ToList();

        var users = conn.Databases.SelectMany(d => d.Users.Select(u => new UserMetadata(u.Name, u.Type, u.DefaultSchema, u.CreateDate)
        {
            Database = d.Name
        })).ToList();

        var metadata = new DatabaseMetadata(tables, foreignKeys, indexes, databases, linkedServers)
        {
            Procedures = procedures,
            Views = views,
            Functions = functions,
            Triggers = triggers,
            UserTypes = userTypes,
            Synonyms = synonyms,
            Users = users,
            Endpoints = endpoints
        };

        return metadata;
    }
#endif
}
