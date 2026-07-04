using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MssqlIntelliSense.Core.Metadata;

public sealed class GraphqlMetadataProvider : IMetadataProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _endpointUrl;

    public GraphqlMetadataProvider(HttpClient httpClient, string endpointUrl)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpointUrl = string.IsNullOrWhiteSpace(endpointUrl) 
            ? throw new ArgumentException("Value cannot be null or whitespace.", nameof(endpointUrl)) 
            : endpointUrl;
    }

    public async Task<DatabaseMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        var requestBody = new
        {
            query = MetadataQuery
        };

        using var response = await _httpClient.PostAsJsonAsync(_endpointUrl, requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseData = await response.Content.ReadFromJsonAsync<GraphqlResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, 
            cancellationToken);

        if (responseData?.Data == null)
        {
            return DatabaseMetadata.Empty;
        }

        var tables = responseData.Data.Tables?.Select(t => new TableMetadata(
            t.Schema ?? "dbo",
            t.Name ?? "",
            t.Columns?.Select(c => new ColumnMetadata(c.Name ?? "", c.DataType ?? "", c.IsNullable, c.Ordinal)).ToArray() ?? Array.Empty<ColumnMetadata>(),
            t.Columns?.Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).Select(c => c.Name ?? "").ToArray() ?? Array.Empty<string>()
        )).ToArray() ?? Array.Empty<TableMetadata>();

        var foreignKeys = responseData.Data.ForeignKeys?.Select(fk => new ForeignKeyMetadata(
            fk.Name ?? "", fk.FromSchema ?? "", fk.FromTable ?? "", fk.FromColumn ?? "",
            fk.ToSchema ?? "", fk.ToTable ?? "", fk.ToColumn ?? "", fk.Ordinal
        )).ToArray() ?? Array.Empty<ForeignKeyMetadata>();

        var indexes = responseData.Data.Indexes?.Select(idx => new IndexMetadata(
            idx.Schema ?? "", idx.Table ?? "", idx.Name ?? "", idx.IsUnique,
            idx.Columns?.ToArray() ?? Array.Empty<string>()
        )).ToArray() ?? Array.Empty<IndexMetadata>();

        return new DatabaseMetadata(tables, foreignKeys, indexes,
            responseData.Data.Databases?.ToArray() ?? Array.Empty<string>(),
            responseData.Data.LinkedServers?.Select(name => new LinkedServerInfo(name, name)).ToArray() ?? Array.Empty<LinkedServerInfo>())
        {
            Procedures = responseData.Data.Procedures?.Select(p => new ProcedureMetadata(p.Schema ?? "", p.Name ?? "")).ToArray() ?? Array.Empty<ProcedureMetadata>()
        };
    }

    /// <summary>
    /// Fetches schema only for the connection matching <paramref name="connectionString"/>
    /// from the GraphQL server. Falls back to the full aggregated metadata when the
    /// server does not yet have data for that connection.
    /// </summary>
    public async Task<DatabaseMetadata> GetMetadataByConnectionStringAsync(
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        var requestBody = new
        {
            query = MetadataByConnectionStringQuery,
            variables = new { connectionString }
        };

        using var response = await _httpClient.PostAsJsonAsync(_endpointUrl, requestBody, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseData = await response.Content.ReadFromJsonAsync<GraphqlByConnectionResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
            cancellationToken);

        var data = responseData?.Data?.SchemaByConnectionString;
        if (data == null)
            return DatabaseMetadata.Empty;

        var tables = data.Tables?.Select(t => new TableMetadata(
            t.Schema ?? "dbo",
            t.Name ?? "",
            t.Columns?.Select(c => new ColumnMetadata(c.Name ?? "", c.DataType ?? "", c.IsNullable, c.Ordinal)).ToArray() ?? Array.Empty<ColumnMetadata>(),
            t.Columns?.Where(c => c.IsPrimaryKey).OrderBy(c => c.Ordinal).Select(c => c.Name ?? "").ToArray() ?? Array.Empty<string>()
        )).ToArray() ?? Array.Empty<TableMetadata>();

        var foreignKeys = data.ForeignKeys?.Select(fk => new ForeignKeyMetadata(
            fk.Name ?? "", fk.FromSchema ?? "", fk.FromTable ?? "", fk.FromColumn ?? "",
            fk.ToSchema ?? "", fk.ToTable ?? "", fk.ToColumn ?? "", fk.Ordinal
        )).ToArray() ?? Array.Empty<ForeignKeyMetadata>();

        var indexes = data.Indexes?.Select(idx => new IndexMetadata(
            idx.Schema ?? "", idx.Table ?? "", idx.Name ?? "", idx.IsUnique,
            idx.Columns?.ToArray() ?? Array.Empty<string>()
        )).ToArray() ?? Array.Empty<IndexMetadata>();

        return new DatabaseMetadata(tables, foreignKeys, indexes,
            data.Databases?.ToArray() ?? Array.Empty<string>(),
            data.LinkedServers?.Select(name => new LinkedServerInfo(name, name)).ToArray() ?? Array.Empty<LinkedServerInfo>())
        {
            Procedures = data.Procedures?.Select(p => new ProcedureMetadata(p.Schema ?? "", p.Name ?? "")).ToArray() ?? Array.Empty<ProcedureMetadata>()
        };
    }


    private const string MetadataQuery = """
        query GetDatabaseSchema {
          tables {
            schema
            name
            columns {
              name
              dataType
              isNullable
              ordinal
              isPrimaryKey
            }
          }
          foreignKeys {
            name
            fromSchema
            fromTable
            fromColumn
            toSchema
            toTable
            toColumn
            ordinal
          }
          indexes {
            schema
            table
            name
            isUnique
            columns
          }
          databases
          linkedServers
          procedures {
            schema
            name
          }
        }
        """;

    private sealed class GraphqlResponse
    {
        public GraphqlData? Data { get; set; }
    }

    private sealed class GraphqlData
    {
        public List<GraphqlTable>? Tables { get; set; }
        public List<GraphqlForeignKey>? ForeignKeys { get; set; }
        public List<GraphqlIndex>? Indexes { get; set; }
        public List<string>? Databases { get; set; }
        public List<string>? LinkedServers { get; set; }
        public List<GraphqlProcedure>? Procedures { get; set; }
    }

    private sealed class GraphqlTable
    {
        public string? Schema { get; set; }
        public string? Name { get; set; }
        public List<GraphqlColumn>? Columns { get; set; }
    }

    private sealed class GraphqlColumn
    {
        public string? Name { get; set; }
        public string? DataType { get; set; }
        public bool IsNullable { get; set; }
        public int Ordinal { get; set; }
        public bool IsPrimaryKey { get; set; }
    }

    private sealed class GraphqlForeignKey
    {
        public string? Name { get; set; }
        public string? FromSchema { get; set; }
        public string? FromTable { get; set; }
        public string? FromColumn { get; set; }
        public string? ToSchema { get; set; }
        public string? ToTable { get; set; }
        public string? ToColumn { get; set; }
        public int Ordinal { get; set; }
    }

    private sealed class GraphqlIndex
    {
        public string? Schema { get; set; }
        public string? Table { get; set; }
        public string? Name { get; set; }
        public bool IsUnique { get; set; }
        public List<string>? Columns { get; set; }
    }

    private sealed class GraphqlProcedure
    {
        public string? Schema { get; set; }
        public string? Name { get; set; }
    }

    // ── Per-connection query ──────────────────────────────────────────────────

    private const string MetadataByConnectionStringQuery = """
        query GetSchemaByConnectionString($connectionString: String!) {
          schemaByConnectionString(connectionString: $connectionString) {
            tables {
              schema
              name
              columns {
                name
                dataType
                isNullable
                ordinal
                isPrimaryKey
              }
            }
            foreignKeys {
              name
              fromSchema
              fromTable
              fromColumn
              toSchema
              toTable
              toColumn
              ordinal
            }
            indexes {
              schema
              table
              name
              isUnique
              columns
            }
            databases
            linkedServers
            procedures {
              schema
              name
            }
          }
        }
        """;

    private sealed class GraphqlByConnectionResponse
    {
        public GraphqlByConnectionData? Data { get; set; }
    }

    private sealed class GraphqlByConnectionData
    {
        public GraphqlData? SchemaByConnectionString { get; set; }
    }
}
