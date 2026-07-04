using System.Collections.Generic;
using MssqlIntelliSense.Core.Completion;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class SchemaCandidate : DbObjectBase, ISchemaCandidate
{
    public SchemaCandidate(string name, IDbObject? databaseOwner, CandidateCollection<ICandidate>? children = null) : base(name)
    {
        DatabaseOwner = databaseOwner;
        DatabaseName = databaseOwner?.DatabaseName ?? "";
        _children = children ?? new CandidateCollection<ICandidate>();
    }

    private readonly CandidateCollection<ICandidate> _children;

    public override IDbObject? Owner => DatabaseOwner;
    public IDbObject? DatabaseOwner { get; }
    public override SqlObjectType ObjectType => SqlObjectType.Schema;
    public override ICandidateCollection Children => _children;
    ICandidateCollection ISchemaCandidate.Candidates => _children;
    public override string CompletionName => Name;
    public override string CompletionText => SqlCompletionHelper.Quote(Name) + ".";
}
