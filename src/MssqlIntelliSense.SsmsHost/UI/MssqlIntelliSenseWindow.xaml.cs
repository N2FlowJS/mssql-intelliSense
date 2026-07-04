using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Completion.Candidates;
using MssqlIntelliSense.Core.Completion.Snippets;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.SsmsHost;

public class TreeViewItemViewModel : INotifyPropertyChanged
{
    private bool _isExpanded;
    private bool _isSelected;
    private string _name = string.Empty;
    private string _icon = string.Empty;
    private ImageSource? _iconSource;
    private ObservableCollection<TreeViewItemViewModel> _children = new ObservableCollection<TreeViewItemViewModel>();
    private bool _childrenLoaded = false; // Tracks if children have been lazily loaded
    private bool _isLoadingChildren;
    public object? Tag { get; set; }
    public Action<TreeViewItemViewModel>? LoadChildrenAction { get; set; } // Optional action to load children on expand
    public Func<TreeViewItemViewModel, Task>? LoadChildrenAsyncAction { get; set; }
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }
    public string Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(nameof(Icon)); }
    }
    public ImageSource? IconSource
    {
        get => _iconSource;
        set { _iconSource = value; OnPropertyChanged(nameof(IconSource)); }
    }
    public ObservableCollection<TreeViewItemViewModel> Children
    {
        get => _children;
        set { _children = value; OnPropertyChanged(nameof(Children)); }
    }
    public bool IsExpanded
    {
        get => _isExpanded;
        set 
        { 
            _isExpanded = value; 
            OnPropertyChanged(nameof(IsExpanded)); 
            if (_isExpanded && !_childrenLoaded && !_isLoadingChildren)
            {
                _ = LoadChildrenAsync();
            }
        }
    }
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private async Task LoadChildrenAsync()
    {
        _isLoadingChildren = true;
        try
        {
            if (LoadChildrenAsyncAction != null)
            {
                await LoadChildrenAsyncAction(this);
            }
            else
            {
                LoadChildrenAction?.Invoke(this);
            }

            _childrenLoaded = true;
        }
        catch (Exception ex)
        {
            Children.Clear();
            Children.Add(new TreeViewItemViewModel { Name = "Error loading: " + ex.Message });
        }
        finally
        {
            _isLoadingChildren = false;
        }
    }
}

public sealed class SchemaExplorerNodeContext
{
    public SchemaExplorerNodeContext(int connectionId, string group, string? databaseName = null)
    {
        ConnectionId = connectionId;
        Group = group;
        DatabaseName = databaseName;
    }

    public int ConnectionId { get; }
    public string Group { get; }
    public string? DatabaseName { get; }
}

public partial class MssqlIntelliSenseWindow : Window
{
    public ObservableCollection<TreeViewItemViewModel> RootNodes { get; set; } = new ObservableCollection<TreeViewItemViewModel>();
    private ConnectionInfo? _selectedConnection;
    private readonly Dictionary<int, (ConnectionInfo conn, DatabaseMetadata metadata)> _connectionData = new();

    public MssqlIntelliSenseWindow()
    {
        InitializeComponent();
        Loaded += MssqlIntelliSenseWindow_Loaded;
        DataContext = this;
    }

    private void MssqlIntelliSenseWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            SettingsNavIcon.Source = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Settings);
            AboutNavIcon.Source = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Information);
            await RefreshConnectionsTreeAsync();
        }).FileAndForget("MssqlIntelliSense/LoadSchemaExplorerWindow");
    }

    private static TreeViewItemViewModel CreateLoadingNode()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        return new TreeViewItemViewModel
        {
            Name = "Loading...",
            Icon = "⏳",
            IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.StatusInformation)
        };
    }

    private void RefreshConnectionsTree()
    {
        _ = RefreshConnectionsTreeAsync();
    }

    private async Task RefreshConnectionsTreeAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            RootNodes.Clear();
            _connectionData.Clear();
            RootNodes.Add(CreateLoadingNode());

            var connections = await Task.Run(MssqlIntelliSenseCacheReader.GetConnections);

            RootNodes.Clear();
            foreach (var conn in connections)
            {
                var connNode = new TreeViewItemViewModel
                {
                    Name = conn.Name,
                    Icon = "🗄️",
                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Connection),
                    Tag = conn,
                    Children = new ObservableCollection<TreeViewItemViewModel> { CreateLoadingNode() }
                };

                connNode.LoadChildrenAsyncAction = (node) =>
                {
                    node.Children.Clear();

                    // Add databases
                    var databasesNode = new TreeViewItemViewModel 
                    { 
                        Name = "Databases", 
                        Icon = "📊", 
                        IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Database),
                        Tag = new SchemaExplorerNodeContext(conn.Id, "databases"),
                        Children = new ObservableCollection<TreeViewItemViewModel> { CreateLoadingNode() } 
                    };
                    databasesNode.LoadChildrenAsyncAction = (dbParent) => LoadDatabaseChildrenAsync(dbParent, conn.Id);
                    node.Children.Add(databasesNode);

                    // Add server objects
                    var serverObjectsNode = new TreeViewItemViewModel
                    {
                        Name = "Server Objects",
                        Icon = "⚙️",
                        IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.ServerObjects),
                        Tag = new SchemaExplorerNodeContext(conn.Id, "server_objects"),
                        Children = new ObservableCollection<TreeViewItemViewModel> { CreateLoadingNode() }
                    };
                    serverObjectsNode.LoadChildrenAsyncAction = (serverObjectsParent) => LoadServerObjectsAsync(serverObjectsParent, conn.Id);
                    node.Children.Add(serverObjectsNode);

                    return Task.CompletedTask;
                };

                RootNodes.Add(connNode);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi khi làm mới cây kết nối: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadServerObjectsAsync(TreeViewItemViewModel serverObjectsNode, int connectionId)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            serverObjectsNode.Children.Clear();

            var endpointsNode = new TreeViewItemViewModel
            {
                Name = "Endpoints",
                Icon = "🔌",
                IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Endpoint),
                Tag = new SchemaExplorerNodeContext(connectionId, "endpoints"),
                Children = new ObservableCollection<TreeViewItemViewModel> { CreateLoadingNode() }
            };
            endpointsNode.LoadChildrenAsyncAction = (endpointParent) => LoadEndpointsAsync(endpointParent, connectionId);
            serverObjectsNode.Children.Add(endpointsNode);

            var linkedServersNode = new TreeViewItemViewModel
            {
                Name = "Linked Servers",
                Icon = "🔗",
                IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.LinkedServer),
                Tag = new SchemaExplorerNodeContext(connectionId, "linked_servers"),
                Children = new ObservableCollection<TreeViewItemViewModel> { CreateLoadingNode() }
            };
            linkedServersNode.LoadChildrenAsyncAction = (lsParent) => LoadLinkedServersAsync(lsParent, connectionId);
            serverObjectsNode.Children.Add(linkedServersNode);
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Load Server Objects Error] {ex.Message}");
            serverObjectsNode.Children.Clear();
            serverObjectsNode.Children.Add(new TreeViewItemViewModel { Name = "Error loading: " + ex.Message, Icon = "❌", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Warning) });
        }
    }

    private async Task LoadEndpointsAsync(TreeViewItemViewModel endpointsNode, int connectionId)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            endpointsNode.Children.Clear();

            var data = await TryGetConnectionDataAsync(connectionId);
            if (data == null) return;
            var metadata = data.Value.metadata;

            var endpoints = await Task.Run(() => metadata.Endpoints.OrderBy(ep => ep.Name).ToList());
            if (endpoints.Count == 0)
            {
                endpointsNode.Children.Add(new TreeViewItemViewModel
                {
                    Name = "No endpoints found",
                    Icon = "ℹ️",
                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.StatusInformation)
                });
                return;
            }

            for (var i = 0; i < endpoints.Count; i++)
            {
                var endpoint = endpoints[i];
                var portText = endpoint.Port > 0 ? $":{endpoint.Port}" : string.Empty;
                endpointsNode.Children.Add(new TreeViewItemViewModel
                {
                    Name = $"{endpoint.Name} ({endpoint.Protocol}{portText}, {endpoint.State})",
                    Icon = "🔌",
                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Endpoint),
                    Tag = endpoint
                });

                if ((i + 1) % 100 == 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Load Endpoints Error] {ex.Message}");
            endpointsNode.Children.Clear();
            endpointsNode.Children.Add(new TreeViewItemViewModel { Name = "Error loading: " + ex.Message, Icon = "❌", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Warning) });
        }
    }

    private async Task LoadDatabaseChildrenAsync(TreeViewItemViewModel databasesNode, int connectionId)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            databasesNode.Children.Clear();
            databasesNode.Children.Add(CreateLoadingNode());

            var data = await TryGetConnectionDataAsync(connectionId);
            databasesNode.Children.Clear();
            if (data == null)
            {
                databasesNode.Children.Add(new TreeViewItemViewModel
                {
                    Name = "No schema cache available",
                    Icon = "ℹ️",
                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.StatusInformation)
                });
                return;
            }

            var metadata = data.Value.metadata;

            var databases = await Task.Run(() => metadata.Tables.Select(t => t.Database)
                    .Concat(metadata.Databases)
                    .Concat(metadata.Views.Select(v => v.Database))
                    .Concat(metadata.Procedures.Select(p => p.Database))
                    .Concat(metadata.Functions.Select(f => f.Database))
                    .Concat(metadata.Users.Select(u => u.Database))
                    .Where(d => !string.IsNullOrWhiteSpace(d))
                    .Distinct()
                    .OrderBy(d => d)
                    .ToList());

            if (databases.Count == 0)
            {
                databasesNode.Children.Add(new TreeViewItemViewModel
                {
                    Name = "No databases found",
                    Icon = "ℹ️",
                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.StatusInformation)
                });
                return;
            }

            foreach (var dbName in databases)
            {
                var dbNode = new TreeViewItemViewModel 
                { 
                    Name = dbName, 
                    Icon = "🗂️", 
                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Database),
                    Tag = new SchemaExplorerNodeContext(connectionId, "database", dbName),
                    Children = new ObservableCollection<TreeViewItemViewModel> { CreateLoadingNode() } 
                };
                dbNode.LoadChildrenAsyncAction = (node) => LoadDbObjectsAsync(node, connectionId, dbName);
                databasesNode.Children.Add(dbNode);
            }
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Load Database Children Error] {ex.Message}");
            databasesNode.Children.Clear();
            databasesNode.Children.Add(new TreeViewItemViewModel { Name = "Error loading: " + ex.Message, Icon = "❌", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Warning) });
        }
    }

    private async Task LoadDbObjectsAsync(TreeViewItemViewModel dbNode, int connectionId, string dbName)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            dbNode.Children.Clear();
            dbNode.Children.Add(CreateLoadingNode());

            var data = await TryGetConnectionDataAsync(connectionId);
            dbNode.Children.Clear();
            if (data == null) return;
            var metadata = data.Value.metadata;

            // Tables
            var tables = await Task.Run(() => metadata.Tables.Where(t => t.Database == dbName).OrderBy(t => t.Schema).ThenBy(t => t.Name).ToList());
            if (tables.Count > 0)
            {
                var tablesNode = new TreeViewItemViewModel 
                { 
                    Name = "Tables", 
                    Icon = "📋", 
                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Table),
                    Children = new ObservableCollection<TreeViewItemViewModel> { CreateLoadingNode() } 
                };
                tablesNode.LoadChildrenAsyncAction = async (node) =>
                {
                    node.Children.Clear();
                    var rendered = 0;
                    foreach (var table in tables)
                    {
                        var tableNode = new TreeViewItemViewModel 
                        { 
                            Name = $"{table.Schema}.{table.Name}", 
                            Icon = "📋", 
                            IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Table),
                            Tag = table, 
                            Children = new ObservableCollection<TreeViewItemViewModel> { CreateLoadingNode() } 
                        };
                        tableNode.LoadChildrenAsyncAction = async (tn) =>
                        {
                            tn.Children.Clear();
                            var colsNode = new TreeViewItemViewModel { Name = "Columns", Icon = "📊", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Column) };
                            var columnRendered = 0;
                            foreach (var col in table.Columns)
                            {
                                var colNode = new TreeViewItemViewModel 
                                { 
                                    Name = $"{col.Name} ({col.DataType})", 
                                    Icon = col.IsNullable ? "🔸" : "🔷", 
                                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Column),
                                    Tag = col 
                                };
                                colsNode.Children.Add(colNode);
                                columnRendered++;
                                if (columnRendered % 100 == 0)
                                {
                                    await Dispatcher.Yield(DispatcherPriority.Background);
                                }
                            }
                            tn.Children.Add(colsNode);
                        };
                        node.Children.Add(tableNode);
                        rendered++;
                        if (rendered % 100 == 0)
                        {
                            await Dispatcher.Yield(DispatcherPriority.Background);
                        }
                    }
                };
                dbNode.Children.Add(tablesNode);
            }

            // Views
            var views = await Task.Run(() => metadata.Views.Where(v => v.Database == dbName).OrderBy(v => v.Schema).ThenBy(v => v.Name).ToList());
            if (views.Count > 0)
            {
                var viewsNode = new TreeViewItemViewModel 
                { 
                    Name = "Views", 
                    Icon = "👁️", 
                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.View),
                    Children = new ObservableCollection<TreeViewItemViewModel> { CreateLoadingNode() } 
                };
                viewsNode.LoadChildrenAsyncAction = async (node) =>
                {
                    node.Children.Clear();
                    var rendered = 0;
                    foreach (var view in views)
                    {
                        var viewNode = new TreeViewItemViewModel 
                        { 
                            Name = $"{view.Schema}.{view.Name}", 
                            Icon = "👁️", 
                            IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.View),
                            Tag = view, 
                            Children = new ObservableCollection<TreeViewItemViewModel> { CreateLoadingNode() } 
                        };
                        viewNode.LoadChildrenAsyncAction = async (vn) =>
                        {
                            vn.Children.Clear();
                            var colsNode = new TreeViewItemViewModel { Name = "Columns", Icon = "📊", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Column) };
                            var columnRendered = 0;
                            foreach (var col in view.Columns)
                            {
                                var colNode = new TreeViewItemViewModel 
                                { 
                                    Name = $"{col.Name} ({col.DataType})", 
                                    Icon = col.IsNullable ? "🔸" : "🔷", 
                                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Column),
                                    Tag = col 
                                };
                                colsNode.Children.Add(colNode);
                                columnRendered++;
                                if (columnRendered % 100 == 0)
                                {
                                    await Dispatcher.Yield(DispatcherPriority.Background);
                                }
                            }
                            vn.Children.Add(colsNode);
                        };
                        node.Children.Add(viewNode);
                        rendered++;
                        if (rendered % 100 == 0)
                        {
                            await Dispatcher.Yield(DispatcherPriority.Background);
                        }
                    }
                };
                dbNode.Children.Add(viewsNode);
            }

            // Procedures
            var procs = await Task.Run(() => metadata.Procedures.Where(p => p.Database == dbName).OrderBy(p => p.Schema).ThenBy(p => p.Name).ToList());
            if (procs.Count > 0)
            {
                var procsNode = new TreeViewItemViewModel { Name = "Procedures", Icon = "⚡", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Procedure) };
                for (var i = 0; i < procs.Count; i++)
                {
                    var proc = procs[i];
                    procsNode.Children.Add(new TreeViewItemViewModel { Name = $"{proc.Schema}.{proc.Name}", Icon = "⚡", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Procedure), Tag = proc });
                    if ((i + 1) % 100 == 0)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }
                dbNode.Children.Add(procsNode);
            }

            // Functions
            var funcs = await Task.Run(() => metadata.Functions.Where(f => f.Database == dbName).OrderBy(f => f.Schema).ThenBy(f => f.Name).ToList());
            if (funcs.Count > 0)
            {
                var funcsNode = new TreeViewItemViewModel { Name = "Functions", Icon = "ƒ", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Function) };
                for (var i = 0; i < funcs.Count; i++)
                {
                    var func = funcs[i];
                    funcsNode.Children.Add(new TreeViewItemViewModel { Name = $"{func.Schema}.{func.Name}", Icon = "ƒ", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Function), Tag = func });
                    if ((i + 1) % 100 == 0)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }
                dbNode.Children.Add(funcsNode);
            }

            // Synonyms
            var synonyms = await Task.Run(() => metadata.Synonyms.Where(s => s.Database == dbName).OrderBy(s => s.Schema).ThenBy(s => s.Name).ToList());
            if (synonyms.Count > 0)
            {
                var synonymsNode = new TreeViewItemViewModel { Name = "Synonyms", Icon = "🔄", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Synonym) };
                for (var i = 0; i < synonyms.Count; i++)
                {
                    var syn = synonyms[i];
                    synonymsNode.Children.Add(new TreeViewItemViewModel { Name = $"{syn.Schema}.{syn.Name}", Icon = "🔄", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Synonym), Tag = syn });
                    if ((i + 1) % 100 == 0)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }
                dbNode.Children.Add(synonymsNode);
            }

            // User Types
            var types = await Task.Run(() => metadata.UserTypes.Where(t => t.Database == dbName).OrderBy(t => t.Schema).ThenBy(t => t.Name).ToList());
            if (types.Count > 0)
            {
                var typesNode = new TreeViewItemViewModel { Name = "User-Defined Types", Icon = "🔤", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.UserType) };
                for (var i = 0; i < types.Count; i++)
                {
                    var type = types[i];
                    typesNode.Children.Add(new TreeViewItemViewModel { Name = $"{type.Schema}.{type.Name}", Icon = "🔤", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.UserType), Tag = type });
                    if ((i + 1) % 100 == 0)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }
                dbNode.Children.Add(typesNode);
            }

            // Users
            var users = await Task.Run(() => metadata.Users.Where(u => u.Database == dbName).OrderBy(u => u.Name).ToList());
            if (users.Count > 0)
            {
                var usersNode = new TreeViewItemViewModel { Name = "Users", Icon = "👥", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.User) };
                for (var i = 0; i < users.Count; i++)
                {
                    var user = users[i];
                    usersNode.Children.Add(new TreeViewItemViewModel { Name = user.Name, Icon = "👤", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.User), Tag = user });
                    if ((i + 1) % 100 == 0)
                    {
                        await Dispatcher.Yield(DispatcherPriority.Background);
                    }
                }
                dbNode.Children.Add(usersNode);
            }
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Load DB Objects Error] {ex.Message}");
            dbNode.Children.Clear();
            dbNode.Children.Add(new TreeViewItemViewModel { Name = "Error loading: " + ex.Message, Icon = "❌", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Warning) });
        }
    }

    private async Task LoadLinkedServersAsync(TreeViewItemViewModel linkedServersNode, int connectionId)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        try
        {
            linkedServersNode.Children.Clear();
            linkedServersNode.Children.Add(CreateLoadingNode());

            var data = await TryGetConnectionDataAsync(connectionId);
            linkedServersNode.Children.Clear();
            if (data == null) return;
            var metadata = data.Value.metadata;

            var linkedServers = await Task.Run(() => metadata.LinkedServers.OrderBy(ls => ls.Name).ToList());
            if (linkedServers.Count == 0)
            {
                linkedServersNode.Children.Add(new TreeViewItemViewModel
                {
                    Name = "No linked servers found",
                    Icon = "ℹ️",
                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.StatusInformation)
                });
                return;
            }

            for (var i = 0; i < linkedServers.Count; i++)
            {
                var ls = linkedServers[i];
                linkedServersNode.Children.Add(new TreeViewItemViewModel 
                { 
                    Name = $"{ls.Name} ({ls.DataSource})", 
                    Icon = "🔗", 
                    IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.LinkedServer),
                    Tag = ls 
                });
                if ((i + 1) % 100 == 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                }
            }
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Load Linked Servers Error] {ex.Message}");
            linkedServersNode.Children.Clear();
            linkedServersNode.Children.Add(new TreeViewItemViewModel { Name = "Error loading: " + ex.Message, Icon = "❌", IconSource = SchemaExplorerIconProvider.GetIcon(SchemaExplorerIcon.Warning) });
        }
    }

    private async Task<(ConnectionInfo conn, DatabaseMetadata metadata)?> TryGetConnectionDataAsync(int connectionId)
    {
        if (_connectionData.TryGetValue(connectionId, out var data))
        {
            return data;
        }

        try
        {
            var loaded = await Task.Run(() =>
            {
                var conn = MssqlIntelliSenseCacheReader.GetConnections().FirstOrDefault(c => c.Id == connectionId);
                if (conn == null)
                {
                    return ((ConnectionInfo conn, DatabaseMetadata metadata)?)null;
                }

                var details = MssqlIntelliSenseCacheReader.GetSchemaDetails(connectionId);
                return (conn, details.Metadata);
            });

            if (loaded != null)
            {
                _connectionData[connectionId] = loaded.Value;
            }

            return loaded;
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Schema Explorer Lazy Load Error] {ex.Message}");
            return null;
        }
    }

    private void ConnectionsTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItemViewModel selectedNode)
        {
            if (selectedNode.Tag is ConnectionInfo conn)
            {
                _selectedConnection = conn;
                ShowConnectionDetails(conn);
            }
            else
            {
                // If we already have detail panel open, load details for selected node
                if (DetailContentPanel.Visibility == Visibility.Visible)
                {
                    LoadDetailForNode(selectedNode);
                }
            }
        }
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectionsTree.Focus();
        ShowSettingsPanel();
    }

    private void AboutNavButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectionsTree.Focus();
        ShowAboutPanel();
    }

    private void LoadDetailForNode(TreeViewItemViewModel node)
    {
        try
        {
            DetailStack.Children.Clear();

            var description = GetNodeDescription(node);
            var keywords = GetNodeKeywords(node);
            AddFormField("Mô tả", description, "Mô tả mục đích sử dụng trong hệ thống.", "description,purpose,about");
            AddFormField("Từ khóa", keywords, "Từ khóa tìm kiếm cho AI agent.", "keywords,search,tags");

            if (node.Tag is ConnectionInfo conn)
            {
                AddConnectionForm(conn);
            }
            else if (node.Tag is TableMetadata table)
            {
                AddFormField("Database", table.Database, "Database chứa bảng.", "database,catalog,table");
                AddFormField("Schema", table.Schema, "Schema sở hữu bảng.", "schema,owner,namespace");
                AddFormField("Table Name", table.Name, "Tên bảng SQL Server.", "table,object,name");
                AddFormField("Primary Keys", table.PrimaryKeyColumns.Count > 0 ? string.Join(", ", table.PrimaryKeyColumns) : "None", "Danh sách cột khóa chính đã cache.", "primary-key,pk,index");
                AddFormField("Column Count", table.Columns.Count.ToString(), "Số cột trong bảng.", "columns,count,metadata");
                AddColumnsForm(table.Columns);
            }
            else if (node.Tag is ViewMetadata view)
            {
                AddFormField("Database", view.Database, "Database chứa view.", "database,catalog,view");
                AddFormField("Schema", view.Schema, "Schema sở hữu view.", "schema,owner,namespace");
                AddFormField("View Name", view.Name, "Tên view SQL Server.", "view,object,name");
                AddFormField("Indexed", view.IsIndexed ? "Yes" : "No", "Cho biết view có indexed view/materialized index hay không.", "indexed-view,index,performance");
                AddFormField("Column Count", view.Columns.Count.ToString(), "Số cột output của view.", "columns,count,metadata");
                AddColumnsForm(view.Columns);
            }
            else if (node.Tag is ProcedureMetadata proc)
            {
                AddFormField("Database", proc.Database, "Database chứa stored procedure.", "database,procedure,catalog");
                AddFormField("Schema", proc.Schema, "Schema sở hữu procedure.", "schema,owner,namespace");
                AddFormField("Procedure Name", proc.Name, "Tên stored procedure.", "stored-procedure,proc,name");
                AddFormField("Object Type", proc.ObjectType, "Mã object type từ SQL Server metadata.", "object-type,procedure,metadata");
                AddParametersForm(proc.Parameters);
            }
            else if (node.Tag is FunctionMetadata func)
            {
                AddFormField("Database", func.Database, "Database chứa function.", "database,function,catalog");
                AddFormField("Schema", func.Schema, "Schema sở hữu function.", "schema,owner,namespace");
                AddFormField("Function Name", func.Name, "Tên scalar/table-valued function.", "function,name,tvf,scalar");
                AddFormField("Function Type", func.FunctionType, "Mã loại function từ SQL Server metadata.", "function-type,fn,tf,if");
                AddFormField("Return Type", string.IsNullOrWhiteSpace(func.ReturnType) ? "Unknown" : func.ReturnType, "Kiểu dữ liệu trả về nếu metadata có sẵn.", "return-type,function,data-type");
                AddParametersForm(func.Parameters);
            }
            else if (node.Tag is ColumnMetadata col)
            {
                AddFormField("Column Name", col.Name, "Tên cột trong table/view.", "column,name,field");
                AddFormField("Data Type", col.DataType, "Kiểu dữ liệu SQL Server.", "data-type,column,type");
                AddFormField("Nullable", col.IsNullable ? "Yes" : "No", "Cho biết cột có cho phép NULL hay không.", "nullable,null,required");
                AddFormField("Ordinal", col.Ordinal.ToString(), "Thứ tự cột trong object.", "ordinal,column-order,metadata");
            }
            else if (node.Tag is UserMetadata user)
            {
                AddFormField("Database", user.Database, "Database chứa principal.", "database,user,security");
                AddFormField("User Name", user.Name, "Tên database user/principal.", "user,principal,security");
                AddFormField("Type", user.Type, "Loại database principal.", "user-type,principal,security");
                AddFormField("Default Schema", user.DefaultSchema, "Schema mặc định khi user tạo object hoặc resolve tên không schema.", "default-schema,user,schema");
                AddFormField("Create Date", string.IsNullOrWhiteSpace(user.CreateDate) ? "Unknown" : user.CreateDate, "Ngày tạo user trong metadata.", "create-date,user,metadata");
            }
            else if (node.Tag is LinkedServerInfo ls)
            {
                AddFormField("Linked Server", ls.Name, "Alias linked server trong SQL Server.", "linked-server,alias,server-object");
                AddFormField("Data Source", ls.DataSource, "Data source thực dùng để tạo connection scan độc lập.", "data-source,server,connection");
                AddFormField("Connection Scope", "Server-level", "Connection linked server không lưu Initial Catalog; danh sách database được lấy từ server.", "server-level,no-catalog,database-list");
            }
            else if (node.Tag is EndpointInfo endpoint)
            {
                AddFormField("Endpoint", endpoint.Name, "Tên endpoint SQL Server thuộc Server Objects.", "endpoint,server-object,name");
                AddFormField("Type", endpoint.Type, "Loại endpoint từ sys.endpoints.", "endpoint,type,metadata");
                AddFormField("Protocol", endpoint.Protocol, "Protocol endpoint đang dùng.", "endpoint,protocol,tcp");
                AddFormField("State", endpoint.State, "Trạng thái endpoint.", "endpoint,state,status");
                AddFormField("Port", endpoint.Port > 0 ? endpoint.Port.ToString() : "None", "TCP port nếu endpoint có cấu hình port.", "endpoint,port,tcp");
            }
            else
            {
                AddFormField("Node", node.Name, "Nhóm hoặc container trong schema explorer.", "tree,node,container");
            }
        }
        catch (Exception ex)
        {
            DetailStack.Children.Clear();
            AddFormField("Message", ex.Message, "Thông báo lỗi từ UI schema explorer.", "exception,error,message");
        }
    }

    private void AddFormHeader(string title, string description, string keywords)
    {
        // Detail headers are intentionally suppressed; the selected node is already visible in the tree.
    }

    private void AddFormField(string label, string value, string description, string keywords)
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = GetThemeBrush(EnvironmentColors.ToolWindowBorderBrushKey, Color.FromRgb(63, 63, 70)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 10, 0, 10),
            Margin = new Thickness(0)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelPanel = new StackPanel();
        labelPanel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = GetThemeBrush(EnvironmentColors.PanelTextBrushKey, Color.FromRgb(178, 178, 178)),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 2, 16, 0),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(labelPanel, 0);
        grid.Children.Add(labelPanel);

        var valuePanel = new StackPanel();
        valuePanel.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(value) ? "None" : value,
            Foreground = GetThemeBrush(EnvironmentColors.ToolWindowTextBrushKey, Color.FromRgb(241, 241, 241)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        valuePanel.Children.Add(new TextBlock
        {
            Text = description,
            Foreground = GetThemeBrush(EnvironmentColors.PanelTextBrushKey, Color.FromRgb(158, 158, 158)),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        valuePanel.Children.Add(new TextBlock
        {
            Text = keywords,
            Foreground = GetThemeBrush(EnvironmentColors.SystemHighlightBrushKey, Color.FromRgb(104, 151, 187)),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(valuePanel, 1);
        grid.Children.Add(valuePanel);

        border.Child = grid;
        DetailStack.Children.Add(border);
    }

    private Brush GetThemeBrush(object key, Color fallbackColor)
    {
        return TryFindResource(key) as Brush ?? new SolidColorBrush(fallbackColor);
    }

    private void AddConnectionForm(ConnectionInfo conn)
    {
        AddFormField("Connection", conn.Name, "Tên connection cache đang chọn.", "connection server cache name");
        AddFormField("Connection String", conn.ConnectionString, "Chuỗi kết nối server-level dùng để scan metadata.", "connection-string server auth metadata");
        AddFormField("Status", conn.IsActive ? "Active" : "Inactive", "Trạng thái ghi nhận gần nhất trong cache.", "status active last-seen");
        AddFormField("Last Seen", FormatDate(conn.LastSeenAt), "Thời điểm extension thấy connection này gần nhất.", "last-seen timestamp connection");
        AddFormField("Schema Updated", FormatDate(conn.SchemaUpdatedAt), "Thời điểm schema cache được refresh gần nhất.", "schema refresh cache timestamp");
    }

    private void AddColumnsForm(IReadOnlyList<ColumnMetadata> columns)
    {
        if (columns.Count == 0)
        {
            AddFormField("Columns", "None", "Không có column metadata trong cache.", "columns,empty,metadata");
            return;
        }

        foreach (var column in columns.OrderBy(c => c.Ordinal))
        {
            AddFormField(
                $"Column {column.Ordinal}",
                $"{column.Name} : {column.DataType} ({(column.IsNullable ? "NULL" : "NOT NULL")})",
                "Column metadata dùng cho autocomplete, AI context và schema search.",
                $"column,{column.Name},{column.DataType},nullable-{column.IsNullable}");
        }
    }

    private void AddParametersForm(IReadOnlyList<FunctionParameterMetadata> parameters)
    {
        if (parameters.Count == 0)
        {
            AddFormField("Parameters", "None", "Object này không có parameter metadata trong cache.", "parameters,empty,metadata");
            return;
        }

        foreach (var parameter in parameters.OrderBy(p => p.Ordinal))
        {
            AddFormField(
                $"Parameter {parameter.Ordinal}",
                $"{parameter.Name} : {parameter.DataType} ({(parameter.IsOutput ? "OUTPUT" : "INPUT")})",
                "Parameter metadata dùng cho completion và AI context.",
                $"parameter,{parameter.Name},{parameter.DataType},output-{parameter.IsOutput}");
        }
    }

    private static string FormatDate(DateTimeOffset? value) =>
        value.HasValue ? value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "Unknown";

    private static string GetNodeDescription(TreeViewItemViewModel node) => node.Tag switch
    {
        ConnectionInfo => "Connection cache entry chứa thông tin server, thời điểm scan và metadata đã lưu.",
        TableMetadata table => table.Description,
        ViewMetadata view => view.Description,
        ProcedureMetadata proc => proc.Description,
        FunctionMetadata func => func.Description,
        ColumnMetadata => "Column metadata mô tả tên, kiểu dữ liệu, nullability và ordinal.",
        UserMetadata => "Database principal metadata dùng cho security/schema context.",
        LinkedServerInfo => "Linked server metadata dùng để tạo scan connection server-level độc lập.",
        EndpointInfo => "SQL Server endpoint metadata thuộc Server Objects.",
        _ => "Schema explorer node hoặc nhóm object trong cache."
    };

    private static string GetNodeKeywords(TreeViewItemViewModel node) => node.Tag switch
    {
        ConnectionInfo => "connection,server,cache,schema,metadata",
        TableMetadata table => table.Keywords,
        ViewMetadata view => view.Keywords,
        ProcedureMetadata proc => proc.Keywords,
        FunctionMetadata func => func.Keywords,
        ColumnMetadata => "column,data-type,nullable,ordinal",
        UserMetadata => "user,principal,security,default-schema",
        LinkedServerInfo => "linked-server,data-source,server-level,scan",
        EndpointInfo => "endpoint,server-object,protocol,port,state",
        _ => "tree,node,container,schema-explorer"
    };

    private void ClearTreeSelection(System.Collections.Generic.IEnumerable<TreeViewItemViewModel> nodes)
    {
        if (nodes == null) return;
        foreach (var node in nodes)
        {
            if (node.IsSelected)
            {
                node.IsSelected = false;
            }
            if (node.Children != null)
            {
                ClearTreeSelection(node.Children);
            }
        }
    }

    private void ShowSettingsPanel()
    {
        ClearTreeSelection(RootNodes);
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        DetailContentPanel.Visibility = Visibility.Collapsed;
        ScanProgressPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;
        LoadSettingsForm();
    }

    private void ShowAboutPanel()
    {
        ClearTreeSelection(RootNodes);
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        DetailContentPanel.Visibility = Visibility.Collapsed;
        ScanProgressPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Visible;
        LoadAboutDetails();
    }

    private void ShowConnectionDetails(ConnectionInfo conn)
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
        ScanProgressPanel.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        DetailContentPanel.Visibility = Visibility.Visible;
        LoadConnectionDetails(conn);
    }

    private void LoadConnectionDetails(ConnectionInfo conn)
    {
        try
        {
            DetailStack.Children.Clear();
            AddConnectionForm(conn);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Lỗi khi tải chi tiết schema: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshSchemaButton_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_selectedConnection == null) return;

        StartSchemaScan(_selectedConnection, MetadataScanScope.All, null, "toàn bộ schema");
    }

    private void ScanSchemaMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ConnectionsTree.SelectedItem is not TreeViewItemViewModel node)
        {
            return;
        }

        var plan = GetScanPlan(node);
        if (plan == null)
        {
            return;
        }

        StartSchemaScan(plan.Value.Connection, plan.Value.Scope, plan.Value.DatabaseName, plan.Value.Label);
    }

    private (ConnectionInfo Connection, MetadataScanScope Scope, string? DatabaseName, string Label)? GetScanPlan(TreeViewItemViewModel node)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (node.Tag is ConnectionInfo conn)
        {
            return (conn, MetadataScanScope.All, null, "toàn bộ connection");
        }

        if (node.Tag is SchemaExplorerNodeContext context)
        {
            var connInfo = MssqlIntelliSenseCacheReader.GetConnections().FirstOrDefault(c => c.Id == context.ConnectionId);
            if (connInfo == null)
            {
                return null;
            }

            return context.Group switch
            {
                "databases" => (connInfo, MetadataScanScope.DatabaseObjects, null, "nhóm Databases"),
                "database" => (connInfo, MetadataScanScope.DatabaseObjects, context.DatabaseName, $"database {context.DatabaseName}"),
                "server_objects" => (connInfo, MetadataScanScope.ServerObjects, null, "nhóm Server Objects"),
                "endpoints" => (connInfo, MetadataScanScope.Endpoints, null, "Endpoints"),
                "linked_servers" => (connInfo, MetadataScanScope.LinkedServers, null, "Linked Servers"),
                _ => (connInfo, MetadataScanScope.All, null, "toàn bộ schema")
            };
        }

        return null;
    }

    private void StartSchemaScan(ConnectionInfo connection, MetadataScanScope scope, string? databaseName, string label)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var connStr = connection.ConnectionString;
        _selectedConnection = connection;

        DetailContentPanel.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        ScanProgressPanel.Visibility = Visibility.Visible;

        ScanProgressTitleText.Text = $"🔍 ĐANG SCAN {label.ToUpperInvariant()}";
        ScanLogTextBox.Clear();
        ScanProgressStatusText.Text = "Đang khởi tạo tiến trình quét...";

        IProgress<string> progress = new System.Progress<string>(message =>
        {
            ScanLogTextBox.AppendText(message + Environment.NewLine);
            ScanLogTextBox.ScrollToEnd();
            ScanProgressStatusText.Text = message;
        });

        MssqlIntelliSensePackage.Instance?.ForceRefreshSchemaForConnectionString(connStr, progress, () =>
        {
            Dispatcher.Invoke(() =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();

                ScanProgressPanel.Visibility = Visibility.Collapsed;
                DetailContentPanel.Visibility = Visibility.Visible;

                _connectionData.Clear();
                RefreshConnectionsTree();
            });
        }, scope, databaseName);
    }

    private void CopyConnStrButton_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_selectedConnection == null) return;
        try
        {
            Clipboard.SetText(_selectedConnection.ConnectionString);
            MessageBox.Show("Đã sao chép chuỗi kết nối (Connection String) vào Clipboard.", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể sao chép: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            if (ConnectionsTree.SelectedItem is TreeViewItemViewModel node)
            {
                Clipboard.SetText(node.Name);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể sao chép: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteConnectionMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (ConnectionsTree.SelectedItem is not TreeViewItemViewModel { Tag: ConnectionInfo conn }) return;

        var result = MessageBox.Show(
            $"Xóa connection cache '{conn.Name}'?\n\nHành động này xóa schema cache cục bộ liên quan, không xóa dữ liệu trên SQL Server.",
            "Xóa connection cache",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            MssqlIntelliSenseCacheWriter.DeleteConnection(conn.Id);
            _selectedConnection = null;
            RefreshConnectionsTree();
            DetailContentPanel.Visibility = Visibility.Collapsed;
            ScanProgressPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Không thể xóa connection cache: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshMenuItem_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        RefreshConnectionsTree();
    }

    private void ConnectionsTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var isConnection = ConnectionsTree.SelectedItem is TreeViewItemViewModel { Tag: ConnectionInfo };
        var isScannable = ConnectionsTree.SelectedItem is TreeViewItemViewModel selectedNode && GetScanPlan(selectedNode) != null;
        ScanSchemaMenuItem.Visibility = isScannable ? Visibility.Visible : Visibility.Collapsed;
        if (isScannable && ConnectionsTree.SelectedItem is TreeViewItemViewModel scanNode)
        {
            var plan = GetScanPlan(scanNode);
            ScanSchemaMenuItem.Header = plan.HasValue ? $"Scan schema: {plan.Value.Label}" : "Scan schema";
        }

        DeleteConnectionMenuItem.Visibility = isConnection ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TreeViewItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (GetVisualParent<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            item.Focus();
            item.IsSelected = true;
            e.Handled = true;
        }
    }

    private static T? GetVisualParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T target)
            {
                return target;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    // ─── Snippet Manager ─────────────────────────────────────────────────
    private void LoadSnippetsList()
    {
        try
        {
            var defaultSnippets = SnippetDefaults.GetDefaultSnippets();
            var allSnippets = new List<Snippet>(defaultSnippets);

            var dir = MssqlIntelliSense.Core.MssqlIntelliSenseConfig.GetAppDataFolder();
            var snippetDir = System.IO.Path.Combine(dir, "snippets");
            if (System.IO.Directory.Exists(snippetDir))
            {
                allSnippets.AddRange(SnippetLoader.LoadFromDirectory(snippetDir));
            }
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Snippet Load Error] {ex.Message}");
        }
    }

    // ─── Settings ────────────────────────────────────────────────────────
    private bool _isPasswordVisible;

    private void TogglePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        _isPasswordVisible = !_isPasswordVisible;
        if (_isPasswordVisible)
        {
            SettingsApiKeyTextBox.Text = SettingsApiKeyPasswordBox.Password;
            SettingsApiKeyTextBox.Visibility = Visibility.Visible;
            SettingsApiKeyPasswordBox.Visibility = Visibility.Collapsed;
            TogglePasswordButton.Content = "Ẩn";
        }
        else
        {
            SettingsApiKeyPasswordBox.Password = SettingsApiKeyTextBox.Text;
            SettingsApiKeyTextBox.Visibility = Visibility.Collapsed;
            SettingsApiKeyPasswordBox.Visibility = Visibility.Visible;
            TogglePasswordButton.Content = "Hiện";
        }
    }

    private void LoadSettingsForm()
    {
        var options = MssqlIntelliSensePackage.GetOptions();
        if (options != null)
        {
            SettingsEndpointTextBox.Text = options.Endpoint;
            SettingsModelTextBox.Text = options.Model;
            SettingsApiKeyPasswordBox.Password = options.ApiKey;
            SettingsApiKeyTextBox.Text = options.ApiKey;
        }
        SettingsSnippetDirTextBox.Text = SqlCompletionSource.SnippetDirectory ?? "";
        RefreshMruStats();
    }

    private void RefreshMruStats()
    {
        try
        {
            var recorder = CandidateUsageRecorder.Instance;
            SettingsMruStatsText.Text =
                $"MRU tracking đang hoạt động với CandidateUsageRecorder.Instance.\n" +
                $"Snippet directory: {(string.IsNullOrEmpty(SqlCompletionSource.SnippetDirectory) ? "mặc định (embedded)" : SqlCompletionSource.SnippetDirectory)}\n" +
                $"UsageRecorder: {(SqlCompletionSource.SharedProvider.UsageRecorder != null ? "đã kích hoạt" : "không kích hoạt")}";
        }
        catch (Exception ex)
        {
            SettingsMruStatsText.Text = $"Lỗi tải MRU stats: {ex.Message}";
        }
    }

    private void SettingsBrowseSnippets_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        dialog.Description = "Chọn thư mục chứa snippet .json";
        dialog.SelectedPath = SettingsSnippetDirTextBox.Text;
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SettingsSnippetDirTextBox.Text = dialog.SelectedPath;
            SqlCompletionSource.SnippetDirectory = dialog.SelectedPath;
        }
    }

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        var options = MssqlIntelliSensePackage.GetOptions();
        if (options != null)
        {
            string apiKey = _isPasswordVisible ? SettingsApiKeyTextBox.Text : SettingsApiKeyPasswordBox.Password;
            string model = SettingsModelTextBox.Text;
            string endpoint = SettingsEndpointTextBox.Text;

            try
            {
                options.SaveSettings(apiKey, model, endpoint);
                SqlCompletionSource.SnippetDirectory = SettingsSnippetDirTextBox.Text?.Trim();
                MessageBox.Show("Cấu hình hệ thống đã được lưu thành công!", "Thông báo", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi khi lưu cấu hình: {ex.Message}", "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void SettingsReset_Click(object sender, RoutedEventArgs e)
    {
        SettingsEndpointTextBox.Text = "https://api.openai.com/v1/responses";
        SettingsModelTextBox.Text = "gpt-4o";
        SettingsApiKeyPasswordBox.Password = string.Empty;
        SettingsApiKeyTextBox.Text = string.Empty;
        SettingsSnippetDirTextBox.Text = "";
    }

    // ─── About ───────────────────────────────────────────────────────────
    private void LoadAboutDetails()
    {
        try
        {
            AboutVersionText.Text = $"Phiên bản: v{MssqlIntelliSensePackage.VersionString}";
            var dbFolder = MssqlIntelliSense.Core.MssqlIntelliSenseConfig.GetAppDataFolder();
            var dbPath = System.IO.Path.Combine(dbFolder, "MssqlIntelliSense.db");
            AboutDbPathText.Text = dbPath;

            if (System.IO.File.Exists(dbPath))
            {
                var fileInfo = new System.IO.FileInfo(dbPath);
                double kbSize = fileInfo.Length / 1024.0;
                AboutDbSizeText.Text = kbSize > 1024 ? $"{kbSize / 1024.0:F2} MB" : $"{kbSize:F2} KB";
            }
            else
            {
                AboutDbSizeText.Text = "Không tìm thấy file";
            }

            var connections = MssqlIntelliSenseCacheReader.GetConnections();
            AboutConnectionsCountText.Text = connections.Count.ToString();
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[About Load Error] {ex.Message}");
        }
    }
}

internal enum SchemaExplorerIcon
{
    Connection,
    Database,
    Table,
    Column,
    View,
    Procedure,
    Function,
    Synonym,
    UserType,
    User,
    LinkedServer,
    ServerObjects,
    Endpoint,
    Settings,
    Information,
    StatusInformation,
    Warning
}

internal static class SchemaExplorerIconProvider
{
    private static readonly Dictionary<SchemaExplorerIcon, ImageSource?> IconCache = new();

    internal static ImageSource? GetIcon(SchemaExplorerIcon icon)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (IconCache.TryGetValue(icon, out var cached))
        {
            return cached;
        }

        var image = GetImageSource(GetMoniker(icon));
        IconCache[icon] = image;
        return image;
    }

    private static ImageMoniker GetMoniker(SchemaExplorerIcon icon) => icon switch
    {
        SchemaExplorerIcon.Connection => KnownMonikers.Database,
        SchemaExplorerIcon.Database => KnownMonikers.Database,
        SchemaExplorerIcon.Table => KnownMonikers.Table,
        SchemaExplorerIcon.Column => KnownMonikers.Column,
        SchemaExplorerIcon.View => KnownMonikers.View,
        SchemaExplorerIcon.Procedure => KnownMonikers.Method,
        SchemaExplorerIcon.Function => KnownMonikers.Method,
        SchemaExplorerIcon.Synonym => KnownMonikers.Reference,
        SchemaExplorerIcon.UserType => KnownMonikers.Property,
        SchemaExplorerIcon.User => KnownMonikers.Class,
        SchemaExplorerIcon.LinkedServer => KnownMonikers.Reference,
        SchemaExplorerIcon.ServerObjects => KnownMonikers.Class,
        SchemaExplorerIcon.Endpoint => KnownMonikers.Interface,
        SchemaExplorerIcon.Settings => KnownMonikers.Property,
        SchemaExplorerIcon.Information => KnownMonikers.StatusInformation,
        SchemaExplorerIcon.StatusInformation => KnownMonikers.StatusInformation,
        SchemaExplorerIcon.Warning => KnownMonikers.StatusInformation,
        _ => KnownMonikers.StatusInformation
    };

    private static ImageSource? GetImageSource(ImageMoniker moniker)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (Package.GetGlobalService(typeof(SVsImageService)) is not IVsImageService2 imageService)
        {
            return null;
        }

        var attributes = new ImageAttributes
        {
            Flags = (uint)_ImageAttributesFlags.IAF_RequiredFlags,
            ImageType = (uint)_UIImageType.IT_Bitmap,
            Format = (uint)_UIDataFormat.DF_WPF,
            LogicalWidth = 16,
            LogicalHeight = 16,
            StructSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(ImageAttributes))
        };

        var image = imageService.GetImage(moniker, attributes);
        if (image == null)
        {
            return null;
        }

        image.get_Data(out var data);
        return data as ImageSource;
    }
}
