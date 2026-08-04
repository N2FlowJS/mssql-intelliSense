using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.SsmsHost;

[Guid(ObjectReviewToolWindowPane.WindowGuidString)]
public class ObjectReviewToolWindowPane : ToolWindowPane
{
    public const string WindowGuidString = "F4D50F91-C651-4C32-9487-F7D8D8280D11";

    private readonly ObjectReviewWindow _control;

    public ObjectReviewToolWindowPane() : base(null)
    {
        Caption = "Object Review";
        _control = new ObjectReviewWindow();
        Content = _control;
    }

    public void SetReviewContent(SqlCompletionItem item, DatabaseMetadata metadata)
    {
        _control.SetReviewContent(item, metadata);
    }

    public void FocusReview()
    {
        _control.FocusDescriptionEditor();
    }
}
