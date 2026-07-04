using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class ViewCandidate : DbObjectBase
{
    private readonly ViewMetadata _view;

    public ViewCandidate(ViewMetadata view, IDbObject? schemaOwner) : base(view.Name)
    {
        _view = view;
        SchemaOwner = schemaOwner;
        DatabaseName = view.Database;
    }

    public IDbObject? SchemaOwner { get; }
    public override IDbObject? Owner => SchemaOwner;
    public override SqlObjectType ObjectType => SqlObjectType.View;
    public override ICandidateCollection Children { get; }
    public override string CompletionName => _view.Name;
    public override string CompletionText => SqlCompletionHelper.Quote(_view.Name);
    public override string RawName => _view.Name;
    public IReadOnlyList<ColumnMetadata> Columns => _view.Columns;
    public string Schema => _view.Schema;

    public ColumnCandidate? FindColumn(string name)
    {
        var col = _view.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return col != null ? new ColumnCandidate(col, this) : null;
    }
}
