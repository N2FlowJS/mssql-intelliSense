using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.DebugApp;

public partial class App : Application
{
    static App()
    {
        AppDomain.CurrentDomain.AssemblyResolve += OnResolveAssembly;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            MssqlIntelliSenseCacheWriter.InitializeDatabase();
        }
        catch { }
    }

    private static Assembly? OnResolveAssembly(object? sender, ResolveEventArgs args)
    {
        try
        {
            var requestedName = new AssemblyName(args.Name).Name;
            if (string.IsNullOrEmpty(requestedName)) return null;

            var targetFileName = requestedName + ".dll";

            var searchFolders = new[]
            {
                @"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\PublicAssemblies",
                @"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\PrivateAssemblies",
                @"C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE",
                @"C:\Program Files\Microsoft SQL Server Management Studio 22\Common7\IDE\PublicAssemblies",
                @"C:\Program Files\Microsoft SQL Server Management Studio 22\Common7\IDE\PrivateAssemblies",
                @"C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\PublicAssemblies",
                @"C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\IDE\PrivateAssemblies",
                @"C:\Program Files (x86)\Microsoft SQL Server Management Studio 19\Common7\IDE\PublicAssemblies",
                @"C:\Program Files (x86)\Microsoft SQL Server Management Studio 19\Common7\IDE\PrivateAssemblies",
                @"C:\Program Files (x86)\Microsoft SQL Server Management Studio 18\Common7\IDE\PublicAssemblies",
                @"C:\Program Files (x86)\Microsoft SQL Server Management Studio 18\Common7\IDE\PrivateAssemblies",
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\PublicAssemblies",
                @"C:\Program Files\Microsoft Visual Studio\2022\Enterprise\Common7\IDE\PrivateAssemblies",
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\PublicAssemblies",
                @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\PrivateAssemblies",
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\PublicAssemblies",
                @"C:\Program Files\Microsoft Visual Studio\2022\Professional\Common7\IDE\PrivateAssemblies",
                AppDomain.CurrentDomain.BaseDirectory
            };

            foreach (var folder in searchFolders)
            {
                if (Directory.Exists(folder))
                {
                    var candidatePath = Path.Combine(folder, targetFileName);
                    if (File.Exists(candidatePath))
                    {
                        return Assembly.LoadFrom(candidatePath);
                    }
                }
            }
        }
        catch { }

        return null;
    }
}
