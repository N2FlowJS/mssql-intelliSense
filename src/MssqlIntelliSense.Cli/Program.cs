using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Cli;

/// <summary>
/// Entry point for the MssqlIntelliSense CLI tool.
/// 
/// Commands:
///   scan       &lt;connectionString&gt;              Scan SQL Server schema and save to cache
///   expand     &lt;sqlFile&gt; [dbName]              Expand SELECT * to column lists
///   qualify    &lt;sqlFile&gt; [dbName]              Add schema prefixes to unqualified tables
///   crud       &lt;schema.table&gt; [dbName] [op]   Generate CRUD stored procedures
///   completions &lt;sqlFile&gt; &lt;caretPos&gt; [dbName&gt; Get completion suggestions at position
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        var command = args[0].ToLowerInvariant();

        try
        {
            return command switch
            {
                "scan"        => await RunScan(args[1..]),
                "expand"      => RunExpand(args[1..]),
                "qualify"     => RunQualify(args[1..]),
                "crud"        => RunCrud(args[1..]),
                "completions" => RunCompletions(args[1..]),
                "--help" or "-h" or "help" => PrintUsageAndReturn(),
                _ => Error($"Unknown command: {command}. Run 'MssqlIntelliSense --help' for usage.")
            };
        }
        catch (Exception ex)
        {
            return Error($"Error: {ex.Message}");
        }
    }

    // ─── scan ──────────────────────────────────────────────────────────────────

    private static async Task<int> RunScan(string[] args)
    {
        if (args.Length < 1)
            return Error("Usage: MssqlIntelliSense scan <connectionString>");

        var connectionString = args[0];
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        Console.WriteLine($"[MssqlIntelliSense scan] Connecting to: {MaskPassword(connectionString)}");

        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch { }

        var name = BuildConnectionName(connectionString);
        var connId = MssqlIntelliSenseCacheWriter.RegisterConnection(NormalizeConnectionString(connectionString), name);

        Console.WriteLine($"[MssqlIntelliSense scan] Scanning schema for: {name}");

        var progress = new Progress<string>(msg => Console.WriteLine($"  {msg}"));
        var provider = new SqlServerMetadataProvider(connectionString);
        var metadata = await provider.GetMetadataAsync(progress, cts.Token);

        MssqlIntelliSenseCacheWriter.SaveSchemaCache(connId, metadata);

        Console.WriteLine($"[MssqlIntelliSense scan] Done. Cached {metadata.Tables.Count} tables, " +
                          $"{metadata.Views.Count} views, {metadata.Procedures.Count} procedures.");
        return 0;
    }

    // ─── expand ───────────────────────────────────────────────────────────────

    private static int RunExpand(string[] args)
    {
        if (args.Length < 1)
            return Error("Usage: MssqlIntelliSense expand <sqlFile> [databaseName]");

        var sqlFile = args[0];
        var dbName = args.Length > 1 ? args[1] : null;

        var sql = ReadSqlInput(sqlFile);
        var metadata = LoadMetadata(dbName);

        // Expand all SELECT * occurrences
        var current = sql;
        int offset = 0;
        bool expanded = false;
        while (true)
        {
            var result = SelectStarExpander.TryExpand(current, offset, metadata);
            if (result == null) break;

            current = SelectStarExpander.ExpandInSql(current, result.StarOffset, metadata) ?? current;
            offset = result.StarOffset + result.ExpandedText.Length;
            expanded = true;
        }

        Console.Write(current);
        return expanded ? 0 : 1;
    }

    // ─── qualify ──────────────────────────────────────────────────────────────

    private static int RunQualify(string[] args)
    {
        if (args.Length < 1)
            return Error("Usage: MssqlIntelliSense qualify <sqlFile> [databaseName]");

        var sqlFile = args[0];
        var dbName = args.Length > 1 ? args[1] : null;

        var sql = ReadSqlInput(sqlFile);
        var metadata = LoadMetadata(dbName);

        var result = TableQualifier.QualifyAll(sql, metadata);
        Console.Write(result);
        return 0;
    }

    // ─── crud ─────────────────────────────────────────────────────────────────

    private static int RunCrud(string[] args)
    {
        if (args.Length < 1)
            return Error("Usage: MssqlIntelliSense crud <schema.table> [databaseName] [all|getall|getbyid|insert|update|delete]");

        var tableParts = args[0].Split('.', 2);
        if (tableParts.Length != 2)
            return Error("Table must be in 'schema.table' format, e.g. 'dbo.Users'");

        var schema = tableParts[0].Trim('[', ']');
        var tableName = tableParts[1].Trim('[', ']');
        var dbName = args.Length > 1 ? args[1] : null;
        var opStr = args.Length > 2 ? args[2] : "all";

        var metadata = LoadMetadata(dbName);
        var table = metadata.Tables.FirstOrDefault(t =>
            t.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) &&
            t.Name.Equals(tableName, StringComparison.OrdinalIgnoreCase));

        if (table == null)
            return Error($"Table '{schema}.{tableName}' not found in schema cache. Run 'MssqlIntelliSense scan' first.");

        var op = opStr.ToLowerInvariant() switch
        {
            "all"     or "a" => CrudOperation.All,
            "getall"  or "ga" => CrudOperation.GetAll,
            "getbyid" or "gb" => CrudOperation.GetById,
            "insert"  or "i"  => CrudOperation.Insert,
            "update"  or "u"  => CrudOperation.Update,
            "delete"  or "d"  => CrudOperation.Delete,
            _ => CrudOperation.All
        };

        var script = CrudGenerator.Generate(table, op);
        Console.Write(script);
        return 0;
    }

    // ─── completions ──────────────────────────────────────────────────────────

    private static int RunCompletions(string[] args)
    {
        if (args.Length < 2)
            return Error("Usage: MssqlIntelliSense completions <sqlFile> <caretPosition> [databaseName]");

        var sqlFile = args[0];
        if (!int.TryParse(args[1], out int caretPos))
            return Error("caretPosition must be an integer.");

        var dbName = args.Length > 2 ? args[2] : null;
        var sql = ReadSqlInput(sqlFile);
        var metadata = LoadMetadata(dbName);

        var provider = new SqlCompletionProvider();
        var completions = provider.GetCompletions(sql, Math.Min(caretPos, sql.Length), metadata);

        foreach (var item in completions)
        {
            Console.WriteLine($"{item.Kind}\t{item.Label}\t{item.InsertText}\t{item.Description}");
        }

        return 0;
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static string ReadSqlInput(string sqlFile)
    {
        if (sqlFile == "-")
            return Console.In.ReadToEnd();

        if (!File.Exists(sqlFile))
            throw new FileNotFoundException($"SQL file not found: {sqlFile}");

        return File.ReadAllText(sqlFile);
    }

    private static DatabaseMetadata LoadMetadata(string? databaseName)
    {
        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch { }

        var connections = MssqlIntelliSenseCacheReader.GetConnections();
        if (connections.Count == 0)
        {
            Console.Error.WriteLine("[MssqlIntelliSense] Warning: No connections cached. Run 'MssqlIntelliSense scan <connStr>' first.");
            return DatabaseMetadata.Empty;
        }

        // Use the most recently seen active connection
        var conn = connections.FirstOrDefault(c => c.IsActive) ?? connections[0];
        var metadata = MssqlIntelliSenseCacheReader.GetSchemaDetails(conn.Id).Metadata;

        if (!string.IsNullOrWhiteSpace(databaseName))
            metadata = MssqlIntelliSenseCacheReader.FilterByDatabase(metadata, databaseName);

        return metadata;
    }

    private static string BuildConnectionName(string connectionString)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            return builder.DataSource;
        }
        catch
        {
            return "SqlServer Connection";
        }
    }

    private static string NormalizeConnectionString(string connectionString)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            builder.InitialCatalog = string.Empty;
            return builder.ConnectionString;
        }
        catch
        {
            return connectionString;
        }
    }

    private static string MaskPassword(string connectionString)
    {
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            if (!string.IsNullOrEmpty(builder.Password))
                builder.Password = "***";
            return builder.ConnectionString;
        }
        catch
        {
            return connectionString;
        }
    }

    private static int Error(string message)
    {
        Console.Error.WriteLine($"[ERROR] {message}");
        return 1;
    }

    private static int PrintUsageAndReturn()
    {
        PrintUsage();
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            MssqlIntelliSense - MSSQL IntelliSense CLI

            Usage: MssqlIntelliSense <command> [options]

            Commands:
              scan <connectionString>
                  Scan SQL Server schema and save to local cache.
                  Example: MssqlIntelliSense scan "Server=.;Database=MyDb;Integrated Security=true"

              expand <sqlFile|-> [databaseName]
                  Expand SELECT * to explicit column list. Use '-' to read from stdin.
                  Example: MssqlIntelliSense expand query.sql MyDb

              qualify <sqlFile|-> [databaseName]
                  Add schema prefixes to unqualified table/view names.
                  Example: MssqlIntelliSense qualify query.sql

              crud <schema.table> [databaseName] [operation]
                  Generate CRUD stored procedures.
                  Operations: all, getall, getbyid, insert, update, delete
                  Example: MssqlIntelliSense crud dbo.Users MyDb all

              completions <sqlFile|-|inline:SQL> <caretPosition> [databaseName]
                  Get completion suggestions at the specified caret position.
                  Output: tab-separated Kind, Label, InsertText, Description
                  Example: MssqlIntelliSense completions query.sql 15

            Global options:
              --help, -h    Show this help
            """);
    }
}
