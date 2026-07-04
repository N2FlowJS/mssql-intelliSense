# SSMS Integration

`MssqlIntelliSense.SsmsHost` is a Visual Studio Shell (VSIX) package for **SSMS 22**, integrating IntelliSense and productivity features directly into the SSMS interface.

## Architecture

```
SSMS 22 (.NET Framework 4.7.2)
└── MssqlIntelliSense.SsmsHost Package (VSIX, .NET 4.7.2 managed)
    ├── Menu: MSSQL IntelliSense
    │   ├── MSSQL IntelliSense (Schema Explorer window)
    │   ├── Refresh Schema
    │   ├── Chat Agent (AI chat window)
    │   └── Tool Lab
    ├── Schema Explorer Window (WPF/XAML)
    │   ├── Sidebar: Connections tree (SQLite cache)
    │   └── Detail panel (object details, settings, about)
    ├── Chat Agent Window (WPF/XAML)
    └── Tool Lab Window (WPF/XAML)
         │
         └── (cross-process boundary)
              │
         .NET 10 CLI Engine (bundled runtime)
         ├── SqlServerMetadataProvider
         ├── SqlCompletionProvider
         ├── DangerousSqlAnalyzer
         ├── OpenAiSqlAgent
         ├── SelectStarExpander
         └── TableQualifier
```

The extension runs in .NET Framework 4.7.2 (SSMS process). Heavy tasks (metadata scanning, SQL parsing, AI inference) are executed in a .NET 10 engine cross-process to avoid conflicts with the SSMS runtime.

## Installation

### Build Release

```powershell
dotnet build src/MssqlIntelliSense.SsmsHost/MssqlIntelliSense.SsmsHost.csproj -c Release
```

VSIX output at:
```
src/MssqlIntelliSense.SsmsHost/bin/Release/MssqlIntelliSense.SsmsHost.vsix
```

### Install VSIX

1. Close SSMS completely
2. Double-click the `.vsix` file or run:
   ```powershell
   vsixinstaller /a /s "path/to/MssqlIntelliSense.SsmsHost.vsix"
   ```
3. Launch SSMS

After installation, the **MSSQL IntelliSense** menu appears on the SSMS main menu bar.

### Fast Development Deployment

For faster development, Debug build auto-deploys to the SSMS extensions folder:

```powershell
dotnet build MssqlIntelliSense.slnx -c Debug
```

The MSBuild target calls `scripts/deploy-ssms.ps1` to:
1. Copy DLLs, `.pkgdef`, .NET 10 CLI engine to:
   ```
   %LocalAppData%\Microsoft\SSMS\22.0_<hash>\Extensions\MssqlIntelliSense.SsmsHost
   ```
2. Delete old extension versions (if any) to avoid type-loading conflicts
3. Touch `extensions.configurationchanged` to force SSMS to rebuild extension cache

### Debug with Visual Studio (F5)

The `MssqlIntelliSense.SsmsHost.csproj.user` file is configured to auto-launch SSMS when debugging:

1. Open solution in Visual Studio
2. Set `MssqlIntelliSense.SsmsHost` as startup project
3. Ensure configuration is set to **Debug**
4. Press **F5**

Visual Studio will:
- Build project
- Deploy files to SSMS extensions directory
- Launch `Ssms.exe`
- Attach debugger automatically

### Manual Deploy and Launch

```powershell
powershell -ExecutionPolicy Bypass -File scripts/deploy-ssms.ps1 -Kill -Launch
```

Flags:
- `-Kill`: Force-close any running `Ssms.exe` (required because SSMS locks extension DLLs)
- `-Launch`: Auto-start SSMS after deployment completes

---

## Menu Items

### MSSQL IntelliSense

Opens **Schema Explorer Window** - the main extension window:

| Panel | Description |
|-------|-------------|
| **Sidebar** | Hierarchical TreeView: Server → Database → Schema → Objects. Click to view details of each object. |
| **Detail Content** | Displays details when an object is selected (columns, indexes, foreign keys, parameters, etc.) |
| **Settings** | Configure AI endpoint, model, API key; snippet directory |
| **About** | Version info, database path, cached connection count |

### Refresh Schema

Re-scans schema for the active connection. Shows progress in the SSMS output pane. Useful when database schema changes.

### Chat Agent

Opens **Chat Agent Window** - AI chat interface directly in SSMS. The agent can query schema metadata to accurately answer questions about SQL.

### Tool Lab

Opens **Tool Lab Window** - auxiliary productivity tools.

---

## Configuration

### Connection String

Go to **Tools > Options > MSSQL IntelliSense > General** to configure the default connection string.

**Recommendation**: Use Windows Authentication (`Integrated Security=true`), do not persist passwords in this setting.

### AI Configuration

AI API key and model are read from SSMS process environment:

**Option 1: System Environment Variables**
```powershell
# In PowerShell before launching SSMS
$env:OPENAI_API_KEY = "sk-..."
$env:OPENAI_MODEL = "gpt-4o"
ssms
```

**Option 2: Via Settings Panel** in Schema Explorer Window (saved in local SQLite DB, not extension settings)

---

## Package Identity

| Property | Value |
|----------|-------|
| **Product** | MSSQL IntelliSense |
| **Version** | 0.2.71 |
| **Package GUID** | 16f11772-cdb0-42ca-a596-d755543518ac |
| **Command Set GUID** | 63a8fcd9-601f-427d-a253-d4942b4ff2aa |

### VS SKU Targeting

- **Shell Version**: Visual Studio Shell 18.x
- **Product ID**: `Microsoft.VisualStudio.Product.Ssms`
- **Version Range**: `[22.0,23.0)` for SSMS 22

> **Note**: Targeting wrong version range (e.g., `[18.0,19.0)`) will cause `NoApplicableSKUsException`.

---

## Cross-Process Execution

SSMS 22 uses .NET Framework 4.7.2. The extension package (VSIX) also runs in the SSMS process. However, heavy tasks are executed in a .NET 10 CLI engine cross-process to:

1. Avoid conflict between .NET Framework and .NET Core/10 runtime
2. Allow use of latest NuGet packages (OpenAI, EF Core, etc.)
3. Isolate stable extension host from potentially unstable engine

The extension uses `AppDomain.CurrentDomain.AssemblyResolve` to preload bundled assemblies from the VSIX package instead of loading from GAC:

```csharp
// Packaged assemblies
static readonly string[] PackagedRuntimeAssemblies =
[
    "OpenAI",
    "System.ClientModel",
    "System.IO.Pipelines",
    "System.Net.ServerSentEvents",
    "Microsoft.Bcl.AsyncInterfaces",
    "System.Threading.Tasks.Extensions",
];
```

---

## Connection Discovery

The extension auto-detects SSMS active connection via:

1. **Primary**: `ServiceCache.ScriptFactory.CurrentlyActiveWndConnectionInfo.UIConnectionInfo`
   - From assembly `SqlPackageBase`
   - Provides: ServerName, DatabaseName, UserName, Password, AuthenticationType

2. **Fallback**: Toolbar database combo
   - Iterates through all visible CommandBars
   - Finds ComboBox containing database name
   - Filters invalid values (containing `\`, `:`, `/`, or ending with `%`)

Active connection is cached for 750ms to avoid spamming the SSMS UI thread.

---

## Self-Healing Cleanup

On initialization, the extension auto-cleans old versions of the extension installed in sibling directories within the extensions folder, avoiding type-loading conflicts when SSMS loads assemblies.

---

## Logging

The extension logs to multiple places:

1. **SSMS Output Window** - "MSSQL IntelliSense" pane (GUID: `5F2E23E6-2005-4D05-B86D-8D5FA5470FE7`)
2. **File**: `%USERPROFILE%\.gemini\antigravity-cli\package_log.txt`

Log format: `[yyyy-MM-dd HH:mm:ss.fff] message`

---

## Registered Commands

| Command ID | Name | Description |
|------------|------|-------------|
| 0x010A | Refresh Schema | Re-scan schema for active connection |
| 0x010B | mssql-intellisense-window | Open Schema Explorer window |
| 0x010C | chat-agent-window | Open Chat Agent window |
| 0x010D | tool-lab-window | Open Tool Lab window |