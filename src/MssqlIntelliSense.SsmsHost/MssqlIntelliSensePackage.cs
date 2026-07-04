using System;
using System.ComponentModel.Design;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;
using EnvDTE;
using Microsoft.VisualStudio.CommandBars;
using System.Collections.Generic;

namespace MssqlIntelliSense.SsmsHost;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("MSSQL IntelliSense", "T-SQL productivity commands for SSMS", "0.1")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideBindingPath]
[ProvideAutoLoad(VSConstants.UICONTEXT.ShellInitialized_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideToolWindow(typeof(ChatAgentToolWindowPane),
    Orientation = ToolWindowOrientation.Right,
    Style = VsDockStyle.Tabbed,
    Window = "{3AE79031-E1BC-11D0-8F78-00A0C9110057}")] // dock alongside Object/Solution Explorer (right pane)
[ProvideToolWindow(typeof(ToolLabToolWindowPane),
    Orientation = ToolWindowOrientation.Right,
    Style = VsDockStyle.Tabbed,
    Window = "{3AE79031-E1BC-11D0-8F78-00A0C9110057}")]
[Guid(PackageGuidString)]
public sealed class MssqlIntelliSensePackage : AsyncPackage
{
    public static MssqlIntelliSensePackage? Instance { get; private set; }

    public static MssqlIntelliSenseOptions? GetOptions()
    {
        return Instance?.GetDialogPage(typeof(MssqlIntelliSenseOptions)) as MssqlIntelliSenseOptions;
    }

    public const string PackageGuidString = "16f11772-cdb0-42ca-a596-d755543518ac";
    private static readonly Guid CommandSet = new("63a8fcd9-601f-427d-a253-d4942b4ff2aa");
    public static readonly Version CurrentVersion = new("0.2.71");
    public static string VersionString => CurrentVersion.ToString();

    private readonly List<CommandBarEvents> _commandBarEvents = new();

    private static readonly (int Id, string Name)[] RegisteredCommands =
    [
        (0x010B, "mssql-intellisense-window"),
        (0x010C, "chat-agent-window"),
        (0x010D, "tool-lab-window"),
    ];

    private static readonly HashSet<string> _activelyScanningConnections = new();
    private static readonly object _scanLock = new();
    private static readonly TimeSpan ActiveConnectionCacheDuration = TimeSpan.FromMilliseconds(750);
    private static string? _cachedActiveConnectionString;
    private static string? _cachedActiveDatabaseName;
    private static DateTime _activeConnectionCacheLoadedAt;
    private static bool _runtimeResolverInstalled;
    private static readonly object RuntimeResolverLock = new();
    private static readonly string[] PackagedRuntimeAssemblies =
    [
        "OpenAI",
        "System.ClientModel",
        "System.IO.Pipelines",
        "System.Net.ServerSentEvents",
        "Microsoft.Bcl.AsyncInterfaces",
        "System.Threading.Tasks.Extensions",
    ];

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        Instance = this;
        InstallPackagedRuntimeResolver();
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        Log($"Initializing MSSQL IntelliSense package (v{VersionString})...");
        if (await GetServiceAsync(typeof(IMenuCommandService)) is not OleMenuCommandService commands) return;

        foreach (var (id, name) in RegisteredCommands)
        {
            Register(commands, id, name);
        }

        RegisterRefreshSchemaCommand(commands);

        // Ensure the custom menu bar is created via DTE
        await EnsureMenuBarAsync(commands, cancellationToken);

        // Run self-healing cleanup of old conflicting directories asynchronously
        _ = Task.Run(() => CleanOldInstallations(), cancellationToken);

        // Initialize SQLitePCL raw provider for in-process SQLite access
        try
        {
            SQLitePCL.Batteries_V2.Init();
        }
        catch (Exception ex)
        {
            Log($"Failed to initialize SQLitePCL batteries: {ex.Message}");
        }

        // Initialize SQLite DB schema on startup
        _ = Task.Run(() => MssqlIntelliSense.Core.Metadata.MssqlIntelliSenseCacheWriter.InitializeDatabase(), cancellationToken);

        // Start in-process connection scanning loop to register active connections
        _ = Task.Run(() => StartConnectionSyncLoopAsync(DisposalToken), cancellationToken);

        Log("MSSQL IntelliSense package initialization completed.");

        var options = (MssqlIntelliSenseOptions)GetDialogPage(typeof(MssqlIntelliSenseOptions));
     
    }

    private static void InstallPackagedRuntimeResolver()
    {
        lock (RuntimeResolverLock)
        {
            if (_runtimeResolverInstalled)
            {
                return;
            }

            _runtimeResolverInstalled = true;
            AppDomain.CurrentDomain.AssemblyResolve += ResolvePackagedRuntimeAssembly;

            foreach (var assemblyName in PackagedRuntimeAssemblies)
            {
                TryPreloadPackagedRuntimeAssembly(assemblyName);
            }
        }
    }

    private static Assembly? ResolvePackagedRuntimeAssembly(object? sender, ResolveEventArgs args)
    {
        try
        {
            var requestedName = new AssemblyName(args.Name).Name;
            if (requestedName == null ||
                !PackagedRuntimeAssemblies.Contains(requestedName, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            return LoadPackagedRuntimeAssembly(requestedName);
        }
        catch (Exception ex)
        {
            Log($"Failed to resolve packaged runtime assembly '{args.Name}': {ex.Message}");
            return null;
        }
    }

    private static void TryPreloadPackagedRuntimeAssembly(string assemblyName)
    {
        try
        {
            var packagedAssemblyName = GetPackagedRuntimeAssemblyName(assemblyName);
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase) &&
                    (packagedAssemblyName == null ||
                     (assembly.GetName().Version?.CompareTo(packagedAssemblyName.Version) ?? -1) >= 0));
            if (loaded != null)
            {
                LogLoadedRuntimeAssembly(assemblyName, loaded, alreadyLoaded: true);
                return;
            }

            var assembly = LoadPackagedRuntimeAssembly(assemblyName);
            if (assembly != null)
            {
                LogLoadedRuntimeAssembly(assemblyName, assembly, alreadyLoaded: false);
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to preload packaged runtime assembly '{assemblyName}': {ex.Message}");
        }
    }

    private static AssemblyName? GetPackagedRuntimeAssemblyName(string assemblyName)
    {
        var assemblyPath = GetPackagedRuntimeAssemblyPath(assemblyName);
        if (assemblyPath == null)
        {
            return null;
        }

        return AssemblyName.GetAssemblyName(assemblyPath);
    }

    private static Assembly? LoadPackagedRuntimeAssembly(string assemblyName)
    {
        var assemblyPath = GetPackagedRuntimeAssemblyPath(assemblyName);
        if (assemblyPath == null)
        {
            return null;
        }

        return Assembly.LoadFrom(assemblyPath);
    }

    private static string? GetPackagedRuntimeAssemblyPath(string assemblyName)
    {
        var packageDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (string.IsNullOrWhiteSpace(packageDirectory))
        {
            return null;
        }

        var assemblyPath = Path.Combine(packageDirectory, assemblyName + ".dll");
        if (!File.Exists(assemblyPath))
        {
            Log($"Packaged runtime assembly not found: {assemblyPath}");
            return null;
        }

        return assemblyPath;
    }

    private static void LogLoadedRuntimeAssembly(string assemblyName, Assembly assembly, bool alreadyLoaded)
    {
        try
        {
            var origin = alreadyLoaded ? "already loaded" : "preloaded";
            Log($"Runtime assembly {origin}: {assemblyName} {assembly.GetName().Version} from {assembly.Location}");
        }
        catch
        {
            Log($"Runtime assembly {(alreadyLoaded ? "already loaded" : "preloaded")}: {assemblyName}");
        }
    }

    private void RegisterRefreshSchemaCommand(OleMenuCommandService commands)
    {
        var cmdId = new CommandID(CommandSet, 0x010A);
        commands.AddCommand(new MenuCommand((_, _) => JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ForceRefreshSchemaAsync(DisposalToken);
            }
            catch (Exception exception)
            {
                Log($"Refresh schema failed: {exception.Message}");
                await ShowMessageAsync("MSSQL IntelliSense", exception.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
            }
        }).FileAndForget("MssqlIntelliSense/RefreshSchema"), cmdId));
    }

    private async Task ForceRefreshSchemaAsync(CancellationToken cancellationToken)
    {
        string? connectionString = null;
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        connectionString = GetActiveConnectionString();

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await ShowMessageAsync("MSSQL IntelliSense", "No active database connection found. Please open a query window connected to a database first.", OLEMSGICON.OLEMSGICON_WARNING);
            return;
        }

        Log($"Manual refresh schema requested for: {connectionString}");

        bool isScanning;
        lock (_scanLock)
        {
            isScanning = _activelyScanningConnections.Contains(connectionString!);
        }

        if (isScanning)
        {
            await ShowMessageAsync("MSSQL IntelliSense", "A schema scan is already running for this database connection.", OLEMSGICON.OLEMSGICON_INFO);
            return;
        }

        Log("Starting manual schema scan in background...");
        
        _ = Task.Run(async () =>
        {
            lock (_scanLock)
            {
                _activelyScanningConnections.Add(connectionString);
            }
            try
            {
                var name = "SqlServer Connection";
                var normalizedConnStr = connectionString;
                try
                {
                    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
                    name = builder.DataSource;
                    builder.Remove("Initial Catalog");
                    normalizedConnStr = builder.ConnectionString;
                }
                catch {}

                var connId = MssqlIntelliSense.Core.Metadata.MssqlIntelliSenseCacheWriter.RegisterConnection(normalizedConnStr, name);
                
                var provider = new MssqlIntelliSense.Core.Metadata.SqlServerMetadataProvider(connectionString);
                var metadata = await provider.GetMetadataAsync(cancellationToken);

                MssqlIntelliSense.Core.Metadata.MssqlIntelliSenseCacheWriter.SaveSchemaCache(connId, metadata);

                Log($"Schema scan completed successfully for: {name}");
                await ShowMessageAsync("MSSQL IntelliSense", $"Schema scan completed successfully for database: {name}", OLEMSGICON.OLEMSGICON_INFO);
            }
            catch (Exception ex)
            {
                Log($"Schema scan failed: {ex.Message}");
                await ShowMessageAsync("MSSQL IntelliSense", $"Schema scan failed: {ex.Message}", OLEMSGICON.OLEMSGICON_CRITICAL);
            }
            finally
            {
                lock (_scanLock)
                {
                    _activelyScanningConnections.Remove(connectionString);
                }
            }
        });
    }

    private async Task EnsureMenuBarAsync(OleMenuCommandService commands, CancellationToken cancellationToken)
    {
        try
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Log("EnsureMenuBarAsync: Switched to main thread.");

            var dte = await GetServiceAsync(typeof(DTE)) as DTE;
            if (dte == null)
            {
                Log("EnsureMenuBarAsync: DTE service is null!");
                return;
            }
            Log("EnsureMenuBarAsync: Obtained DTE service successfully.");

            var commandBars = dte.CommandBars as CommandBars;
            if (commandBars == null)
            {
                Log("EnsureMenuBarAsync: dte.CommandBars is null!");
                return;
            }
            Log("EnsureMenuBarAsync: Obtained dte.CommandBars successfully.");

            var menuBar = commandBars["MenuBar"];
            if (menuBar == null)
            {
                Log("EnsureMenuBarAsync: MenuBar is null in commandBars!");
                return;
            }
            Log("EnsureMenuBarAsync: Obtained MenuBar.");

            CommandBarPopup? popup = null;

            // Search for existing menu bar
            foreach (CommandBarControl control in menuBar.Controls)
            {
                try
                {
                    if (string.Equals(control.Caption?.Replace("&", string.Empty), "MSSQL IntelliSense", StringComparison.OrdinalIgnoreCase))
                    {
                        popup = control as CommandBarPopup;
                        Log("EnsureMenuBarAsync: Found existing menu popup control.");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Log($"EnsureMenuBarAsync: Error checking control caption: {ex.Message}");
                }
            }

            // Re-create the menu bar to ensure it is fresh and handles events properly
            if (popup != null)
            {
                try
                {
                    Log("EnsureMenuBarAsync: Deleting existing popup menu...");
                    popup.Delete();
                    Log("EnsureMenuBarAsync: Deleted existing popup menu.");
                }
                catch (Exception ex)
                {
                    Log($"EnsureMenuBarAsync: Error deleting existing popup: {ex.Message}");
                }
                popup = null;
            }

            Log("EnsureMenuBarAsync: Creating new popup menu control...");
            popup = (CommandBarPopup)menuBar.Controls.Add(
                MsoControlType.msoControlPopup, Type.Missing, Type.Missing, Type.Missing, true);
            popup.Caption = "MSSQL IntelliSense";
            Log("EnsureMenuBarAsync: New popup menu created.");

            var menuItems = new (int Id, string Caption)[]
            {
                (0x010B, "MSSQL IntelliSense"),
                (0x010A, "Refresh Schema"),
                (0x010C, "Chat Agent"),
                (0x010D, "Tool Lab"),
            };

            foreach (var item in menuItems)
            {
                Log($"EnsureMenuBarAsync: Adding control button '{item.Caption}'...");
                var button = (CommandBarButton)popup.CommandBar.Controls.Add(
                    MsoControlType.msoControlButton, Type.Missing, Type.Missing, Type.Missing, true);
                button.Caption = item.Caption;

                Log($"EnsureMenuBarAsync: Binding command event handler for '{item.Caption}'...");
                var events = dte.Events.CommandBarEvents[button];
                var commandId = new CommandID(CommandSet, item.Id);
                events.Click += (object clickedControl, ref bool handled, ref bool cancelDefault) =>
                {
                    ThreadHelper.ThrowIfNotOnUIThread();
                    handled = commands.GlobalInvoke(commandId);
                    cancelDefault = handled;
                };
                _commandBarEvents.Add(events);
                Log($"EnsureMenuBarAsync: Bound event for '{item.Caption}' successfully.");
            }
            Log("EnsureMenuBarAsync: All menu items constructed successfully.");
        }
        catch (Exception ex)
        {
            Log($"Failed to ensure custom DTE menu bar: {ex.Message}");
        }
    }



    private void Register(OleMenuCommandService commands, int id, string engineCommand)
    {
        commands.AddCommand(new MenuCommand((_, _) => JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ExecuteAsync(engineCommand, DisposalToken);
            }
            catch (Exception exception)
            {
                Log($"Command '{engineCommand}' failed: {exception.Message}");
                await ShowMessageAsync("MSSQL IntelliSense", exception.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
            }
        }).FileAndForget($"MssqlIntelliSense/{engineCommand}"), new CommandID(CommandSet, id)));
    }

    public static void OnOptionsChanged()
    {
        Log("Options changed.");
    }

    private async Task ExecuteAsync(string command, CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        Log($"Executing command '{command}'...");

        if (command == "mssql-intellisense-window")
        {
            await OpenMssqlIntelliSenseWindowAsync(cancellationToken);
            return;
        }
        else if (command == "chat-agent-window")
        {
            await OpenChatAgentWindowAsync(cancellationToken);
            return;
        }
        else if (command == "tool-lab-window")
        {
            await OpenToolLabWindowAsync(cancellationToken);
            return;
        }
    }

    private async Task OpenChatAgentWindowAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        try
        {
            // FindToolWindow creates the pane if it does not yet exist (create: true).
            var window = await ShowToolWindowAsync(
                typeof(ChatAgentToolWindowPane),
                id: 0,
                create: true,
                cancellationToken: cancellationToken) as ChatAgentToolWindowPane;

            if (window?.Frame is not IVsWindowFrame frame)
            {
                Log("Failed to open Chat Agent tool window: frame is null.");
                await ShowMessageAsync("MSSQL IntelliSense", "Failed to open Chat Agent panel.", OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            ErrorHandler.ThrowOnFailure(frame.Show());
        }
        catch (Exception ex)
        {
            var message = GetDeepestExceptionMessage(ex);
            Log($"Failed to open Chat Agent window: {ex}");
            await ShowMessageAsync("MSSQL IntelliSense", $"Failed to open Chat Agent Window: {message}", OLEMSGICON.OLEMSGICON_CRITICAL);
        }
    }

    private static string GetDeepestExceptionMessage(Exception ex)
    {
        while (ex.InnerException != null)
        {
            ex = ex.InnerException;
        }

        return ex.Message;
    }

    private async Task OpenToolLabWindowAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        try
        {
            var window = await ShowToolWindowAsync(
                typeof(ToolLabToolWindowPane),
                id: 0,
                create: true,
                cancellationToken: cancellationToken) as ToolLabToolWindowPane;

            if (window?.Frame is not IVsWindowFrame frame)
            {
                Log("Failed to open Tool Lab tool window: frame is null.");
                await ShowMessageAsync("MSSQL IntelliSense", "Failed to open Tool Lab panel.", OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            ErrorHandler.ThrowOnFailure(frame.Show());
        }
        catch (Exception ex)
        {
            Log($"Failed to open Tool Lab window: {ex.Message}");
            await ShowMessageAsync("MSSQL IntelliSense", $"Failed to open Tool Lab Window: {ex.Message}", OLEMSGICON.OLEMSGICON_CRITICAL);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
    }


    private static MssqlIntelliSenseWindow? _MssqlIntelliSenseWindowInstance;

    private async Task OpenMssqlIntelliSenseWindowAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        if (_MssqlIntelliSenseWindowInstance != null)
        {
            _MssqlIntelliSenseWindowInstance.Activate();
            if (_MssqlIntelliSenseWindowInstance.WindowState == System.Windows.WindowState.Minimized)
            {
                _MssqlIntelliSenseWindowInstance.WindowState = System.Windows.WindowState.Normal;
            }
            return;
        }

        try
        {
            var window = new MssqlIntelliSenseWindow();
            var shell = await GetServiceAsync(typeof(SVsUIShell)) as IVsUIShell;
            if (shell != null)
            {
                shell.GetDialogOwnerHwnd(out var hwnd);
                if (hwnd != IntPtr.Zero)
                {
                    new System.Windows.Interop.WindowInteropHelper(window).Owner = hwnd;
                }
            }
            window.Closed += (s, e) => _MssqlIntelliSenseWindowInstance = null;
            _MssqlIntelliSenseWindowInstance = window;
            window.Show();
        }
        catch (Exception ex)
        {
            Log($"Failed to open MSSQL IntelliSense window: {ex.Message}");
            await ShowMessageAsync("MSSQL IntelliSense", $"Failed to open MSSQL IntelliSense Window: {ex.Message}", OLEMSGICON.OLEMSGICON_CRITICAL);
        }
    }

    internal void ForceRefreshSchemaForConnectionString(string connectionString, IProgress<string>? progress, Action onComplete)
    {
        ForceRefreshSchemaForConnectionString(connectionString, progress, onComplete, MssqlIntelliSense.Core.Metadata.MetadataScanScope.All, null);
    }

    internal void ForceRefreshSchemaForConnectionString(
        string connectionString,
        IProgress<string>? progress,
        Action onComplete,
        MssqlIntelliSense.Core.Metadata.MetadataScanScope scope,
        string? databaseName)
    {
        bool isScanning;
        lock (_scanLock)
        {
            isScanning = _activelyScanningConnections.Contains(connectionString + "|" + scope + "|" + databaseName);
        }

        if (isScanning)
        {
            JoinableTaskFactory.RunAsync(async () =>
            {
                await ShowMessageAsync("MSSQL IntelliSense", "A schema scan is already running for this database connection.", OLEMSGICON.OLEMSGICON_INFO);
            }).FileAndForget("MssqlIntelliSense/AlreadyScanning");
            return;
        }

        Log($"Manual refresh schema requested from Cache Viewer for: {connectionString}");

        _ = Task.Run(async () =>
        {
            lock (_scanLock)
            {
                _activelyScanningConnections.Add(connectionString + "|" + scope + "|" + databaseName);
            }
            try
            {
                var name = "SqlServer Connection";
                var normalizedConnStr = connectionString;
                try
                {
                    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
                    name = builder.DataSource;
                    builder.Remove("Initial Catalog");
                    normalizedConnStr = builder.ConnectionString;
                }
                catch {}

                var connId = MssqlIntelliSense.Core.Metadata.MssqlIntelliSenseCacheWriter.RegisterConnection(normalizedConnStr, name);
                
                var provider = new MssqlIntelliSense.Core.Metadata.SqlServerMetadataProvider(connectionString);
                var scannedMetadata = await provider.GetMetadataAsync(progress, scope, databaseName, DisposalToken);
                var metadata = scope == MssqlIntelliSense.Core.Metadata.MetadataScanScope.All
                    ? scannedMetadata
                    : MergeMetadata(MssqlIntelliSense.Core.Metadata.MssqlIntelliSenseCacheReader.GetSchemaDetails(connId).Metadata, scannedMetadata, scope, databaseName);

                MssqlIntelliSense.Core.Metadata.MssqlIntelliSenseCacheWriter.SaveSchemaCache(connId, metadata);

                Log($"Schema scan completed successfully for: {name}");
                onComplete?.Invoke();
            }
            catch (Exception ex)
            {
                Log($"Schema scan failed: {ex.Message}");
                JoinableTaskFactory.RunAsync(async () =>
                {
                    await ShowMessageAsync("MSSQL IntelliSense", $"Schema scan failed: {ex.Message}", OLEMSGICON.OLEMSGICON_CRITICAL);
                }).FileAndForget("MssqlIntelliSense/ScanFailed");
            }
            finally
            {
                lock (_scanLock)
                {
                    _activelyScanningConnections.Remove(connectionString + "|" + scope + "|" + databaseName);
                }
            }
        });
    }

    private static MssqlIntelliSense.Core.Metadata.DatabaseMetadata MergeMetadata(
        MssqlIntelliSense.Core.Metadata.DatabaseMetadata existing,
        MssqlIntelliSense.Core.Metadata.DatabaseMetadata scanned,
        MssqlIntelliSense.Core.Metadata.MetadataScanScope scope,
        string? databaseName)
    {
        bool InDatabase(string value) =>
            string.IsNullOrWhiteSpace(databaseName) ||
            value.Equals(databaseName, StringComparison.OrdinalIgnoreCase);

        var databases = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.DatabaseList)
            ? scanned.Databases
            : existing.Databases.Union(scanned.Databases, StringComparer.OrdinalIgnoreCase).ToArray();

        var tables = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.Tables)
            ? existing.Tables.Where(t => !InDatabase(t.Database)).Concat(scanned.Tables).ToArray()
            : existing.Tables;
        var fks = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.Relations)
            ? existing.ForeignKeys.Where(fk => !InDatabase(fk.Database)).Concat(scanned.ForeignKeys).ToArray()
            : existing.ForeignKeys;
        var indexes = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.Indexes)
            ? existing.Indexes.Where(i => !InDatabase(i.Database)).Concat(scanned.Indexes).ToArray()
            : existing.Indexes;
        var linkedServers = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.LinkedServers)
            ? scanned.LinkedServers
            : existing.LinkedServers;
        var endpoints = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.Endpoints)
            ? scanned.Endpoints
            : existing.Endpoints;

        return new MssqlIntelliSense.Core.Metadata.DatabaseMetadata(tables, fks, indexes, databases, linkedServers)
        {
            Procedures = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.Programmability)
                ? existing.Procedures.Where(p => !InDatabase(p.Database)).Concat(scanned.Procedures).ToArray()
                : existing.Procedures,
            Views = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.Programmability)
                ? existing.Views.Where(v => !InDatabase(v.Database)).Concat(scanned.Views).ToArray()
                : existing.Views,
            Functions = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.Programmability)
                ? existing.Functions.Where(f => !InDatabase(f.Database)).Concat(scanned.Functions).ToArray()
                : existing.Functions,
            Triggers = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.Programmability)
                ? existing.Triggers.Where(t => !InDatabase(t.Database)).Concat(scanned.Triggers).ToArray()
                : existing.Triggers,
            UserTypes = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.Security)
                ? existing.UserTypes.Where(t => !InDatabase(t.Database)).Concat(scanned.UserTypes).ToArray()
                : existing.UserTypes,
            Synonyms = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.Security)
                ? existing.Synonyms.Where(s => !InDatabase(s.Database)).Concat(scanned.Synonyms).ToArray()
                : existing.Synonyms,
            Users = scope.HasFlag(MssqlIntelliSense.Core.Metadata.MetadataScanScope.Security)
                ? existing.Users.Where(u => !InDatabase(u.Database)).Concat(scanned.Users).ToArray()
                : existing.Users,
            Endpoints = endpoints
        };
    }




    private static async Task StartConnectionSyncLoopAsync(CancellationToken cancellationToken)
    {
        Log("In-process connection scanning loop started.");
        await Task.Delay(5000, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                string? currentConnectionString = null;

                if (Instance != null)
                {
                    await Instance.JoinableTaskFactory.RunAsync(async () =>
                    {
                        await Instance.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
                        currentConnectionString = GetActiveConnectionString();
                    });
                }

                if (!string.IsNullOrWhiteSpace(currentConnectionString))
                {
                    var name = "SqlServer Connection";
                    var normalizedConnStr = currentConnectionString;
                    try
                    {
                        var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(currentConnectionString);
                        name = builder.DataSource;
                        builder.Remove("Initial Catalog");
                        normalizedConnStr = builder.ConnectionString;
                    }
                    catch {}

                    MssqlIntelliSense.Core.Metadata.MssqlIntelliSenseCacheWriter.RegisterConnection(normalizedConnStr, name);
                }
            }
            catch (Exception ex)
            {
                Log($"Error in connection scanning loop: {ex.Message}");
            }

            await Task.Delay(5000, cancellationToken);
        }
    }

    internal static async Task<(string ApiKey, string Model, string Endpoint)> FetchLlmSettingsStaticAsync(CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        var options = GetOptions();
        if (options != null)
        {
            return (options.ApiKey, options.Model, options.Endpoint);
        }
        return (string.Empty, "gpt-4o", "https://api.openai.com/v1/responses");
    }

    private async Task ShowMessageAsync(string title, string message, OLEMSGICON icon)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync();
        VsShellUtilities.ShowMessageBox(this, message, title, icon, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    internal static string? GetActiveConnectionString()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (DateTime.UtcNow - _activeConnectionCacheLoadedAt < ActiveConnectionCacheDuration)
            {
                return _cachedActiveConnectionString;
            }

            var uiConnectionInfo = GetActiveUiConnectionInfo();
            if (uiConnectionInfo == null)
            {
                CacheActiveConnection(null, null);
                return null;
            }

            var uiType = uiConnectionInfo.GetType();
            var serverName = uiType.GetProperty("ServerName")?.GetValue(uiConnectionInfo) as string;
            var databaseName = GetDatabaseNameFromUiConnectionInfo(uiConnectionInfo) ?? GetActiveDatabaseNameFromToolbar();
            var userName = uiType.GetProperty("UserName")?.GetValue(uiConnectionInfo) as string;
            var password = uiType.GetProperty("Password")?.GetValue(uiConnectionInfo) as string;
            
            var authTypeProp = uiType.GetProperty("AuthenticationType");
            int authType = authTypeProp != null ? Convert.ToInt32(authTypeProp.GetValue(uiConnectionInfo)) : 0;
            bool isWindowsAuth = authType == 0 || string.IsNullOrEmpty(userName);

            if (string.IsNullOrEmpty(serverName))
            {
                CacheActiveConnection(null, null);
                return null;
            }

            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder
            {
                DataSource = serverName,
                InitialCatalog = string.IsNullOrEmpty(databaseName) ? "master" : databaseName,
                TrustServerCertificate = true
            };

            if (isWindowsAuth)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.UserID = userName;
                builder.Password = password;
            }

            CacheActiveConnection(builder.ConnectionString, databaseName);
            return _cachedActiveConnectionString;
        }
        catch
        {
            CacheActiveConnection(null, null);
            return null;
        }
    }

    internal static string? GetActiveDatabaseName()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (DateTime.UtcNow - _activeConnectionCacheLoadedAt < ActiveConnectionCacheDuration)
            {
                return _cachedActiveDatabaseName;
            }

            var connectionString = GetActiveConnectionString();
            if (!string.IsNullOrWhiteSpace(_cachedActiveDatabaseName))
            {
                return _cachedActiveDatabaseName;
            }

            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                try
                {
                    var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
                    return string.IsNullOrWhiteSpace(builder.InitialCatalog) ? null : builder.InitialCatalog;
                }
                catch
                {
                    return null;
                }
            }

            var uiConnectionInfo = GetActiveUiConnectionInfo();
            if (uiConnectionInfo != null)
            {
                var databaseName = GetDatabaseNameFromUiConnectionInfo(uiConnectionInfo);
                if (!string.IsNullOrWhiteSpace(databaseName))
                {
                    return databaseName;
                }
            }

            return GetActiveDatabaseNameFromToolbar();
        }
        catch
        {
            return null;
        }
    }

    private static void CacheActiveConnection(string? connectionString, string? databaseName)
    {
        _cachedActiveConnectionString = connectionString;
        _cachedActiveDatabaseName = string.IsNullOrWhiteSpace(databaseName) ? null : databaseName;
        _activeConnectionCacheLoadedAt = DateTime.UtcNow;
    }

    private static object? GetActiveUiConnectionInfo()
    {
        var serviceCacheType = Type.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache, SqlPackageBase");
        if (serviceCacheType == null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                serviceCacheType = assembly.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
                if (serviceCacheType != null) break;
            }
        }

        if (serviceCacheType == null) return null;

        var scriptFactoryProp = serviceCacheType.GetProperty("ScriptFactory", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (scriptFactoryProp == null) return null;

        var scriptFactory = scriptFactoryProp.GetValue(null);
        if (scriptFactory == null) return null;

        var activeConnProp = scriptFactory.GetType().GetProperty("CurrentlyActiveWndConnectionInfo", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (activeConnProp == null) return null;

        var connectionInfo = activeConnProp.GetValue(scriptFactory);
        if (connectionInfo == null) return null;

        var uiConnProp = connectionInfo.GetType().GetProperty("UIConnectionInfo", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return uiConnProp?.GetValue(connectionInfo);
    }

    private static string? GetDatabaseNameFromUiConnectionInfo(object uiConnectionInfo)
    {
        var uiType = uiConnectionInfo.GetType();
        foreach (var propertyName in new[] { "DatabaseName", "InitialCatalog", "Database", "CurrentDatabase" })
        {
            var value = uiType.GetProperty(propertyName)?.GetValue(uiConnectionInfo) as string;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        if (uiType.GetProperty("AdvancedOptions")?.GetValue(uiConnectionInfo) is System.Collections.Specialized.NameValueCollection options)
        {
            foreach (var key in new[] { "Database", "Initial Catalog", "InitialCatalog", "Current Database" })
            {
                var value = options[key];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? GetActiveDatabaseNameFromToolbar()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Instance?.GetService(typeof(DTE)) is not DTE dte ||
            dte.CommandBars is not CommandBars commandBars)
        {
            return null;
        }

        foreach (CommandBar commandBar in commandBars)
        {
            if (!commandBar.Visible) continue;

            var databaseName = FindDatabaseNameComboText(commandBar.Controls);
            if (!string.IsNullOrWhiteSpace(databaseName))
            {
                return databaseName;
            }
        }

        return null;
    }

    private static string? FindDatabaseNameComboText(CommandBarControls controls)
    {
        foreach (CommandBarControl control in controls)
        {
            try
            {
                if (control is CommandBarComboBox combo)
                {
                    var text = combo.Text?.Trim();
                    if (IsLikelyDatabaseName(text))
                    {
                        return text;
                    }
                }

                if (control is CommandBarPopup popup)
                {
                    var popupCommandBar = popup.CommandBar;
                    if (popupCommandBar == null) continue;

                    var popupControls = popupCommandBar.Controls;
                    if (popupControls == null) continue;

                    var nested = FindDatabaseNameComboText(popupControls);
                    if (!string.IsNullOrWhiteSpace(nested))
                    {
                        return nested;
                    }
                }
            }
            catch
            {
                // Some SSMS command bar controls throw when inspected; ignore them.
            }
        }

        return null;
    }

    private static bool IsLikelyDatabaseName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length > 128) return false;
        if (value.Contains("\\") || value.Contains(":") || value.Contains("/")) return false;
        if (value.EndsWith("%", StringComparison.Ordinal)) return false;

        return !value.Equals("Execute", StringComparison.OrdinalIgnoreCase) &&
               !value.Equals("master", StringComparison.OrdinalIgnoreCase);
    }

    private void CleanOldInstallations()
    {
        try
        {
            var currentAssemblyPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var currentDir = System.IO.Path.GetDirectoryName(currentAssemblyPath);
            if (string.IsNullOrEmpty(currentDir)) return;
            var parentDir = System.IO.Path.GetDirectoryName(currentDir); // This is the "Extensions" folder
            
            if (string.IsNullOrEmpty(parentDir) || !System.IO.Directory.Exists(parentDir)) return;
            
            foreach (var dir in System.IO.Directory.GetDirectories(parentDir))
            {
                if (string.Equals(dir, currentDir, StringComparison.OrdinalIgnoreCase)) continue;
                
                var dllPath = System.IO.Path.Combine(dir, "MssqlIntelliSense.SsmsHost.dll");
                if (System.IO.File.Exists(dllPath))
                {
                    try
                    {
                        System.IO.Directory.Delete(dir, true);
                    }
                    catch
                    {
                        // Sibling folder DLL might be locked if SSMS somehow loaded it; ignore and proceed
                    }
                }
            }
        }
        catch
        {
            // Fail silently to prevent package initialization crashes
        }
    }

    private static IVsOutputWindowPane? _outputPane;
    private static readonly Guid OutputPaneGuid = new("5F2E23E6-2005-4D05-B86D-8D5FA5470FE7");

    public static void Log(string message)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var formatted = $"[{timestamp}] {message}";

            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var logFile = System.IO.Path.Combine(profile, ".gemini", "antigravity-cli", "package_log.txt");
                var dir = System.IO.Path.GetDirectoryName(logFile);
                if (dir != null && !System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                System.IO.File.AppendAllText(logFile, formatted + Environment.NewLine);
            }
            catch { }

            var jtf = Instance?.JoinableTaskFactory ?? ThreadHelper.JoinableTaskFactory;
            jtf.RunAsync(async () =>
            {
                await jtf.SwitchToMainThreadAsync();
                
                if (_outputPane == null)
                {
                    var outWindow = GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                    if (outWindow != null)
                    {
                        var guid = OutputPaneGuid;
                        outWindow.GetPane(ref guid, out _outputPane);
                        if (_outputPane == null)
                        {
                            outWindow.CreatePane(ref guid, "MSSQL IntelliSense", 1, 1);
                            outWindow.GetPane(ref guid, out _outputPane);
                        }
                    }
                }

                if (_outputPane != null)
                {
                    _outputPane.OutputString(formatted + Environment.NewLine);
                }
            }).FileAndForget("MssqlIntelliSense/LogOutput");
        }
        catch
        {
            // Fail-safe to avoid crashing the extension
        }
    }
}

















































