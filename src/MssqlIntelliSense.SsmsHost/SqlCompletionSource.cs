using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Completion.Candidates;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.SsmsHost;

[Export(typeof(ICompletionSourceProvider))]
[ContentType("text")]
[Name("MSSQL IntelliSense Autocomplete Provider")]
internal sealed class SqlCompletionSourceProvider : ICompletionSourceProvider
{
    public ICompletionSource TryCreateCompletionSource(ITextBuffer textBuffer)
    {
        MssqlIntelliSensePackage.Log($"[Autocomplete] Source created for content type: {textBuffer.ContentType.TypeName}");
        return new SqlCompletionSource(textBuffer);
    }
}

[Export(typeof(IVsTextViewCreationListener))]
[ContentType("text")]
[TextViewRole(PredefinedTextViewRoles.Editable)]
[Name("MSSQL IntelliSense Autocomplete Command Filter")]
internal sealed class SqlCompletionCommandFilterProvider : IVsTextViewCreationListener
{
    [Import]
    internal IVsEditorAdaptersFactoryService AdapterService { get; set; } = null!;

    [Import]
    internal ICompletionBroker CompletionBroker { get; set; } = null!;

    public void VsTextViewCreated(IVsTextView textViewAdapter)
    {
        var textView = AdapterService.GetWpfTextView(textViewAdapter);
        if (textView == null) return;

        MssqlIntelliSensePackage.Log($"[Autocomplete] Text view created for content type: {textView.TextBuffer.ContentType.TypeName}");
        textView.Properties.GetOrCreateSingletonProperty(() => 
            new SqlCompletionCommandFilter(textViewAdapter, textView, CompletionBroker));
    }
}

internal sealed class SqlCompletionCommandFilter : IOleCommandTarget
{
    private static readonly string[] SpaceTriggerKeywords =
    [
        "FROM",
        "JOIN",
        "APPLY",
        "INTO",
        "UPDATE",
        "TABLE",
        "VIEW",
        "PROC",
        "PROCEDURE",
        "FUNCTION",
        "EXEC",
        "EXECUTE",
        "MERGE",
        "USING",
        "TRUNCATE",
        "DELETE",
        "INSERT",
        "WHERE",
        "HAVING",
        "AND",
        "OR",
        "ON",
        "SET",
        "BY",
        "OUTPUT"
    ];

    private readonly IVsTextView _textViewAdapter;
    private readonly IWpfTextView _textView;
    private readonly ICompletionBroker _completionBroker;
    private readonly IOleCommandTarget _nextCommandTarget;
    private readonly System.Windows.Threading.DispatcherTimer _longClickTimer;
    private System.Windows.Point _mouseDownPoint;
    private bool _isLongClickHandled;
    private ICompletionSession? _currentSession;
    private string? _lastReviewedCompletionKey;
    private DateTime _lastReviewedAtUtc;

    private static bool _isGlobalHandlerRegistered;
    private static readonly object GlobalHandlerLock = new();
    private static SqlCompletionCommandFilter? _activeFilter;

    public SqlCompletionCommandFilter(IVsTextView textViewAdapter, IWpfTextView textView, ICompletionBroker completionBroker)
    {
        _textViewAdapter = textViewAdapter;
        _textView = textView;
        _completionBroker = completionBroker;
        _activeFilter = this;

        // Add filter to the command chain
        textViewAdapter.AddCommandFilter(this, out _nextCommandTarget);

        _longClickTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _longClickTimer.Tick += OnLongClickTimerTick;

        RegisterGlobalMouseHandlers();

        MssqlIntelliSensePackage.Log($"[Autocomplete] Command filter attached for content type: {textView.TextBuffer.ContentType.TypeName}");
    }

    private static void RegisterGlobalMouseHandlers()
    {
        lock (GlobalHandlerLock)
        {
            if (_isGlobalHandlerRegistered) return;
            _isGlobalHandlerRegistered = true;

            try
            {
                System.Windows.EventManager.RegisterClassHandler(
                    typeof(System.Windows.UIElement),
                    System.Windows.UIElement.PreviewMouseLeftButtonDownEvent,
                    new System.Windows.Input.MouseButtonEventHandler(OnGlobalPreviewMouseLeftButtonDown),
                    true);

                System.Windows.EventManager.RegisterClassHandler(
                    typeof(System.Windows.UIElement),
                    System.Windows.UIElement.PreviewMouseLeftButtonUpEvent,
                    new System.Windows.Input.MouseButtonEventHandler(OnGlobalPreviewMouseLeftButtonUp),
                    true);

                System.Windows.EventManager.RegisterClassHandler(
                    typeof(System.Windows.UIElement),
                    System.Windows.UIElement.PreviewMouseMoveEvent,
                    new System.Windows.Input.MouseEventHandler(OnGlobalPreviewMouseMove),
                    true);

                MssqlIntelliSensePackage.Log("[Autocomplete] Registered global WPF mouse handlers for long-click ObjectReview.");
            }
            catch (Exception ex)
            {
                MssqlIntelliSensePackage.Log($"[Autocomplete] RegisterGlobalMouseHandlers error: {ex.Message}");
            }
        }
    }

    private static void OnGlobalPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_activeFilter != null)
        {
            _activeFilter._mouseDownPoint = e.GetPosition(null);
            _activeFilter._isLongClickHandled = false;
            _activeFilter._longClickTimer.Stop();
            _activeFilter._longClickTimer.Start();
        }
    }

    private static void OnGlobalPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_activeFilter != null && _activeFilter._longClickTimer.IsEnabled)
        {
            var currentPoint = e.GetPosition(null);
            if (Math.Abs(currentPoint.X - _activeFilter._mouseDownPoint.X) > 15 ||
                Math.Abs(currentPoint.Y - _activeFilter._mouseDownPoint.Y) > 15)
            {
                _activeFilter._longClickTimer.Stop();
            }
        }
    }

    private static void OnGlobalPreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_activeFilter != null)
        {
            _activeFilter._longClickTimer.Stop();
            if (_activeFilter._isLongClickHandled)
            {
                _activeFilter._isLongClickHandled = false;
                e.Handled = true;
            }
        }
    }

    private void OnLongClickTimerTick(object? sender, EventArgs e)
    {
        _longClickTimer.Stop();
        _isLongClickHandled = true;

        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                if (_currentSession != null && _currentSession.SelectedCompletionSet != null && _currentSession.SelectedCompletionSet.SelectionStatus.IsSelected)
                {
                    var selectedCompletion = _currentSession.SelectedCompletionSet.SelectionStatus.Completion;
                    if (selectedCompletion != null)
                    {
                        var match = GetCompletionItem(_currentSession, selectedCompletion);
                        _currentSession.Properties.TryGetProperty(
                            SqlCompletionSource.CompletionMetadataPropertyKey,
                            out DatabaseMetadata? metadata);
                        TryOpenObjectReview(match, metadata);
                        return;
                    }
                }

                var caretPos = _textView.Caret.Position.BufferPosition;
                var sql = _textView.TextSnapshot.GetText();
                SqlCompletionSource.TryOpenObjectReviewFromSql(sql, caretPos.Position);
            }
            catch (Exception ex)
            {
                MssqlIntelliSensePackage.Log($"[Autocomplete LongClick Error] {ex.Message}");
            }
        });
    }

    public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (pguidCmdGroup == VSConstants.VSStd2K)
        {
            for (var i = 0; i < cCmds; i++)
            {
                if (IsCompletionCommand(prgCmds[i].cmdID))
                {
                    prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                    return VSConstants.S_OK;
                }
            }
        }

        return _nextCommandTarget.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
    }

    public int Exec(ref Guid pguidCmdGroup, uint cmdID, uint cmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (pguidCmdGroup == VSConstants.VSStd2K)
        {
            switch ((VSConstants.VSStd2KCmdID)cmdID)
            {
                case VSConstants.VSStd2KCmdID.AUTOCOMPLETE:
                case VSConstants.VSStd2KCmdID.COMPLETEWORD:
                case VSConstants.VSStd2KCmdID.PARAMINFO:
                case VSConstants.VSStd2KCmdID.SHOWMEMBERLIST:
                    if (TriggerCompletion())
                    {
                        return VSConstants.S_OK;
                    }
                    break;

                case VSConstants.VSStd2KCmdID.RETURN:
                case VSConstants.VSStd2KCmdID.TAB:
                    if (_currentSession != null && !_currentSession.IsDismissed)
                    {
                        if (_currentSession.SelectedCompletionSet != null && 
                            _currentSession.SelectedCompletionSet.SelectionStatus.IsSelected)
                        {
                            var selectedCompletion = _currentSession.SelectedCompletionSet.SelectionStatus.Completion;
                            if (selectedCompletion != null)
                            {
                                if (selectedCompletion.DisplayText.StartsWith("✨ Ask AI:"))
                                {
                                    var promptText = selectedCompletion.Description;
                                    var sessionToDismiss = _currentSession;
                                    sessionToDismiss.Dismiss();

                                    var caretPos = _textView.Caret.Position.BufferPosition;
                                    var line = caretPos.GetContainingLine();
                                    var lineSpan = new SnapshotSpan(_textView.TextBuffer.CurrentSnapshot, line.Start, line.Length);
                                    
                                    _ = SqlCompletionSource.GenerateAndReplaceSqlAsync(_textView, lineSpan, promptText);
                                    return VSConstants.S_OK;
                                }

                                // Find matching SqlCompletionItem to retrieve CaretOffset
                                SqlCompletionItem? match = null;
                                DatabaseMetadata? completionMetadata = null;
                                if (_currentSession.Properties.TryGetProperty(
                                    SqlCompletionSource.CompletionItemsPropertyKey,
                                    out IReadOnlyList<SqlCompletionItem> items))
                                {
                                    match = items.FirstOrDefault(
                                        i => i.Label.Equals(selectedCompletion.DisplayText, StringComparison.Ordinal));
                                }
                                _currentSession.Properties.TryGetProperty(
                                    SqlCompletionSource.CompletionMetadataPropertyKey,
                                    out completionMetadata);

                                RecordCompletionUsage(selectedCompletion);

                                // Get the start position of the replaced span in the pre-commit snapshot
                                int startPos = _currentSession.SelectedCompletionSet.ApplicableTo.GetStartPoint(_textView.TextSnapshot).Position;

                                _currentSession.Commit();

                                if (match != null && match.SelectionStart >= 0 && match.SelectionEnd >= match.SelectionStart)
                                {
                                    var selectionStart = Math.Min(startPos + match.SelectionStart, _textView.TextSnapshot.Length);
                                    var selectionEnd = Math.Min(startPos + match.SelectionEnd, _textView.TextSnapshot.Length);
                                    var selectionSpan = new SnapshotSpan(
                                        _textView.TextSnapshot,
                                        Span.FromBounds(selectionStart, selectionEnd));
                                    _textView.Selection.Select(selectionSpan, isReversed: false);
                                    _textView.Caret.MoveTo(selectionSpan.End);
                                }
                                else if (match != null && match.CaretOffset >= 0)
                                {
                                    // Move caret to startPos + match.CaretOffset in the post-commit snapshot
                                    var newPosition = new SnapshotPoint(_textView.TextSnapshot, startPos + match.CaretOffset);
                                    _textView.Caret.MoveTo(newPosition);
                                }

                                return VSConstants.S_OK;
                            }
                        }
                    }
                    break;

                case VSConstants.VSStd2KCmdID.TYPECHAR:
                    var typedValue = System.Runtime.InteropServices.Marshal.GetObjectForNativeVariant(pvaIn);
                    var typedChar = typedValue switch
                    {
                        ushort value => (char)value,
                        short value => (char)value,
                        int value => (char)value,
                        char value => value,
                        string { Length: > 0 } value => value[0],
                        _ => (char?)null
                    };

                    if (typedChar == null)
                    {
                        MssqlIntelliSensePackage.Log($"[Autocomplete] Ignored TYPECHAR value type: {typedValue?.GetType().FullName ?? "<null>"}");
                        return _nextCommandTarget.Exec(ref pguidCmdGroup, cmdID, cmdexecopt, pvaIn, pvaOut);
                    }

                    char ch = typedChar.Value;
                    
                    int result = _nextCommandTarget.Exec(ref pguidCmdGroup, cmdID, cmdexecopt, pvaIn, pvaOut);

                    bool shouldTrigger = char.IsLetterOrDigit(ch) || ch == '_' || ch == '@' || ch == '#' || ch == '.';
                    
                    var caretPosForComment = _textView.Caret.Position.BufferPosition;
                    var currentLine = caretPosForComment.GetContainingLine();
                    string lineText = currentLine.GetText();
                    bool isComment = lineText.TrimStart().StartsWith("--");
                    
                    if (ch == ' ' && !isComment)
                    {
                        shouldTrigger = ShouldTriggerAfterSpace(lineText, caretPosForComment.Position - currentLine.Start.Position);
                    }

                    if (isComment && (ch == ' ' || char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
                    {
                        shouldTrigger = true;
                    }

                    if (shouldTrigger)
                    {
                        if (_currentSession == null || _currentSession.IsDismissed)
                        {
                            TriggerCompletion();
                        }
                        else
                        {
                            _currentSession.Filter();
                        }
                    }
                    return result;

                case VSConstants.VSStd2KCmdID.BACKSPACE:
                case VSConstants.VSStd2KCmdID.DELETE:
                    int deleteResult = _nextCommandTarget.Exec(ref pguidCmdGroup, cmdID, cmdexecopt, pvaIn, pvaOut);
                    if (_currentSession != null && !_currentSession.IsDismissed)
                    {
                        _currentSession.Filter();
                    }
                    return deleteResult;
            }
        }

        return _nextCommandTarget.Exec(ref pguidCmdGroup, cmdID, cmdexecopt, pvaIn, pvaOut);
    }

    private static bool IsCompletionCommand(uint cmdID)
    {
        var command = (VSConstants.VSStd2KCmdID)cmdID;
        return command == VSConstants.VSStd2KCmdID.AUTOCOMPLETE ||
               command == VSConstants.VSStd2KCmdID.COMPLETEWORD ||
               command == VSConstants.VSStd2KCmdID.PARAMINFO ||
               command == VSConstants.VSStd2KCmdID.SHOWMEMBERLIST;
    }

    private bool TriggerCompletion()
    {
        if (_completionBroker.IsCompletionActive(_textView))
        {
            _currentSession = _completionBroker.GetSessions(_textView).FirstOrDefault();
            MssqlIntelliSensePackage.Log($"[Autocomplete] Reusing active completion session: {_currentSession != null}");
            return _currentSession != null;
        }

        _currentSession = _completionBroker.TriggerCompletion(_textView);
        if (_currentSession != null)
        {
            _currentSession.Committed += OnSessionCommitted;
            _currentSession.Dismissed += OnSessionDismissed;
            MssqlIntelliSensePackage.Log("[Autocomplete] Triggered completion session.");
            return true;
        }
        MssqlIntelliSensePackage.Log("[Autocomplete] Completion broker returned null session.");
        return false;
    }

    private void OnSessionCommitted(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (sender is not ICompletionSession session ||
            session.SelectedCompletionSet?.SelectionStatus.Completion is not { } selected)
        {
            return;
        }

        RecordCompletionUsage(selected);
    }

    private void OnSessionDismissed(object sender, EventArgs e)
    {
        if (_currentSession != null)
        {
            _currentSession.Committed -= OnSessionCommitted;
            _currentSession.Dismissed -= OnSessionDismissed;
            _currentSession = null;
        }
    }

    private static SqlCompletionItem? GetCompletionItem(ICompletionSession session, Completion selected)
    {
        if (session.Properties.TryGetProperty(
            SqlCompletionSource.CompletionItemsPropertyKey,
            out IReadOnlyList<SqlCompletionItem> items))
        {
            return items.FirstOrDefault(
                i => i.Label.Equals(selected.DisplayText, StringComparison.Ordinal));
        }

        return null;
    }

    private void TryOpenObjectReview(SqlCompletionItem? match, DatabaseMetadata? metadata)
    {
        if (match == null)
        {
            MssqlIntelliSensePackage.Log("[Autocomplete] Object review skipped: no matching completion item.");
            return;
        }

        if (metadata == null || metadata == DatabaseMetadata.Empty)
        {
            MssqlIntelliSensePackage.Log($"[Autocomplete] Object review skipped for {match.Label}: no metadata.");
            return;
        }

        if (!CanReviewCompletion(match.Kind))
        {
            MssqlIntelliSensePackage.Log($"[Autocomplete] Object review skipped for {match.Label}: kind {match.Kind} not reviewable.");
            return;
        }

        var key = $"{match.Kind}:{match.Label}:{match.InsertText}";
        var now = DateTime.UtcNow;
        if (key.Equals(_lastReviewedCompletionKey, StringComparison.Ordinal) &&
            now - _lastReviewedAtUtc < TimeSpan.FromSeconds(1))
        {
            return;
        }

        _lastReviewedCompletionKey = key;
        _lastReviewedAtUtc = now;
        MssqlIntelliSensePackage.Log($"[Autocomplete] Opening object review for {match.Kind}: {match.Label}");
        ObjectReviewWindow.ShowForCompletion(match, metadata);
    }

    private void RecordCompletionUsage(Completion? selected)
    {
        if (selected == null || _currentSession == null) return;

        if (_currentSession.Properties.TryGetProperty(
            SqlCompletionSource.CompletionItemsPropertyKey,
            out IReadOnlyList<SqlCompletionItem> items))
        {
            var match = items.FirstOrDefault(
                i => i.Label.Equals(selected.DisplayText, StringComparison.Ordinal) &&
                     i.Kind != SqlCompletionKind.Keyword);
            if (match != null)
            {
                SqlCompletionSource.SharedProvider.RecordUsage(match);
            }
        }
    }

    private static bool CanReviewCompletion(SqlCompletionKind kind) =>
        kind is SqlCompletionKind.Table or
            SqlCompletionKind.View or
            SqlCompletionKind.Procedure or
            SqlCompletionKind.Function or
            SqlCompletionKind.UserType or
            SqlCompletionKind.Synonym;

    private static bool ShouldTriggerAfterSpace(string lineText, int caretColumn)
    {
        if (caretColumn <= 0 || caretColumn > lineText.Length) return false;

        var beforeCaret = lineText.Substring(0, caretColumn).TrimEnd();
        if (beforeCaret.Length == 0) return false;

        var lastTokenStart = beforeCaret.Length - 1;
        while (lastTokenStart >= 0)
        {
            var ch = beforeCaret[lastTokenStart];
            if (!char.IsLetter(ch) && ch != '_') break;
            lastTokenStart--;
        }

        var lastToken = beforeCaret.Substring(lastTokenStart + 1);
        return SpaceTriggerKeywords.Any(keyword =>
            keyword.Equals(lastToken, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class SqlCompletionSource : ICompletionSource
{
    private sealed class MetadataCacheEntry
    {
        public MetadataCacheEntry(DatabaseMetadata metadata, DateTime loadedAt)
        {
            Metadata = metadata;
            LoadedAt = loadedAt;
        }

        public DatabaseMetadata Metadata { get; }

        public DateTime LoadedAt { get; }
    }

    private static readonly TimeSpan MetadataRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly ITextBuffer _textBuffer;
    private bool _isDisposed;

    internal const string CompletionItemsPropertyKey = "MssqlIntelliSenseCompletionItems";
    internal const string CompletionMetadataPropertyKey = "MssqlIntelliSenseCompletionMetadata";

    internal static string? SnippetDirectory
    {
        get => SharedProvider.SnippetDirectory;
        set => SharedProvider.SnippetDirectory = value;
    }
    internal static readonly SqlCompletionProvider SharedProvider = new()
    {
        UsageRecorder = CandidateUsageRecorder.Instance,
    };

    private static readonly Dictionary<string, MetadataCacheEntry> MetadataCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LoadingConnections = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();
    /// <summary>Tracks the last cache key so we can evict when the user switches server.</summary>
    private static string? _lastCacheKey;

    public SqlCompletionSource(ITextBuffer textBuffer)
    {
        _textBuffer = textBuffer;
    }

    private static DatabaseMetadata? GetDatabaseMetadata(string? sql = null, int caretPosition = -1)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var connectionString = MssqlIntelliSensePackage.GetActiveConnectionString();
        var useDatabaseOverride = TryGetUseDatabaseBeforeCaret(sql, caretPosition);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var cachedConnection = MssqlIntelliSenseCacheReader.GetConnections()
                .OrderByDescending(c => c.IsActive)
                .ThenByDescending(c => c.SchemaUpdatedAt ?? c.LastSeenAt ?? DateTimeOffset.MinValue)
                .FirstOrDefault();
            connectionString = cachedConnection?.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
                return DatabaseMetadata.Empty;
        }

        // Parse active database from the connection string (InitialCatalog).
        // We cache full metadata per server/auth (cacheKey strips InitialCatalog)
        // so that switching databases doesn't force a JSON cache re-read.
        string activeDatabase = string.Empty;
        string cacheKey = connectionString!;
        try
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
            activeDatabase = builder.InitialCatalog ?? string.Empty;
            if (string.IsNullOrWhiteSpace(activeDatabase) || activeDatabase.Equals("master", StringComparison.OrdinalIgnoreCase))
            {
                activeDatabase = MssqlIntelliSensePackage.GetActiveDatabaseName() ?? activeDatabase;
            }
            if (!string.IsNullOrWhiteSpace(useDatabaseOverride))
            {
                activeDatabase = useDatabaseOverride!;
            }
            // Normalize cache key: server + auth only (no InitialCatalog)
            builder.Remove("Initial Catalog");
            cacheKey = builder.ConnectionString;
        }
        catch { /* keep connectionString as fallback */ }

        lock (CacheLock)
        {
            // When user switches server, evict the old entry immediately
            if (_lastCacheKey != null && !_lastCacheKey.Equals(cacheKey, StringComparison.OrdinalIgnoreCase))
            {
                MetadataCache.Remove(_lastCacheKey);
            }
            _lastCacheKey = cacheKey;

            if (MetadataCache.TryGetValue(cacheKey, out var entry))
            {
                if (DateTime.UtcNow - entry.LoadedAt > MetadataRefreshInterval)
                {
                    QueueMetadataRefresh(cacheKey, connectionString!);
                }

                return FilterByActiveDatabase(entry.Metadata, activeDatabase);
            }

            QueueMetadataRefresh(cacheKey, connectionString!);
            return DatabaseMetadata.Empty;
        }
    }

    private static DatabaseMetadata FilterByActiveDatabase(DatabaseMetadata metadata, string activeDatabase) =>
        string.IsNullOrWhiteSpace(activeDatabase)
            ? metadata
            : MssqlIntelliSenseCacheReader.FilterByDatabase(metadata, activeDatabase);

    private static string? TryGetUseDatabaseBeforeCaret(string? sql, int caretPosition)
    {
        if (string.IsNullOrWhiteSpace(sql) || caretPosition <= 0)
        {
            return null;
        }

        var length = Math.Min(caretPosition, sql!.Length);
        var beforeCaret = sql.Substring(0, length);
        var matches = Regex.Matches(
            beforeCaret,
            @"(?im)^\s*USE\s+(?:\[([^\]]+)\]|""([^""]+)""|([A-Za-z0-9_.-]+))\s*;?");

        if (matches.Count == 0)
        {
            return null;
        }

        var match = matches[matches.Count - 1];
        return match.Groups[1].Success ? match.Groups[1].Value.Replace("]]", "]")
            : match.Groups[2].Success ? match.Groups[2].Value.Replace("\"\"", "\"")
            : match.Groups[3].Success ? match.Groups[3].Value
            : null;
    }

    internal static bool TryOpenObjectReviewFromSql(string sql, int caretPosition)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var token = ExtractObjectTokenAtCaret(sql, caretPosition);
        if (string.IsNullOrWhiteSpace(token))
        {
            MssqlIntelliSensePackage.Log("[Autocomplete] Review-at-caret skipped: no token at caret.");
            return false;
        }

        var metadata = GetDatabaseMetadata(sql, caretPosition);
        if (metadata == null || metadata == DatabaseMetadata.Empty)
        {
            MssqlIntelliSensePackage.Log($"[Autocomplete] Review-at-caret skipped for '{token}': no metadata.");
            return false;
        }

        if (!TryCreateReviewItem(token!, metadata, out var item))
        {
            MssqlIntelliSensePackage.Log($"[Autocomplete] Review-at-caret skipped: no reviewable object matched '{token}'.");
            return false;
        }

        MssqlIntelliSensePackage.Log($"[Autocomplete] Review-at-caret opening object review for {item.Kind}: {item.Label}");
        ObjectReviewWindow.ShowForCompletion(item, metadata);
        return true;
    }

    private static bool TryCreateReviewItem(string token, DatabaseMetadata metadata, out SqlCompletionItem item)
    {
        var parts = token.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(UnquoteIdentifier)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .ToArray();

        var objectName = parts.Length == 0 ? token : parts[parts.Length - 1];
        var schemaName = parts.Length >= 2 ? parts[parts.Length - 2] : null;

        bool SchemaMatches(string schema) =>
            string.IsNullOrWhiteSpace(schemaName) ||
            schema.Equals(schemaName, StringComparison.OrdinalIgnoreCase);

        static bool NameMatches(string candidate, string typed) =>
            candidate.Equals(typed, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(typed, StringComparison.OrdinalIgnoreCase);

        var view = metadata.Views
            .Where(v => SchemaMatches(v.Schema))
            .OrderByDescending(v => v.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(v => NameMatches(v.Name, objectName));
        if (view != null)
        {
            item = BuildReviewItem(SqlCompletionKind.View, view.Schema, view.Name, SqlDefinitionFormatter.FormatViewDefinition(view));
            return true;
        }

        var table = metadata.Tables
            .Where(t => SchemaMatches(t.Schema))
            .OrderByDescending(t => t.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(t => NameMatches(t.Name, objectName));
        if (table != null)
        {
            item = BuildReviewItem(SqlCompletionKind.Table, table.Schema, table.Name, SqlDefinitionFormatter.FormatTableDefinition(table));
            return true;
        }

        var proc = metadata.Procedures
            .Where(p => SchemaMatches(p.Schema))
            .OrderByDescending(p => p.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(p => NameMatches(p.Name, objectName));
        if (proc != null)
        {
            item = BuildReviewItem(SqlCompletionKind.Procedure, proc.Schema, proc.Name, SqlDefinitionFormatter.FormatProcedureDefinition(proc));
            return true;
        }

        var function = metadata.Functions
            .Where(f => SchemaMatches(f.Schema))
            .OrderByDescending(f => f.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(f => NameMatches(f.Name, objectName));
        if (function != null)
        {
            item = BuildReviewItem(SqlCompletionKind.Function, function.Schema, function.Name, SqlDefinitionFormatter.FormatFunctionDefinition(function));
            return true;
        }

        var userType = metadata.UserTypes
            .Where(t => SchemaMatches(t.Schema))
            .OrderByDescending(t => t.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(t => NameMatches(t.Name, objectName));
        if (userType != null)
        {
            item = BuildReviewItem(SqlCompletionKind.UserType, userType.Schema, userType.Name, SqlDefinitionFormatter.FormatUserTypeDefinition(userType));
            return true;
        }

        var synonym = metadata.Synonyms
            .Where(s => SchemaMatches(s.Schema))
            .OrderByDescending(s => s.Name.Equals(objectName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(s => NameMatches(s.Name, objectName));
        if (synonym != null)
        {
            item = BuildReviewItem(SqlCompletionKind.Synonym, synonym.Schema, synonym.Name, SqlDefinitionFormatter.FormatSynonymDefinition(synonym));
            return true;
        }

        item = null!;
        return false;

        static SqlCompletionItem BuildReviewItem(SqlCompletionKind kind, string schema, string name, string description) =>
            new($"{schema}.{name}", $"[{schema}].[{name}]", kind, description);
    }

    private static string? ExtractObjectTokenAtCaret(string sql, int caretPosition)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var position = Math.Max(0, Math.Min(caretPosition, sql.Length));
        var start = position;
        while (start > 0 && IsObjectTokenChar(sql[start - 1]))
        {
            start--;
        }

        var end = position;
        while (end < sql.Length && IsObjectTokenChar(sql[end]))
        {
            end++;
        }

        return end <= start ? null : sql.Substring(start, end - start).Trim();
    }

    private static bool IsObjectTokenChar(char ch) =>
        char.IsLetterOrDigit(ch) || ch is '_' or '.' or '[' or ']' or '#' or '@' or '$';

    private static string UnquoteIdentifier(string value) =>
        value.Trim().Trim('[', ']').Replace("]]", "]");

    private static void QueueMetadataRefresh(string cacheKey, string connectionString)
    {
        lock (CacheLock)
        {
            if (!LoadingConnections.Add(cacheKey))
            {
                return;
            }
        }

        _ = Task.Run(() =>
        {
            try
            {
                var fullMetadata = MssqlIntelliSenseCacheReader.GetMetadataByConnectionString(connectionString);
                lock (CacheLock)
                {
                    MetadataCache[cacheKey] = new MetadataCacheEntry(fullMetadata, DateTime.UtcNow);
                }
            }
            catch (Exception ex)
            {
                MssqlIntelliSensePackage.Log($"[Autocomplete Cache Error] {ex.Message}");
            }
            finally
            {
                lock (CacheLock)
                {
                    LoadingConnections.Remove(cacheKey);
                }
            }
        });
    }


    public void AugmentCompletionSession(ICompletionSession session, IList<CompletionSet> completionSets)
    {
        if (_isDisposed) return;

        ThreadHelper.ThrowIfNotOnUIThread();

        SnapshotPoint? triggerPoint = session.GetTriggerPoint(_textBuffer.CurrentSnapshot);
        if (!triggerPoint.HasValue) return;

        ITextSnapshot snapshot = _textBuffer.CurrentSnapshot;
        var triggerSnapshotPoint = triggerPoint.Value;
        
        var line = triggerSnapshotPoint.GetContainingLine();
        string lineText = line.GetText();
        var trimmed = lineText.TrimStart();
        if (trimmed.StartsWith("--"))
        {
            var prompt = trimmed.Substring(2).Trim();
            if (prompt.Length >= 3 && !prompt.StartsWith("⚡") && !prompt.StartsWith("⚠️"))
            {
                var applicableToComment = snapshot.CreateTrackingSpan(
                    new Span(line.Start.Position, line.Length),
                    SpanTrackingMode.EdgeInclusive);
                
                var completionsComment = new List<Completion>
                {
                    new Completion(
                        $"✨ Ask AI: \"{prompt}\"",
                        prompt,
                        prompt,
                        CompletionIconProvider.GetIcon(SqlCompletionKind.Keyword),
                        "MSSQL IntelliSense"
                    )
                };

                completionSets.Add(new CompletionSet(
                    "MSSQL IntelliSense",
                    "MSSQL IntelliSense",
                    applicableToComment,
                    completionsComment,
                    Enumerable.Empty<Completion>()
                ));
                return;
            }
        }

        string sql = snapshot.GetText();
        int caretPosition = triggerSnapshotPoint.Position;

        DatabaseMetadata metadata = DatabaseMetadata.Empty;
        var cached = GetDatabaseMetadata(sql, caretPosition);
        if (cached != null)
        {
            metadata = cached;
        }

        var completionItems = SharedProvider.GetCompletions(sql, caretPosition, metadata);
        MssqlIntelliSensePackage.Log(
            $"[Autocomplete] Augment caret={caretPosition}, contentType={_textBuffer.ContentType.TypeName}, " +
            $"metadata tables={metadata.Tables.Count}, views={metadata.Views.Count}, procs={metadata.Procedures.Count}, funcs={metadata.Functions.Count}, " +
            $"items={completionItems?.Count ?? 0}");

        if (completionItems == null || completionItems.Count == 0) return;

        // Store items in session for RecordUsage and object review on commit.
        session.Properties[CompletionItemsPropertyKey] = completionItems;
        session.Properties[CompletionMetadataPropertyKey] = metadata;

        // Find the start of the current word being typed (including quoted identifiers starting with [
        var start = triggerSnapshotPoint.Position;
        while (start > line.Start.Position)
        {
            char ch = snapshot[start - 1];
            // Check if we found opening bracket
            if (ch == '[')
            {
                start--;
                break;
            }
            // Stop at other non-word characters
            if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '@' && ch != '$' && ch != '#')
            {
                break;
            }
            start--;
        }

        var applicableTo = snapshot.CreateTrackingSpan(
            new Span(start, triggerSnapshotPoint.Position - start),
            SpanTrackingMode.EdgeInclusive);

        var completions = completionItems.Select(item => new Completion(
            item.Label,
            item.InsertText,
            item.Description,
            CompletionIconProvider.GetIcon(item.Kind),
            CompletionIconProvider.GetAutomationText(item.Kind)
        )).ToList();

        completionSets.Add(new CompletionSet(
            "MSSQL IntelliSense",
            "MSSQL IntelliSense",
            applicableTo,
            completions,
            Enumerable.Empty<Completion>()
        ));
    }

    internal static async Task GenerateAndReplaceSqlAsync(ITextView textView, SnapshotSpan lineSpan, string promptText)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var textBuffer = textView.TextBuffer;
        
        var trackingSpan = textBuffer.CurrentSnapshot.CreateTrackingSpan(lineSpan, SpanTrackingMode.EdgeInclusive);
        textBuffer.Replace(lineSpan, "-- ⚡ Generating SQL...");

        string generatedSql = "";
        string? errorMessage = null;
        var connection = MssqlIntelliSensePackage.GetActiveConnectionString();
        try
        {
            await Task.Run(async () =>
            {
                var options = await MssqlIntelliSensePackage.FetchLlmSettingsStaticAsync(default);
                
                DatabaseMetadata metadata = DatabaseMetadata.Empty;
                if (!string.IsNullOrEmpty(connection))
                {
                    metadata = MssqlIntelliSenseCacheReader.GetMetadataByConnectionString(connection!);
                }

                var agentOptions = new MssqlIntelliSense.Core.Ai.OpenAiSqlAgentOptions
                {
                    ApiKey = options.ApiKey,
                    Model = options.Model,
                    ToolApprovalHandler = async (toolCall, _) =>
                    {
                        bool approved = false;
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        var message =
                            "AI agent muốn dùng tool trước khi tạo SQL.\n\n" +
                            $"Tool: {toolCall.Name}\n" +
                            $"Mô tả: {toolCall.Description}\n" +
                            $"Arguments: {toolCall.ArgumentsJson}\n\n" +
                            "Bạn có cho phép thực thi tool này không?";

                        approved = System.Windows.MessageBox.Show(
                            message,
                            "Duyệt tool trước khi chạy",
                            System.Windows.MessageBoxButton.YesNo,
                            System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

                        return approved;
                    }
                };
                if (Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpointUri))
                {
                    agentOptions = agentOptions with { Endpoint = endpointUri };
                }

                using (var httpClient = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) })
                {
                    var agent = new MssqlIntelliSense.Core.Ai.OpenAiSqlAgent(httpClient, agentOptions);
                    var result = await agent.ImproveSqlAsync(
                        sql: "-- " + promptText,
                        metadata: metadata,
                        instruction: $"Generate only the SQL query for the prompt: '{promptText}'. Do not explain or include markdown blocks.",
                        cancellationToken: default
                    );
                    generatedSql = result.ImprovedSql;
                }
            });
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var currentSpan = trackingSpan.GetSpan(textBuffer.CurrentSnapshot);
        if (string.IsNullOrWhiteSpace(errorMessage) && !string.IsNullOrWhiteSpace(generatedSql))
        {
            textBuffer.Replace(currentSpan, generatedSql);
        }
        else
        {
            textBuffer.Replace(currentSpan, $"-- {promptText}{Environment.NewLine}-- ⚠️ AI Generation Failed: {errorMessage}");
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
    }
}

internal static class CompletionIconProvider
{
    private static readonly Dictionary<SqlCompletionKind, ImageSource?> IconCache = new();

    internal static ImageSource? GetIcon(SqlCompletionKind kind)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (IconCache.TryGetValue(kind, out var cached))
        {
            return cached;
        }

        var icon = GetImageSource(GetMoniker(kind));
        IconCache[kind] = icon;
        return icon;
    }

    internal static string GetAutomationText(SqlCompletionKind kind) => kind switch
    {
        SqlCompletionKind.Keyword => "Keyword",
        SqlCompletionKind.Schema => "Schema",
        SqlCompletionKind.Table => "Table",
        SqlCompletionKind.Column => "Column",
        SqlCompletionKind.Database => "Database",
        SqlCompletionKind.LinkedServer => "Linked server",
        SqlCompletionKind.View => "View",
        SqlCompletionKind.Procedure => "Stored procedure",
        SqlCompletionKind.Function => "Function",
        SqlCompletionKind.UserType => "User type",
        SqlCompletionKind.Synonym => "Synonym",
        SqlCompletionKind.BaseType => "Data type",
        SqlCompletionKind.Variable => "Variable",
        SqlCompletionKind.Snippet => "Snippet",
        _ => "Completion item"
    };

    private static ImageMoniker GetMoniker(SqlCompletionKind kind) => kind switch
    {
        SqlCompletionKind.Keyword => KnownMonikers.IntellisenseKeyword,
        SqlCompletionKind.Schema => KnownMonikers.Class,
        SqlCompletionKind.Table => KnownMonikers.Table,
        SqlCompletionKind.Column => KnownMonikers.Column,
        SqlCompletionKind.Database => KnownMonikers.Database,
        SqlCompletionKind.LinkedServer => KnownMonikers.Database,
        SqlCompletionKind.View => KnownMonikers.View,
        SqlCompletionKind.Procedure => KnownMonikers.Method,
        SqlCompletionKind.Function => KnownMonikers.Method,
        SqlCompletionKind.UserType => KnownMonikers.Property,
        SqlCompletionKind.Synonym => KnownMonikers.Reference,
        SqlCompletionKind.BaseType => KnownMonikers.Property,
        SqlCompletionKind.Variable => KnownMonikers.Property,
        SqlCompletionKind.Snippet => KnownMonikers.Snippet,
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
