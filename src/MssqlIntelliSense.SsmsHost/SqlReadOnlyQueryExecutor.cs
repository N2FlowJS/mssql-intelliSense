using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace MssqlIntelliSense.SsmsHost;

internal static class SqlReadOnlyQueryExecutor
{
    private const int CommandTimeoutSeconds = 30;
    private const int MaxRows = 500;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex UnsafeSqlPattern = new(
        @"\b(insert|update|delete|merge|drop|alter|create|truncate|grant|revoke|deny|backup|restore|kill|dbcc|use)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<string> ExecuteAsync(string connectionString, string? databaseName, string query)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return JsonSerializer.Serialize(new { error = "No active SQL connection is available." }, JsonOptions);
        }

        var trimmedQuery = query?.Trim() ?? string.Empty;
        var validationError = ValidateReadOnlyQuery(trimmedQuery);
        if (!string.IsNullOrWhiteSpace(validationError))
        {
            return JsonSerializer.Serialize(new { error = validationError }, JsonOptions);
        }

        var stopwatch = Stopwatch.StartNew();
        using var connection = new SqlConnection(BuildConnectionString(connectionString, databaseName));
        await connection.OpenAsync();

        using var command = connection.CreateCommand();
        command.CommandText = trimmedQuery;
        command.CommandTimeout = CommandTimeoutSeconds;

        using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess);
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(i => new
            {
                ordinal = i,
                name = reader.GetName(i),
                dataType = reader.GetDataTypeName(i)
            })
            .ToList();

        var rows = new List<Dictionary<string, object?>>();
        while (rows.Count < MaxRows && await reader.ReadAsync(CancellationToken.None))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
                row[reader.GetName(i)] = NormalizeValue(value);
            }

            rows.Add(row);
        }

        var truncated = rows.Count == MaxRows && await reader.ReadAsync(CancellationToken.None);
        stopwatch.Stop();

        return JsonSerializer.Serialize(new
        {
            query = trimmedQuery,
            database = connection.Database,
            columns,
            rows,
            rowCount = rows.Count,
            truncated,
            maxRows = MaxRows,
            elapsedMs = stopwatch.ElapsedMilliseconds
        }, JsonOptions);
    }

    private static string BuildConnectionString(string connectionString, string? databaseName)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            TrustServerCertificate = true
        };

        if (!string.IsNullOrWhiteSpace(databaseName))
        {
            builder.InitialCatalog = databaseName;
        }

        return builder.ConnectionString;
    }

    private static string? ValidateReadOnlyQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Query is required for execute.";
        }

        var normalized = StripLeadingComments(query).TrimStart();
        if (UnsafeSqlPattern.IsMatch(normalized))
        {
            return "Only read-only SQL is allowed. DML, DDL, permission, backup/restore, DBCC, KILL and USE statements are blocked.";
        }

        if (Regex.IsMatch(normalized, @"\bexec(?:ute)?\b", RegexOptions.IgnoreCase))
        {
            return IsSafeMetadataExec(normalized)
                ? null
                : "EXEC is limited to safe metadata procedures such as sp_help, sp_helptext, sp_columns and sp_tables.";
        }

        return StartsWithAny(normalized, "select", "with", "declare")
            ? null
            : "Only SELECT, WITH, DECLARE or safe metadata EXEC queries are allowed.";
    }

    private static bool IsSafeMetadataExec(string query)
    {
        return Regex.IsMatch(
            query,
            @"^\s*exec(?:ute)?\s+(?:sys\.)?(?:sp_help|sp_helptext|sp_columns|sp_tables)\b",
            RegexOptions.IgnoreCase);
    }

    private static bool StartsWithAny(string value, params string[] prefixes)
    {
        return prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripLeadingComments(string query)
    {
        var value = query;
        while (true)
        {
            var trimmed = value.TrimStart();
            if (trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                var nextLine = trimmed.IndexOfAny(new[] { '\r', '\n' });
                value = nextLine < 0 ? string.Empty : trimmed.Substring(nextLine + 1);
                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = trimmed.IndexOf("*/", StringComparison.Ordinal);
                value = end < 0 ? string.Empty : trimmed.Substring(end + 2);
                continue;
            }

            return trimmed;
        }
    }

    private static object? NormalizeValue(object? value)
    {
        return value switch
        {
            null => null,
            byte[] bytes => Convert.ToBase64String(bytes),
            DateTime dateTime => dateTime,
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            Guid guid => guid,
            _ => value
        };
    }
}
