# MSSQL IntelliSense Walkthrough

## End-to-End Workflow

### Step 1: Install Extension (SSMS)

```powershell
dotnet build src/MssqlIntelliSense.SsmsHost/MssqlIntelliSense.SsmsHost.csproj -c Release
```

Close SSMS, then install the VSIX at `src/MssqlIntelliSense.SsmsHost/bin/Release/MssqlIntelliSense.SsmsHost.vsix`.

### Step 2: Open Schema Explorer

Launch SSMS, open a query window connected to your database. Click **MSSQL IntelliSense → MSSQL IntelliSense** menu to open Schema Explorer.

The first time, the sidebar is empty because no connections have been cached yet.

### Step 3: Scan Schema

**Option 1: Via menu**
Click **Refresh Schema** in the MSSQL IntelliSense menu. The extension will:
1. Detect active connection from SSMS
2. Scan full schema (tables, columns, views, procedures, functions, triggers, indexes, foreign keys, user types, synonyms, users)
3. Save to SQLite cache at `%APPDATA%\MssqlIntelliSense\MssqlIntelliSense.db`
4. Display tree view in sidebar

**Option 2: Via CLI**
```powershell
dotnet run --project src/MssqlIntelliSense.Cli -- scan "Server=.;Database=MyDb;Integrated Security=true"
```

### Step 4: Explore Schema

In the Schema Explorer sidebar, browse the tree to see details:

```
Server01
└── MyDb
    └── dbo
        ├── Tables
        │   ├── Users (Id, Name, Email, CreatedAt)
        │   ├── Orders (Id, UserId, Total, Status)
        │   └── Products (Id, Name, Price, Stock)
        ├── Views
        │   └── vw_OrderSummary
        ├── Stored Procedures
        │   ├── sp_GetUsers
        │   └── sp_CreateOrder
        ├── Functions
        │   └── fn_CalculateTotal
        ├── Triggers
        └── ... (User Types, Synonyms, etc.)
```

Click any object to view details in the right detail panel.

### Step 5: Write SQL with IntelliSense

In the SSMS query window, when typing:

```sql
SELECT u.    -- ← IntelliSense suggests columns from Users table
FROM Users u
     JOIN    -- ← Suggests tables to JOIN
```

The extension provides context-aware completion based on cached schema.

### Step 6: Analyze SQL

After writing statements, use **Analyze SQL** (if integrated) to detect issues:

- `DELETE FROM Orders` without WHERE → **Error**
- `UPDATE Orders SET Status = 'Shipped'` without WHERE → **Error**
- `SELECT * FROM Orders` → **Warning**
- Comparing `int` column with string literal → **Warning** (implicit conversion)

### Step 7: Improve SQL with AI

**Option 1: Via Chat Agent Window**
1. Open **Chat Agent** from MSSQL IntelliSense menu
2. Enter request: "Optimize this query and add index suggestions"
3. Paste your SQL
4. Agent queries schema metadata and returns optimized SQL with explanation

**Option 2: Via CLI**
```powershell
$env:OPENAI_API_KEY = "sk-..."
$env:OPENAI_MODEL = "gpt-4o"

dotnet run --project src/MssqlIntelliSense.Cli -- improve query.sql `
  --instruction "Optimize this query and explain the changes" `
  --connection "Server=.;Database=MyDb;Integrated Security=true"
```

The AI agent can call tools to read schema:
- `list_tables` - List all tables
- `get_table_schema` - Read columns, primary key of a table
- `get_table_relations` - Read foreign keys of a table
- `get_table_indexes` - Read indexes of a table
- `search_schema_objects` - Search objects by keyword

### Step 8: Expand SELECT *

```powershell
dotnet run --project src/MssqlIntelliSense.Cli -- expand query.sql MyDb
```

```sql
-- Before:
SELECT * FROM Users

-- After expand:
SELECT u.Id, u.Name, u.Email, u.CreatedAt FROM Users u
```

### Step 9: Qualify Table Names

```powershell
dotnet run --project src/MssqlIntelliSense.Cli -- qualify query.sql MyDb
```

```sql
-- Before:
SELECT * FROM Users u JOIN Orders o ON u.Id = o.UserId

-- After qualify:
SELECT * FROM dbo.Users u JOIN dbo.Orders o ON u.Id = o.UserId
```

### Step 10: Generate CRUD Procedures

```powershell
dotnet run --project src/MssqlIntelliSense.Cli -- crud dbo.Orders MyDb all
```

Sample output (GetById procedure):

```sql
CREATE PROCEDURE dbo.Orders_GetById
    @Id INT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Id, UserId, Total, Status, CreatedAt
    FROM dbo.Orders
    WHERE Id = @Id;
END
```

---

## Caching and Metadata Flow

```
┌─────────────────┐      ┌──────────────────┐      ┌─────────────────────┐
│ SSMS Query      │      │ Schema Explorer  │      │ SQLite Cache        │
│ Active Window  │ ──── │ Refresh Schema   │ ──── │ MssqlIntelliSense   │
│                 │      │                  │      │ .db                 │
└─────────────────┘      └──────────────────┘      └─────────────────────┘
                                  │
                                  ▼
                        ┌──────────────────┐
                        │ SqlServer        │
                        │ MetadataProvider │
                        │ (scan sys tables)│
                        └──────────────────┘
```

---

## CLI for CI/CD

The CLI can be used in build scripts or CI pipelines:

```powershell
# 1. Scan and cache schema
dotnet run --project src/MssqlIntelliSense.Cli -- scan $env:CONNECTION_STRING

# 2. Check SQL syntax (using parse)
dotnet run --project src/MssqlIntelliSense.Cli -- parse query.sql
# Exit code 0 = valid, 1 = syntax errors

# 3. Expand SELECT * in batch
Get-ChildItem -Recurse -Filter "*.sql" | ForEach-Object {
    $expanded = dotnet run --project src/MssqlIntelliSense.Cli -- expand $_.FullName
    if ($LASTEXITCODE -eq 0) { $expanded | Set-Content $_.FullName }
}
```

---

## Metadata Scan Scope

When scanning schema, you can specify scope:

| Scope | Description |
|-------|-------------|
| `All` | Scan everything (default) |
| `DatabaseList` | List databases only |
| `DatabaseObjects` | Scan tables, views, procedures, functions, triggers, user types, synonyms, users |
| `Tables` | Tables and columns only |
| `Relations` | Foreign keys only |
| `Indexes` | Indexes only |
| `Programmability` | Procedures, views, functions, triggers |
| `Security` | User types, synonyms, users |
| `LinkedServers` | Linked servers |
| `Endpoints` | Endpoints |