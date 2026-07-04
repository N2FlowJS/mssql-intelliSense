# MSSQL IntelliSense

A lightweight, testable T-SQL productivity engine for SQL Server. The project consists of three main components:

- **`MssqlIntelliSense.Core`**: Reusable .NET Core library containing all SQL processing logic (parsing, completion, formatting, AI, metadata)
- **`MssqlIntelliSense.Cli`**: Command-line tool for schema scanning, SELECT * expansion, table qualification, and CRUD procedure generation
- **`MssqlIntelliSense.SsmsHost`**: VSIX package installer for SQL Server Management Studio (SSMS) 22, integrating directly into the SSMS UI

---

## Features

### IntelliSense & Completion

- **Schema completion**: Suggests schemas, tables, views, columns, stored procedures, functions, triggers, user types, synonyms
- **Context-aware**: Analyzes cursor position to provide relevant suggestions (after `FROM`, `JOIN`, `alias.`, etc.)
- **Keyword completion**: Suggests T-SQL keywords
- **Qualified name completion**: Completes fully qualified names for ambiguous columns

### SQL Processing

- **Parsing**: Accurate T-SQL parsing using `Microsoft.SqlServer.TransactSql.ScriptDom`
- **Formatting**: Standards-compliant SQL formatting
- **SELECT * Expansion**: Replaces `SELECT *` with explicit column lists
- **Table Qualification**: Adds schema prefixes to unqualified table/view names

### SQL Analysis

- **Dangerous SQL Detection**: Identifies hazardous statements:
  - `DELETE` without `WHERE`
  - `UPDATE` without `WHERE`
  - `SELECT *` (best practice violation)
  - Implicit type conversion in comparisons

### AI Assistant

- **OpenAI Integration**: Uses OpenAI Responses API with structured output
- **Schema-aware**: AI can query metadata (tables, columns, foreign keys, indexes)
- **SQL Improvement**: Improves, optimizes, and explains SQL statements per request
- **Tool-augmented Agent**: Agent calls tools to read schema before generating SQL

### Metadata Management

- **Schema Scanning**: Connects to SQL Server and scans full schema (tables, columns, views, procedures, functions, triggers, indexes, foreign keys, user types, synonyms, users, linked servers, endpoints)
- **SQLite Caching**: Stores scanned metadata in local SQLite database
- **Multi-database Support**: Scans multiple databases on the same instance
- **Incremental Refresh**: Partial schema updates

### CRUD Generator

- **Automatic Stored Procedure Generation**: Generates complete CRUD stored procedures for a table
- **Operations**: `GetAll`, `GetById`, `Insert`, `Update`, `Delete`
- **Smart Primary Key Detection**: Automatically identifies primary key and uses it in appropriate procedures

---

## Requirements

- **.NET SDK 10** (for building and running Core and CLI)
- **SQL Server** (connection required only for metadata-related commands)
- **SSMS 22** (for extension installation)

---

## Build and Test

```powershell
dotnet build MssqlIntelliSense.slnx
dotnet test MssqlIntelliSense.slnx
```

---

## Project Structure

```
src/
├── MssqlIntelliSense.Core/          # Core library
│   ├── Ai/                          # OpenAI agent
│   ├── Analysis/                     # Dangerous SQL analyzer
│   ├── Cache/                       # SQLite caching (EF Core)
│   ├── Completion/                   # IntelliSense completion providers
│   │   └── Candidates/              # Completion item models
│   ├── Formatting/                   # SQL formatter
│   ├── Metadata/                    # SQL Server metadata provider
│   │   └── Entities/               # Cache entity models
│   └── Parsing/                     # T-SQL parser service
├── MssqlIntelliSense.Cli/           # Command-line interface
└── MssqlIntelliSense.SsmsHost/      # SSMS 22 VSIX extension
    ├── UI/                         # XAML windows (Schema Explorer, Chat Agent, Tool Lab)
    └── Properties/                  # Launch settings
```

---

## CLI

### Scan Schema

Scan SQL Server schema and save to local cache:

```powershell
dotnet run --project src/MssqlIntelliSense.Cli -- scan "Server=.;Database=MyDb;Integrated Security=true"
```

Sample output:
```
[MssqlIntelliSense scan] Connecting to: Server=.;Database=MyDb;Integrated Security=true
[MssqlIntelliSense scan] Scanning schema for: .
[MssqlIntelliSense scan] Done. Cached 42 tables, 15 views, 28 procedures.
```

### Expand SELECT *

Expand `SELECT *` to explicit column list:

```powershell
dotnet run --project src/MssqlIntelliSense.Cli -- expand query.sql MyDb
dotnet run --project src/MssqlIntelliSense.Cli -- expand query.sql    # Uses cached database
```

### Qualify Table Names

Add schema prefix to unqualified table names:

```powershell
dotnet run --project src/MssqlIntelliSense.Cli -- qualify query.sql MyDb
```

### Generate CRUD Procedures

Generate CRUD stored procedures for a table:

```powershell
# Generate all CRUD procedures
dotnet run --project src/MssqlIntelliSense.Cli -- crud dbo.Users MyDb all

# Generate only GetAll procedure
dotnet run --project src/MssqlIntelliSense.Cli -- crud dbo.Users MyDb getall

# Generate only Insert procedure
dotnet run --project src/MssqlIntelliSense.Cli -- crud dbo.Users MyDb insert

# Available operations: all, getall, getbyid, insert, update, delete (or abbreviations: ga, gb, i, u, d)
```

### Completions

Get completion suggestions at a specific position:

```powershell
dotnet run --project src/MssqlIntelliSense.Cli -- completions query.sql 15 MyDb
```

Output (tab-separated: Kind, Label, InsertText, Description):
```
Table	dbo.Users	dbo.Users	Table dbo.Users in database MyDb. Contains columns: Id, Name, Email.
Column	u.Id	u.Id	Type: int, Nullable: No
Column	u.Name	u.Name	Type: nvarchar(100), Nullable: No
Keyword	SELECT	SELECT	T-SQL keyword
```

---

## AI Assistant

The AI assistant uses OpenAI Responses API with structured output. Configure via environment variables or config file:

```powershell
$env:OPENAI_API_KEY = "sk-..."
$env:OPENAI_MODEL = "gpt-4o"
dotnet run --project src/MssqlIntelliSense.Cli -- improve query.sql --instruction "Optimize this query"
```

Add `--connection "..."` to include metadata (tables, foreign-keys, indexes):

```powershell
dotnet run --project src/MssqlIntelliSense.Cli -- improve query.sql `
  --instruction "Optimize this query" `
  --connection "Server=.;Database=MyDb;Integrated Security=true"
```

Model and endpoint are configurable, not hard-coded.

---

## SSMS Integration

The SSMS 22 extension provides:

### Menu: **MSSQL IntelliSense**

| Menu Item | Description |
|-----------|-------------|
| **MSSQL IntelliSense** | Opens Schema Explorer window (tree view of database connections and schema cache) |
| **Refresh Schema** | Re-scans schema for active connection |
| **Chat Agent** | Opens Chat Agent window (AI chat interface directly in SSMS) |
| **Tool Lab** | Opens Tool Lab window (auxiliary tools) |

### Schema Explorer Window

The main window displays:

- **Sidebar**: TreeView of scanned connections with hierarchical structure: Server → Database → Schema → Objects
- **Detail Panel**: View details of each object (tables, columns, indexes, foreign keys, procedures, functions, etc.)
- **Settings Panel**: Configure AI endpoint, model, API key; snippet directory; MRU stats
- **About Panel**: Version info, database path, connection count

### Installation

Build and install:

```powershell
dotnet build src/MssqlIntelliSense.SsmsHost/MssqlIntelliSense.SsmsHost.csproj -c Release
```

Then close SSMS and open:
```
src/MssqlIntelliSense.SsmsHost/bin/Release/MssqlIntelliSense.SsmsHost.vsix
```

### Fast Development Deployment

For faster development, Debug build auto-deploys to SSMS extensions folder:

```powershell
dotnet build MssqlIntelliSense.slnx -c Debug
```

The `scripts/deploy-ssms.ps1` script will automatically:
- Copy DLLs, `.pkgdef`, and .NET 10 CLI engine to `%LocalAppData%\Microsoft\SSMS\22.0_<hash>\Extensions\MssqlIntelliSense.SsmsHost`
- Delete old extension versions to avoid type-loading conflicts
- Trigger SSMS to rebuild extension cache

### Debug with Visual Studio

1. Open solution in Visual Studio
2. Set `MssqlIntelliSense.SsmsHost` as startup project
3. Ensure configuration is set to **Debug**
4. Press **F5** - Visual Studio will build, deploy, launch SSMS, and attach debugger

### Debug/Launch via PowerShell

```powershell
powershell -ExecutionPolicy Bypass -File scripts/deploy-ssms.ps1 -Kill -Launch
```

- `-Kill`: Closes any running SSMS instances before deploying
- `-Launch`: Automatically starts SSMS after deployment

### Connection String Configuration

Go to **Tools > Options > MSSQL IntelliSense > General** to configure default connection string. Windows Authentication is recommended; do not persist database passwords in this setting.

AI API key and model are read from SSMS process environment, not stored in extension settings.

---

## Metadata Model

Hierarchical metadata model of SQL Server objects:

```
Server
└── Database
    └── Schema
        ├── Table
        │   ├── Columns (name, type, nullable, ordinal, isPrimaryKey)
        │   ├── Indexes (name, unique, columns)
        │   ├── ForeignKeys (name, from/to schema/table/column)
        │   └── Triggers
        ├── View
        │   └── Columns
        ├── Stored Procedure
        │   └── Parameters (name, type, isOutput, ordinal)
        ├── Scalar Function
        │   ├── Parameters
        │   └── ReturnType
        ├── Table-Valued Function
        │   ├── Parameters
        │   └── Columns
        ├── User-Defined Type
        │   └── Columns (if table type)
        ├── Synonym
        └── Users
└── Server Objects
    ├── Linked Servers
    └── Endpoints
```

All metadata is serialized and stored in SQLite database at:
```
%APPDATA%\MssqlIntelliSense\MssqlIntelliSense.db
```

---

## Architecture Notes

### Cross-Process Design

SSMS 22 runs on .NET Framework 4.7.2. The extension host runs in SSMS process, but the .NET 10 engine is launched cross-process to avoid conflicts. Communication between host and engine may use named pipes or stdout/stderr.

### Packaged Runtime Resolution

The extension bundles required .NET runtime assemblies (`OpenAI`, `System.ClientModel`, etc.) and uses `AppDomain.CurrentDomain.AssemblyResolve` to load correct assemblies from the package directory instead of from GAC.

### Connection Discovery

SSMS extension auto-detects active connection from:
1. `ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo.UIConnectionInfo`
2. Database name combo on toolbar (fallback)

---

## License

MIT