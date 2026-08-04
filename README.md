# MSSQL IntelliSense

MSSQL IntelliSense is a T-SQL productivity extension for SQL Server and SSMS. It combines local schema scanning, context-aware completion, SQL utilities, object review, and an AI chat agent that can inspect approved metadata tools before answering.

The project is designed around a conservative rule: database-specific answers should come from scanned metadata or approved read-only tool output, not from model guesses.

## Main Components

| Project | Purpose |
| --- | --- |
| `src/MssqlIntelliSense.Core` | Reusable SQL engine: parsing, completion, formatting, analysis, metadata cache, lexical search, AI tool execution. |
| `src/MssqlIntelliSense.Cli` | Command-line tool for schema scan, completion tests, `SELECT *` expansion, table qualification, and CRUD generation. |
| `src/MssqlIntelliSense.SsmsHost` | SSMS 22 VSIX extension with tool windows, chat agent, Tool Lab, object review, and active connection integration. |
| `src/MssqlIntelliSense.DebugApp` | Local WPF/debug harness for testing extension UI outside SSMS. |
| `src/MssqlIntelliSense.UpdateServer` | Update/release support server project. |

## Features

### SSMS Integration

- Dockable **AI SQL Chat Agent** window.
- Dockable **AI SQL Tool Lab** window for manually running metadata tools.
- Object review at caret.
- Active SSMS connection and active database detection.
- Schema cache loading from the active connection.
- Tool selection UI for enabling/disabling chat tools.
- Markdown rendering for assistant, tool, and action cards.

### Chat Agent

- Uses OpenAI-compatible **Chat Completions tool calling**.
- Supports custom OpenAI-compatible endpoints and models.
- Lets the model request tools, then asks the user for approval before executing them.
- Renders action approval cards and tool output directly in the chat.
- Keeps final assistant responses below the tool cards, matching the actual reasoning flow.
- Sends compact tool context back to the LLM while keeping full markdown visible in the UI.
- Keeps compact session history to reduce request tokens.
- Selects only relevant tool schemas for each user message when possible.

Available chat tools:

| Tool | Description |
| --- | --- |
| `search_objects` | Lexical search across tables, views, procedures, functions, names, columns/parameters, definitions, and custom descriptions. |
| `list_tables` | Lists cached tables by schema/name/query. |
| `get_table_schema` | Returns columns, data types, nullability, primary key flags, and descriptions for one table. |
| `get_table_relations` | Returns cached foreign-key relationships involving one table. |
| `get_table_indexes` | Returns cached indexes for one table. |
| `find_column` | Finds table/view columns by name or description. |
| `list_endpoints` | Lists cached SQL Server endpoints. |
| `execute` | Runs read-only SQL against the active SSMS connection. Unsafe SQL is blocked. |

### Lexical Metadata Search

The agent uses local lexical scoring for schema/object discovery. Semantic embedding providers and vector fallback paths are intentionally not part of the current flow. This keeps metadata search deterministic, lightweight, local, and easier to debug.

### SQL Completion

- Schema, table, view, column, stored procedure, function, synonym, user type, and keyword suggestions.
- Context-aware suggestions after `FROM`, `JOIN`, aliases, qualified names, and common T-SQL positions.
- Fully qualified name support for ambiguous objects.

### SQL Utilities

- Parse T-SQL with `Microsoft.SqlServer.TransactSql.ScriptDom`.
- Format SQL.
- Expand `SELECT *` into explicit column lists.
- Add schema qualification to unqualified table/view names.
- Generate CRUD stored procedures.
- Detect risky patterns such as `DELETE`/`UPDATE` without `WHERE`, `SELECT *`, and implicit conversion risks.

### Metadata Cache

- Scans SQL Server metadata for tables, columns, views, procedures, functions, indexes, foreign keys, triggers, user types, synonyms, users, linked servers, and endpoints.
- Stores metadata locally for fast completion and AI tools.
- Supports active connection registration and database filtering.

Cache location:

```text
%APPDATA%\MssqlIntelliSense\cache.json
```

## Requirements

- Windows.
- SQL Server Management Studio 22 for the VSIX extension.
- .NET SDK 10 for building and running the solution.
- SQL Server access for schema scanning and read-only `execute` tool usage.
- An OpenAI-compatible Chat Completions endpoint for AI chat.

## Build And Test

Build the whole solution:

```powershell
dotnet build MssqlIntelliSense.slnx
```

Run tests:

```powershell
dotnet test MssqlIntelliSense.slnx
```

Useful focused commands:

```powershell
dotnet test tests\MssqlIntelliSense.Core.Tests\MssqlIntelliSense.Core.Tests.csproj -c Debug
dotnet build src\MssqlIntelliSense.SsmsHost\MssqlIntelliSense.SsmsHost.csproj -c Debug -p:DisableSsmsDeploy=true
dotnet build src\MssqlIntelliSense.DebugApp\MssqlIntelliSense.DebugApp.csproj -c Debug -p:DisableSsmsDeploy=true
```

When building SSMS host and DebugApp, prefer serial builds if Visual Studio/VSSDK temporary files are locked.

## CLI Usage

Run commands through the CLI project:

```powershell
dotnet run --project src\MssqlIntelliSense.Cli -- <command> [args]
```

### Scan Schema

```powershell
dotnet run --project src\MssqlIntelliSense.Cli -- scan "Server=.;Database=MyDb;Integrated Security=true;TrustServerCertificate=true"
```

This registers the connection and writes metadata to the local cache.

### Expand SELECT *

```powershell
dotnet run --project src\MssqlIntelliSense.Cli -- expand query.sql MyDb
dotnet run --project src\MssqlIntelliSense.Cli -- expand - MyDb
```

Use `-` to read SQL from standard input.

### Qualify Table Names

```powershell
dotnet run --project src\MssqlIntelliSense.Cli -- qualify query.sql MyDb
```

### Generate CRUD Procedures

```powershell
dotnet run --project src\MssqlIntelliSense.Cli -- crud dbo.Users MyDb all
dotnet run --project src\MssqlIntelliSense.Cli -- crud dbo.Users MyDb getall
dotnet run --project src\MssqlIntelliSense.Cli -- crud dbo.Users MyDb insert
```

Supported operations:

```text
all, getall, getbyid, insert, update, delete
a, ga, gb, i, u, d
```

### Completion Probe

```powershell
dotnet run --project src\MssqlIntelliSense.Cli -- completions query.sql 120 MyDb
```

Output is tab-separated:

```text
Kind    Label    InsertText    Description
```

## AI Configuration

In SSMS, configure AI settings from:

```text
Tools > Options > MSSQL IntelliSense > General
```

The extension supports OpenAI-compatible endpoints such as:

```text
https://api.openai.com/v1/chat/completions
https://your-compatible-provider.example/v1/chat/completions
```

The SDK endpoint resolver also accepts a base endpoint and normalizes `/chat/completions` or `/responses` suffixes when needed.

Recommended settings:

- Use a Chat Completions compatible model that supports tool calls.
- Prefer Windows Authentication for SQL Server connections.
- Avoid storing database passwords in connection strings.
- Keep only needed tools enabled in the chat tool menu to reduce request size.

## Chat Agent Flow

1. User sends a message in SSMS.
2. The extension resolves the active SQL connection/database.
3. The schema cache is loaded for the active connection.
4. The chat registry selects relevant enabled tool schemas.
5. The LLM receives the system prompt, compact session history, user message, and selected tool schemas.
6. If the LLM requests a tool, the extension shows an approval card.
7. Approved tools run locally.
8. Full markdown tool output is shown in the UI.
9. A compact version of the tool output is sent back to the LLM.
10. The final assistant answer is rendered below the tool output.

This order is intentional so the visual transcript matches the agent execution order.

## Tool Output And Token Control

Tool executors return markdown strings. The UI renders the full markdown, while the LLM receives a compact tool context through `ChatToolOutputFormatter.FormatForAgentContext`.

Current token-saving behavior:

- Tool context is capped by character and line limits.
- Session history stores compact user/assistant turns.
- Old tool results are not replayed as long-lived chat history.
- Tool schemas are filtered by user intent before each request.
- `search_objects` uses concise lexical results rather than embedding/vector payloads.

## SSMS Installation

Build the VSIX installer:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

Install or update:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -Launch
```

Release VSIX output:

```text
src\MssqlIntelliSense.SsmsHost\bin\Release\net472\MssqlIntelliSense.SsmsHost.vsix
```

Use the install script for release installs. Do not manually copy random build output into the SSMS extensions folder for release usage.

## Local SSMS Development

Fast deploy/debug:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy-ssms.ps1 -Kill -Launch
```

Flags:

| Flag | Description |
| --- | --- |
| `-Kill` | Closes running SSMS before deployment. |
| `-Launch` | Starts SSMS after deployment. |

Debug build deployment copies the extension into the user-local SSMS extension directory and clears old conflicting versions.

## Visual Studio Debugging

1. Open `MssqlIntelliSense.slnx`.
2. Set `MssqlIntelliSense.SsmsHost` as startup project.
3. Use `Debug` configuration.
4. Press `F5`.

The project launches/deploys into SSMS for extension debugging.

## DebugApp

`MssqlIntelliSense.DebugApp` is useful when iterating on WPF controls without launching SSMS each time:

```powershell
dotnet run --project src\MssqlIntelliSense.DebugApp\MssqlIntelliSense.DebugApp.csproj -c Debug
```

Use it for UI rendering checks, chat/tool window layout, and local behavior that does not require live SSMS services.

## Project Structure

```text
src/
  MssqlIntelliSense.Core/
    Ai/                 OpenAI-compatible agent helpers and tool executors
    Analysis/           SQL risk analysis
    Cache/              Metadata cache helpers
    Completion/         IntelliSense providers
    Formatting/         SQL formatting
    Metadata/           SQL Server metadata provider and entities
    Parsing/            T-SQL parsing services
  MssqlIntelliSense.Cli/
    Program.cs          CLI entry point
  MssqlIntelliSense.SsmsHost/
    UI/                 SSMS tool windows, chat, Tool Lab, markdown renderer
    Properties/         Launch/debug settings
  MssqlIntelliSense.DebugApp/
    Local WPF debug host
  MssqlIntelliSense.UpdateServer/
    Update/release support
tests/
  MssqlIntelliSense.Core.Tests/
docs/
  walkthrough.md
  SSMS_INTEGRATION.md
scripts/
  build-installer.ps1
  deploy-ssms.ps1
  install.ps1
```

## Metadata Model

```text
Server
  Database
    Schema
      Table
        Columns
        Indexes
        ForeignKeys
        Triggers
      View
        Columns
      Stored Procedure
        Parameters
      Function
        Parameters
        Return type / columns
      User-defined type
      Synonym
      User
  Server Objects
    Linked Servers
    Endpoints
```

## Troubleshooting

### Chat Agent Shows No Specific Tables

Scan schema for the active connection first. The agent only treats object names, columns, relationships, and indexes as confirmed when they are present in approved tool output or loaded cache.

### Tool Approval Or Tool Result Looks Out Of Order

The intended order is approval card, tool output card, then final assistant card. If a previous debug VSIX is still loaded, close SSMS and redeploy:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\deploy-ssms.ps1 -Kill -Launch
```

### Provider Endpoint Fails

Verify that the configured endpoint is Chat Completions compatible and that the selected model supports tool calls.

### Build Fails With Locked VSSDK Temporary Files

Close Visual Studio/SSMS and rerun the build. If building multiple WPF/VSSDK projects, run them serially.

### Unsafe SQL Is Blocked

The `execute` tool is read-only. Use it for `SELECT`, `WITH`, `DECLARE`, and safe metadata queries. DML, DDL, and unsafe execution paths are blocked intentionally.

## License

MIT
