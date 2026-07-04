using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class ColumnCandidate : CandidateBase
{
    private readonly ColumnMetadata _column;

    public ColumnCandidate(ColumnMetadata column, ICandidate? parentCandidate) : base(column.Name)
    {
        _column = column;
        ParentCandidate = parentCandidate;
    }

    public ICandidate? ParentCandidate { get; }
    public override bool HasOwner => ParentCandidate != null;
    public override ICandidate? OwnerCandidate => ParentCandidate;
    public override string? OwnerCandidateName => ParentCandidate?.Name;
    public override SqlObjectType ObjectType => SqlObjectType.Column;
    public override ICandidateCollection Children => null!;
    public override string CompletionName => _column.Name;
    public override string CompletionText => SqlCompletionHelper.Quote(_column.Name);
    public string DataType => _column.DataType;
    public bool IsNullable => _column.IsNullable;
    public int Ordinal => _column.Ordinal;
}
