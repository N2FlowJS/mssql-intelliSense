using System.Linq;

namespace MssqlIntelliSense.Core.Metadata;

/// <summary>Thông tin một linked server: tên alias và data_source thực.</summary>
public sealed record LinkedServerInfo(string Name, string DataSource);

/// <summary>SQL Server endpoint thuộc instance-level Server Objects.</summary>
public sealed record EndpointInfo(string Name, string Type, string Protocol, string State, int Port);

public sealed record DatabaseMetadata(
    IReadOnlyList<TableMetadata> Tables,
    IReadOnlyList<ForeignKeyMetadata> ForeignKeys,
    IReadOnlyList<IndexMetadata> Indexes,
    IReadOnlyList<string> Databases,
    IReadOnlyList<LinkedServerInfo> LinkedServers)
{
    public static DatabaseMetadata Empty { get; } = new([], [], [], [], []);

    public IReadOnlyList<ProcedureMetadata>  Procedures  { get; init; } = Array.Empty<ProcedureMetadata>();
    public IReadOnlyList<ViewMetadata>       Views       { get; init; } = Array.Empty<ViewMetadata>();
    public IReadOnlyList<FunctionMetadata>   Functions   { get; init; } = Array.Empty<FunctionMetadata>();
    public IReadOnlyList<TriggerMetadata>    Triggers    { get; init; } = Array.Empty<TriggerMetadata>();
    public IReadOnlyList<UserTypeMetadata>   UserTypes   { get; init; } = Array.Empty<UserTypeMetadata>();
    public IReadOnlyList<SynonymMetadata>    Synonyms    { get; init; } = Array.Empty<SynonymMetadata>();
    public IReadOnlyList<UserMetadata>       Users       { get; init; } = Array.Empty<UserMetadata>();
    public IReadOnlyList<EndpointInfo>       Endpoints   { get; init; } = Array.Empty<EndpointInfo>();

    public TableMetadata? FindTable(string? schema, string name) => Tables.FirstOrDefault(table =>
        table.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
        (schema is null || table.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)));
}

// ─── Existing types ────────────────────────────────────────────────────────────

public sealed record ProcedureMetadata(string Schema, string Name)
{
    public string Database { get; init; } = "";
    /// <summary>P = Stored Procedure, FN/TF/IF/FS/FT/PC = Function variant</summary>
    public string ObjectType { get; init; } = "P";
    public string Connection { get; init; } = "";
    public int ConnectionId { get; init; } = 0;
    public IReadOnlyList<FunctionParameterMetadata> Parameters { get; init; }
        = Array.Empty<FunctionParameterMetadata>();

    public string Description => $"Stored procedure {Schema}.{Name} trong database {Database}. Tham số: {(Parameters.Count > 0 ? string.Join(", ", Parameters.Select(p => $"{p.Name} ({p.DataType})")) : "Không có")}.";
    public string Keywords => $"procedure,stored-procedure,proc,{Schema},{Name},{string.Join(",", Parameters.Select(p => p.Name.TrimStart('@').ToLowerInvariant()))}";
}

public sealed record TableMetadata(
    string Schema,
    string Name,
    IReadOnlyList<ColumnMetadata> Columns,
    IReadOnlyList<string> PrimaryKeyColumns)
{
    public string Database { get; init; } = "";
    public string Connection { get; init; } = "";
    public int ConnectionId { get; init; } = 0;
    public string PrimaryKeyColumnsString => string.Join(", ", PrimaryKeyColumns);

    public string Description => $"Bảng {Schema}.{Name} trong database {Database}. Chứa các cột: {string.Join(", ", Columns.Select(c => c.Name))}.";
    public string Keywords => $"table,{Schema},{Name},{string.Join(",", Columns.Select(c => c.Name.ToLowerInvariant()))}";
}

public sealed record ColumnMetadata(string Name, string DataType, bool IsNullable, int Ordinal);

public sealed record ForeignKeyMetadata(
    string Name,
    string FromSchema,
    string FromTable,
    string FromColumn,
    string ToSchema,
    string ToTable,
    string ToColumn,
    int Ordinal)
{
    public string Database { get; init; } = "";
    public string Connection { get; init; } = "";
    public int ConnectionId { get; init; } = 0;
}

public sealed record IndexMetadata(
    string Schema,
    string Table,
    string Name,
    bool IsUnique,
    IReadOnlyList<string> Columns)
{
    public string Database { get; init; } = "";
    public string Connection { get; init; } = "";
    public int ConnectionId { get; init; } = 0;
}

// ─── New types ─────────────────────────────────────────────────────────────────

/// <summary>SQL Server View with optional column list.</summary>
public sealed record ViewMetadata(
    string Schema,
    string Name,
    IReadOnlyList<ColumnMetadata> Columns)
{
    public string Database  { get; init; } = "";
    public bool   IsIndexed { get; init; } = false;
    public string Connection { get; init; } = "";
    public int ConnectionId { get; init; } = 0;

    public string Description => $"View {Schema}.{Name} trong database {Database}. Chứa các cột: {string.Join(", ", Columns.Select(c => c.Name))}.";
    public string Keywords => $"view,{Schema},{Name},{string.Join(",", Columns.Select(c => c.Name.ToLowerInvariant()))}";
}

/// <summary>
/// Scalar / Inline-Table / Multi-Statement Table-Valued / Aggregate / CLR function.
/// </summary>
public sealed record FunctionMetadata(string Schema, string Name)
{
    public string Database    { get; init; } = "";
    /// <summary>FN = Scalar, TF = Multi-stmt TVF, IF = Inline TVF, AF = Aggregate, FS/FT = CLR</summary>
    public string FunctionType { get; init; } = "FN";
    public string ReturnType  { get; init; } = "";
    public IReadOnlyList<FunctionParameterMetadata> Parameters { get; init; }
        = Array.Empty<FunctionParameterMetadata>();
    public string Connection { get; init; } = "";
    public int ConnectionId { get; init; } = 0;

    public string Description => $"Function {Schema}.{Name} ({FunctionType}) trong database {Database}. Kiểu trả về: {ReturnType}. Tham số: {(Parameters.Count > 0 ? string.Join(", ", Parameters.Select(p => $"{p.Name} ({p.DataType})")) : "Không có")}.";
    public string Keywords => $"function,func,fn,{Schema},{Name},{ReturnType.ToLowerInvariant()},{string.Join(",", Parameters.Select(p => p.Name.TrimStart('@').ToLowerInvariant()))}";
}

public sealed record FunctionParameterMetadata(string Name, string DataType, bool IsOutput, int Ordinal);

/// <summary>DML / DDL / Logon trigger.</summary>
public sealed record TriggerMetadata(string Schema, string Name, string TableSchema, string TableName)
{
    public string Database   { get; init; } = "";
    /// <summary>TR = DML, TA = CLR DML, E = DDL</summary>
    public string TriggerType { get; init; } = "TR";
    public bool   IsEnabled  { get; init; } = true;
    public string Events     { get; init; } = "";
    public string Connection { get; init; } = "";
    public int ConnectionId { get; init; } = 0;
}

/// <summary>User-Defined Type (alias, CLR, table type).</summary>
public sealed record UserTypeMetadata(string Schema, string Name)
{
    public string Database     { get; init; } = "";
    public string BaseType     { get; init; } = "";
    public bool   IsNullable   { get; init; } = true;
    public bool   IsTableType  { get; init; } = false;
    /// <summary>Columns of a Table-Valued UDT.</summary>
    public IReadOnlyList<ColumnMetadata> Columns { get; init; } = Array.Empty<ColumnMetadata>();
    public string Connection { get; init; } = "";
    public int ConnectionId { get; init; } = 0;
}

/// <summary>SQL Server Synonym pointing to another object.</summary>
public sealed record SynonymMetadata(string Schema, string Name, string TargetObject)
{
    public string Database { get; init; } = "";
    public string Connection { get; init; } = "";
    public int ConnectionId { get; init; } = 0;
}

/// <summary>Database user / principal (from sys.database_principals).</summary>
public sealed record UserMetadata(string Name, string Type, string DefaultSchema, string CreateDate)
{
    public string Database { get; init; } = "";
    public string Connection { get; init; } = "";
    public int ConnectionId { get; init; } = 0;
}
