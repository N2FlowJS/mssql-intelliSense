# MSSQL IntelliSense

A small, testable T-SQL productivity engine for SQL Server. The implementation provides a reusable .NET core, CLI, and a host-independent SSMS command adapter.

## Requirements

- .NET SDK 10
- SQL Server connectivity is only required for metadata-backed commands

## Build and test

```powershell
dotnet build MssqlIntelliSense.slnx
dotnet test MssqlIntelliSense.slnx
```

## CLI

```powershell
dotnet run --project src/MssqlIntelliSense.Cli -- format query.sql
dotnet run --project src/MssqlIntelliSense.Cli -- parse query.sql
dotnet run --project src/MssqlIntelliSense.Cli -- explain query.sql
dotnet run --project src/MssqlIntelliSense.Cli -- expand-select-star query.sql --connection "Server=.;Database=App;Integrated Security=true;TrustServerCertificate=true"
dotnet run --project src/MssqlIntelliSense.Cli -- suggest-join query.sql --connection "Server=.;Database=App;Integrated Security=true;TrustServerCertificate=true"
dotnet run --project src/MssqlIntelliSense.Cli -- complete query.sql --position 42 --connection "Server=.;Database=App;Integrated Security=true;TrustServerCertificate=true"
```

`format` writes formatted SQL to stdout, so it can be redirected to another file. `parse` reports syntax errors with line and column. `explain` reports safety warnings. Metadata is read from SQL Server system catalogs and cached in memory through `CachingMetadataProvider`.

`complete` returns context-aware completion items as JSON. It suggests schemas and tables after `FROM`/`JOIN`, columns after `alias.`, qualified names for ambiguous columns, and T-SQL keywords. `--position` is a zero-based character offset and defaults to the end of the file. Database-backed suggestions require `--connection`; keyword completion works without one.

## AI improvement

The AI assistant uses the OpenAI Responses API with strict structured output. Supply credentials through environment variables; keys are never accepted as command arguments or written to output.

```powershell
$env:OPENAI_API_KEY = "..."
$env:OPENAI_MODEL = "your-enabled-model"
dotnet run --project src/MssqlIntelliSense.Cli -- improve query.sql --instruction "Optimize this query and explain the changes"
```

Add `--connection "..."` to include relevant table, foreign-key, and index metadata. The model is deliberately explicit rather than hard-coded so deployments can choose a model available to their OpenAI project.

## SSMS adapter

`MssqlIntelliSense.SsmsExtension` exposes four editor commands: format, explain, expand `SELECT *`, and suggest JOIN. See [docs/SSMS_INTEGRATION.md](docs/SSMS_INTEGRATION.md) for the version-specific VSIX wiring boundary.

`MssqlIntelliSense.SsmsHost` is the installable SSMS 22 host. Build it with:

```powershell
dotnet build src/MssqlIntelliSense.SsmsHost/MssqlIntelliSense.SsmsHost.csproj -c Release
```

The generated VSIX is placed under `src/MssqlIntelliSense.SsmsHost/bin/Release`. SSMS 22 uses .NET Framework 4.7.2, so the host launches the bundled .NET 10 engine across a process boundary. Metadata commands require a connection string under **Tools > Options > MSSQL IntelliSense > General**. Improve SQL reads `OPENAI_API_KEY` and `OPENAI_MODEL` from the SSMS process environment and never stores the API key in extension settings.


The metadata model is a rich, hierarchical representation of SQL Server database objects designed for intelligent IntelliSense-style code completion. The hierarchy flows:

Server -> Database -> Schema -> Table/View (with Columns, Indexes, Triggers, FKs, Constraints)
                             -> Stored Procedure (with Parameters)
                             -> Scalar Function (with Parameters, Return Type)
                             -> Table-Valued Function (with Parameters, Columns)
                             -> Synonym, Type, etc.
       -> Server Object -> Link-server
                        -> EndPoint
Every object implements the base ICandidate interface providing name, ownership, type classification (SqlObjectType), and completion text. Database objects further implement IDbObject for system/user object flags and descriptions. The ILoadableCandidate interface enables lazy-loading of metadata from the server. All children are stored in ICandidateCollection<T> instances supporting case-insensitive lookup by name and filtering by type.