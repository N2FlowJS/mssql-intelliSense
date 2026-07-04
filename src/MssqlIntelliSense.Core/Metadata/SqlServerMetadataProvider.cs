using Microsoft.Data.SqlClient;

namespace MssqlIntelliSense.Core.Metadata;

public sealed class SqlServerMetadataProvider : IMetadataProvider
{
    private readonly string _connectionString;

    public SqlServerMetadataProvider(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(connectionString));
        _connectionString = connectionString;
    }

    public async Task<DatabaseMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        return await GetMetadataAsync(null, cancellationToken);
    }

    public async Task<DatabaseMetadata> GetMetadataAsync(IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        return await GetMetadataAsync(progress, MetadataScanScope.All, null, cancellationToken);
    }

    public async Task<DatabaseMetadata> GetMetadataAsync(
        IProgress<string>? progress,
        MetadataScanScope scope,
        string? databaseName,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Đang khởi tạo kết nối SQL Server...");
        using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            progress?.Report("Kết nối thành công.");

            var tables       = new List<TableMetadata>();
            var foreignKeys  = new List<ForeignKeyMetadata>();
            var indexes      = new List<IndexMetadata>();
            var databases    = new List<string>();
            var linkedServers = new List<LinkedServerInfo>();
            var endpoints    = new List<EndpointInfo>();
            var procedures   = new List<ProcedureMetadata>();
            var views        = new List<ViewMetadata>();
            var functions    = new List<FunctionMetadata>();
            var triggers     = new List<TriggerMetadata>();
            var userTypes    = new List<UserTypeMetadata>();
            var synonyms     = new List<SynonymMetadata>();
            var users        = new List<UserMetadata>();

            // ── 1. Database list ────────────────────────────────────────────
            if (scope.HasFlag(MetadataScanScope.DatabaseList) || scope.HasFlag(MetadataScanScope.DatabaseObjects))
            {
                progress?.Report("Đang tải danh sách cơ sở dữ liệu...");
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name;";
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                            while (await reader.ReadAsync(cancellationToken))
                                databases.Add(reader.GetString(0));
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Failed to query sys.databases: {ex.Message}");
                }
            }

            if (databases.Count == 0 && !string.IsNullOrEmpty(connection.Database))
                databases.Add(connection.Database);
            if (!string.IsNullOrWhiteSpace(databaseName))
                databases = databases
                    .Where(db => db.Equals(databaseName, StringComparison.OrdinalIgnoreCase))
                    .DefaultIfEmpty(databaseName!)
                    .ToList();

            var originalDatabase = connection.Database;

            // ── 2. Per-database scan ────────────────────────────────────────
            if (scope.HasFlag(MetadataScanScope.DatabaseObjects))
            foreach (var dbName in databases)
            {
                progress?.Report($"[CSDL: {dbName}] Bắt đầu quét schema...");
                try { connection.ChangeDatabase(dbName); }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Could not access database {dbName}: {ex.Message}");
                    continue;
                }

                // ── Tables & Columns ──────────────────────────────────────
                if (scope.HasFlag(MetadataScanScope.Tables))
                {
                progress?.Report($"[CSDL: {dbName}] Đang quét Bảng & Cột...");
                var tableRows = new List<(string Schema, string Table, string Column, string Type, bool Nullable, int Ordinal, bool PrimaryKey)>();
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = TablesSql;
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                            while (await reader.ReadAsync(cancellationToken))
                                tableRows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                                    reader.GetString(3), reader.GetBoolean(4),
                                    System.Convert.ToInt32(reader.GetValue(5)), reader.GetBoolean(6)));
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan tables in {dbName}: {ex.Message}");
                }

                var dbTables = tableRows
                    .GroupBy(row => (row.Schema, row.Table))
                    .Select(group => new TableMetadata(
                        group.Key.Schema, group.Key.Table,
                        group.OrderBy(r => r.Ordinal).Select(r => new ColumnMetadata(r.Column, r.Type, r.Nullable, r.Ordinal)).ToArray(),
                        group.Where(r => r.PrimaryKey).OrderBy(r => r.Ordinal).Select(r => r.Column).ToArray())
                    { Database = dbName })
                    .ToArray();
                tables.AddRange(dbTables);
                }

                // ── Foreign Keys ──────────────────────────────────────────
                if (scope.HasFlag(MetadataScanScope.Relations))
                {
                progress?.Report($"[CSDL: {dbName}] Đang quét Khóa ngoại (Foreign Keys)...");
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = ForeignKeysSql;
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                            while (await reader.ReadAsync(cancellationToken))
                                foreignKeys.Add(new ForeignKeyMetadata(
                                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                                    System.Convert.ToInt32(reader.GetValue(7)))
                                { Database = dbName });
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan foreign keys in {dbName}: {ex.Message}");
                }
                }

                // ── Indexes ───────────────────────────────────────────────
                if (scope.HasFlag(MetadataScanScope.Indexes))
                {
                progress?.Report($"[CSDL: {dbName}] Đang quét các Chỉ mục (Indexes)...");
                var indexRows = new List<(string Schema, string Table, string Name, bool Unique, string Column, int Ordinal)>();
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = IndexesSql;
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                            while (await reader.ReadAsync(cancellationToken))
                                indexRows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                                    reader.GetBoolean(3), reader.GetString(4),
                                    System.Convert.ToInt32(reader.GetValue(5))));
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan indexes in {dbName}: {ex.Message}");
                }

                indexes.AddRange(indexRows
                    .GroupBy(row => (row.Schema, row.Table, row.Name, row.Unique))
                    .Select(group => new IndexMetadata(
                        group.Key.Schema, group.Key.Table, group.Key.Name, group.Key.Unique,
                        group.OrderBy(r => r.Ordinal).Select(r => r.Column).ToArray())
                    { Database = dbName }));
                }

                // ── Stored Procedures ─────────────────────────────────────
                if (scope.HasFlag(MetadataScanScope.Programmability))
                {
                progress?.Report($"[CSDL: {dbName}] Đang quét Thủ tục lưu trữ (Procedures)...");
                var procRows = new List<(string Schema, string Name, string Type, string? ParamName, string? ParamType, bool IsOutput, int ParamOrdinal)>();
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = ProceduresSql;
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                            while (await reader.ReadAsync(cancellationToken))
                                procRows.Add((
                                    reader.GetString(0),
                                    reader.GetString(1),
                                    reader.GetString(2),
                                    reader.IsDBNull(3) ? null : reader.GetString(3),
                                    reader.IsDBNull(4) ? null : reader.GetString(4),
                                    !reader.IsDBNull(5) && reader.GetBoolean(5),
                                    reader.IsDBNull(6) ? 0 : reader.GetInt32(6)
                                ));
                    }

                    procedures.AddRange(procRows
                        .GroupBy(row => (row.Schema, row.Name, row.Type))
                        .Select(group =>
                        {
                            var parameters = group
                                .Where(r => !string.IsNullOrEmpty(r.ParamName))
                                .Select(r => new FunctionParameterMetadata(r.ParamName!, r.ParamType ?? "int", r.IsOutput, r.ParamOrdinal))
                                .ToArray();
                            return new ProcedureMetadata(group.Key.Schema, group.Key.Name)
                            {
                                Database = dbName,
                                ObjectType = group.Key.Type,
                                Parameters = parameters
                            };
                        }));
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan procedures in {dbName}: {ex.Message}");
                }

                // ── Views ─────────────────────────────────────────────────
                progress?.Report($"[CSDL: {dbName}] Đang quét Khung nhìn (Views)...");
                var viewRows = new List<(string Schema, string View, string Column, string Type, bool Nullable, int Ordinal, bool IsIndexed)>();
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = ViewsSql;
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                            while (await reader.ReadAsync(cancellationToken))
                                viewRows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                                    reader.GetString(3), reader.GetBoolean(4),
                                    System.Convert.ToInt32(reader.GetValue(5)), reader.GetBoolean(6)));
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan views in {dbName}: {ex.Message}");
                }

                views.AddRange(viewRows
                    .GroupBy(row => (row.Schema, row.View, row.IsIndexed))
                    .Select(group => new ViewMetadata(
                        group.Key.Schema, group.Key.View,
                        group.OrderBy(r => r.Ordinal).Select(r => new ColumnMetadata(r.Column, r.Type, r.Nullable, r.Ordinal)).ToArray())
                    { Database = dbName, IsIndexed = group.Key.IsIndexed }));

                // ── Functions (Scalar / TVF / CLR) ────────────────────────
                progress?.Report($"[CSDL: {dbName}] Đang quét các Hàm (Functions)...");
                var fnRows = new List<(string Schema, string Name, string FnType, string ReturnType, string ParamName, string ParamType, bool IsOutput, int ParamOrdinal)>();
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = FunctionsSql;
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                            while (await reader.ReadAsync(cancellationToken))
                                fnRows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                                    !reader.IsDBNull(6) && reader.GetBoolean(6),
                                    reader.IsDBNull(7) ? 0 : System.Convert.ToInt32(reader.GetValue(7))));
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan functions in {dbName}: {ex.Message}");
                }

                functions.AddRange(fnRows
                    .GroupBy(row => (row.Schema, row.Name, row.FnType, row.ReturnType))
                    .Select(group =>
                    {
                        var parameters = group
                            .Where(r => !string.IsNullOrEmpty(r.ParamName))
                            .Select(r => new FunctionParameterMetadata(r.ParamName, r.ParamType, r.IsOutput, r.ParamOrdinal))
                            .ToArray();
                        return new FunctionMetadata(group.Key.Schema, group.Key.Name)
                        {
                            Database     = dbName,
                            FunctionType = group.Key.FnType,
                            ReturnType   = group.Key.ReturnType,
                            Parameters   = parameters
                        };
                    }));

                // ── Triggers ──────────────────────────────────────────────
                progress?.Report($"[CSDL: {dbName}] Đang quét các Trigger...");
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = TriggersSql;
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                            while (await reader.ReadAsync(cancellationToken))
                                triggers.Add(new TriggerMetadata(
                                    reader.IsDBNull(0) ? "dbo" : reader.GetString(0),
                                    reader.GetString(1),
                                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                                    reader.IsDBNull(3) ? "" : reader.GetString(3))
                                {
                                    Database    = dbName,
                                    TriggerType = reader.IsDBNull(4) ? "TR" : reader.GetString(4).Trim(),
                                    IsEnabled   = !reader.IsDBNull(5) && reader.GetBoolean(5),
                                    Events      = reader.IsDBNull(6) ? "" : reader.GetString(6)
                                });
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan triggers in {dbName}: {ex.Message}");
                }
                }

                // ── User-Defined Types ────────────────────────────────────
                if (scope.HasFlag(MetadataScanScope.Security))
                {
                progress?.Report($"[CSDL: {dbName}] Đang quét Kiểu dữ liệu tự định nghĩa (User Types)...");
                var udtRows = new List<(string Schema, string Name, string BaseType, bool Nullable, bool IsTable, string ColName, string ColType, bool ColNullable, int ColOrdinal)>();
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = UserTypesSql;
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                            while (await reader.ReadAsync(cancellationToken))
                                udtRows.Add((
                                    reader.GetString(0), reader.GetString(1),
                                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                                    !reader.IsDBNull(3) && reader.GetBoolean(3),
                                    !reader.IsDBNull(4) && reader.GetBoolean(4),
                                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                                    reader.IsDBNull(6) ? "" : reader.GetString(6),
                                    !reader.IsDBNull(7) && reader.GetBoolean(7),
                                    reader.IsDBNull(8) ? 0 : System.Convert.ToInt32(reader.GetValue(8))));
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan user types in {dbName}: {ex.Message}");
                }

                userTypes.AddRange(udtRows
                    .GroupBy(row => (row.Schema, row.Name, row.BaseType, row.Nullable, row.IsTable))
                    .Select(group =>
                    {
                        var columns = group
                            .Where(r => !string.IsNullOrEmpty(r.ColName))
                            .Select(r => new ColumnMetadata(r.ColName, r.ColType, r.ColNullable, r.ColOrdinal))
                            .ToArray();
                        return new UserTypeMetadata(group.Key.Schema, group.Key.Name)
                        {
                            Database    = dbName,
                            BaseType    = group.Key.BaseType,
                            IsNullable  = group.Key.Nullable,
                            IsTableType = group.Key.IsTable,
                            Columns     = columns
                        };
                    }));

                // ── Synonyms ──────────────────────────────────────────────
                progress?.Report($"[CSDL: {dbName}] Đang quét các Từ đồng nghĩa (Synonyms)...");
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = SynonymsSql;
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                            while (await reader.ReadAsync(cancellationToken))
                                synonyms.Add(new SynonymMetadata(
                                    reader.GetString(0), reader.GetString(1), reader.GetString(2))
                                { Database = dbName });
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan synonyms in {dbName}: {ex.Message}");
                }

                // ── Users ────────────────────────────────────────────────
                progress?.Report($"[CSDL: {dbName}] Đang quét Users...");
                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = UsersSql;
                        using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                            while (await reader.ReadAsync(cancellationToken))
                                users.Add(new UserMetadata(
                                    reader.GetString(0),
                                    reader.GetString(1),
                                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                                    reader.IsDBNull(3) ? "" : reader.GetDateTime(3).ToString("yyyy-MM-dd"))
                                { Database = dbName });
                    }
                }
                catch (Exception ex)
                {
                    System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan users in {dbName}: {ex.Message}");
                }
                }
            }

            // Restore original database
            try { connection.ChangeDatabase(originalDatabase); } catch { }

            // ── 3. Linked Servers (instance-level) ─────────────────────────
            if (scope.HasFlag(MetadataScanScope.LinkedServers))
            {
            progress?.Report("Đang quét các máy chủ liên kết (Linked Servers)...");
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = LinkedServersSql;
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            var lsName   = reader.GetString(0);
                            var lsSource = reader.IsDBNull(1) ? lsName : reader.GetString(1);
                            linkedServers.Add(new LinkedServerInfo(lsName, lsSource));
                        }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan linked servers: {ex.Message}");
            }
            }

            // ── 4. Endpoints (instance-level Server Objects) ────────────────
            if (scope.HasFlag(MetadataScanScope.Endpoints))
            {
            progress?.Report("Đang quét Endpoints...");
            try
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = EndpointsSql;
                    using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            endpoints.Add(new EndpointInfo(
                                reader.GetString(0),
                                reader.IsDBNull(1) ? "" : reader.GetString(1),
                                reader.IsDBNull(2) ? "" : reader.GetString(2),
                                reader.IsDBNull(3) ? "" : reader.GetString(3),
                                reader.IsDBNull(4) ? 0 : reader.GetInt32(4)));
                        }
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine($"[Metadata Scan Warning] Failed to scan endpoints: {ex.Message}");
            }
            }

            // ── 5. Linked Servers: đăng ký là connection riêng (scan ở tầng Package) ──
            foreach (var ls in linkedServers)
            {
                progress?.Report($"[Linked Server: {ls.Name}] Đã ghi nhận → sẽ được scan riêng.");
            }


            return new DatabaseMetadata(tables.ToArray(), foreignKeys.ToArray(), indexes.ToArray(), databases.ToArray(), linkedServers.ToArray())
            {
                Procedures = procedures.ToArray(),
                Views      = views.ToArray(),
                Functions  = functions.ToArray(),
                Triggers   = triggers.ToArray(),
                UserTypes  = userTypes.ToArray(),
                Synonyms   = synonyms.ToArray(),
                Users      = users.ToArray(),
                Endpoints  = endpoints.ToArray()
            };
        }
    }

    // ─────────────────────────────────── SQL Queries ─────────────────────────

    private const string TablesSql = """
        SELECT s.name, o.name, c.name, ty.name, c.is_nullable, c.column_id,
               CONVERT(bit, CASE WHEN pk.column_id IS NULL THEN 0 ELSE 1 END)
        FROM sys.tables o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.columns c ON c.object_id = o.object_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN (
            SELECT ic.object_id, ic.column_id
            FROM sys.indexes i JOIN sys.index_columns ic
              ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            WHERE i.is_primary_key = 1
        ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
        ORDER BY s.name, o.name, c.column_id;
        """;

    private const string ViewsSql = """
        SELECT s.name, v.name, c.name, ty.name, c.is_nullable, c.column_id,
               CONVERT(bit, CASE WHEN idx.object_id IS NOT NULL THEN 1 ELSE 0 END) AS is_indexed
        FROM sys.views v
        JOIN sys.schemas s ON s.schema_id = v.schema_id
        JOIN sys.columns c ON c.object_id = v.object_id
        JOIN sys.types ty ON ty.user_type_id = c.user_type_id
        LEFT JOIN (
            SELECT DISTINCT object_id FROM sys.indexes WHERE index_id = 1 AND type IN (1,5)
        ) idx ON idx.object_id = v.object_id
        ORDER BY s.name, v.name, c.column_id;
        """;

    private const string ForeignKeysSql = """
        SELECT fk.name, fs.name, ft.name, fc.name, ts.name, tt.name, tc.name, fkc.constraint_column_id
        FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.tables ft ON ft.object_id = fkc.parent_object_id
        JOIN sys.schemas fs ON fs.schema_id = ft.schema_id
        JOIN sys.columns fc ON fc.object_id = ft.object_id AND fc.column_id = fkc.parent_column_id
        JOIN sys.tables tt ON tt.object_id = fkc.referenced_object_id
        JOIN sys.schemas ts ON ts.schema_id = tt.schema_id
        JOIN sys.columns tc ON tc.object_id = tt.object_id AND tc.column_id = fkc.referenced_column_id
        ORDER BY fk.name, fkc.constraint_column_id;
        """;

    private const string IndexesSql = """
        SELECT s.name, t.name, i.name, i.is_unique, c.name, ic.key_ordinal
        FROM sys.indexes i
        JOIN sys.tables t ON t.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
        JOIN sys.columns c ON c.object_id = t.object_id AND c.column_id = ic.column_id
        WHERE i.is_hypothetical = 0 AND i.name IS NOT NULL AND ic.key_ordinal > 0
        ORDER BY s.name, t.name, i.name, ic.key_ordinal;
        """;

    private const string ProceduresSql = """
        SELECT
            s.name                      AS [schema],
            o.name                      AS [name],
            o.type                      AS [type],
            p.name                      AS [param_name],
            pty.name                    AS [param_type],
            p.is_output                 AS [is_output],
            p.parameter_id              AS [param_ordinal]
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.parameters p ON p.object_id = o.object_id AND p.parameter_id > 0
        LEFT JOIN sys.types pty ON pty.user_type_id = p.user_type_id
        WHERE o.type = 'P'
        ORDER BY s.name, o.name, p.parameter_id;
        """;

    private const string FunctionsSql = """
        SELECT
            s.name                      AS [schema],
            o.name                      AS [name],
            o.type                      AS [fn_type],
            tp.name                     AS [return_type],
            p.name                      AS [param_name],
            pty.name                    AS [param_type],
            p.is_output                 AS [is_output],
            p.parameter_id              AS [param_ordinal]
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.parameters p ON p.object_id = o.object_id AND p.parameter_id > 0
        LEFT JOIN sys.types pty ON pty.user_type_id = p.user_type_id
        LEFT JOIN sys.parameters ret ON ret.object_id = o.object_id AND ret.parameter_id = 0
        LEFT JOIN sys.types tp ON tp.user_type_id = ret.user_type_id
        WHERE o.type IN ('FN', 'TF', 'IF', 'FS', 'FT', 'AF')
        ORDER BY s.name, o.name, p.parameter_id;
        """;

    private const string TriggersSql = """
        SELECT
            ts.name                     AS trigger_schema,
            tr.name                     AS trigger_name,
            ps.name                     AS parent_schema,
            po.name                     AS parent_name,
            tr.type                     AS trigger_type,
            tr.is_disabled = 0          AS is_enabled,
            STRING_AGG(te.type_desc, ', ') AS events
        FROM sys.triggers tr
        LEFT JOIN sys.objects po ON po.object_id = tr.parent_id
        LEFT JOIN sys.schemas ps ON ps.schema_id = po.schema_id
        LEFT JOIN sys.schemas ts ON ts.schema_id = ISNULL(po.schema_id, 1)
        LEFT JOIN sys.trigger_events te ON te.object_id = tr.object_id
        WHERE tr.is_ms_shipped = 0
        GROUP BY ts.name, tr.name, ps.name, po.name, tr.type, tr.is_disabled;
        """;

    private const string UserTypesSql = """
        SELECT
            s.name                      AS [schema],
            ut.name                     AS [name],
            bt.name                     AS [base_type],
            ut.is_nullable              AS [is_nullable],
            ut.is_table_type            AS [is_table],
            ttc.name                    AS [col_name],
            cty.name                    AS [col_type],
            ttc.is_nullable             AS [col_nullable],
            ttc.column_id               AS [col_ordinal]
        FROM sys.types ut
        JOIN sys.schemas s ON s.schema_id = ut.schema_id
        LEFT JOIN sys.types bt ON bt.user_type_id = ut.system_type_id AND bt.is_user_defined = 0
        LEFT JOIN sys.table_types tt ON tt.user_type_id = ut.user_type_id
        LEFT JOIN sys.columns ttc ON ttc.object_id = tt.type_table_object_id
        LEFT JOIN sys.types cty ON cty.user_type_id = ttc.user_type_id
        WHERE ut.is_user_defined = 1
        ORDER BY s.name, ut.name, ttc.column_id;
        """;

    private const string SynonymsSql = """
        SELECT s.name, sy.name, sy.base_object_name
        FROM sys.synonyms sy
        JOIN sys.schemas s ON s.schema_id = sy.schema_id
        ORDER BY s.name, sy.name;
        """;

    private const string LinkedServersSql = """
        SELECT name, data_source FROM sys.servers WHERE is_linked = 1 ORDER BY name;
        """;

    private const string EndpointsSql = """
        SELECT
            e.name,
            e.type_desc,
            e.protocol_desc,
            e.state_desc,
            ISNULL(tcp.port, 0) AS port
        FROM sys.endpoints e
        LEFT JOIN sys.tcp_endpoints tcp ON tcp.endpoint_id = e.endpoint_id
        ORDER BY e.name;
        """;

    private const string UsersSql = """
        SELECT
            dp.name                     AS [name],
            dp.type_desc                AS [type],
            dp.default_schema_name      AS [default_schema],
            dp.create_date              AS [create_date]
        FROM sys.database_principals dp
        WHERE dp.principal_id > 4
          AND dp.type IN ('S', 'U', 'G', 'R')
        ORDER BY dp.name;
        """;
}
