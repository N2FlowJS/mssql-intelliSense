using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion;

/// <summary>
/// Loại stored procedure CRUD cần sinh.
/// </summary>
public enum CrudOperation
{
    GetAll,
    GetById,
    Insert,
    Update,
    Delete,
    /// <summary>Sinh đầy đủ cả 5 loại GetAll, GetById, Insert, Update, Delete.</summary>
    All
}

/// <summary>
/// Sinh code stored procedure CRUD cho một bảng SQL Server.
/// Hỗ trợ tất cả kiểu cột, PK đơn và composite, nullable params.
/// </summary>
public static class CrudGenerator
{
    /// <summary>
    /// Sinh stored procedure SQL cho bảng chỉ định.
    /// </summary>
    /// <param name="table">Metadata bảng cần tạo CRUD.</param>
    /// <param name="operation">Loại operation cần sinh.</param>
    /// <param name="schemaPrefix">Prefix schema của SP (mặc định cùng với bảng).</param>
    /// <param name="spPrefix">Prefix tên SP (mặc định "usp_").</param>
    /// <returns>Script T-SQL tạo stored procedure.</returns>
    public static string Generate(
        TableMetadata table,
        CrudOperation operation,
        string? schemaPrefix = null,
        string spPrefix = "usp_")
    {
        if (table == null) throw new ArgumentNullException(nameof(table));

        schemaPrefix ??= table.Schema;
        var sb = new StringBuilder();

        if (operation == CrudOperation.All)
        {
            foreach (var op in new[] { CrudOperation.GetAll, CrudOperation.GetById, CrudOperation.Insert, CrudOperation.Update, CrudOperation.Delete })
            {
                sb.AppendLine(GenerateSingle(table, op, schemaPrefix, spPrefix));
                sb.AppendLine("GO");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine(GenerateSingle(table, operation, schemaPrefix, spPrefix));
        }

        return sb.ToString();
    }

    // ─── Private generators ────────────────────────────────────────────────────

    private static string GenerateSingle(TableMetadata table, CrudOperation op, string schema, string spPrefix) => op switch
    {
        CrudOperation.GetAll    => GenerateGetAll(table, schema, spPrefix),
        CrudOperation.GetById   => GenerateGetById(table, schema, spPrefix),
        CrudOperation.Insert    => GenerateInsert(table, schema, spPrefix),
        CrudOperation.Update    => GenerateUpdate(table, schema, spPrefix),
        CrudOperation.Delete    => GenerateDelete(table, schema, spPrefix),
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    private static string SpName(string schema, string spPrefix, string tableName, string suffix)
        => $"{Q(schema)}.{Q($"{spPrefix}{tableName}_{suffix}")}";

    private static string Q(string name) => $"[{name.Trim('[', ']')}]";

    private static string ToSqlParam(string name) =>
        name.StartsWith("@") ? name : $"@{name}";

    // ─── GetAll ───────────────────────────────────────────────────────────────

    private static string GenerateGetAll(TableMetadata t, string schema, string prefix)
    {
        var cols = string.Join(",\r\n    ", t.Columns.OrderBy(c => c.Ordinal).Select(c => Q(c.Name)));
        return $@"-- =============================================
-- Stored Procedure: Get All {t.Schema}.{t.Name}
-- =============================================
CREATE OR ALTER PROCEDURE {SpName(schema, prefix, t.Name, "GetAll")}
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
    {cols}
    FROM {Q(t.Schema)}.{Q(t.Name)};
END";
    }

    // ─── GetById ─────────────────────────────────────────────────────────────

    private static string GenerateGetById(TableMetadata t, string schema, string prefix)
    {
        var pkCols = GetPrimaryKeyColumns(t);
        if (pkCols.Count == 0) pkCols = new List<ColumnMetadata> { t.Columns[0] };

        var paramList = string.Join(",\r\n    ", pkCols.Select(c => $"{ToSqlParam(c.Name)} {c.DataType}"));
        var cols = string.Join(",\r\n    ", t.Columns.OrderBy(c => c.Ordinal).Select(c => Q(c.Name)));
        var whereClause = string.Join("\r\n    AND ", pkCols.Select(c => $"{Q(c.Name)} = {ToSqlParam(c.Name)}"));

        return $@"-- =============================================
-- Stored Procedure: Get {t.Schema}.{t.Name} By PK
-- =============================================
CREATE OR ALTER PROCEDURE {SpName(schema, prefix, t.Name, "GetById")}
    {paramList}
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
    {cols}
    FROM {Q(t.Schema)}.{Q(t.Name)}
    WHERE {whereClause};
END";
    }

    // ─── Insert ───────────────────────────────────────────────────────────────

    private static string GenerateInsert(TableMetadata t, string schema, string prefix)
    {
        var pkCols = GetPrimaryKeyColumns(t);
        var pkNames = pkCols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Columns để insert: bỏ PK nếu chỉ có 1 cột PK (identity), giữ nếu composite
        var insertCols = pkCols.Count == 1
            ? t.Columns.Where(c => !pkNames.Contains(c.Name)).OrderBy(c => c.Ordinal).ToList()
            : t.Columns.OrderBy(c => c.Ordinal).ToList();

        var paramList = string.Join(",\r\n    ", insertCols.Select(c =>
            $"{ToSqlParam(c.Name)} {c.DataType}{(c.IsNullable ? " = NULL" : "")}"));
        var colList = string.Join(", ", insertCols.Select(c => Q(c.Name)));
        var valueList = string.Join(", ", insertCols.Select(c => ToSqlParam(c.Name)));

        var outputClause = pkCols.Count == 1
            ? $"\r\n    OUTPUT INSERTED.{Q(pkCols[0].Name)}"
            : string.Empty;

        return $@"-- =============================================
-- Stored Procedure: Insert into {t.Schema}.{t.Name}
-- =============================================
CREATE OR ALTER PROCEDURE {SpName(schema, prefix, t.Name, "Insert")}
    {paramList}
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO {Q(t.Schema)}.{Q(t.Name)} ({colList}){outputClause}
    VALUES ({valueList});
END";
    }

    // ─── Update ───────────────────────────────────────────────────────────────

    private static string GenerateUpdate(TableMetadata t, string schema, string prefix)
    {
        var pkCols = GetPrimaryKeyColumns(t);
        if (pkCols.Count == 0) pkCols = new List<ColumnMetadata> { t.Columns[0] };

        var pkNames = pkCols.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updateCols = t.Columns.Where(c => !pkNames.Contains(c.Name)).OrderBy(c => c.Ordinal).ToList();

        if (updateCols.Count == 0) updateCols = t.Columns.OrderBy(c => c.Ordinal).ToList();

        var allParams = t.Columns.OrderBy(c => c.Ordinal)
            .Select(c => $"{ToSqlParam(c.Name)} {c.DataType}{(c.IsNullable ? " = NULL" : "")}");
        var paramList = string.Join(",\r\n    ", allParams);

        var setClause = string.Join(",\r\n    ", updateCols.Select(c => $"{Q(c.Name)} = {ToSqlParam(c.Name)}"));
        var whereClause = string.Join("\r\n    AND ", pkCols.Select(c => $"{Q(c.Name)} = {ToSqlParam(c.Name)}"));

        return $@"-- =============================================
-- Stored Procedure: Update {t.Schema}.{t.Name}
-- =============================================
CREATE OR ALTER PROCEDURE {SpName(schema, prefix, t.Name, "Update")}
    {paramList}
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE {Q(t.Schema)}.{Q(t.Name)}
    SET
    {setClause}
    WHERE {whereClause};
END";
    }

    // ─── Delete ───────────────────────────────────────────────────────────────

    private static string GenerateDelete(TableMetadata t, string schema, string prefix)
    {
        var pkCols = GetPrimaryKeyColumns(t);
        if (pkCols.Count == 0) pkCols = new List<ColumnMetadata> { t.Columns[0] };

        var paramList = string.Join(",\r\n    ", pkCols.Select(c => $"{ToSqlParam(c.Name)} {c.DataType}"));
        var whereClause = string.Join("\r\n    AND ", pkCols.Select(c => $"{Q(c.Name)} = {ToSqlParam(c.Name)}"));

        return $@"-- =============================================
-- Stored Procedure: Delete from {t.Schema}.{t.Name}
-- =============================================
CREATE OR ALTER PROCEDURE {SpName(schema, prefix, t.Name, "Delete")}
    {paramList}
AS
BEGIN
    SET NOCOUNT ON;

    DELETE FROM {Q(t.Schema)}.{Q(t.Name)}
    WHERE {whereClause};
END";
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static List<ColumnMetadata> GetPrimaryKeyColumns(TableMetadata t)
    {
        if (t.PrimaryKeyColumns == null || t.PrimaryKeyColumns.Count == 0)
            return new List<ColumnMetadata>();

        var result = new List<ColumnMetadata>();
        foreach (var pkName in t.PrimaryKeyColumns)
        {
            var col = t.Columns.FirstOrDefault(c =>
                c.Name.Equals(pkName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (col != null) result.Add(col);
        }
        return result;
    }
}
