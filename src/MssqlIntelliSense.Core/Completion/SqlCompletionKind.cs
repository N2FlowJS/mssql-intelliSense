namespace MssqlIntelliSense.Core.Completion;

public enum SqlCompletionKind
{
    Keyword,
    Schema,
    Table,
    Column,
    Database,
    LinkedServer,
    View,
    Procedure,
    Function,
    UserType,
    Synonym,
    BaseType,
    Snippet
}

public sealed record SqlCompletionItem(
    string Label,
    string InsertText,
    SqlCompletionKind Kind,
    string Description,
    int CaretOffset = -1,
    int SelectionStart = -1,
    int SelectionEnd = -1);
