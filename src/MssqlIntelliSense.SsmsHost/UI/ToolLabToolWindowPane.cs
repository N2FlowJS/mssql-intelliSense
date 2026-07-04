using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace MssqlIntelliSense.SsmsHost;

[Guid(ToolLabToolWindowPane.WindowGuidString)]
public class ToolLabToolWindowPane : ToolWindowPane
{
    public const string WindowGuidString = "B8D273C7-2E8C-4EC4-8B19-61C7054A9F42";

    public ToolLabToolWindowPane() : base(null)
    {
        Caption = "AI SQL Tool Lab";
        Content = new ToolLabControl();
    }
}
