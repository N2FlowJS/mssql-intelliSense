#if NET
using Microsoft.EntityFrameworkCore;

namespace MssqlIntelliSense.Core.Cache;

/// <summary>
/// EF Core DbContext cho SQLite cache của MssqlIntelliSense.
/// Dùng <see cref="EnsureCreated"/> (không dùng Migrations) để tương thích với netstandard2.0 runtime.
/// </summary>
public sealed class MssqlIntelliSenseDbContext : DbContext
{
    private readonly string _connectionString;

    public MssqlIntelliSenseDbContext(string connectionString)
    {
        _connectionString = connectionString;
    }

    // ── Root ──────────────────────────────────────────────────────────────────
    public DbSet<ConnectionEntity>       Connections    => Set<ConnectionEntity>();

    // ── Per-connection schema cache ───────────────────────────────────────────
    public DbSet<CacheTableEntity>       CacheTables       => Set<CacheTableEntity>();
    public DbSet<CacheColumnEntity>      CacheColumns      => Set<CacheColumnEntity>();
    public DbSet<CacheViewEntity>        CacheViews        => Set<CacheViewEntity>();
    public DbSet<CacheViewColumnEntity>  CacheViewColumns  => Set<CacheViewColumnEntity>();
    public DbSet<CacheForeignKeyEntity>  CacheForeignKeys  => Set<CacheForeignKeyEntity>();
    public DbSet<CacheIndexEntity>       CacheIndexes      => Set<CacheIndexEntity>();
    public DbSet<CacheIndexColumnEntity> CacheIndexColumns => Set<CacheIndexColumnEntity>();
    public DbSet<CacheProcedureEntity>   CacheProcedures   => Set<CacheProcedureEntity>();
    public DbSet<CacheProcedureParamEntity> CacheProcedureParams => Set<CacheProcedureParamEntity>();
    public DbSet<CacheFunctionEntity>    CacheFunctions    => Set<CacheFunctionEntity>();
    public DbSet<CacheFunctionParamEntity> CacheFunctionParams => Set<CacheFunctionParamEntity>();
    public DbSet<CacheTriggerEntity>     CacheTriggers     => Set<CacheTriggerEntity>();
    public DbSet<CacheUserTypeEntity>    CacheUserTypes    => Set<CacheUserTypeEntity>();
    public DbSet<CacheUdtColumnEntity>   CacheUdtColumns   => Set<CacheUdtColumnEntity>();
    public DbSet<CacheSynonymEntity>     CacheSynonyms     => Set<CacheSynonymEntity>();
    public DbSet<CacheUserEntity>        CacheUsers        => Set<CacheUserEntity>();
    public DbSet<CacheDatabaseEntity>    CacheDatabases    => Set<CacheDatabaseEntity>();
    public DbSet<CacheLinkedServerEntity> CacheLinkedServers => Set<CacheLinkedServerEntity>();
    public DbSet<CacheEndpointEntity> CacheEndpoints => Set<CacheEndpointEntity>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseSqlite(_connectionString, sqlite => sqlite.CommandTimeout(30));
    }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        // ── connections ──────────────────────────────────────────────────────
        mb.Entity<ConnectionEntity>(e =>
        {
            e.ToTable("connections");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(c => c.Name).HasColumnName("name").IsRequired();
            e.Property(c => c.ConnectionString).HasColumnName("connection_string").IsRequired();
            e.Property(c => c.IsActive).HasColumnName("is_active");
            e.Property(c => c.LastSeenAt).HasColumnName("last_seen_at");
            e.Property(c => c.SchemaUpdatedAt).HasColumnName("schema_updated_at");

            e.HasIndex(c => c.ConnectionString)
             .IsUnique()
             .HasDatabaseName("IX_connections_connection_string");
        });

        // ── cache_tables ─────────────────────────────────────────────────────
        mb.Entity<CacheTableEntity>(e =>
        {
            e.ToTable("cache_tables");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(t => t.DatabaseId).HasColumnName("database_id");
            e.Property(t => t.Schema).HasColumnName("schema");
            e.Property(t => t.Name).HasColumnName("name");
            e.Property(t => t.PkColumns).HasColumnName("pk_columns");

            e.HasOne(t => t.Database).WithMany(d => d.Tables)
             .HasForeignKey(t => t.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => t.DatabaseId).HasDatabaseName("IX_cache_tables_db");
        });

        // ── cache_columns ─────────────────────────────────────────────────────
        mb.Entity<CacheColumnEntity>(e =>
        {
            e.ToTable("cache_columns");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(c => c.TableId).HasColumnName("table_id");
            e.Property(c => c.Name).HasColumnName("name");
            e.Property(c => c.DataType).HasColumnName("data_type");
            e.Property(c => c.IsNullable).HasColumnName("is_nullable");
            e.Property(c => c.Ordinal).HasColumnName("ordinal");

            e.HasOne(c => c.Table).WithMany(t => t.Columns)
             .HasForeignKey(c => c.TableId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── cache_views ───────────────────────────────────────────────────────
        mb.Entity<CacheViewEntity>(e =>
        {
            e.ToTable("cache_views");
            e.HasKey(v => v.Id);
            e.Property(v => v.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(v => v.DatabaseId).HasColumnName("database_id");
            e.Property(v => v.Schema).HasColumnName("schema");
            e.Property(v => v.Name).HasColumnName("name");
            e.Property(v => v.IsIndexed).HasColumnName("is_indexed");

            e.HasOne(v => v.Database).WithMany(d => d.Views)
             .HasForeignKey(v => v.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(v => v.DatabaseId).HasDatabaseName("IX_cache_views_db");
        });

        // ── cache_view_columns ────────────────────────────────────────────────
        mb.Entity<CacheViewColumnEntity>(e =>
        {
            e.ToTable("cache_view_columns");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(c => c.ViewId).HasColumnName("view_id");
            e.Property(c => c.Name).HasColumnName("name");
            e.Property(c => c.DataType).HasColumnName("data_type");
            e.Property(c => c.IsNullable).HasColumnName("is_nullable");
            e.Property(c => c.Ordinal).HasColumnName("ordinal");

            e.HasOne(c => c.View).WithMany(v => v.Columns)
             .HasForeignKey(c => c.ViewId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── cache_foreign_keys ────────────────────────────────────────────────
        mb.Entity<CacheForeignKeyEntity>(e =>
        {
            e.ToTable("cache_foreign_keys");
            e.HasKey(fk => fk.Id);
            e.Property(fk => fk.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(fk => fk.DatabaseId).HasColumnName("database_id");
            e.Property(fk => fk.Name).HasColumnName("name");
            e.Property(fk => fk.FromSchema).HasColumnName("from_schema");
            e.Property(fk => fk.FromTable).HasColumnName("from_table");
            e.Property(fk => fk.FromColumn).HasColumnName("from_column");
            e.Property(fk => fk.ToSchema).HasColumnName("to_schema");
            e.Property(fk => fk.ToTable).HasColumnName("to_table");
            e.Property(fk => fk.ToColumn).HasColumnName("to_column");
            e.Property(fk => fk.Ordinal).HasColumnName("ordinal");

            e.HasOne(fk => fk.Database).WithMany(d => d.ForeignKeys)
             .HasForeignKey(fk => fk.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(fk => fk.DatabaseId).HasDatabaseName("IX_cache_fk_db");
        });

        // ── cache_indexes ─────────────────────────────────────────────────────
        mb.Entity<CacheIndexEntity>(e =>
        {
            e.ToTable("cache_indexes");
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(i => i.DatabaseId).HasColumnName("database_id");
            e.Property(i => i.Schema).HasColumnName("schema");
            e.Property(i => i.TableName).HasColumnName("table_name");
            e.Property(i => i.Name).HasColumnName("name");
            e.Property(i => i.IsUnique).HasColumnName("is_unique");

            e.HasOne(i => i.Database).WithMany(d => d.Indexes)
             .HasForeignKey(i => i.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(i => i.DatabaseId).HasDatabaseName("IX_cache_idx_db");
        });

        // ── cache_index_cols ──────────────────────────────────────────────────
        mb.Entity<CacheIndexColumnEntity>(e =>
        {
            e.ToTable("cache_index_cols");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(c => c.IndexId).HasColumnName("index_id");
            e.Property(c => c.ColumnName).HasColumnName("column_name");
            e.Property(c => c.Ordinal).HasColumnName("ordinal");

            e.HasOne(c => c.Index).WithMany(i => i.Columns)
             .HasForeignKey(c => c.IndexId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── cache_procedures ──────────────────────────────────────────────────
        mb.Entity<CacheProcedureEntity>(e =>
        {
            e.ToTable("cache_procedures");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.DatabaseId).HasColumnName("database_id");
            e.Property(p => p.Schema).HasColumnName("schema");
            e.Property(p => p.Name).HasColumnName("name");
            e.Property(p => p.ObjectType).HasColumnName("object_type");

            e.HasOne(p => p.Database).WithMany(d => d.Procedures)
             .HasForeignKey(p => p.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => p.DatabaseId).HasDatabaseName("IX_cache_proc_db");
        });

        // ── cache_proc_params ─────────────────────────────────────────────────
        mb.Entity<CacheProcedureParamEntity>(e =>
        {
            e.ToTable("cache_proc_params");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.ProcedureId).HasColumnName("procedure_id");
            e.Property(p => p.Name).HasColumnName("name");
            e.Property(p => p.DataType).HasColumnName("data_type");
            e.Property(p => p.IsOutput).HasColumnName("is_output");
            e.Property(p => p.Ordinal).HasColumnName("ordinal");

            e.HasOne(p => p.Procedure).WithMany(p => p.Parameters)
             .HasForeignKey(p => p.ProcedureId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── cache_functions ───────────────────────────────────────────────────
        mb.Entity<CacheFunctionEntity>(e =>
        {
            e.ToTable("cache_functions");
            e.HasKey(f => f.Id);
            e.Property(f => f.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(f => f.DatabaseId).HasColumnName("database_id");
            e.Property(f => f.Schema).HasColumnName("schema");
            e.Property(f => f.Name).HasColumnName("name");
            e.Property(f => f.FnType).HasColumnName("fn_type");
            e.Property(f => f.ReturnType).HasColumnName("return_type");

            e.HasOne(f => f.Database).WithMany(d => d.Functions)
             .HasForeignKey(f => f.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(f => f.DatabaseId).HasDatabaseName("IX_cache_fn_db");
        });

        // ── cache_fn_params ───────────────────────────────────────────────────
        mb.Entity<CacheFunctionParamEntity>(e =>
        {
            e.ToTable("cache_fn_params");
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(p => p.FunctionId).HasColumnName("function_id");
            e.Property(p => p.Name).HasColumnName("name");
            e.Property(p => p.DataType).HasColumnName("data_type");
            e.Property(p => p.IsOutput).HasColumnName("is_output");
            e.Property(p => p.Ordinal).HasColumnName("ordinal");

            e.HasOne(p => p.Function).WithMany(f => f.Parameters)
             .HasForeignKey(p => p.FunctionId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── cache_triggers ────────────────────────────────────────────────────
        mb.Entity<CacheTriggerEntity>(e =>
        {
            e.ToTable("cache_triggers");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(t => t.DatabaseId).HasColumnName("database_id");
            e.Property(t => t.Schema).HasColumnName("schema");
            e.Property(t => t.Name).HasColumnName("name");
            e.Property(t => t.TableSchema).HasColumnName("table_schema");
            e.Property(t => t.TableName).HasColumnName("table_name");
            e.Property(t => t.TriggerType).HasColumnName("trigger_type");
            e.Property(t => t.IsEnabled).HasColumnName("is_enabled");
            e.Property(t => t.Events).HasColumnName("events");

            e.HasOne(t => t.Database).WithMany(d => d.Triggers)
             .HasForeignKey(t => t.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(t => t.DatabaseId).HasDatabaseName("IX_cache_trg_db");
        });

        // ── cache_user_types ──────────────────────────────────────────────────
        mb.Entity<CacheUserTypeEntity>(e =>
        {
            e.ToTable("cache_user_types");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(u => u.DatabaseId).HasColumnName("database_id");
            e.Property(u => u.Schema).HasColumnName("schema");
            e.Property(u => u.Name).HasColumnName("name");
            e.Property(u => u.BaseType).HasColumnName("base_type");
            e.Property(u => u.IsNullable).HasColumnName("is_nullable");
            e.Property(u => u.IsTableType).HasColumnName("is_table_type");

            e.HasOne(u => u.Database).WithMany(d => d.UserTypes)
             .HasForeignKey(u => u.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(u => u.DatabaseId).HasDatabaseName("IX_cache_udt_db");
        });

        // ── cache_udt_columns ─────────────────────────────────────────────────
        mb.Entity<CacheUdtColumnEntity>(e =>
        {
            e.ToTable("cache_udt_columns");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(c => c.UserTypeId).HasColumnName("user_type_id");
            e.Property(c => c.Name).HasColumnName("name");
            e.Property(c => c.DataType).HasColumnName("data_type");
            e.Property(c => c.IsNullable).HasColumnName("is_nullable");
            e.Property(c => c.Ordinal).HasColumnName("ordinal");

            e.HasOne(c => c.UserType).WithMany(u => u.Columns)
             .HasForeignKey(c => c.UserTypeId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── cache_synonyms ────────────────────────────────────────────────────
        mb.Entity<CacheSynonymEntity>(e =>
        {
            e.ToTable("cache_synonyms");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(s => s.DatabaseId).HasColumnName("database_id");
            e.Property(s => s.Schema).HasColumnName("schema");
            e.Property(s => s.Name).HasColumnName("name");
            e.Property(s => s.TargetObject).HasColumnName("target_object");

            e.HasOne(s => s.Database).WithMany(d => d.Synonyms)
             .HasForeignKey(s => s.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.DatabaseId).HasDatabaseName("IX_cache_syn_db");
        });

        // ── cache_users ───────────────────────────────────────────────────────
        mb.Entity<CacheUserEntity>(e =>
        {
            e.ToTable("cache_users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(u => u.DatabaseId).HasColumnName("database_id");
            e.Property(u => u.Name).HasColumnName("name");
            e.Property(u => u.Type).HasColumnName("type");
            e.Property(u => u.DefaultSchema).HasColumnName("default_schema");
            e.Property(u => u.CreateDate).HasColumnName("create_date");

            e.HasOne(u => u.Database).WithMany(d => d.Users)
             .HasForeignKey(u => u.DatabaseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(u => u.DatabaseId).HasDatabaseName("IX_cache_users_db");
        });

        // ── cache_databases ───────────────────────────────────────────────────
        mb.Entity<CacheDatabaseEntity>(e =>
        {
            e.ToTable("cache_databases");
            e.HasKey(d => d.Id);
            e.Property(d => d.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(d => d.ConnectionId).HasColumnName("connection_id");
            e.Property(d => d.Name).HasColumnName("name");

            e.HasOne(d => d.Connection).WithMany(c => c.Databases)
             .HasForeignKey(d => d.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── cache_linked_servers ──────────────────────────────────────────────
        mb.Entity<CacheLinkedServerEntity>(e =>
        {
            e.ToTable("cache_linked_servers");
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(l => l.ConnectionId).HasColumnName("connection_id");
            e.Property(l => l.Name).HasColumnName("name");
            e.Property(l => l.DataSource).HasColumnName("data_source");

            e.HasOne(l => l.Connection).WithMany(c => c.LinkedServers)
             .HasForeignKey(l => l.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── cache_endpoints ──────────────────────────────────────────────────
        mb.Entity<CacheEndpointEntity>(e =>
        {
            e.ToTable("cache_endpoints");
            e.HasKey(ep => ep.Id);
            e.Property(ep => ep.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(ep => ep.ConnectionId).HasColumnName("connection_id");
            e.Property(ep => ep.Name).HasColumnName("name");
            e.Property(ep => ep.Type).HasColumnName("type");
            e.Property(ep => ep.Protocol).HasColumnName("protocol");
            e.Property(ep => ep.State).HasColumnName("state");
            e.Property(ep => ep.Port).HasColumnName("port");

            e.HasOne(ep => ep.Connection).WithMany(c => c.Endpoints)
             .HasForeignKey(ep => ep.ConnectionId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Đảm bảo database + schema tồn tại, bật WAL mode, chạy migrations thủ công nếu cần.
    /// </summary>
    public void EnsureInitialized()
    {
        Database.EnsureCreated();
        Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        Database.ExecuteSqlRaw("PRAGMA foreign_keys=ON;");

        // Migration: thêm schema_updated_at vào connections nếu chưa có (existing DB)
        try { Database.ExecuteSqlRaw("ALTER TABLE connections ADD COLUMN schema_updated_at TEXT;"); }
        catch { /* column already exists */ }

        // Migration: thêm data_source vào cache_linked_servers nếu chưa có
        try { Database.ExecuteSqlRaw("ALTER TABLE cache_linked_servers ADD COLUMN data_source TEXT NOT NULL DEFAULT '';"); }
        catch { /* column already exists */ }

        Database.ExecuteSqlRaw("""
            CREATE TABLE IF NOT EXISTS cache_endpoints (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                connection_id INTEGER NOT NULL,
                name TEXT NOT NULL,
                type TEXT NOT NULL,
                protocol TEXT NOT NULL,
                state TEXT NOT NULL,
                port INTEGER NOT NULL,
                FOREIGN KEY (connection_id) REFERENCES connections (id) ON DELETE CASCADE
            );
            """);

        // Drop legacy connection_schemas table if it exists
        Database.ExecuteSqlRaw("DROP TABLE IF EXISTS connection_schemas;");
    }
}
#endif
