using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class UserTypeCandidate : DbObjectBase
{
    private readonly UserTypeMetadata _type;

    public UserTypeCandidate(UserTypeMetadata type, IDbObject? schemaOwner) : base(type.Name)
    {
        _type = type;
        SchemaOwner = schemaOwner;
        DatabaseName = type.Database;
    }

    public IDbObject? SchemaOwner { get; }
    public override IDbObject? Owner => SchemaOwner;
    public override SqlObjectType ObjectType => SqlObjectType.UserDefinedType;
    public override ICandidateCollection Children => null!;
    public override string CompletionName => _type.Name;
    public override string CompletionText => SqlCompletionHelper.Quote(_type.Name);
    public override string RawName => _type.Name;
    public string Schema => _type.Schema;
    public string BaseType => _type.BaseType;
    public IReadOnlyList<ColumnMetadata> Columns => _type.Columns;
    public bool IsTableType => _type.IsTableType;

    public ColumnCandidate? FindColumn(string name)
    {
        var col = _type.Columns.FirstOrDefault(c => c.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        return col != null ? new ColumnCandidate(col, this) : null;
    }
}
