# SSMS integration

`MssqlIntelliSense.SsmsExtension` is the host-independent adapter between an SSMS query editor and `MssqlIntelliSense.Core`. `MssqlIntelliSense.SsmsHost` is the installable Visual Studio Shell package for SSMS 22.

## Build and install for SSMS 22

```powershell
dotnet build src/MssqlIntelliSense.SsmsHost/MssqlIntelliSense.SsmsHost.csproj -c Release
```

Close SSMS, then open `src/MssqlIntelliSense.SsmsHost/bin/Release/MssqlIntelliSense.SsmsHost.vsix`. After installation, the commands appear under a dedicated **MSSQL IntelliSense** menu on the main toolbar.

## Fast Debugging & Development Setup

To speed up development and avoid the slow VSIXInstaller UI, a fast-deployment mechanism is integrated:

### 1. Automatic Deployment on Build (Visual Studio / MSBuild)
When you build the solution in `Debug` configuration:
```powershell
dotnet build MssqlIntelliSense.slnx -c Debug
```
An MSBuild target automatically triggers the deployment script [deploy-ssms.ps1](../scripts/deploy-ssms.ps1). This script:
- Copies the debug DLLs, `.pkgdef`, and the .NET 10 CLI engine directly to `%LocalAppData%\Microsoft\SSMS\22.0_<hash>\Extensions\MssqlIntelliSense.SsmsHost`.
- Deletes any old VSIX-installed versions of the extension under other subdirectories to prevent type-loading conflicts.
- Touches `extensions.configurationchanged` to force SSMS to rebuild its extension cache.

### 2. Debugging with Visual Studio (F5)
The [MssqlIntelliSense.SsmsHost.csproj.user](../src/MssqlIntelliSense.SsmsHost/MssqlIntelliSense.SsmsHost.csproj.user) file is configured to launch SSMS automatically.
1. Open the solution in Visual Studio.
2. Set `MssqlIntelliSense.SsmsHost` as your startup project.
3. Make sure the configuration is set to **Debug**.
4. Press **F5**. Visual Studio will build, deploy the files to the SSMS Extensions directory, launch `Ssms.exe`, and attach the debugger automatically.

### 3. Deploy and Launch via PowerShell Script
You can also deploy manually and control launching/killing of SSMS directly via:
```powershell
powershell -ExecutionPolicy Bypass -File scripts/deploy-ssms.ps1 -Kill -Launch
```
- `-Kill`: Force-closes any running instances of `Ssms.exe` before copying files (required since SSMS locks extension DLLs).
- `-Launch`: Automatically starts SSMS after deployment finishes.

---

Although the underlying shell version for SSMS 22 is Visual Studio Shell 18.x, the product installation target itself is identified as `Microsoft.VisualStudio.Ssms` with version range `[22.0,23.0)`. Targeting `Microsoft.VisualStudio.Product.Ssms` or using wrong version ranges like `[18.0,19.0)` will cause `NoApplicableSKUsException`.

For `Expand SELECT *` and `Suggest JOIN`, configure a connection string under **Tools > Options > MSSQL IntelliSense > General**. Prefer Windows authentication and do not persist a database password in this setting.

## Implemented host behavior

The SSMS 22 package:

1. Registers seven commands: Format SQL, Explain SQL, Expand SELECT *, Suggest JOIN, Improve SQL, Qualify Table Names, and Generate CRUD Procedures.
2. Implement `ISsmsEditorContext` using the active SQL editor selection.
3. Runs the .NET 10 CLI engine outside the SSMS .NET Framework 4.7.2 process.
4. Replaces the selection for formatting, expansion, and AI improvement; displays results for analysis/JOIN suggestions.
5. Displays actionable errors without crashing the host package.

The host must marshal editor reads and writes onto the UI thread when its shell API requires it. SQL parsing, formatting, analysis, and metadata lookup should remain off the UI thread.

## Example host wiring

```csharp

```

Keeping this boundary separate lets the same core work with different SSMS releases and makes command behavior testable without launching SSMS. A future host can replace the configured connection string with SSMS active-connection discovery without changing the engine.
