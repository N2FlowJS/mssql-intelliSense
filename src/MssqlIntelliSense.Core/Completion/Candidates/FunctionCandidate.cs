using System.Collections.Generic;
using System.Linq;
using MssqlIntelliSense.Core.Completion;
using MssqlIntelliSense.Core.Metadata;

namespace MssqlIntelliSense.Core.Completion.Candidates;

public sealed class FunctionCandidate : DbObjectBase
{
    private readonly FunctionMetadata _function;

    public FunctionCandidate(FunctionMetadata function, IDbObject? schemaOwner) : base(function.Name)
    {
        _function = function;
        SchemaOwner = schemaOwner;
        DatabaseName = function.Database;
    }

    public IDbObject? SchemaOwner { get; }
    public override IDbObject? Owner => SchemaOwner;
    public override SqlObjectType ObjectType => _function.FunctionType switch
    {
        "FN" => SqlObjectType.ScalarFunction,
        "TF" or "IF" => SqlObjectType.TableValuedFunction,
        "AF" => SqlObjectType.AggregateFunction,
        _ => SqlObjectType.ScalarFunction,
    };
    public override ICandidateCollection Children { get; }
    public override string CompletionName => _function.Name;
    public override string CompletionText => SqlCompletionHelper.Quote(_function.Name);
    public override string RawName => _function.Name;
    public IReadOnlyList<FunctionParameterMetadata> Parameters => _function.Parameters;
    public string Schema => _function.Schema;
    public string FunctionType => _function.FunctionType;
    public string ReturnType => _function.ReturnType;
}
