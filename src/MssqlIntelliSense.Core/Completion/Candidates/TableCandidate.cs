using System;
using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class TableCandidate : DbObjectBase
{
    private readonly TableMetadata _table;

    public TableCandidate(TableMetadata table, IDbObject? schemaOwner)
        : base(table.Name)
    {
        _table = table;
        SchemaOwner = schemaOwner;
        DatabaseName = table.Database;
    }

    public IDbObject? SchemaOwner { get; }
    public override IDbObject? Owner => SchemaOwner;
    public override SqlObjectType ObjectType => SqlObjectType.Table;
    public override ICandidateCollection Children { get; }
    public override string CompletionName => _table.Name;
    public override string CompletionText => SqlCompletionHelper.Quote(_table.Name);
    public override string RawName => _table.Name;
    public IReadOnlyList<ColumnMetadata> Columns => _table.Columns;
    public IReadOnlyList<string> PrimaryKeyColumns => _table.PrimaryKeyColumns;
    public string Schema => _table.Schema;
    internal TableMetadata Source => _table;

    public ColumnCandidate? FindColumn(string name)
    {
        var col = _table.Columns.FirstOrDefault(c => c.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        return col != null ? new ColumnCandidate(col, this) : null;
    }
}
