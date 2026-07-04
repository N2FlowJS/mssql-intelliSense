namespace MssqlIntelliSense.Core.Completion.Candidates;

public enum SqlObjectType
{
    All = -1,
    Unknown = 0,
    Table,
    View,
    StoredProcedure,
    ScalarFunction,
    TableValuedFunction,
    AggregateFunction,
    Column,
    Schema,
    Database,
    Server,
    LinkedServer,
    UserDefinedType,
    Synonym,
    Trigger,
    Index,
    ForeignKey,
    PrimaryKey,
    Snippet,
    Keyword
}
