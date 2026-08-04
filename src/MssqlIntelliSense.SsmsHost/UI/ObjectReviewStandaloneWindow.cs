using System;
using System.Windows;
using System.Windows.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.SsmsHost;

internal sealed class ObjectReviewStandaloneWindow : Window
{
    private static ObjectReviewStandaloneWindow? _instance;
    private readonly ObjectReviewWindow _panel;

    private ObjectReviewStandaloneWindow()
    {
        Title = "MSSQL IntelliSense Object Review";
        Width = 820;
        Height = 700;
        MinWidth = 560;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = ObjectReviewWindowBrushes.GetBrush(EnvironmentColors.ToolWindowBackgroundBrushKey, System.Windows.Media.Color.FromRgb(31, 31, 31));
        Foreground = ObjectReviewWindowBrushes.GetBrush(EnvironmentColors.ToolWindowTextBrushKey, System.Windows.Media.Colors.White);
        _panel = new ObjectReviewWindow();
        Content = _panel;
        Closed += (_, _) => _instance = null;
    }

    public static void ShowForCompletion(SqlCompletionItem item, DatabaseMetadata metadata, Window? owner)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var window = _instance ??= new ObjectReviewStandaloneWindow();
        if (owner != null)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            SetSsmsOwner(window);
        }

        window._panel.SetReviewContent(item, metadata);
        window.Show();
        window.Activate();
        window._panel.FocusDescriptionEditor();
    }

    private static void SetSsmsOwner(Window window)
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (ServiceProvider.GlobalProvider.GetService(typeof(SVsUIShell)) is IVsUIShell shell &&
                ErrorHandler.Succeeded(shell.GetDialogOwnerHwnd(out var hwnd)) &&
                hwnd != IntPtr.Zero)
            {
                new WindowInteropHelper(window).Owner = hwnd;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
        }
        catch (Exception ex)
        {
            MssqlIntelliSensePackage.Log($"[Object Review Owner Error] {ex.Message}");
        }
    }
}

internal static class ObjectReviewWindowBrushes
{
    public static System.Windows.Media.Brush GetBrush(object key, System.Windows.Media.Color defaultColor)
    {
        if (Application.Current?.TryFindResource(key) is System.Windows.Media.Brush brush)
        {
            return brush;
        }

        return new System.Windows.Media.SolidColorBrush(defaultColor);
    }
}
