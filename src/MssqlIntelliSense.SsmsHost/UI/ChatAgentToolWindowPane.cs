using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.SsmsHost;

[Guid(ChatAgentToolWindowPane.WindowGuidString)]
public class ChatAgentToolWindowPane : ToolWindowPane
{
    public const string WindowGuidString = "A1B2C3D4-E5F6-4A5B-8C9D-1E2F3A4B5C6D";

    private readonly ChatAgentControl _control;

    public ChatAgentToolWindowPane() : base(null)
    {
        Caption = "AI SQL Chat Agent";
        _control = new ChatAgentControl();
        Content = _control;
    }

    public void SetSelectedConnection(ConnectionInfo? connection)
    {
        _control.SetSelectedConnection(connection);
    }
}
