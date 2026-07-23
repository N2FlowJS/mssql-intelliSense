using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class ProcedureCandidate : DbObjectBase
{
    private readonly ProcedureMetadata _procedure;

    public ProcedureCandidate(ProcedureMetadata procedure, IDbObject? schemaOwner) : base(procedure.Name)
    {
        _procedure = procedure;
        SchemaOwner = schemaOwner;
        DatabaseName = procedure.Database;
        Children = new CandidateCollection<ICandidate>();
    }

    public IDbObject? SchemaOwner { get; }
    public override IDbObject? Owner => SchemaOwner;
    public override SqlObjectType ObjectType => SqlObjectType.StoredProcedure;
    public override ICandidateCollection Children { get; }
    public override string CompletionName => _procedure.Name;
    public override string CompletionText => SqlCompletionHelper.Quote(_procedure.Name);
    public override string RawName => _procedure.Name;
    public IReadOnlyList<FunctionParameterMetadata> Parameters => _procedure.Parameters;
    public string Schema => _procedure.Schema;
    public string ObjectTypeName => _procedure.ObjectType;

    public string GetExecParameterList() =>
        Parameters.Count > 0
            ? $"({string.Join(", ", Parameters.Select(p => $"{p.Name} = ?"))})"
            : "";
}
