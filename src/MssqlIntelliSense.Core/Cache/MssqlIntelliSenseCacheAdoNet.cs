#if !NET
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Cache
{
    public static class MssqlIntelliSenseCacheAdoNet
    {
        private static readonly object _writeLock = new();

        private static SqliteConnection GetOpenConnection()
        {
            var conn = new SqliteConnection(MssqlIntelliSenseConfig.GetDbConnectionString());
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
                cmd.ExecuteNonQuery();
            }
            return conn;
        }

        public static void InitializeDatabase()
        {
            var dbFolder = MssqlIntelliSenseConfig.GetAppDataFolder();
            if (!System.IO.Directory.Exists(dbFolder))
            {
                System.IO.Directory.CreateDirectory(dbFolder);
            }

            lock (_writeLock)
            {
                using (var conn = new SqliteConnection(MssqlIntelliSenseConfig.GetDbConnectionString()))
            {
                conn.Open();

                using (var cmd = conn.CreateCommand())
                {
                    // 1. Enable WAL mode & foreign keys
                    cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
                    cmd.ExecuteNonQuery();

                    // 2. Create connections table
                    cmd.CommandText = @"
                        CREATE TABLE IF NOT EXISTS connections (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            name TEXT NOT NULL,
                            connection_string TEXT NOT NULL UNIQUE,
                            is_active INTEGER NOT NULL,
                            last_seen_at TEXT NULL,
                            schema_updated_at TEXT NULL
                        );";
                    cmd.ExecuteNonQuery();

                    // Migration: add schema_updated_at if it doesn't exist
                    try
                    {
                        cmd.CommandText = "ALTER TABLE connections ADD COLUMN schema_updated_at TEXT NULL;";
                        cmd.ExecuteNonQuery();
                    }
                    catch { /* already exists */ }

                    // Migration: add data_source to cache_linked_servers if it doesn't exist
                    try
                    {
                        cmd.CommandText = "ALTER TABLE cache_linked_servers ADD COLUMN data_source TEXT NOT NULL DEFAULT '';";
                        cmd.ExecuteNonQuery();
                    }
                    catch { /* already exists */ }

                    // Drop legacy table if exists
                    try
                    {
                        cmd.CommandText = "DROP TABLE IF EXISTS connection_schemas;";
                        cmd.ExecuteNonQuery();
                    }
                    catch { }

                    // 3. Create all other cache tables
                    string[] tableQueries = new[]
                    {
                        @"CREATE TABLE IF NOT EXISTS cache_databases (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            connection_id INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            FOREIGN KEY (connection_id) REFERENCES connections (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_tables (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            database_id INTEGER NOT NULL,
                            schema TEXT NOT NULL,
                            name TEXT NOT NULL,
                            pk_columns TEXT NOT NULL,
                            FOREIGN KEY (database_id) REFERENCES cache_databases (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_columns (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            table_id INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            data_type TEXT NOT NULL,
                            is_nullable INTEGER NOT NULL,
                            ordinal INTEGER NOT NULL,
                            FOREIGN KEY (table_id) REFERENCES cache_tables (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_views (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            database_id INTEGER NOT NULL,
                            schema TEXT NOT NULL,
                            name TEXT NOT NULL,
                            is_indexed INTEGER NOT NULL,
                            FOREIGN KEY (database_id) REFERENCES cache_databases (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_view_columns (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            view_id INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            data_type TEXT NOT NULL,
                            is_nullable INTEGER NOT NULL,
                            ordinal INTEGER NOT NULL,
                            FOREIGN KEY (view_id) REFERENCES cache_views (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_foreign_keys (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            database_id INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            from_schema TEXT NOT NULL,
                            from_table TEXT NOT NULL,
                            from_column TEXT NOT NULL,
                            to_schema TEXT NOT NULL,
                            to_table TEXT NOT NULL,
                            to_column TEXT NOT NULL,
                            ordinal INTEGER NOT NULL,
                            FOREIGN KEY (database_id) REFERENCES cache_databases (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_indexes (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            database_id INTEGER NOT NULL,
                            schema TEXT NOT NULL,
                            table_name TEXT NOT NULL,
                            name TEXT NOT NULL,
                            is_unique INTEGER NOT NULL,
                            FOREIGN KEY (database_id) REFERENCES cache_databases (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_index_cols (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            index_id INTEGER NOT NULL,
                            column_name TEXT NOT NULL,
                            ordinal INTEGER NOT NULL,
                            FOREIGN KEY (index_id) REFERENCES cache_indexes (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_procedures (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            database_id INTEGER NOT NULL,
                            schema TEXT NOT NULL,
                            name TEXT NOT NULL,
                            object_type TEXT NOT NULL,
                            FOREIGN KEY (database_id) REFERENCES cache_databases (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_functions (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            database_id INTEGER NOT NULL,
                            schema TEXT NOT NULL,
                            name TEXT NOT NULL,
                            fn_type TEXT NOT NULL,
                            return_type TEXT NOT NULL,
                            FOREIGN KEY (database_id) REFERENCES cache_databases (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_fn_params (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            function_id INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            data_type TEXT NOT NULL,
                            is_output INTEGER NOT NULL,
                            ordinal INTEGER NOT NULL,
                            FOREIGN KEY (function_id) REFERENCES cache_functions (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_triggers (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            database_id INTEGER NOT NULL,
                            schema TEXT NOT NULL,
                            name TEXT NOT NULL,
                            table_schema TEXT NOT NULL,
                            table_name TEXT NOT NULL,
                            trigger_type TEXT NOT NULL,
                            is_enabled INTEGER NOT NULL,
                            events TEXT NOT NULL,
                            FOREIGN KEY (database_id) REFERENCES cache_databases (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_user_types (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            database_id INTEGER NOT NULL,
                            schema TEXT NOT NULL,
                            name TEXT NOT NULL,
                            base_type TEXT NOT NULL,
                            is_nullable INTEGER NOT NULL,
                            is_table_type INTEGER NOT NULL,
                            FOREIGN KEY (database_id) REFERENCES cache_databases (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_udt_columns (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            user_type_id INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            data_type TEXT NOT NULL,
                            is_nullable INTEGER NOT NULL,
                            ordinal INTEGER NOT NULL,
                            FOREIGN KEY (user_type_id) REFERENCES cache_user_types (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_synonyms (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            database_id INTEGER NOT NULL,
                            schema TEXT NOT NULL,
                            name TEXT NOT NULL,
                            target_object TEXT NOT NULL,
                            FOREIGN KEY (database_id) REFERENCES cache_databases (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_proc_params (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            procedure_id INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            data_type TEXT NOT NULL,
                            is_output INTEGER NOT NULL,
                            ordinal INTEGER NOT NULL,
                            FOREIGN KEY (procedure_id) REFERENCES cache_procedures (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_users (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            database_id INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            type TEXT NOT NULL,
                            default_schema TEXT NOT NULL,
                            create_date TEXT NOT NULL,
                            FOREIGN KEY (database_id) REFERENCES cache_databases (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_linked_servers (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            connection_id INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            data_source TEXT NOT NULL DEFAULT '',
                            FOREIGN KEY (connection_id) REFERENCES connections (id) ON DELETE CASCADE
                        );",

                        @"CREATE TABLE IF NOT EXISTS cache_endpoints (
                            id INTEGER PRIMARY KEY AUTOINCREMENT,
                            connection_id INTEGER NOT NULL,
                            name TEXT NOT NULL,
                            type TEXT NOT NULL,
                            protocol TEXT NOT NULL,
                            state TEXT NOT NULL,
                            port INTEGER NOT NULL,
                            FOREIGN KEY (connection_id) REFERENCES connections (id) ON DELETE CASCADE
                        );"
                    };

                    foreach (var sql in tableQueries)
                    {
                        cmd.CommandText = sql;
                        cmd.ExecuteNonQuery();
                    }

                    // Indexes
                    string[] indexQueries = new[]
                    {
                        "CREATE INDEX IF NOT EXISTS IX_cache_tables_db ON cache_tables (database_id);",
                        "CREATE INDEX IF NOT EXISTS IX_cache_views_db ON cache_views (database_id);",
                        "CREATE INDEX IF NOT EXISTS IX_cache_fk_db ON cache_foreign_keys (database_id);",
                        "CREATE INDEX IF NOT EXISTS IX_cache_idx_db ON cache_indexes (database_id);",
                        "CREATE INDEX IF NOT EXISTS IX_cache_proc_db ON cache_procedures (database_id);",
                        "CREATE INDEX IF NOT EXISTS IX_cache_fn_db ON cache_functions (database_id);",
                        "CREATE INDEX IF NOT EXISTS IX_cache_trg_db ON cache_triggers (database_id);",
                        "CREATE INDEX IF NOT EXISTS IX_cache_udt_db ON cache_user_types (database_id);",
                        "CREATE INDEX IF NOT EXISTS IX_cache_syn_db ON cache_synonyms (database_id);",
                        "CREATE INDEX IF NOT EXISTS IX_cache_proc_params_proc ON cache_proc_params (procedure_id);",
                        "CREATE INDEX IF NOT EXISTS IX_cache_users_db ON cache_users (database_id);"
                    };

                    foreach (var idx in indexQueries)
                    {
                        cmd.CommandText = idx;
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            }
        }

        public static int RegisterConnection(string connectionString, string name)
        {
            var normalizedConnectionString = NormalizeServerConnectionString(connectionString);
            var legacyConnectionString = NormalizeServerConnectionStringLegacy(connectionString);

            lock (_writeLock)
            {
            using (var conn = GetOpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    // check existing
                    cmd.CommandText = "SELECT id FROM connections WHERE LOWER(connection_string) = LOWER($connStr) OR LOWER(connection_string) = LOWER($legacyConnStr);";
                    cmd.Parameters.AddWithValue("$connStr", normalizedConnectionString);
                    cmd.Parameters.AddWithValue("$legacyConnStr", legacyConnectionString);
                    var result = cmd.ExecuteScalar();

                    if (result != null)
                    {
                        int id = Convert.ToInt32(result);
                        cmd.CommandText = "UPDATE connections SET name = $name, connection_string = $connStr, last_seen_at = $lastSeen, is_active = 1 WHERE id = $id;";
                        cmd.Parameters.Clear();
                        cmd.Parameters.AddWithValue("$name", name);
                        cmd.Parameters.AddWithValue("$connStr", normalizedConnectionString);
                        cmd.Parameters.AddWithValue("$lastSeen", DateTimeOffset.UtcNow.ToString("o"));
                        cmd.Parameters.AddWithValue("$id", id);
                        cmd.ExecuteNonQuery();
                        return id;
                    }

                    cmd.CommandText = @"
                        INSERT INTO connections (name, connection_string, is_active, last_seen_at)
                        VALUES ($name, $connStr, 1, $lastSeen);
                        SELECT last_insert_rowid();";
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("$name", name);
                    cmd.Parameters.AddWithValue("$connStr", normalizedConnectionString);
                    cmd.Parameters.AddWithValue("$lastSeen", DateTimeOffset.UtcNow.ToString("o"));

                    return Convert.ToInt32(cmd.ExecuteScalar());
                }
            }
            }
        }

        private static string NormalizeServerConnectionString(string connectionString)
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

        private static string NormalizeServerConnectionStringLegacy(string connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                return connectionString;

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

        public static void DeleteConnection(int connectionId, IProgress<string>? progress = null)
        {
            lock (_writeLock)
            {
            const int maxRetries = 3;
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    DeleteConnectionInternal(connectionId, progress);
                    return;
                }
                catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 5 && attempt < maxRetries)
                {
                    progress?.Report($"⚠️ Database bị khóa, thử lại lần {attempt + 1}/{maxRetries}...");
                    System.Threading.Thread.Sleep(1000 * attempt);
                }
            }
            }
        }

        private static void DeleteConnectionInternal(int connectionId, IProgress<string>? progress)
        {
            using (var conn = GetOpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Parameters.AddWithValue("$connId", connectionId);

                    progress?.Report("Đang xóa cột chỉ mục (index columns)...");
                    cmd.CommandText = "DELETE FROM cache_index_cols WHERE index_id IN (SELECT id FROM cache_indexes WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId));";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa cột bảng (table columns)...");
                    cmd.CommandText = "DELETE FROM cache_columns WHERE table_id IN (SELECT id FROM cache_tables WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId));";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa cột view (view columns)...");
                    cmd.CommandText = "DELETE FROM cache_view_columns WHERE view_id IN (SELECT id FROM cache_views WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId));";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa tham số thủ tục (procedure params)...");
                    cmd.CommandText = "DELETE FROM cache_proc_params WHERE procedure_id IN (SELECT id FROM cache_procedures WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId));";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa tham số hàm (function params)...");
                    cmd.CommandText = "DELETE FROM cache_fn_params WHERE function_id IN (SELECT id FROM cache_functions WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId));";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa cột kiểu dữ liệu (UDT columns)...");
                    cmd.CommandText = "DELETE FROM cache_udt_columns WHERE user_type_id IN (SELECT id FROM cache_user_types WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId));";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa chỉ mục (indexes)...");
                    cmd.CommandText = "DELETE FROM cache_indexes WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId);";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa bảng (tables)...");
                    cmd.CommandText = "DELETE FROM cache_tables WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId);";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa view (views)...");
                    cmd.CommandText = "DELETE FROM cache_views WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId);";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa khóa ngoại (foreign keys)...");
                    cmd.CommandText = "DELETE FROM cache_foreign_keys WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId);";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa thủ tục (procedures)...");
                    cmd.CommandText = "DELETE FROM cache_procedures WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId);";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa hàm (functions)...");
                    cmd.CommandText = "DELETE FROM cache_functions WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId);";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa trigger (triggers)...");
                    cmd.CommandText = "DELETE FROM cache_triggers WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId);";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa kiểu dữ liệu (user types)...");
                    cmd.CommandText = "DELETE FROM cache_user_types WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId);";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa từ đồng nghĩa (synonyms)...");
                    cmd.CommandText = "DELETE FROM cache_synonyms WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId);";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa người dùng (users)...");
                    cmd.CommandText = "DELETE FROM cache_users WHERE database_id IN (SELECT id FROM cache_databases WHERE connection_id = $connId);";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa database cache...");
                    cmd.CommandText = "DELETE FROM cache_databases WHERE connection_id = $connId;";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa máy chủ liên kết (linked servers)...");
                    cmd.CommandText = "DELETE FROM cache_linked_servers WHERE connection_id = $connId;";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa endpoints...");
                    cmd.CommandText = "DELETE FROM cache_endpoints WHERE connection_id = $connId;";
                    cmd.ExecuteNonQuery();

                    progress?.Report("Đang xóa kết nối...");
                    cmd.CommandText = "DELETE FROM connections WHERE id = $connId;";
                    cmd.ExecuteNonQuery();

                    progress?.Report("✅ Xóa hoàn tất.");
                }
            }
        }

        public static DateTimeOffset? GetSchemaUpdatedAt(int connectionId)
        {
            using (var conn = GetOpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT schema_updated_at FROM connections WHERE id = $id;";
                    cmd.Parameters.AddWithValue("$id", connectionId);
                    var val = cmd.ExecuteScalar();
                    if (val == null || val == DBNull.Value) return null;
                    if (DateTimeOffset.TryParse(val.ToString(), out var dt)) return dt;
                    return null;
                }
            }
        }

        public static void SaveSchemaCache(int connectionId, DatabaseMetadata metadata)
        {
            lock (_writeLock)
            {
            using (var conn = GetOpenConnection())
            {
                // Update connection's schema_updated_at
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "UPDATE connections SET schema_updated_at = $updatedAt WHERE id = $id;";
                    cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("o"));
                    cmd.Parameters.AddWithValue("$id", connectionId);
                    cmd.ExecuteNonQuery();
                }

                // Cascade delete existing databases
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM cache_databases WHERE connection_id = $connId;";
                    cmd.Parameters.AddWithValue("$connId", connectionId);
                    cmd.ExecuteNonQuery();
                }

                // Delete existing linked servers
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM cache_linked_servers WHERE connection_id = $connId;";
                    cmd.Parameters.AddWithValue("$connId", connectionId);
                    cmd.ExecuteNonQuery();
                }

                // Delete existing endpoints
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM cache_endpoints WHERE connection_id = $connId;";
                    cmd.Parameters.AddWithValue("$connId", connectionId);
                    cmd.ExecuteNonQuery();
                }

                // Gather all database names
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
                    .Concat(metadata.Users.Select(x => x.Database))
                    .Where(db => !string.IsNullOrWhiteSpace(db))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var dbName in dbNames)
                {
                    long databaseId;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO cache_databases (connection_id, name) VALUES ($connId, $name); SELECT last_insert_rowid();";
                        cmd.Parameters.AddWithValue("$connId", connectionId);
                        cmd.Parameters.AddWithValue("$name", dbName);
                        databaseId = Convert.ToInt64(cmd.ExecuteScalar());
                    }

                    // Insert Tables & Columns
                    var tables = metadata.Tables.Where(t => string.Equals(t.Database, dbName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var t in tables)
                    {
                        long tableId;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "INSERT INTO cache_tables (database_id, schema, name, pk_columns) VALUES ($dbId, $schema, $name, $pkCols); SELECT last_insert_rowid();";
                            cmd.Parameters.AddWithValue("$dbId", databaseId);
                            cmd.Parameters.AddWithValue("$schema", t.Schema);
                            cmd.Parameters.AddWithValue("$name", t.Name);
                            cmd.Parameters.AddWithValue("$pkCols", t.PrimaryKeyColumnsString);
                            tableId = Convert.ToInt64(cmd.ExecuteScalar());
                        }

                        foreach (var c in t.Columns)
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "INSERT INTO cache_columns (table_id, name, data_type, is_nullable, ordinal) VALUES ($tableId, $name, $dataType, $isNullable, $ordinal);";
                                cmd.Parameters.AddWithValue("$tableId", tableId);
                                cmd.Parameters.AddWithValue("$name", c.Name);
                                cmd.Parameters.AddWithValue("$dataType", c.DataType);
                                cmd.Parameters.AddWithValue("$isNullable", c.IsNullable ? 1 : 0);
                                cmd.Parameters.AddWithValue("$ordinal", c.Ordinal);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    // Insert Views & Columns
                    var views = metadata.Views.Where(v => string.Equals(v.Database, dbName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var v in views)
                    {
                        long viewId;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "INSERT INTO cache_views (database_id, schema, name, is_indexed) VALUES ($dbId, $schema, $name, $isIndexed); SELECT last_insert_rowid();";
                            cmd.Parameters.AddWithValue("$dbId", databaseId);
                            cmd.Parameters.AddWithValue("$schema", v.Schema);
                            cmd.Parameters.AddWithValue("$name", v.Name);
                            cmd.Parameters.AddWithValue("$isIndexed", v.IsIndexed ? 1 : 0);
                            viewId = Convert.ToInt64(cmd.ExecuteScalar());
                        }

                        foreach (var c in v.Columns)
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "INSERT INTO cache_view_columns (view_id, name, data_type, is_nullable, ordinal) VALUES ($viewId, $name, $dataType, $isNullable, $ordinal);";
                                cmd.Parameters.AddWithValue("$viewId", viewId);
                                cmd.Parameters.AddWithValue("$name", c.Name);
                                cmd.Parameters.AddWithValue("$dataType", c.DataType);
                                cmd.Parameters.AddWithValue("$isNullable", c.IsNullable ? 1 : 0);
                                cmd.Parameters.AddWithValue("$ordinal", c.Ordinal);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    // Insert Foreign Keys
                    var foreignKeys = metadata.ForeignKeys.Where(fk => string.Equals(fk.Database, dbName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var fk in foreignKeys)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"INSERT INTO cache_foreign_keys (database_id, name, from_schema, from_table, from_column, to_schema, to_table, to_column, ordinal)
                                                VALUES ($dbId, $name, $fromSchema, $fromTable, $fromColumn, $toSchema, $toTable, $toColumn, $ordinal);";
                            cmd.Parameters.AddWithValue("$dbId", databaseId);
                            cmd.Parameters.AddWithValue("$name", fk.Name);
                            cmd.Parameters.AddWithValue("$fromSchema", fk.FromSchema);
                            cmd.Parameters.AddWithValue("$fromTable", fk.FromTable);
                            cmd.Parameters.AddWithValue("$fromColumn", fk.FromColumn);
                            cmd.Parameters.AddWithValue("$toSchema", fk.ToSchema);
                            cmd.Parameters.AddWithValue("$toTable", fk.ToTable);
                            cmd.Parameters.AddWithValue("$toColumn", fk.ToColumn);
                            cmd.Parameters.AddWithValue("$ordinal", fk.Ordinal);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    // Insert Indexes & Columns
                    var indexes = metadata.Indexes.Where(i => string.Equals(i.Database, dbName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var i in indexes)
                    {
                        long indexId;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "INSERT INTO cache_indexes (database_id, schema, table_name, name, is_unique) VALUES ($dbId, $schema, $tableName, $name, $isUnique); SELECT last_insert_rowid();";
                            cmd.Parameters.AddWithValue("$dbId", databaseId);
                            cmd.Parameters.AddWithValue("$schema", i.Schema);
                            cmd.Parameters.AddWithValue("$tableName", i.Table);
                            cmd.Parameters.AddWithValue("$name", i.Name);
                            cmd.Parameters.AddWithValue("$isUnique", i.IsUnique ? 1 : 0);
                            indexId = Convert.ToInt64(cmd.ExecuteScalar());
                        }

                        for (int idx = 0; idx < i.Columns.Count; idx++)
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "INSERT INTO cache_index_cols (index_id, column_name, ordinal) VALUES ($indexId, $columnName, $ordinal);";
                                cmd.Parameters.AddWithValue("$indexId", indexId);
                                cmd.Parameters.AddWithValue("$columnName", i.Columns[idx]);
                                cmd.Parameters.AddWithValue("$ordinal", idx + 1);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    // Insert Stored Procedures & Parameters
                    var procedures = metadata.Procedures.Where(p => string.Equals(p.Database, dbName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var p in procedures)
                    {
                        long procId;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "INSERT INTO cache_procedures (database_id, schema, name, object_type) VALUES ($dbId, $schema, $name, $objType); SELECT last_insert_rowid();";
                            cmd.Parameters.AddWithValue("$dbId", databaseId);
                            cmd.Parameters.AddWithValue("$schema", p.Schema);
                            cmd.Parameters.AddWithValue("$name", p.Name);
                            cmd.Parameters.AddWithValue("$objType", p.ObjectType);
                            procId = Convert.ToInt64(cmd.ExecuteScalar());
                        }

                        foreach (var param in p.Parameters)
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "INSERT INTO cache_proc_params (procedure_id, name, data_type, is_output, ordinal) VALUES ($procId, $name, $dataType, $isOut, $ordinal);";
                                cmd.Parameters.AddWithValue("$procId", procId);
                                cmd.Parameters.AddWithValue("$name", param.Name);
                                cmd.Parameters.AddWithValue("$dataType", param.DataType);
                                cmd.Parameters.AddWithValue("$isOut", param.IsOutput ? 1 : 0);
                                cmd.Parameters.AddWithValue("$ordinal", param.Ordinal);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    // Insert Functions & Parameters
                    var functions = metadata.Functions.Where(f => string.Equals(f.Database, dbName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var f in functions)
                    {
                        long fnId;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "INSERT INTO cache_functions (database_id, schema, name, fn_type, return_type) VALUES ($dbId, $schema, $name, $fnType, $retType); SELECT last_insert_rowid();";
                            cmd.Parameters.AddWithValue("$dbId", databaseId);
                            cmd.Parameters.AddWithValue("$schema", f.Schema);
                            cmd.Parameters.AddWithValue("$name", f.Name);
                            cmd.Parameters.AddWithValue("$fnType", f.FunctionType);
                            cmd.Parameters.AddWithValue("$retType", f.ReturnType);
                            fnId = Convert.ToInt64(cmd.ExecuteScalar());
                        }

                        foreach (var param in f.Parameters)
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "INSERT INTO cache_fn_params (function_id, name, data_type, is_output, ordinal) VALUES ($fnId, $name, $dataType, $isOut, $ordinal);";
                                cmd.Parameters.AddWithValue("$fnId", fnId);
                                cmd.Parameters.AddWithValue("$name", param.Name);
                                cmd.Parameters.AddWithValue("$dataType", param.DataType);
                                cmd.Parameters.AddWithValue("$isOut", param.IsOutput ? 1 : 0);
                                cmd.Parameters.AddWithValue("$ordinal", param.Ordinal);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    // Insert Triggers
                    var triggers = metadata.Triggers.Where(trg => string.Equals(trg.Database, dbName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var trg in triggers)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = @"INSERT INTO cache_triggers (database_id, schema, name, table_schema, table_name, trigger_type, is_enabled, events)
                                                VALUES ($dbId, $schema, $name, $tableSchema, $tableName, $trgType, $isEnabled, $events);";
                            cmd.Parameters.AddWithValue("$dbId", databaseId);
                            cmd.Parameters.AddWithValue("$schema", trg.Schema);
                            cmd.Parameters.AddWithValue("$name", trg.Name);
                            cmd.Parameters.AddWithValue("$tableSchema", trg.TableSchema);
                            cmd.Parameters.AddWithValue("$tableName", trg.TableName);
                            cmd.Parameters.AddWithValue("$trgType", trg.TriggerType);
                            cmd.Parameters.AddWithValue("$isEnabled", trg.IsEnabled ? 1 : 0);
                            cmd.Parameters.AddWithValue("$events", trg.Events);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    // Insert User-Defined Types
                    var userTypes = metadata.UserTypes.Where(u => string.Equals(u.Database, dbName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var u in userTypes)
                    {
                        long udtId;
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "INSERT INTO cache_user_types (database_id, schema, name, base_type, is_nullable, is_table_type) VALUES ($dbId, $schema, $name, $baseType, $isNullable, $isTableType); SELECT last_insert_rowid();";
                            cmd.Parameters.AddWithValue("$dbId", databaseId);
                            cmd.Parameters.AddWithValue("$schema", u.Schema);
                            cmd.Parameters.AddWithValue("$name", u.Name);
                            cmd.Parameters.AddWithValue("$baseType", u.BaseType);
                            cmd.Parameters.AddWithValue("$isNullable", u.IsNullable ? 1 : 0);
                            cmd.Parameters.AddWithValue("$isTableType", u.IsTableType ? 1 : 0);
                            udtId = Convert.ToInt64(cmd.ExecuteScalar());
                        }

                        foreach (var col in u.Columns)
                        {
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = "INSERT INTO cache_udt_columns (user_type_id, name, data_type, is_nullable, ordinal) VALUES ($udtId, $name, $dataType, $isNullable, $ordinal);";
                                cmd.Parameters.AddWithValue("$udtId", udtId);
                                cmd.Parameters.AddWithValue("$name", col.Name);
                                cmd.Parameters.AddWithValue("$dataType", col.DataType);
                                cmd.Parameters.AddWithValue("$isNullable", col.IsNullable ? 1 : 0);
                                cmd.Parameters.AddWithValue("$ordinal", col.Ordinal);
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    // Insert Synonyms
                    var synonyms = metadata.Synonyms.Where(s => string.Equals(s.Database, dbName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var s in synonyms)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "INSERT INTO cache_synonyms (database_id, schema, name, target_object) VALUES ($dbId, $schema, $name, $targetObj);";
                            cmd.Parameters.AddWithValue("$dbId", databaseId);
                            cmd.Parameters.AddWithValue("$schema", s.Schema);
                            cmd.Parameters.AddWithValue("$name", s.Name);
                            cmd.Parameters.AddWithValue("$targetObj", s.TargetObject);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    // Insert Users
                    var users = metadata.Users.Where(u => string.Equals(u.Database, dbName, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var u in users)
                    {
                        using (var cmd = conn.CreateCommand())
                        {
                            cmd.CommandText = "INSERT INTO cache_users (database_id, name, type, default_schema, create_date) VALUES ($dbId, $name, $type, $defaultSchema, $createDate);";
                            cmd.Parameters.AddWithValue("$dbId", databaseId);
                            cmd.Parameters.AddWithValue("$name", u.Name);
                            cmd.Parameters.AddWithValue("$type", u.Type);
                            cmd.Parameters.AddWithValue("$defaultSchema", u.DefaultSchema);
                            cmd.Parameters.AddWithValue("$createDate", u.CreateDate);
                            cmd.ExecuteNonQuery();
                        }
                    }
                }

                // Insert Linked Servers (instance-level)
                foreach (var ls in metadata.LinkedServers)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO cache_linked_servers (connection_id, name, data_source) VALUES ($connId, $name, $ds);";
                        cmd.Parameters.AddWithValue("$connId", connectionId);
                        cmd.Parameters.AddWithValue("$name", ls.Name);
                        cmd.Parameters.AddWithValue("$ds", ls.DataSource);
                        cmd.ExecuteNonQuery();
                    }
                }

                // Insert Endpoints (instance-level Server Objects)
                foreach (var ep in metadata.Endpoints)
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO cache_endpoints (connection_id, name, type, protocol, state, port) VALUES ($connId, $name, $type, $protocol, $state, $port);";
                        cmd.Parameters.AddWithValue("$connId", connectionId);
                        cmd.Parameters.AddWithValue("$name", ep.Name);
                        cmd.Parameters.AddWithValue("$type", ep.Type);
                        cmd.Parameters.AddWithValue("$protocol", ep.Protocol);
                        cmd.Parameters.AddWithValue("$state", ep.State);
                        cmd.Parameters.AddWithValue("$port", ep.Port);
                        cmd.ExecuteNonQuery();
                    }
                }
            }
            }
        }

        public static IReadOnlyList<ConnectionInfo> GetConnections()
        {
            var list = new List<ConnectionInfo>();
            using (var conn = GetOpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, name, connection_string, is_active, last_seen_at, schema_updated_at FROM connections ORDER BY last_seen_at DESC, name ASC;";
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int id = reader.GetInt32(0);
                            string name = reader.GetString(1);
                            string connStr = reader.GetString(2);
                            bool isActive = reader.GetInt32(3) != 0;
                            DateTimeOffset? lastSeen = null;
                            if (!reader.IsDBNull(4))
                            {
                                if (DateTimeOffset.TryParse(reader.GetString(4), out var dt))
                                    lastSeen = dt;
                            }
                            DateTimeOffset? schemaUpdated = null;
                            if (!reader.IsDBNull(5))
                            {
                                if (DateTimeOffset.TryParse(reader.GetString(5), out var dt))
                                    schemaUpdated = dt;
                            }
                            list.Add(new ConnectionInfo(id, name, connStr, isActive, lastSeen, schemaUpdated));
                        }
                    }
                }
            }
            return list;
        }

        public static (DatabaseMetadata Metadata, string RawJson, DateTimeOffset? SchemaUpdatedAt) GetSchemaDetails(int connectionId)
        {
            using (var conn = GetOpenConnection())
            {
                var details = GetSchemaDetailsByConnectionId(connectionId, conn);
                if (details.SchemaUpdatedAt == null)
                {
                    return (DatabaseMetadata.Empty, string.Empty, null);
                }
                var rawJson = System.Text.Json.JsonSerializer.Serialize(details.Metadata);
                return (details.Metadata, rawJson, details.SchemaUpdatedAt);
            }
        }

        public static DatabaseMetadata GetMetadataByConnectionString(string connectionString)
        {
            var normalizedConnectionString = NormalizeServerConnectionString(connectionString);
            var legacyConnectionString = NormalizeServerConnectionStringLegacy(connectionString);

            using (var conn = GetOpenConnection())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT id FROM connections WHERE LOWER(connection_string) = LOWER($connStr) OR LOWER(connection_string) = LOWER($legacyConnStr);";
                    cmd.Parameters.AddWithValue("$connStr", normalizedConnectionString);
                    cmd.Parameters.AddWithValue("$legacyConnStr", legacyConnectionString);
                    var res = cmd.ExecuteScalar();
                    if (res == null || res == DBNull.Value) return DatabaseMetadata.Empty;
                    int connId = Convert.ToInt32(res);
                    return GetSchemaDetailsByConnectionId(connId, conn).Metadata;
                }
            }
        }

        private static (DatabaseMetadata Metadata, DateTimeOffset? SchemaUpdatedAt) GetSchemaDetailsByConnectionId(int connectionId, SqliteConnection conn)
        {
            // 1. Get SchemaUpdatedAt
            DateTimeOffset? schemaUpdatedAt = null;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT schema_updated_at FROM connections WHERE id = $id;";
                cmd.Parameters.AddWithValue("$id", connectionId);
                var val = cmd.ExecuteScalar();
                if (val != null && val != DBNull.Value && DateTimeOffset.TryParse(val.ToString(), out var dt))
                {
                    schemaUpdatedAt = dt;
                }
            }

            // 2. Get Databases
            var databases = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM cache_databases WHERE connection_id = $connId ORDER BY name;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read()) databases.Add(reader.GetString(0));
                }
            }

            // 3. Get Linked Servers
            var linkedServers = new List<LinkedServerInfo>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, data_source FROM cache_linked_servers WHERE connection_id = $connId ORDER BY name;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var name = reader.GetString(0);
                        var ds = reader.IsDBNull(1) ? name : reader.GetString(1);
                        linkedServers.Add(new LinkedServerInfo(name, ds));
                    }
                }
            }

            // 4. Get Endpoints
            var endpoints = new List<EndpointInfo>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name, type, protocol, state, port FROM cache_endpoints WHERE connection_id = $connId ORDER BY name;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        endpoints.Add(new EndpointInfo(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetInt32(4)));
                    }
                }
            }

            // 5. Get Tables and Columns
            var tablesDict = new Dictionary<long, (string Schema, string Name, string PkCols, string Database, List<ColumnMetadata> Cols)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT t.id, t.schema, t.name, t.pk_columns, c.name, c.data_type, c.is_nullable, c.ordinal, db.name
                    FROM cache_tables t
                    JOIN cache_databases db ON t.database_id = db.id
                    LEFT JOIN cache_columns c ON t.id = c.table_id
                    WHERE db.connection_id = $connId
                    ORDER BY t.id, c.ordinal;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long tid = reader.GetInt64(0);
                        if (!tablesDict.TryGetValue(tid, out var tinfo))
                        {
                            tinfo = (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(8), new List<ColumnMetadata>());
                            tablesDict[tid] = tinfo;
                        }
                        if (!reader.IsDBNull(4))
                        {
                            tinfo.Cols.Add(new ColumnMetadata(
                                reader.GetString(4),
                                reader.GetString(5),
                                reader.GetInt32(6) != 0,
                                reader.GetInt32(7)
                            ));
                        }
                    }
                }
            }
            var tables = tablesDict.Values.Select(t => new TableMetadata(
                t.Schema,
                t.Name,
                t.Cols,
                t.PkCols.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            ) { Database = t.Database }).ToList();

            // 5. Get Views and Columns
            var viewsDict = new Dictionary<long, (string Schema, string Name, bool IsIndexed, string Database, List<ColumnMetadata> Cols)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT v.id, v.schema, v.name, v.is_indexed, c.name, c.data_type, c.is_nullable, c.ordinal, db.name
                    FROM cache_views v
                    JOIN cache_databases db ON v.database_id = db.id
                    LEFT JOIN cache_view_columns c ON v.id = c.view_id
                    WHERE db.connection_id = $connId
                    ORDER BY v.id, c.ordinal;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long vid = reader.GetInt64(0);
                        if (!viewsDict.TryGetValue(vid, out var vinfo))
                        {
                            vinfo = (reader.GetString(1), reader.GetString(2), reader.GetInt32(3) != 0, reader.GetString(8), new List<ColumnMetadata>());
                            viewsDict[vid] = vinfo;
                        }
                        if (!reader.IsDBNull(4))
                        {
                            vinfo.Cols.Add(new ColumnMetadata(
                                reader.GetString(4),
                                reader.GetString(5),
                                reader.GetInt32(6) != 0,
                                reader.GetInt32(7)
                            ));
                        }
                    }
                }
            }
            var views = viewsDict.Values.Select(v => new ViewMetadata(
                v.Schema,
                v.Name,
                v.Cols
            ) { Database = v.Database, IsIndexed = v.IsIndexed }).ToList();

            // 6. Get Foreign Keys
            var foreignKeys = new List<ForeignKeyMetadata>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT fk.name, fk.from_schema, fk.from_table, fk.from_column, fk.to_schema, fk.to_table, fk.to_column, fk.ordinal, db.name
                    FROM cache_foreign_keys fk
                    JOIN cache_databases db ON fk.database_id = db.id
                    WHERE db.connection_id = $connId
                    ORDER BY fk.id;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        foreignKeys.Add(new ForeignKeyMetadata(
                            reader.GetString(0),
                            reader.GetString(1),
                            reader.GetString(2),
                            reader.GetString(3),
                            reader.GetString(4),
                            reader.GetString(5),
                            reader.GetString(6),
                            reader.GetInt32(7)
                        ) { Database = reader.GetString(8) });
                    }
                }
            }

            // 7. Get Indexes and Columns
            var indexesDict = new Dictionary<long, (string Schema, string TableName, string Name, bool IsUnique, string Database, List<string> Cols)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT i.id, i.schema, i.table_name, i.name, i.is_unique, c.column_name, db.name
                    FROM cache_indexes i
                    JOIN cache_databases db ON i.database_id = db.id
                    LEFT JOIN cache_index_cols c ON i.id = c.index_id
                    WHERE db.connection_id = $connId
                    ORDER BY i.id, c.ordinal;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long iid = reader.GetInt64(0);
                        if (!indexesDict.TryGetValue(iid, out var iinfo))
                        {
                            iinfo = (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4) != 0, reader.GetString(6), new List<string>());
                            indexesDict[iid] = iinfo;
                        }
                        if (!reader.IsDBNull(5))
                        {
                            iinfo.Cols.Add(reader.GetString(5));
                        }
                    }
                }
            }
            var indexes = indexesDict.Values.Select(i => new IndexMetadata(
                i.Schema,
                i.TableName,
                i.Name,
                i.IsUnique,
                i.Cols
            ) { Database = i.Database }).ToList();

            // 8. Get Procedures and Params
            var proceduresDict = new Dictionary<long, (string Schema, string Name, string ObjectType, string Database, List<FunctionParameterMetadata> Params)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT p.id, p.schema, p.name, p.object_type, pp.name, pp.data_type, pp.is_output, pp.ordinal, db.name
                    FROM cache_procedures p
                    JOIN cache_databases db ON p.database_id = db.id
                    LEFT JOIN cache_proc_params pp ON p.id = pp.procedure_id
                    WHERE db.connection_id = $connId
                    ORDER BY p.id, pp.ordinal;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long pid = reader.GetInt64(0);
                        if (!proceduresDict.TryGetValue(pid, out var pinfo))
                        {
                            pinfo = (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(8), new List<FunctionParameterMetadata>());
                            proceduresDict[pid] = pinfo;
                        }
                        if (!reader.IsDBNull(4))
                        {
                            pinfo.Params.Add(new FunctionParameterMetadata(
                                reader.GetString(4),
                                reader.GetString(5),
                                reader.GetInt32(6) != 0,
                                reader.GetInt32(7)
                            ));
                        }
                    }
                }
            }

            var procedures = proceduresDict.Values.Select(p => new ProcedureMetadata(p.Schema, p.Name)
            {
                ObjectType = p.ObjectType,
                Database = p.Database,
                Parameters = p.Params
            }).ToList();

            // 9. Get Functions and Params
            var functionsDict = new Dictionary<long, (string Schema, string Name, string FnType, string ReturnType, string Database, List<FunctionParameterMetadata> Params)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT f.id, f.schema, f.name, f.fn_type, f.return_type, p.name, p.data_type, p.is_output, p.ordinal, db.name
                    FROM cache_functions f
                    JOIN cache_databases db ON f.database_id = db.id
                    LEFT JOIN cache_fn_params p ON f.id = p.function_id
                    WHERE db.connection_id = $connId
                    ORDER BY f.id, p.ordinal;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long fid = reader.GetInt64(0);
                        if (!functionsDict.TryGetValue(fid, out var finfo))
                        {
                            finfo = (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(9), new List<FunctionParameterMetadata>());
                            functionsDict[fid] = finfo;
                        }
                        if (!reader.IsDBNull(5))
                        {
                            finfo.Params.Add(new FunctionParameterMetadata(
                                reader.GetString(5),
                                reader.GetString(6),
                                reader.GetInt32(7) != 0,
                                reader.GetInt32(8)
                            ));
                        }
                    }
                }
            }
            var functions = functionsDict.Values.Select(f => new FunctionMetadata(f.Schema, f.Name)
            {
                Database = f.Database,
                FunctionType = f.FnType,
                ReturnType = f.ReturnType,
                Parameters = f.Params
            }).ToList();

            // 10. Get Triggers
            var triggers = new List<TriggerMetadata>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT t.schema, t.name, t.table_schema, t.table_name, t.trigger_type, t.is_enabled, t.events, db.name
                    FROM cache_triggers t
                    JOIN cache_databases db ON t.database_id = db.id
                    WHERE db.connection_id = $connId;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        triggers.Add(new TriggerMetadata(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
                        {
                            TriggerType = reader.GetString(4),
                            IsEnabled = reader.GetInt32(5) != 0,
                            Events = reader.GetString(6),
                            Database = reader.GetString(7)
                        });
                    }
                }
            }

            // 11. Get User-Defined Types
            var udtDict = new Dictionary<long, (string Schema, string Name, string BaseType, bool IsNullable, bool IsTableType, string Database, List<ColumnMetadata> Cols)>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT u.id, u.schema, u.name, u.base_type, u.is_nullable, u.is_table_type, c.name, c.data_type, c.is_nullable, c.ordinal, db.name
                    FROM cache_user_types u
                    JOIN cache_databases db ON u.database_id = db.id
                    LEFT JOIN cache_udt_columns c ON u.id = c.user_type_id
                    WHERE db.connection_id = $connId
                    ORDER BY u.id, c.ordinal;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        long uid = reader.GetInt64(0);
                        if (!udtDict.TryGetValue(uid, out var uinfo))
                        {
                            uinfo = (reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4) != 0, reader.GetInt32(5) != 0, reader.GetString(10), new List<ColumnMetadata>());
                            udtDict[uid] = uinfo;
                        }
                        if (!reader.IsDBNull(6))
                        {
                            uinfo.Cols.Add(new ColumnMetadata(
                                reader.GetString(6),
                                reader.GetString(7),
                                reader.GetInt32(8) != 0,
                                reader.GetInt32(9)
                            ));
                        }
                    }
                }
            }
            var userTypes = udtDict.Values.Select(u => new UserTypeMetadata(u.Schema, u.Name)
            {
                Database = u.Database,
                BaseType = u.BaseType,
                IsNullable = u.IsNullable,
                IsTableType = u.IsTableType,
                Columns = u.Cols
            }).ToList();

            // 12. Get Synonyms
            var synonyms = new List<SynonymMetadata>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT s.schema, s.name, s.target_object, db.name
                    FROM cache_synonyms s
                    JOIN cache_databases db ON s.database_id = db.id
                    WHERE db.connection_id = $connId;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        synonyms.Add(new SynonymMetadata(reader.GetString(0), reader.GetString(1), reader.GetString(2))
                        {
                            Database = reader.GetString(3)
                        });
                    }
                }
            }

            // 13. Get Users
            var users = new List<UserMetadata>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
                    SELECT u.name, u.type, u.default_schema, u.create_date, db.name
                    FROM cache_users u
                    JOIN cache_databases db ON u.database_id = db.id
                    WHERE db.connection_id = $connId;";
                cmd.Parameters.AddWithValue("$connId", connectionId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        users.Add(new UserMetadata(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
                        {
                            Database = reader.GetString(4)
                        });
                    }
                }
            }

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

            return (metadata, schemaUpdatedAt);
        }
    }
}
#endif
