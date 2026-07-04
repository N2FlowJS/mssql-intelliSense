using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class SynonymCandidate : DbObjectBase
{
    private readonly SynonymMetadata _synonym;

    public SynonymCandidate(SynonymMetadata synonym, IDbObject? schemaOwner) : base(synonym.Name)
    {
        _synonym = synonym;
        SchemaOwner = schemaOwner;
        DatabaseName = synonym.Database;
    }

    public IDbObject? SchemaOwner { get; }
    public override IDbObject? Owner => SchemaOwner;
    public override SqlObjectType ObjectType => SqlObjectType.Synonym;
    public override ICandidateCollection Children => null!;
    public override string CompletionName => _synonym.Name;
    public override string CompletionText => SqlCompletionHelper.Quote(_synonym.Name);
    public override string RawName => _synonym.Name;
    public string Schema => _synonym.Schema;
    public string TargetObject => _synonym.TargetObject;
}
